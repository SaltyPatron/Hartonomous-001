cmake_minimum_required(VERSION 3.22)
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
#
# On Linux the Docker pgext build mixes compilers: CC=icx (Intel oneAPI)
# for C and CXX=g++ for C++ (icpx 2025.3.2 has a SIGSEGV in
# hir-ssa-deconstruction on laplacian_eigenmap.cpp). icx's OpenMP flag is
# `-fiopenmp`; g++'s is `-fopenmp`. We MUST NOT apply OpenMP_C_FLAGS to
# C++ files — instead, link the per-language OpenMP::OpenMP_C and
# OpenMP::OpenMP_CXX INTERFACE targets, which carry language-scoped
# compile options via generator expressions internally.
if(MSVC)
    set(HARTONOMOUS_OPENMP_FLAGS "/openmp:llvm")
else()
    find_package(OpenMP REQUIRED)
    set(HARTONOMOUS_OPENMP_FLAGS "")
endif()

include(FetchContent)

set(HARTONOMOUS_EXTERNAL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../external")

# BLAKE3 — prefer system install, fall back to FetchContent.
#
# Why not just FetchContent? BLAKE3 1.5.4's c/CMakeLists.txt branches on
# CMAKE_C_COMPILER_ID for SIMD-flag tables: GNU / Clang / AppleClang / MSVC
# only. The Intel icx (IntelLLVM) compiler falls through every branch and the
# configure step asserts. BLAKE3 is one hash, gains nothing from icx vs gcc,
# and the right answer is to build it once with gcc (in the Docker pgext
# layer) then link it here as a system library. icx remains the toolchain
# for the libhartonomous numerical kernels where it actually wins.
find_package(BLAKE3 CONFIG QUIET)
if(BLAKE3_FOUND)
    # find_package gives the namespaced target BLAKE3::blake3; the rest of this
    # file links against bare `blake3`. Alias so both branches expose the same
    # link target name.
    if(NOT TARGET blake3)
        add_library(blake3 ALIAS BLAKE3::blake3)
    endif()
else()
    FetchContent_Declare(
        blake3
        GIT_REPOSITORY https://github.com/BLAKE3-team/BLAKE3.git
        GIT_TAG        1.5.4
        SOURCE_SUBDIR  c
    )
    set(BLAKE3_TESTING OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    FetchContent_MakeAvailable(blake3)
endif()

# ── Eigen 3.4.0 (header-only linear algebra) ───────────────────
# Used for templated fixed-size matrix math, SVD (BDCSVD), Jacobi
# rotations, and as the backing vector type for Spectra. Prefer local
# clones/submodules so clean native builds do not block on network fetches.
set(HARTONOMOUS_EIGEN_SOURCE_DIR "" CACHE PATH "Local Eigen source directory")
if(NOT HARTONOMOUS_EIGEN_SOURCE_DIR AND DEFINED ENV{HARTONOMOUS_EIGEN_SOURCE_DIR})
    set(HARTONOMOUS_EIGEN_SOURCE_DIR "$ENV{HARTONOMOUS_EIGEN_SOURCE_DIR}")
endif()
if(NOT HARTONOMOUS_EIGEN_SOURCE_DIR AND EXISTS "${HARTONOMOUS_EXTERNAL_DIR}/eigen/Eigen/Core")
    set(HARTONOMOUS_EIGEN_SOURCE_DIR "${HARTONOMOUS_EXTERNAL_DIR}/eigen")
endif()
if(NOT HARTONOMOUS_EIGEN_SOURCE_DIR AND DEFINED ENV{HOME} AND EXISTS "$ENV{HOME}/Projects/eigen/Eigen/Core")
    set(HARTONOMOUS_EIGEN_SOURCE_DIR "$ENV{HOME}/Projects/eigen")
endif()
if(HARTONOMOUS_EIGEN_SOURCE_DIR AND EXISTS "${HARTONOMOUS_EIGEN_SOURCE_DIR}/Eigen/Core")
    add_library(eigen INTERFACE)
    target_include_directories(eigen SYSTEM INTERFACE "${HARTONOMOUS_EIGEN_SOURCE_DIR}")
else()
    FetchContent_Declare(
        eigen
        GIT_REPOSITORY https://gitlab.com/libeigen/eigen.git
        GIT_TAG        3.4.0
    )
    set(EIGEN_BUILD_DOC OFF CACHE BOOL "" FORCE)
    set(EIGEN_BUILD_TESTING OFF CACHE BOOL "" FORCE)
    set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
    set(EIGEN_BUILD_PKGCONFIG OFF CACHE BOOL "" FORCE)
    FetchContent_GetProperties(eigen)
    if(NOT eigen_POPULATED)
        FetchContent_Populate(eigen)
        add_library(eigen INTERFACE)
        target_include_directories(eigen SYSTEM INTERFACE ${eigen_SOURCE_DIR})
    endif()
endif()

# ── Spectra 1.0.1 (header-only large sparse eigensolver) ───────
# Lanczos / Arnoldi on top of Eigen. Used for exact top-k eigenvectors
# of large symmetric sparse matrices (normalized Laplacian of k-NN
# graphs for embedding fireflies — Phase 3.B). Determinism achieved by
# MKL CBWR + fixed random-vector seed.
set(HARTONOMOUS_SPECTRA_SOURCE_DIR "" CACHE PATH "Local Spectra source directory")
if(NOT HARTONOMOUS_SPECTRA_SOURCE_DIR AND DEFINED ENV{HARTONOMOUS_SPECTRA_SOURCE_DIR})
    set(HARTONOMOUS_SPECTRA_SOURCE_DIR "$ENV{HARTONOMOUS_SPECTRA_SOURCE_DIR}")
endif()
if(NOT HARTONOMOUS_SPECTRA_SOURCE_DIR AND EXISTS "${HARTONOMOUS_EXTERNAL_DIR}/spectra/include/Spectra/SymEigsSolver.h")
    set(HARTONOMOUS_SPECTRA_SOURCE_DIR "${HARTONOMOUS_EXTERNAL_DIR}/spectra")
endif()
if(HARTONOMOUS_SPECTRA_SOURCE_DIR AND EXISTS "${HARTONOMOUS_SPECTRA_SOURCE_DIR}/include/Spectra/SymEigsSolver.h")
    add_library(spectra INTERFACE)
    target_include_directories(spectra SYSTEM INTERFACE "${HARTONOMOUS_SPECTRA_SOURCE_DIR}/include")
    target_link_libraries(spectra INTERFACE eigen)
else()
    FetchContent_Declare(
        spectra
        GIT_REPOSITORY https://github.com/yixuan/spectra.git
        GIT_TAG        v1.0.1
    )
    set(SPECTRA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
    FetchContent_GetProperties(spectra)
    if(NOT spectra_POPULATED)
        FetchContent_Populate(spectra)
        add_library(spectra INTERFACE)
        target_include_directories(spectra SYSTEM INTERFACE ${spectra_SOURCE_DIR}/include)
        target_link_libraries(spectra INTERFACE eigen)
    endif()
endif()

set(HARTONOMOUS_SOURCES
    src/version.c
    src/runtime_info.c
    src/hash.c
    src/s3_geometry.c
    src/karcher_s3.c
    src/super_fibonacci.c
    src/hilbert.c
    src/tensor_decode.c
    src/gemm.c
    src/svd.c
    src/procrustes.c
    src/knn.c
    src/knearest_exact.c
    src/laplacian_eigenmap.cpp
    src/kmeans_plusplus.c
    src/delaunay_4d.cpp
    src/sparse_eigs.c
    src/gram_schmidt.c
    src/merkle.c
    src/distance.c
    src/centroid.c
    src/box4d.c
    src/trajectory.c
    src/glicko_bulk.c
    # ── Native text decomposition (was substrate.text_decompose round-trip) ──
    src/ucd_atoms_blob.c
    src/text_decompose.c
    # ── Phase A.0.4 synthesis primitive stubs (Phase B.1 fills them in) ──
    src/synth_stubs.c
)

set(HARTONOMOUS_HEADERS
    include/hartonomous.h
)

if(HARTONOMOUS_BUILD_SHARED)
    add_library(hartonomous SHARED ${HARTONOMOUS_SOURCES} ${HARTONOMOUS_HEADERS})
else()
    add_library(hartonomous STATIC ${HARTONOMOUS_SOURCES} ${HARTONOMOUS_HEADERS})
endif()

# Generated UCD 17.0.0 property tables consumed directly by text_decompose.c.
# libhartonomous must carry these on every platform: on Linux it is a dependency
# of the PostgreSQL extension, so its data relocations are resolved before the
# extension's own generated objects are available as providers. Leaving them as
# weak imports makes normalization/segmentation silently fall back to NULL-table
# behavior in SQL callers.
target_sources(hartonomous PRIVATE
     ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src/generated/pg_ucd_segmentation.c
     ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src/generated/pg_ucd_classification.c
     ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src/generated/pg_ucd_casing.c
     ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src/generated/pg_ucd_pictographic.c
     ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src/generated/pg_ucd_decomp.c
 )

target_include_directories(hartonomous
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        # Generated UCD headers (pg_unicode_version.h, pg_ucd_segmentation.h,
        # pg_ucd_classification.h, pg_ucd_pictographic.h, pg_ucd_atoms_blob.h)
        # currently live under the PG extension source tree; libhartonomous
        # consumes the same files. text_decompose.c #includes
        # "generated/pg_unicode_version.h" — the include path below resolves
        # that against ext/hartonomous_pg/src/.
        ${CMAKE_CURRENT_SOURCE_DIR}/../hartonomous_pg/src
)

target_link_libraries(hartonomous
    PRIVATE
        blake3
        eigen
        spectra
        MKL::MKL
)
if(NOT MSVC)
    # Both targets needed: OpenMP::OpenMP_C carries `-fiopenmp` for icx-compiled
    # *.c files; OpenMP::OpenMP_CXX carries `-fopenmp` for g++-compiled *.cpp
    # files (laplacian_eigenmap.cpp, delaunay_4d.cpp). Without the CXX target
    # the C++ compile fails with "unrecognized command-line option '-fiopenmp'".
    target_link_libraries(hartonomous PRIVATE OpenMP::OpenMP_C OpenMP::OpenMP_CXX)
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

# Statically embed libstdc++ and libgcc into libhartonomous.so on Linux so the
# .so is self-contained and runs on any distro with glibc >= the build host's.
# Required because the builder is Ubuntu 22.04 (gcc 11 libstdc++) while the
# postgis runtime image is Debian bullseye (gcc 10 libstdc++). Eliminates the
# GLIBCXX_3.4.29 / GLIBC_2.34 ABI mismatch at load time.
if(NOT MSVC)
    target_link_options(hartonomous PRIVATE
        -static-libstdc++
        -static-libgcc
    )
endif()

# Workaround: Intel icpx 2025.3.2 crashes (clang frontend SIGSEGV) inside its
# hir-ssa-deconstruction optimization pass when compiling
# src/laplacian_eigenmap.cpp at -O3 (the CMake Release default). Drop the
# optimization level for this single file so the build completes; the perf
# delta is irrelevant during ingestion (Lanczos eigensolve is dominated by
# Spectra/MKL calls, not host-side loop optimization).
# icpx 2025.3.2 SIGSEGV workaround now lives in docker/pgext.Dockerfile
# (CXX=g++ for libhartonomous). On the Windows / native builds the C++
# sources compile with MSVC and hit no crash, so no per-file override is
# needed here.

if(HARTONOMOUS_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
