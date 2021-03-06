using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Node.Connections;
using Node.Encryption;
using Node.Messages;
using Node.Routing;
using Node.Serialization;

namespace Node.Data
{
    internal class DataManager : IDataManager
    {
        public DataManager(IDataStorage dataStorage, string dataFilePath, IRoutingManager routingManager, IEncryptionManager encryptionManager)
        {
            this.dataStorage = dataStorage;
            this.dataFilePath = dataFilePath;
            this.routingManager = routingManager;
            this.encryptionManager = encryptionManager;
            random = new Random();
            pendingMessages = new List<QueueEntry>();
        }

        public bool ProcessMessage(IMessage message, IConnection connection)
        {
            if (message == null)
                return true;
            return
                ProcessData(message as DataMessage) ||
                ProcessRedirect(message as RedirectMessage) ||
                ProcessPull(message as PullMessage, connection);
        }

        public void PushMessages(IEnumerable<IConnection> readyConnections)
        {
            foreach (var connection in readyConnections)
            {
                var pending = pendingMessages.FirstOrDefault(m => Equals(m.Destination, connection.RemoteAddress));
                if (pending.Destination == null)
                    continue;

                Console.WriteLine("[{0}] Pending :  {1} {2}", routingManager.Map.OwnAddress, pending.Message, pending.RawData);
                
                var result = pending.Message != null ? connection.Push(pending.Message) : connection.Push(pending.RawData);
                if (result == SendResult.Success)
                {
                    Console.WriteLine("[{0}] Pushed a pending message to {1}", routingManager.Map.OwnAddress, connection.RemoteAddress);
                    pendingMessages.Remove(pending);
                }
            }
        }

        public void FlushData()
        {
            using (var stream = File.Create(dataFilePath))
                new StreamSerializer(stream).Write(dataStorage);
        }

        public void DispatchData(string key, byte[] data, IAddress destination)
        {
            var message = new DataMessage(DataAction.Put, key, data, routingManager.Map.OwnAddress);
            if (Equals(destination, routingManager.Map.OwnAddress))
                ProcessData(message);
            else
                EnqueueMessage(message, destination);
        }

        public void RequestData(string key, IAddress destination)
        {
            var message = new DataMessage(DataAction.Get, key, new byte[0], routingManager.Map.OwnAddress);
            if (Equals(destination, routingManager.Map.OwnAddress))
                ProcessData(message);
            else
                EnqueueMessage(message, destination);
        }

        public event Action<DataMessage> OnReceivedData = data => { };

        public bool PullPendingMessage(IConnection connection)
        {
            var message = pendingMessages
                .FirstOrDefault(m => Equals(m.Destination, connection.RemoteAddress) && m.UnwrappedMessage != null);
            if (message.UnwrappedMessage == null)
                return false;
            var result = connection.Push(message.UnwrappedMessage);
            if (result == SendResult.Success)
            {
                pendingMessages.Remove(message);
                return true;
            }
            return false;
        }

        public IDataStorage DataStorage => dataStorage;

        private void EnqueueMessage(DataMessage message, IAddress destination)
        {
            for (int i = 0; i < 1; i++)
            {
                //var path = routingManager.Map.Links.CreateRandomPath(routingManager.Map.OwnAddress, destination, 2, 5, random);
                var path = routingManager.Map.Links.CreateShortestPath(routingManager.Map.OwnAddress, destination);
                if (path == null || path.Count == 0)
                    continue;
                var pathBody = path.GetPathBody();
                var wrapped = WrapMessage(message, pathBody);
                Console.WriteLine("[{0}] Added pending message : {1} {2} - {3} by path {4} to {5}", routingManager.Map.OwnAddress, message, wrapped, message.Key,
                    string.Join(", ", path), destination);
                pendingMessages.Add(new QueueEntry(wrapped, message, path[1]));
            }
        }

        private IMessage WrapMessage(IMessage message, List<IAddress> path)
        {
            while (path.Count > 0)
            {
                var length = new MessageContainer(message).WriteToBuffer(serializerBuffer, 0);
                encryptionManager.EncryptData(serializerBuffer, MessageContainer.HeaderSize, length - MessageContainer.HeaderSize, path.Last());
                var wrapped = new RedirectMessage(path.Last(), serializerBuffer.Take(length).ToArray());
                message = wrapped;
                path = path.Take(path.Count - 1).ToList();
            }
            return message;
        }

        private bool ProcessRedirect(RedirectMessage redirectMessage)
        {
            if (redirectMessage == null)
                return false;

            Console.WriteLine("[{0}] Process redirect : {1}", routingManager.Map.OwnAddress, redirectMessage.Destination);

            pendingMessages.Add(new QueueEntry(redirectMessage.Data, redirectMessage.Destination));
            return true;
        }

        private bool ProcessData(DataMessage dataMessage)
        {
            if (dataMessage == null)
                return false;

            Console.WriteLine("[{0}] Process data : {1} ({2})", routingManager.Map.OwnAddress, dataMessage.Key, dataMessage.Action);

            switch (dataMessage.Action)
            {
                case DataAction.None:
                    OnReceivedData(dataMessage);
                    break;
                case DataAction.Put:
                    Console.WriteLine("[{0}] Put by key {1}", routingManager.Map.OwnAddress, dataMessage.Key);
                    if (dataStorage.PutData(dataMessage.Key, dataMessage.Data))
                        FlushData();
                    break;
                case DataAction.Get:
                    var data = dataStorage.GetData(dataMessage.Key);
                    Console.WriteLine("[{0}] Get by key {1}", routingManager.Map.OwnAddress, dataMessage.Key);
                    if (data != null)
                    {
                        var message = new DataMessage(DataAction.None, dataMessage.Key, data, routingManager.Map.OwnAddress);
                        if (Equals(dataMessage.Source, routingManager.Map.OwnAddress))
                            ProcessData(message);
                        else
                            EnqueueMessage(message, dataMessage.Source);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return true;
        }

        private bool ProcessPull(PullMessage pullMessage, IConnection connection)
        {
            if (pullMessage == null)
                return false;
            //Console.WriteLine("[{0}] Processing pull message ({1}) from {2}", routingManager.Map.OwnAddress, pullMessage.Limit, connection.RemoteAddress);
            for (int i = 0; i < pullMessage.Limit; i++)
            {
                if (!PullPendingMessage(connection))
                    break;
                //Console.WriteLine("[{0}] Fast-forwarded a message to {1}", routingManager.Map.OwnAddress, connection.RemoteAddress);
            }
            return true;
        }

        private readonly List<QueueEntry> pendingMessages;

        private readonly IDataStorage dataStorage;
        private readonly string dataFilePath;
        private readonly IRoutingManager routingManager;
        private readonly IEncryptionManager encryptionManager;

        private readonly byte[] serializerBuffer = new byte[1024 * 1024 * 4];
        private readonly Random random;

        private struct QueueEntry
        {
            public QueueEntry(IMessage message, IMessage unwrappedMessage, IAddress destination)
            {
                Message = message;
                UnwrappedMessage = unwrappedMessage;
                Destination = destination;
                RawData = null;
            }
            public QueueEntry(byte[] rawData, IAddress destination)
            {
                RawData = rawData;
                Destination = destination;
                Message = null;
                UnwrappedMessage = null;
            }

            public readonly IMessage Message;
            public readonly IMessage UnwrappedMessage;
            public readonly byte[] RawData;
            public readonly IAddress Destination;
        }
    }
}