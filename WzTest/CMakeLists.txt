cmake_minimum_required(VERSION 3.14)
project(WzTest CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# OpenSSL 필요 (AES 키스트림 생성용)
find_package(OpenSSL REQUIRED)

add_executable(wz_test wz_test.cpp)

target_link_libraries(wz_test PRIVATE OpenSSL::SSL OpenSSL::Crypto)

# Release 빌드 최적화
target_compile_options(wz_test PRIVATE
    $<$<CONFIG:Release>:-O2>
)

message(STATUS "OpenSSL version: ${OPENSSL_VERSION}")
message(STATUS "Build type: ${CMAKE_BUILD_TYPE}")
