# Native Build System

**Status**: 🔶 Partially superseded by `compute-library.md`

This document is the stable, shared-scaffolding half of the native build:
PGXS extension, CMake layout, CTest wiring, platform matrix, packaging. It
pre-dates the two-artifact split (`hartonomous_ingest` + `hartonomous_query`).
For anything touching the ingest artifact — MKL/TBB/TCM/oneDPL link order,
Intel icx compiler selection, determinism flags, CBWR/ISA enforcement, and
the oneAPI component inventory — **`compute-library.md` is authoritative**.

Differences at a glance:

| Area | This doc says | Authoritative source |
|---|---|---|
| Number of native artifacts | 1 (`libhartonomous`) | **Two** — `hartonomous_ingest` (MKL ILP64 + TBB) and `hartonomous_query` (MKL-free). See `compute-library.md` § *Two-artifact split*. |
| SIMD ceiling | AVX-512/AVX2/SSE4.1 | AVX-512 **removed** — the deployment target (14900KS) has AVX-512 fused off at microcode. Ceiling is AVX2 + FMA3 + AVX-VNNI + BMI2. See `compute-library.md` § *ISA dispatch*. |
| C/C++ dependencies | BLAKE3 only | Ingest artifact additionally depends on MKL 2025.3+, TBB, TCM (`tbbmalloc`), oneDPL, Eigen, Spectra. Query artifact still BLAKE3 only. See `compute-library.md` § *Intel oneAPI component inventory*. |
| Primary compiler | MSVC 17 2022 / GCC / Clang | Ingest artifact preferred compiler is Intel **icx**; query artifact stays on MSVC/GCC/Clang. See `compute-library.md` § *Intel toolchain*. |
| Generator | Visual Studio 17 2022 | Visual Studio 18 2026 is the current dev toolchain; VS 17 2022 is still a supported fallback. |

How the C/C++ components are built, tested, and packaged for both PostgreSQL and .NET consumption.

---

## Build Tools

| Component | Build System | Rationale |
|-----------|-------------|-----------|
| Shared library (libhartonomous) | CMake | Cross-platform, handles SIMD flag management, integrates with Google Test |
| PG extension (hartonomous.so) | PGXS (Makefile) | PostgreSQL's standard extension build system. Knows where to install. |
| Tests | CMake + CTest | Google Test discovered and run via CTest |

Two separate build systems. The shared library is built with CMake. The PG extension Makefile compiles the same source files (via include) using PGXS and links them into a PostgreSQL loadable module.

---

## CMake Configuration

### `ext/native/CMakeLists.txt`

```cmake
cmake_minimum_required(VERSION 3.25)
project(hartonomous VERSION 1.0 LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# ── Shared library ──────────────────────────────────

add_library(hartonomous SHARED
    src/blake3/blake3.c
    src/blake3/blake3_portable.c
    src/s3_geometry.c
    src/super_fibonacci.c
    src/hilbert.c
    src/simd_dispatch.c
    src/merkle.c
)

target_include_directories(hartonomous PUBLIC include)
target_compile_definitions(hartonomous PRIVATE HTNS_BUILD_DLL)

# SIMD source files (conditional on platform)
if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")
    target_sources(hartonomous PRIVATE
        src/blake3/blake3_sse41.c
        src/blake3/blake3_avx2.c
        src/blake3/blake3_avx512.c
    )
    # Per-file compile flags for SIMD
    set_source_files_properties(src/blake3/blake3_sse41.c
        PROPERTIES COMPILE_FLAGS "-msse4.1")
    set_source_files_properties(src/blake3/blake3_avx2.c
        PROPERTIES COMPILE_FLAGS "-mavx2")
    set_source_files_properties(src/blake3/blake3_avx512.c
        PROPERTIES COMPILE_FLAGS "-mavx512f -mavx512vl")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|ARM64")
    target_sources(hartonomous PRIVATE
        src/blake3/blake3_neon.c
    )
endif()

# Optimization
target_compile_options(hartonomous PRIVATE
    $<$<C_COMPILER_ID:GNU,Clang>:-O3 -Wall -Wextra -Werror>
    $<$<C_COMPILER_ID:MSVC>:/O2 /W4 /WX>
)

# ── Tests ───────────────────────────────────────────

enable_testing()
find_package(GTest REQUIRED)

add_executable(hartonomous_tests
    tests/test_blake3.cpp
    tests/test_s3.cpp
    tests/test_hilbert.cpp
    tests/test_super_fibonacci.cpp
    tests/test_simd.cpp
)

target_link_libraries(hartonomous_tests
    PRIVATE hartonomous GTest::gtest GTest::gtest_main)
target_include_directories(hartonomous_tests PRIVATE include)

gtest_discover_tests(hartonomous_tests)
```

