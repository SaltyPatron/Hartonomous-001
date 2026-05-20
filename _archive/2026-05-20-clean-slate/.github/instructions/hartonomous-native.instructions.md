---
name: Hartonomous Native Rules
description: Native compute, interop, and determinism constraints for Hartonomous native code and build files.
applyTo: 'ext/**'
---

## Native library: `ext/libhartonomous/`

C/C++ library built with CMake. Provides BLAKE3 hashing and S3 geometric operations.

| Path | Content |
|------|---------|
| `ext/libhartonomous/src/` | Source files (C/C++) |
| `ext/libhartonomous/include/` | Public headers |
| `ext/libhartonomous/tests/` | Native test suite |
| `ext/libhartonomous/CMakeLists.txt` | Build configuration |
| `ext/libhartonomous/build.bat` | Windows build script |

Build: `scripts/build/Native.ps1`. Test: `scripts/test/Native.ps1`.

## PostgreSQL extension: `ext/hartonomous_pg/`

Custom PostGIS distance metrics for the S3 geometric substrate.

| Path | Content |
|------|---------|
| `ext/hartonomous_pg/src/` | C source |
| `ext/hartonomous_pg/sql/` | Extension SQL definitions |
| `ext/hartonomous_pg/test/` | pgTAP tests |

Build/install: `scripts/build/PgExtension.ps1`. Test: `scripts/test/Pg.ps1`.

## P/Invoke boundary

P/Invoke declarations live in `src/Hartonomous.Core/Native/` (`Blake3Native`, `S3Native`, `SuperFibonacciNative`, `HilbertNative`) and `src/Hartonomous.Core/Compute/Internal/NativeCompute.cs` (consolidated P/Invoke surface). Native DLL copy rules are centralized in `native-dll.targets` (imported by `Directory.Build.props`). Never copy-paste NativeLibrary or DllImport ItemGroups across csproj files.

## Compute facade

The C# facade at `src/Hartonomous.Core/Compute/` is the ONLY caller of the native library. No other project references MKL, Eigen, Spectra, or any compute dependency directly. The call chain is: decomposer/engine → `IComputeFacade` → `ComputeFacade` → `NativeCompute` (P/Invoke) → `libhartonomous`.

## Determinism requirements

Every ingestion-time computation must be bitwise-reproducible across repeated runs on the same input (Law #6).

### Prohibited
- HNSW, pgvector ANN, random projection, LSH, Nyström, randomized SVD, stochastic trace estimation
- Quantization or normalization of content values
- Any approximation method for content operations

### Required
- **MKL `CBWR=AUTO,STRICT`** enforced at process start — identical reduction order within ISA class
- **Fixed PRNG seeds** for all seeded procedures (Lanczos starting vectors, Super-Fibonacci offsets)
- Seeds declared on decomposer config or spec-defined

### Sparsity rules
Sparsity is honest recording, not approximation. For text/audio/image/video: content bytes ARE content and are preserved. For AI models: weight *patterns* are content; gradient jitter (per Lottery Ticket Hypothesis) is not stored.

## BLAKE3

BLAKE3 is the only hash function. All content hashing goes through `Blake3Native.Blake3()`. Entity hashes are computed over content only — never over position, ordinal, filename, tensor-name, line number, or source offset. Placement metadata lives on edges (`has_source`, `in_model`, sequence position), `provenance`, or the `sequence` table.
