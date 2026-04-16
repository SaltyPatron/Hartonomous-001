cmake_minimum_required(VERSION 3.24)
project(hartonomous
    VERSION 0.1.0
    LANGUAGES C CXX
    DESCRIPTION "Hartonomous native shared library"
)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

option(HARTONOMOUS_BUILD_TESTS "Build Google Test suite" ON)
option(HARTONOMOUS_BUILD_SHARED "Build shared library" ON)

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

include(FetchContent)

# BLAKE3 reference implementation. Its CMakeLists.txt lives in the c/ subdir
# and produces a `blake3` target with SIMD dispatch (AVX-512/AVX2/SSE4.1 on x86,
# NEON on ARM). We link it statically into libhartonomous.
FetchContent_Declare(
    blake3
    GIT_REPOSITORY https://github.com/BLAKE3-team/BLAKE3.git
    GIT_TAG        1.5.4
    SOURCE_SUBDIR  c
)
set(BLAKE3_TESTING OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(blake3)

set(HARTONOMOUS_SOURCES
    src/version.c
    src/hash.c
    src/s3_geometry.c
    src/super_fibonacci.c
    src/hilbert.c
)

set(HARTONOMOUS_HEADERS
    include/hartonomous.h
)

if(HARTONOMOUS_BUILD_SHARED)
    add_library(hartonomous SHARED ${HARTONOMOUS_SOURCES} ${HARTONOMOUS_HEADERS})
else()
    add_library(hartonomous STATIC ${HARTONOMOUS_SOURCES} ${HARTONOMOUS_HEADERS})
endif()

target_include_directories(hartonomous
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(hartonomous PRIVATE blake3)

target_compile_definitions(hartonomous PRIVATE HARTONOMOUS_BUILD)

if(MSVC)
    target_compile_options(hartonomous PRIVATE /W4 /permissive- /utf-8)
else()
    target_compile_options(hartonomous PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(hartonomous PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
)

if(HARTONOMOUS_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