### MSVC (Windows) SIMD Flags

MSVC does not require `-mavx2` flags — it uses intrinsic headers directly. The `COMPILE_FLAGS` properties are `$<$<NOT:$<C_COMPILER_ID:MSVC>>:...>` guarded:

```cmake
if(NOT MSVC)
    set_source_files_properties(src/blake3/blake3_avx2.c
        PROPERTIES COMPILE_FLAGS "-mavx2")
    # ...
endif()
```

---

## PGXS Build

### `ext/pg/Makefile`

```makefile
MODULE_big = hartonomous
OBJS = hartonomous.o pg_blake3.o pg_traversal.o pg_geometry.o \
       ../native/src/blake3/blake3.o \
       ../native/src/blake3/blake3_portable.o \
       ../native/src/blake3/blake3_sse41.o \
       ../native/src/blake3/blake3_avx2.o \
       ../native/src/blake3/blake3_avx512.o \
       ../native/src/s3_geometry.o \
       ../native/src/super_fibonacci.o \
       ../native/src/hilbert.o \
       ../native/src/simd_dispatch.o \
       ../native/src/merkle.o

EXTENSION = hartonomous
DATA = hartonomous--1.0.sql
REGRESS = extension_load blake3_hash s3_distance s3_centroid neighbors

PG_CFLAGS = -I../native/include -O3

PG_CONFIG ?= pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)

# SIMD flags for specific object files
../native/src/blake3/blake3_sse41.o: PG_CFLAGS += -msse4.1
../native/src/blake3/blake3_avx2.o: PG_CFLAGS += -mavx2
../native/src/blake3/blake3_avx512.o: PG_CFLAGS += -mavx512f -mavx512vl
```

### Windows (MSVC)

`Makefile.win` using `nmake`:

```makefile
PG_CONFIG = "C:\Program Files\PostgreSQL\17\bin\pg_config.exe"
INCLUDEDIR = $(shell $(PG_CONFIG) --includedir-server)
LIBDIR = $(shell $(PG_CONFIG) --pkglibdir)
SHAREDIR = $(shell $(PG_CONFIG) --sharedir)

# ... MSVC compile commands with /O2 ...
```

---

## Platform Matrix

| Platform | Architecture | SIMD ceiling | Status |
|----------|-------------|------|--------|
| Windows x64 | AMD64 | AVX2 + FMA3 + AVX-VNNI + BMI2 | Primary development (14900KS, AVX-512 fused off at microcode) |
| Linux x86_64 | AMD64 | AVX2 + FMA3 + AVX-VNNI + BMI2 | Primary deployment |
| macOS ARM64 | AArch64 | NEON | Development (if needed) — scalar fallbacks acceptable |

No cross-compilation. Each platform builds natively on that platform.

---

## Build Targets

| Target | Output | Consumer |
|--------|--------|----------|
| `hartonomous_ingest.dll` / `.so` | Ingest artifact — MKL ILP64 + TBB + TCM + Eigen + Spectra + oneDPL + BLAKE3; all ingestion-time compute | `Hartonomous.Core.Compute.Ingestion.*` via P/Invoke |
| `hartonomous_query.dll` / `.so` | Query artifact — scalar math + SIMD intrinsics only; no MKL, no TBB | `Hartonomous.Core.Compute.Inference.*` via P/Invoke; statically linked into PG extension |
| `hartonomous.so` / `.dll` (PG) | PostgreSQL loadable module — statically links the query artifact's objects | PostgreSQL `LOAD` / `CREATE EXTENSION` |
| `hartonomous_tests` | Test executable | CTest / developer |

---

## Dependencies

**Query artifact** (`hartonomous_query`) — no external deps beyond the system:

