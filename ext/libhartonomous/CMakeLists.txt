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

# ── Intel oneAPI discovery ────────────────────────────────────
# MKL (BLAS/LAPACK/Sparse), OpenMP, IPP. All mandatory — CPU-only mandate
# says every optimization is in. No fallback kernels, no "follow-up".

set(ONEAPI_ROOT_DEFAULT "C:/Program Files (x86)/Intel/oneAPI")
if(NOT DEFINED ENV{ONEAPI_ROOT} AND EXISTS "${ONEAPI_ROOT_DEFAULT}")
    set(ENV{ONEAPI_ROOT} "${ONEAPI_ROOT_DEFAULT}")
endif()

# MKL config module ships at <oneAPI>/mkl/latest/lib/cmake/mkl
set(MKL_ROOT_HINT "$ENV{ONEAPI_ROOT}/mkl/latest")
list(APPEND CMAKE_PREFIX_PATH "${MKL_ROOT_HINT}/lib/cmake/mkl")

set(MKL_INTERFACE lp64 CACHE STRING "" FORCE)            # 32-bit int indexing — matches our int64 API math but MKL lp64 is the mainstream
set(MKL_THREADING intel_thread CACHE STRING "" FORCE)    # iomp5 — deterministic with CBWR=AUTO,STRICT
set(MKL_LINK dynamic CACHE STRING "" FORCE)
find_package(MKL CONFIG REQUIRED)

# Use MSVC's LLVM-based OpenMP runtime — OpenMP 4.5+ supports int64_t loop
# counters (classic /openmp is stuck at 2.0 and rejects signed long long).
# We bypass find_package(OpenMP) because its probe emits plain /openmp.
if(MSVC)
    set(HARTONOMOUS_OPENMP_FLAGS "/openmp:llvm")
else()
    find_package(OpenMP REQUIRED)
    set(HARTONOMOUS_OPENMP_FLAGS "${OpenMP_C_FLAGS}")
endif()

include(FetchContent)

# BLAKE3 reference implementation.
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
    src/runtime_info.c
    src/hash.c
    src/s3_geometry.c
    src/super_fibonacci.c
    src/hilbert.c
    src/tensor_decode.c
    src/gemm.c
    src/knn.c
    src/sparse_eigs.c
    src/gram_schmidt.c
    src/merkle.c
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

target_link_libraries(hartonomous
    PRIVATE
        blake3
        MKL::MKL
)
if(NOT MSVC)
    target_link_libraries(hartonomous PRIVATE OpenMP::OpenMP_C)
endif()

target_compile_definitions(hartonomous PRIVATE HARTONOMOUS_BUILD)

# AVX-512 for tensor_decode SIMD fast paths on the 14900KS target.
# x86-64-v4 (AVX-512F+BW+CD+DQ+VL) covers the intrinsics we use.
if(MSVC)
    target_compile_options(hartonomous PRIVATE /W4 /permissive- /utf-8 /arch:AVX2 ${HARTONOMOUS_OPENMP_FLAGS})
else()
    target_compile_options(hartonomous PRIVATE -Wall -Wextra -Wpedantic -mavx2 -mfma ${HARTONOMOUS_OPENMP_FLAGS})
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
