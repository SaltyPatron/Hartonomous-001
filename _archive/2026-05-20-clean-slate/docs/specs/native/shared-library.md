# Shared Native Library

**Status**: ✅ Complete

C shared library (`libhartonomous.so` / `hartonomous.dll`) containing code shared between the PostgreSQL extension and the C# application layer. Single implementation, two consumers.

---

## Architecture

```
┌─────────────────────┐    ┌─────────────────────┐
│   PG Extension      │    │   C# (.NET 9.0)     │
│   (hartonomous.so)  │    │   (P/Invoke)        │
│                     │    │                     │
│  #include           │    │  [DllImport]        │
│  "hartonomous.h"    │    │  NativeMethods.cs   │
└────────┬────────────┘    └────────┬────────────┘
         │                          │
         │    static link           │    dynamic link
         │    (compile-time)        │    (runtime)
         ▼                          ▼
    ┌─────────────────────────────────────┐
    │        libhartonomous               │
    │                                     │
    │  blake3.c    s3_geometry.c          │
    │  hilbert.c   super_fibonacci.c      │
    │  simd_dispatch.c                    │
    └─────────────────────────────────────┘
```

**PG Extension**: Statically links libhartonomous object files into `hartonomous.so`. No dynamic library dependency at PostgreSQL runtime.

**C# Application**: Dynamically loads `libhartonomous.dll`/`.so` via P/Invoke at runtime from `runtimes/{rid}/native/`.

---

## Public API

### Header: `hartonomous.h`

```c
#ifndef HARTONOMOUS_H
#define HARTONOMOUS_H

#include <stdint.h>
#include <stddef.h>

#define HTNS_VERSION_MAJOR 1
#define HTNS_VERSION_MINOR 0

#ifdef _WIN32
  #ifdef HTNS_BUILD_DLL
    #define HTNS_API __declspec(dllexport)
  #else
    #define HTNS_API __declspec(dllimport)
  #endif
#else
  #define HTNS_API __attribute__((visibility("default")))
#endif

/* Error codes */
typedef enum {
    HTNS_OK           = 0,
    HTNS_ERR_NULL     = -1,   /* NULL pointer argument */
    HTNS_ERR_SIZE     = -2,   /* Invalid size argument */
    HTNS_ERR_OVERFLOW = -3    /* Buffer overflow */
} htns_error;

/* ── BLAKE3 ─────────────────────────────────────────── */

/* One-shot hash: input → 32-byte output */
HTNS_API htns_error htns_blake3_hash(
    const uint8_t* input, size_t input_len,
    uint8_t output[32]);

/* Streaming hash for large inputs */
typedef struct htns_blake3_hasher htns_blake3_hasher;

HTNS_API htns_error htns_blake3_hasher_init(htns_blake3_hasher** out);
HTNS_API htns_error htns_blake3_hasher_update(
    htns_blake3_hasher* hasher,
    const uint8_t* input, size_t input_len);
HTNS_API htns_error htns_blake3_hasher_finalize(
    htns_blake3_hasher* hasher,
    uint8_t output[32]);
HTNS_API void htns_blake3_hasher_free(htns_blake3_hasher* hasher);

/* Merkle hash: ordered array of child hashes → parent hash */
HTNS_API htns_error htns_blake3_merkle(
    const uint8_t* child_hashes, size_t child_count,
    uint8_t output[32]);

/* ── S3 Geometry ────────────────────────────────────── */

/* Geodesic distance between two 4D points on S3 */
HTNS_API double htns_s3_distance(
    const double p1[4], const double p2[4]);

/* Centroid of N points on S3 (vector mean → renormalize) */
HTNS_API htns_error htns_s3_centroid(
    const double* points, size_t point_count,
    double result[4]);

/* ── Super-Fibonacci ────────────────────────────────── */

/* Project parameter vector to S3 via Super-Fibonacci lattice */
HTNS_API htns_error htns_super_fibonacci(
    const double* params, size_t ndims,
    double result[4]);

/* ── Hilbert Curve ──────────────────────────────────── */

/* Compute Hilbert curve index for 4D point */
HTNS_API uint64_t htns_hilbert_index(
    const double point[4], int order);

/* Inverse: Hilbert index → 4D point */
HTNS_API htns_error htns_hilbert_inverse(
    uint64_t index, int order,
    double result[4]);

#endif /* HARTONOMOUS_H */
```

---

## Memory Contract

**Rule**: The shared library NEVER allocates memory that the caller must free (with one exception: the streaming hasher).

| Function | Allocation | Ownership |
|----------|-----------|-----------|
| `htns_blake3_hash` | None. Writes to caller-provided `output[32]`. | Caller owns output buffer. |
| `htns_blake3_hasher_init` | `malloc` for hasher struct. | Library owns. Caller frees via `htns_blake3_hasher_free`. |
| `htns_blake3_hasher_update` | None. | N/A |
| `htns_blake3_hasher_finalize` | None. Writes to caller-provided buffer. Frees hasher. | Caller owns output buffer. Hasher freed by library. |
| `htns_s3_distance` | None. Returns scalar on stack. | N/A |
| `htns_s3_centroid` | None. Writes to caller-provided `result[4]`. | Caller owns. |
| `htns_super_fibonacci` | None. Writes to caller-provided `result[4]`. | Caller owns. |
| `htns_hilbert_index` | None. Returns scalar on stack. | N/A |

