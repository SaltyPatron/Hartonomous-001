#
# hartonomous_ucd_embedded — substrate-compatible UCD/UCA atom lookup
# library, no PostgreSQL deps. Suitable for embedded targets with
# limited ROM (~3 MB minimum, ~12 MB full).
#
# Build profiles:
#
#   cmake -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build
#
# Options:
#   HUCD_BUILD_REFERENCE_CLI    Build the huc CLI (default ON)
#   HUCD_BUILD_PARITY_TEST      Build the parity test binary (default OFF)
#   HUCD_TIER                   BOTH | TIER1_ONLY  (default BOTH)
#   HUCD_DECOMPRESS             NONE | ZSTD | LZ4  (default NONE)
#   HUCD_INCLUDE_NAMES          ON | OFF (default ON; saves 1 MB if OFF)
#   HUCD_INCLUDE_DECOMP         ON | OFF (default ON; saves 50 KB)
#   HUCD_INCLUDE_UCA            ON | OFF (default ON; saves 600 KB)
#
cmake_minimum_required(VERSION 3.20)
project(hartonomous_ucd_embedded
    VERSION 17.0.0
    LANGUAGES C
    DESCRIPTION "Substrate-compatible UCD/UCA atom lookup, no PG deps")

option(HUCD_BUILD_REFERENCE_CLI "Build the huc reference CLI" ON)
option(HUCD_BUILD_PARITY_TEST   "Build the parity test driver" OFF)
set(HUCD_TIER "BOTH" CACHE STRING "Tier set: BOTH or TIER1_ONLY")
set(HUCD_DECOMPRESS "NONE" CACHE STRING "Decompressor: NONE, ZSTD, or LZ4")
option(HUCD_INCLUDE_NAMES  "Bundle codepoint names" ON)
option(HUCD_INCLUDE_DECOMP "Bundle NFD/NFC decomposition" ON)
option(HUCD_INCLUDE_UCA    "Bundle UCA collation weights" ON)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Position-independent: linkable into shared libs / used as static lib.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

add_library(hartonomous_ucd_embedded STATIC
    src/huc_loader.c)
target_include_directories(hartonomous_ucd_embedded
    PUBLIC  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

# Compile-time feature flags propagated to the loader.
target_compile_definitions(hartonomous_ucd_embedded PRIVATE
    HUCD_TIER_$<UPPER_CASE:${HUCD_TIER}>
    HUCD_DECOMPRESS_$<UPPER_CASE:${HUCD_DECOMPRESS}>
    $<$<BOOL:${HUCD_INCLUDE_NAMES}>:HUCD_HAS_NAMES>
    $<$<BOOL:${HUCD_INCLUDE_DECOMP}>:HUCD_HAS_DECOMP>
    $<$<BOOL:${HUCD_INCLUDE_UCA}>:HUCD_HAS_UCA>)

# Optional decompressors. The library does NOT vendor zstd/lz4 — caller
# provides via find_package or precompiled link path. NONE = no compression
# support; bin files must already be raw.
if(HUCD_DECOMPRESS STREQUAL "ZSTD")
    find_package(zstd REQUIRED)
    target_link_libraries(hartonomous_ucd_embedded PUBLIC zstd::libzstd_static)
elseif(HUCD_DECOMPRESS STREQUAL "LZ4")
    find_package(lz4 REQUIRED)
    target_link_libraries(hartonomous_ucd_embedded PUBLIC lz4::lz4)
endif()

if(HUCD_BUILD_REFERENCE_CLI)
    add_executable(huc reference/huc_cli.c)
    target_link_libraries(huc PRIVATE hartonomous_ucd_embedded)
endif()

if(HUCD_BUILD_PARITY_TEST)
    add_executable(huc_parity test/parity_test.c)
    target_link_libraries(huc_parity PRIVATE hartonomous_ucd_embedded)
endif()

# Install rules
include(GNUInstallDirs)
install(TARGETS hartonomous_ucd_embedded
    EXPORT hartonomous_ucd_embedded-targets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR})
install(FILES include/hartonomous_ucd.h
    DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