| Dependency | How Obtained | Version |
|-----------|-------------|---------|
| BLAKE3 C reference | Vendored in `src/blake3/` | Pinned commit from BLAKE3 repo |
| PostgreSQL server headers | System package (`postgresql-server-dev-18`) or installer | 18+ |
| PostGIS headers | System package or source build | 3.5+ |
| Google Test | System package or `FetchContent` in CMake | 1.14+ |
| CMake | System install | 3.25+ |

**Ingest artifact** (`hartonomous_ingest`) — Intel oneAPI stack is load-bearing:

| Dependency | How Obtained | Version |
|-----------|-------------|---------|
| Intel MKL (ILP64 + TBB threading layer) | Intel oneAPI Base Toolkit — `C:\Program Files (x86)\Intel\oneAPI\mkl\latest\` on Windows; `/opt/intel/oneapi/mkl/latest/` on Linux | 2025.3+ |
| Intel TBB | Intel oneAPI Base Toolkit | Matches MKL release |
| Intel TCM (`tbbmalloc`) | Intel oneAPI Base Toolkit | Matches MKL release |
| oneDPL | Intel oneAPI Base Toolkit (header-only) | Matches MKL release |
| Intel icx compiler | Intel oneAPI HPC Toolkit (preferred) | Matches oneAPI release |
| Eigen | Vendored header-only | 3.4+ |
| Spectra | Vendored header-only | 1.0+ |

The ingest artifact's CMake configuration, link line, and determinism flags are specified in `compute-library.md` § *Build configuration* and § *MKL linkage — TBB threading layer*. CI must verify the cross-artifact audit from `compute-library.md` § *Test matrix* — the query artifact must contain zero MKL / TBB / Eigen / Spectra symbols.

The `hartonomous_query` artifact and the PG extension deliberately do NOT depend on any Intel oneAPI component — they must install on a plain system without Intel redistributables. This is a hard constraint, both for PG backend safety and for the decentralized-substrate roadmap (a contributor's laptop running the substrate for usage credits must not need Intel MKL installed).

---

## Build Commands

### Shared Library

```bash
# Linux / macOS
cd ext/native
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
ctest --test-dir build

# Windows (MSVC)
cd ext\native
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
ctest --test-dir build -C Release
```

### PG Extension

```bash
# Linux
cd ext/pg
make PG_CONFIG=/usr/bin/pg_config
make install
make installcheck   # runs pg_regress tests

# Windows
cd ext\pg
nmake /f Makefile.win
nmake /f Makefile.win install
```

### Full Build (all components)

```bash
# 1. Native library
cd ext/native && cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build

# 2. Copy to .NET runtime directory
cp build/libhartonomous.so ../runtimes/linux-x64/native/

# 3. PG extension
cd ../pg && make && make install

# 4. .NET solution
cd ../../ && dotnet build Hartonomous.sln

# 5. Run all tests
cd ext/native && ctest --test-dir build
cd ext/pg && make installcheck
cd ../../ && dotnet test Hartonomous.Tests
```

---

## Packaging

### NuGet Runtime Package

The shared library is placed in the .NET publish output via RID-specific directories:

```
runtimes/
  win-x64/
    native/
      hartonomous.dll
  linux-x64/
    native/
      libhartonomous.so
  osx-arm64/
    native/
      libhartonomous.dylib
```

Included in the `.csproj` via:

```xml
<ItemGroup>
  <None Include="runtimes/**/*" Pack="true" PackagePath="runtimes" />
</ItemGroup>
```

### PG Extension Package

Standard PGXS `make install` places files in:
- `$(pg_config --pkglibdir)/hartonomous.so`
- `$(pg_config --sharedir)/extension/hartonomous.control`
- `$(pg_config --sharedir)/extension/hartonomous--1.0.sql`

---

## Version Coordination

| Component | Version Source | Must Match |
|-----------|---------------|-----------|
| Shared library | `HTNS_VERSION_MAJOR.HTNS_VERSION_MINOR` in `hartonomous.h` | ≥ PG extension's expected version |
| PG extension | `default_version` in `hartonomous.control` | Uses shared library API |
| .NET P/Invoke | `NativeMethods` signatures | Must match `hartonomous.h` declarations exactly |

All three are in the same repository. Version changes are coordinated in a single commit. No separate package versioning.
