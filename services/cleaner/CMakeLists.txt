cmake_minimum_required (VERSION 2.8.11)
project (CLEANER)

find_package(Boost COMPONENTS system filesystem serialization REQUIRED)
find_package(OpenSSL REQUIRED)
find_package(Threads REQUIRED)

set(CMAKE_CXX_FLAGS         "${CMAKE_CXX_FLAGS} -std=c++11 -Wall -Wno-unknown-pragmas -Wno-sign-compare -Woverloaded-virtual -Wwrite-strings -Wno-unused -Wno-deprecated")
set(CMAKE_CXX_FLAGS_DEBUG   "-O0 -g3")
set(CMAKE_CXX_FLAGS_RELEASE "-O3")

add_executable (cleaner src/main.cpp src/servant.cpp src/room.cpp src/program.cpp src/pass_checker.cpp src/saveloader.cpp src/session.cpp src/command.cpp src/parser.cpp src/server.cpp)

target_link_libraries(cleaner 
  ${Boost_FILESYSTEM_LIBRARY}
  ${Boost_SYSTEM_LIBRARY}
  ${Boost_SERIALIZATION_LIBRARY}
  ${OPENSSL_LIBRARIES}
  ${CMAKE_THREAD_LIBS_INIT}
)

set_target_properties( cleaner
    PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