**PG Extension context**: The PG extension uses `palloc` for its own buffers, passes them to libhartonomous functions. libhartonomous writes results into those `palloc'd` buffers. No `malloc`/`free` crossing boundaries.

**C# context**: P/Invoke marshals `byte[]` or `Span<byte>` as pinned pointers. libhartonomous writes into the pinned managed memory. No unmanaged allocation to free.

---

## SIMD Dispatch

Runtime CPU feature detection at first function call:

```c
// simd_dispatch.c
typedef void (*blake3_compress_fn)(/* ... */);
static blake3_compress_fn g_blake3_compress = NULL;

static void detect_cpu_features(void)
{
    if (cpu_supports_avx512())
        g_blake3_compress = blake3_compress_avx512;
    else if (cpu_supports_avx2())
        g_blake3_compress = blake3_compress_avx2;
    else if (cpu_supports_sse41())
        g_blake3_compress = blake3_compress_sse41;
    else
        g_blake3_compress = blake3_compress_portable;
}
```

| ISA | Functions Accelerated |
|-----|----------------------|
| AVX-512 | BLAKE3 compress, S3 distance (4-wide double) |
| AVX2 | BLAKE3 compress, S3 distance |
| SSE4.1 | BLAKE3 compress |
| NEON (ARM) | BLAKE3 compress (for macOS dev machines) |
| Portable C | All functions (fallback) |

CPU detection uses `__cpuid` (MSVC) / `__get_cpuid` (GCC/Clang). One-time detection, function pointer cached for process lifetime.

---

## P/Invoke Surface (C#)

```csharp
internal static partial class NativeMethods
{
    private const string LibName = "hartonomous";

    [LibraryImport(LibName, EntryPoint = "htns_blake3_hash")]
    internal static partial int Blake3Hash(
        ReadOnlySpan<byte> input, nuint inputLen,
        Span<byte> output);

    [LibraryImport(LibName, EntryPoint = "htns_blake3_merkle")]
    internal static partial int Blake3Merkle(
        ReadOnlySpan<byte> childHashes, nuint childCount,
        Span<byte> output);

    [LibraryImport(LibName, EntryPoint = "htns_s3_distance")]
    internal static partial double S3Distance(
        ReadOnlySpan<double> p1, ReadOnlySpan<double> p2);

    [LibraryImport(LibName, EntryPoint = "htns_s3_centroid")]
    internal static partial int S3Centroid(
        ReadOnlySpan<double> points, nuint pointCount,
        Span<double> result);

    [LibraryImport(LibName, EntryPoint = "htns_super_fibonacci")]
    internal static partial int SuperFibonacci(
        ReadOnlySpan<double> parameters, nuint ndims,
        Span<double> result);

    [LibraryImport(LibName, EntryPoint = "htns_hilbert_index")]
    internal static partial ulong HilbertIndex(
        ReadOnlySpan<double> point, int order);
}
```

**Source generation**: Uses `[LibraryImport]` (not `[DllImport]`) for compile-time marshaling code generation. No runtime reflection.

**Library resolution**: .NET resolves `hartonomous` to `runtimes/win-x64/native/hartonomous.dll` (or linux-x64/libhartonomous.so) automatically via RID-specific runtime directory. No custom `NativeLibrary.SetDllImportResolver` needed.

---

## Source Structure

```
ext/native/
  include/
    hartonomous.h           ← public header
  src/
    blake3/
      blake3.c              ← BLAKE3 reference implementation (vendored)
      blake3_avx512.c       ← AVX-512 intrinsics
      blake3_avx2.c         ← AVX2 intrinsics
      blake3_sse41.c        ← SSE4.1 intrinsics
      blake3_neon.c         ← ARM NEON intrinsics
      blake3_portable.c     ← Portable C fallback
    s3_geometry.c           ← S3 distance, centroid
    super_fibonacci.c       ← Super-Fibonacci lattice projection
    hilbert.c               ← Hilbert curve encoding/decoding
    simd_dispatch.c         ← CPU feature detection + function pointer dispatch
    merkle.c                ← Merkle hash (ordered child array → BLAKE3)
  tests/
    test_blake3.cpp         ← Google Test
    test_s3.cpp
    test_hilbert.cpp
    test_super_fibonacci.cpp
    test_simd.cpp
    vectors/
      blake3_test_vectors.json
  CMakeLists.txt
```

BLAKE3 source files are vendored from https://github.com/BLAKE3-team/BLAKE3/tree/master/c — committed directly, not a submodule. Pinned to a specific release commit.
