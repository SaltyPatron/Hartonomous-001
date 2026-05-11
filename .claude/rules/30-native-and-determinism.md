---
description: Native compute, interop, and determinism rules for Hartonomous.
paths:
  - ext/**
  - src/**/Native/**
  - src/**/Compute/**
  - Directory.Build.props
  - native-dll.targets
---

## Compute facade boundary

All numerical compute goes through `Hartonomous.Core.Compute.*`. The facade hierarchy:

```
IComputeFacade (src/Hartonomous.Core/Compute/IComputeFacade.cs)
├── IIngestionCompute — SVD, Lanczos eigensolve, sparse matvec, k-NN construction
├── IInferenceCompute — S3 distance, Fréchet distance extensions, Voronoi cell ops
└── ICommonCompute    — BLAKE3, Super-Fibonacci S3 projection, Hilbert index,
                        Gram-Schmidt, orthonormalization, deterministic top-k
```

Default implementation: `ComputeFacade` (singleton via `ComputeFacade.Instance`). No other project references MKL, Eigen, Spectra, or any transitive native binding directly. If a primitive doesn't exist in the facade yet, add it there — don't bypass.

## Native library: `ext/libhartonomous/`

- C/C++ flat API compiled via CMake (`CMakeLists.txt`)
- Build: `scripts/build/Native.ps1` or `ext/libhartonomous/build.bat`
- P/Invoke declarations live in `src/Hartonomous.Core/Native/`:
  - `Blake3Native.cs`: `hartonomous_blake3()` and incremental hasher (`Blake3Init`, `Blake3Update`, `Blake3Finalize`)
  - `NativeCompute.cs` (in `Compute/Internal/`): all native bindings routed through here
- DLL copy rules: centralized in `native-dll.targets` (imported by `Directory.Build.props`). Never copy-paste native ItemGroups across csproj files.

## PostgreSQL extension: `ext/hartonomous_pg/`

- C extension for S3-specific distance metrics beyond what PostGIS provides natively
- Build: `scripts/build/PgExtension.ps1` or `ext/hartonomous_pg/Makefile`
- Install: `scripts/db/InstallExtension.ps1`
- Tests: `scripts/test/Pg.ps1`

## BLAKE3 is the only hash function

All content hashing goes through `Hartonomous.Core.Compute.Common.Blake3` → `Blake3Native.Blake3()`. Do not add alternate hash functions for substrate identity. The streaming hasher `Blake3Hasher` enables multi-GB tensor hashing without full-content buffers.

## Determinism requirements (Law #6)

Same input + same decomposer version = same substrate state, byte for byte. Enforced by:

- **No approximation methods**: no HNSW, no pgvector ANN, no random projection, no LSH, no Nyström, no randomized SVD, no stochastic trace estimation
- **No quantization of content values**: BF16 → F32 → F64 decoded losslessly, never compressed
- **MKL `CBWR=AUTO,STRICT`**: enforced at process start for identical reduction order
- **All PRNG takes fixed seeds**: Lanczos starting vectors, Super-Fibonacci offsets — seeds declared on decomposer config or spec-defined
- **Sparsity is not approximation**: relationships that don't exist are not stored; gradient jitter (no knowledge per Lottery Ticket) is not stored. Content bytes and weight patterns ARE preserved.

## Identity versus placement

Entity hashes cover content only — never position, ordinal, filename, tensor-name, line number, or source offset. Placement lives on edges (`has_source`, `in_model`), the `sequence` table (with `position` and `count`), or `provenance`. Same content in two places = one entity with two edges, not two entities.

## Native execution on Windows

Do not invoke raw `.exe` paths from the terminal (e.g. `./bin/Release/hartonomous_tests.exe`). On this workstation a raw executable invocation trips a permission prompt. Route native tests through `ctest -C Release --output-on-failure` from the CMake build directory; route managed tests through `dotnet test`. If a raw executable invocation is genuinely the only path, pause and ask first — never spring a prompt.

## Toolchain discovery on Windows

Do not stop at `which cmake` / `which ninja` / `which cl`. Visual Studio installs ship their own copies that are not on `PATH` by default. Always also probe:

- `vswhere.exe` at `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` to enumerate VS installations.
- Per VS install: `<vs>/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe`, `<vs>/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe`, `<vs>/MSBuild/Current/Bin/MSBuild.exe`.
- C/C++ compilers via `vcvarsall.bat` / `VsDevCmd.bat` at `<vs>/VC/Auxiliary/Build/` or `<vs>/Common7/Tools/`.

Preferred toolchain for the native build: Visual Studio 2026 MSVC 14.50 (generator `Visual Studio 18 2026`), falling back to Visual Studio 2022 Community — never VS 2022 BuildTools — before declaring a tool unavailable.
