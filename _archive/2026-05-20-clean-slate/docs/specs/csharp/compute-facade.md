# Compute Facade

**Status**: 🔜 M3-full (ships alongside the native compute library, task #34)

Single C# surface over the native compute library. Every numerical primitive the rest of the codebase needs goes through this facade. No project outside `Hartonomous.Core.Compute.*` P/Invokes the native library or references MKL / Eigen / Spectra / ONNX Runtime / any other numerical binding.

This is the direct implementation of CLAUDE.md § *Compute Facade* and § *Determinism & Exact Math*. If a primitive doesn't exist in the facade yet, add it to the facade — don't bypass.

---

## Layout

```
src/Hartonomous.Core/
  Compute/
    Common/              ← used by both ingest and inference
      Blake3.cs
      Merkle.cs
      SuperFibonacci.cs
      S3Geometry.cs
      Hilbert.cs
      GramSchmidt.cs
      TopKStable.cs
    Ingestion/           ← used only during decomposition
      TensorDecode.cs
      Gemm.cs
      Svd.cs
      Csr.cs
      CsrMatVec.cs
      SparseSymEigs.cs
      KnnCosineGraph.cs
    Inference/           ← used only during query traversal
      S3Distance.cs      ← re-exports Common.S3Geometry for discoverability
      VoronoiCell.cs
      FrechetDistance.cs
    Internal/            ← NOT public API — internal P/Invoke + init plumbing
      NativeIngest.cs
      NativeQuery.cs
      NativeInit.cs
      NativeError.cs
      Htns.cs            ← shared error-code translation + init gate
```

`Hartonomous.Core.Compute.Internal.*` is `internal` visibility. All other classes are `public sealed static`. No instance state. No locking. Callers pass `Span<T>` or `ReadOnlySpan<T>` for buffers; the facade pins them across the P/Invoke boundary.

---

## Process init contract

Every process that uses the facade calls `Htns.EnsureInitialized()` at startup. The CLI, the API host, and the test harness do this in their DI composition roots. Subsequent calls are a no-op — `EnsureInitialized` is idempotent and thread-safe.

```csharp
namespace Hartonomous.Core.Compute.Internal;

public static class Htns
{
    /// <summary>
    /// Invokes htns_init, verifies the determinism contract (MKL_CBWR=AUTO,STRICT,
    /// ISA ceiling, thread count, DAZ/FTZ off). Throws ComputeInitException if the
    /// contract cannot be satisfied on this host.
    /// Idempotent. Thread-safe via LazyInitializer.
    /// </summary>
    public static void EnsureInitialized();

    /// <summary>
    /// Build info from the loaded native artifact: version, MKL interface, BLAKE3
    /// commit, linked Eigen/Spectra versions. Asserted equal to expected at init.
    /// </summary>
    public static BuildInfo Build { get; }
}

public sealed record BuildInfo(
    string LibraryVersion,
    string MklVersion,
    string EigenVersion,
    string SpectraVersion,
    string Blake3Commit,
    string IsaCeiling);           // e.g. "AVX2+FMA3+AVX-VNNI+BMI2"
```

`ComputeInitException` is thrown for: missing native artifact, MKL CBWR rejection, ISA floor not met, build-info mismatch. The process does not fall back — it fails loud (CLAUDE.md § *Error Handling*).

---

## Common primitives

Used by both ingestion and inference code paths. Stateless.

```csharp
namespace Hartonomous.Core.Compute.Common;

public static class Blake3
{
    /// <summary>32-byte BLAKE3 hash. Output buffer must be exactly 32 bytes.</summary>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output32);

    /// <summary>Compute a 32-byte digest and return it as a new byte[].</summary>
    public static byte[] Hash(ReadOnlySpan<byte> input);

    /// <summary>Streaming hasher for inputs that don't fit in memory at once.</summary>
    public static Blake3Hasher NewHasher();
}

public sealed class Blake3Hasher : IDisposable
{
    public void Update(ReadOnlySpan<byte> input);
    public void Finalize(Span<byte> output32);
    public void Dispose();
}

public static class Merkle
{
    /// <summary>
    /// Merkle roll-up of an ordered array of 32-byte child hashes. Order is part of
    /// the content. Caller ensures canonical ordering (e.g. by child entity hash).
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> childHashes32, Span<byte> output32);
}

public static class SuperFibonacci
{
    /// <summary>
    /// Project a parameter vector onto S³ (4D unit sphere) via the Super-Fibonacci
    /// lattice. Returns a 4-double unit quaternion. Deterministic: identical
    /// parameters → identical output bit-for-bit.
    /// </summary>
    public static void Project(ReadOnlySpan<double> parameters, Span<double> result4);
}

public static class S3Geometry
{
    /// <summary>Geodesic distance between two 4D points on S³.</summary>
    public static double Distance(ReadOnlySpan<double> p4, ReadOnlySpan<double> q4);

    /// <summary>
    /// S³ centroid of N 4D points — vector mean followed by renormalization.
    /// Deterministic summation order.
    /// </summary>
    public static void Centroid(ReadOnlySpan<double> points, int pointCount, Span<double> result4);
}

public static class Hilbert
{
    public static ulong Index(ReadOnlySpan<double> point4, int order);
    public static void  Inverse(ulong index, int order, Span<double> result4);
}

public static class GramSchmidt
{
    /// <summary>
    /// In-place modified Gram-Schmidt orthonormalization of K row-major vectors of
    /// length N. Stable; never classical GS. Deterministic column order.
    /// </summary>
    public static void OrthonormalizeInPlace(Span<double> vectors, int k, int n);
}

public static class TopKStable
{
    /// <summary>
    /// Deterministic top-k selection with stable tie-break. When two values are
    /// equal, the lower <paramref name="secondaryKey"/> wins; when secondaryKey is
    /// also equal, the lower index wins. Guarantees bit-identical output across runs.
    /// </summary>
    public static void Select(
        ReadOnlySpan<double> values,
        ReadOnlySpan<long>   secondaryKey,   // may be empty → uses index
        int k,
        Span<long>    outIndices,
        Span<double>  outValues);
}
```

---

## Ingestion primitives

Called only from decomposers and analysis passes. Loads `hartonomous_ingest.dll` / `libhartonomous_ingest.so` — the ILP64, MKL-linked artifact. All index arguments are `long` (64-bit) to match MKL_ILP64 on the native side.

```csharp
namespace Hartonomous.Core.Compute.Ingestion;

public enum TensorDtype
{
    F64 = 0, F32 = 1, F16 = 2, BF16 = 3,
    I8 = 4, U8 = 5, I16 = 6, I32 = 7, I64 = 8,
    U16 = 9, U32 = 10, U64 = 11,
    Bool = 12, F8E4M3 = 13, F8E5M2 = 14
}

public static class TensorDecode
{
    /// <summary>
    /// Decode a packed little-endian tensor buffer into a double[] via lossless
    /// widening. Never quantizes, never normalizes. Every integer dtype and every
    /// float dtype (F16, BF16, F32, F64, F8*) is representable exactly in f64.
    /// </summary>
    public static void ToF64(
        ReadOnlySpan<byte> src, TensorDtype srcDtype,
        Span<double> dst);

    /// <summary>
    /// Same, targeting single precision. Only defined where widening src → f32 is
    /// lossless. Throws UnsupportedDtypeException for F64, I32/I64, U32/U64, F8*.
    /// </summary>
    public static void ToF32(
        ReadOnlySpan<byte> src, TensorDtype srcDtype,
        Span<float> dst);
}

public enum TransposeOp { None = 0, Transpose = 1 }

public static class Gemm
{
    /// <summary>
    /// C = α · op(A) · op(B) + β · C. Chunked over the K dimension for matrices
    /// that exceed L3. Deterministic tile schedule — same inputs → same output.
    /// </summary>
    public static void F64(
        TransposeOp opA, TransposeOp opB,
        long m, long n, long k,
        double alpha,
        ReadOnlySpan<double> a, long lda,
        ReadOnlySpan<double> b, long ldb,
        double beta,
        Span<double> c, long ldc);

    public static void F32(
        TransposeOp opA, TransposeOp opB,
        long m, long n, long k,
        float alpha,
        ReadOnlySpan<float> a, long lda,
        ReadOnlySpan<float> b, long ldb,
        float beta,
        Span<float> c, long ldc);
}

public static class Svd
{
    /// <summary>
    /// Full SVD of a dense M × N matrix — A = U · diag(s) · Vᵀ. Output buffers
    /// caller-sized. Deterministic under MKL_CBWR=AUTO,STRICT.
    /// </summary>
    public static void F64(
        long m, long n,
        ReadOnlySpan<double> a, long lda,
        Span<double> u,  long ldu,
        Span<double> s,                    // length = min(m, n)
        Span<double> vt, long ldvt);
}

/// <summary>
/// CSR sparse matrix. Owns no memory — caller retains all arrays for the
/// lifetime of any operation that references this struct.
/// </summary>
public readonly ref struct CsrF64
{
    public readonly long NRows;
    public readonly long NCols;
    public readonly long Nnz;
    public readonly ReadOnlySpan<long>   RowPtr;   // length NRows + 1
    public readonly ReadOnlySpan<long>   ColIdx;   // length Nnz
    public readonly ReadOnlySpan<double> Values;   // length Nnz

    public CsrF64(long nRows, long nCols, long nnz,
        ReadOnlySpan<long> rowPtr, ReadOnlySpan<long> colIdx, ReadOnlySpan<double> values);
}

public static class CsrMatVec
{
    /// <summary>y = α · A · x + β · y. MKL Sparse BLAS under CBWR=AUTO,STRICT.</summary>
    public static void F64(
        CsrF64 a,
        double alpha, ReadOnlySpan<double> x,
        double beta,  Span<double> y);
}

public static class SparseSymEigs
{
    /// <summary>
    /// Top-k algebraic eigenvalues/vectors of a symmetric CSR matrix via Spectra's
    /// SymEigsSolver running over MKL Sparse BLAS. Deterministic: the starting
    /// Lanczos vector is seeded from <paramref name="seed"/>.
    ///
    /// For Laplacian eigenmaps we invoke this on M = 2·I − L (the shift/scale form
    /// of the symmetric normalized Laplacian) and extract top eigenvalues of M,
    /// which correspond to the smallest eigenvalues of L — without ever forming
    /// the dense Laplacian.
    /// </summary>
    public static SparseEigsResult F64(
        CsrF64 a,
        int k,
        int maxIter,
        double tol,
        ulong seed,
        Span<double> eigenvalues,              // length k
        Span<double> eigenvectorsColumnMajor); // NRows × k column-major
}

public sealed record SparseEigsResult(int IterationsUsed, bool Converged);

public static class KnnCosineGraph
{
    /// <summary>
    /// Exact symmetric k-nearest-neighbors graph on cosine similarity over
    /// L2-normalized row vectors. Output is a CSR with symmetrized edges.
    ///
    /// Quadratic in N by construction. No HNSW, no IVF, no LSH, no random
    /// projection — this is the exact graph.
    /// </summary>
    public static KnnGraphF64 BuildF64(
        long n, long d,
        ReadOnlySpan<double> rowsNormalizedRowMajor, long ld,
        int k);
}

public sealed class KnnGraphF64
{
    public long NRows { get; }
    public long Nnz { get; }
    public long[]   RowPtr { get; }   // length NRows + 1
    public long[]   ColIdx { get; }   // length Nnz
    public double[] Values { get; }   // length Nnz

    public CsrF64 AsCsr();
}
```

---

## Inference primitives

Called only from query-path code (traversal, ranking, Voronoi cells). Loads `hartonomous_query.dll` / `libhartonomous_query.so` — the LP64, MKL-free artifact, compatible with PostgreSQL backend context.

```csharp
namespace Hartonomous.Core.Compute.Inference;

public static class S3Distance
{
    /// <summary>
    /// Re-export of Common.S3Geometry.Distance with a query-side discoverable name.
    /// </summary>
    public static double Between(ReadOnlySpan<double> p4, ReadOnlySpan<double> q4);
}

public static class VoronoiCell
{
    /// <summary>
    /// Given a set of seed points on S³ and a candidate query point, return the
    /// index of the seed whose geodesic distance to the query is smallest, with
    /// stable tie-break on seed index.
    /// </summary>
    public static int NearestSeedIndex(
        ReadOnlySpan<double> seedsPacked4, int seedCount,
        ReadOnlySpan<double> query4);

    /// <summary>
    /// All seed indices whose Voronoi cell contains the query point (ties). For a
    /// generic query this returns exactly one index; returns multiple only when
    /// the query is equidistant to two or more seeds within floating-point epsilon.
    /// </summary>
    public static int[] CellMembers(
        ReadOnlySpan<double> seedsPacked4, int seedCount,
        ReadOnlySpan<double> query4,
        double epsilon);
}

public static class FrechetDistance
{
    /// <summary>
    /// Discrete Fréchet distance between two ordered S³ paths. Used by the Gödel
    /// engine when comparing candidate traversal paths. Quadratic in path length —
    /// no approximation.
    /// </summary>
    public static double DiscreteF64(
        ReadOnlySpan<double> pathAPacked4, int lenA,
        ReadOnlySpan<double> pathBPacked4, int lenB);
}
```

---

## Internal P/Invoke layer

All `[LibraryImport]` declarations live under `Hartonomous.Core.Compute.Internal`. Source-generated marshaling (no runtime reflection). Spans are pinned by the source generator.

```csharp
namespace Hartonomous.Core.Compute.Internal;

internal static partial class NativeIngest
{
    private const string Lib = "hartonomous_ingest";

    [LibraryImport(Lib, EntryPoint = "htns_init")]
    internal static partial int Init();

    [LibraryImport(Lib, EntryPoint = "htns_svd_f64")]
    internal static partial int SvdF64(
        long m, long n,
        ReadOnlySpan<double> a, long lda,
        Span<double> u, long ldu,
        Span<double> s,
        Span<double> vt, long ldvt);

    // … one partial per native entry point; names match htns_* with leading htns_ stripped
}

internal static partial class NativeQuery
{
    private const string Lib = "hartonomous_query";

    [LibraryImport(Lib, EntryPoint = "htns_init")]
    internal static partial int Init();

    // … query-artifact surface only
}

internal static class NativeError
{
    /// <summary>
    /// Translate an htns_error code to an exception. HTNS_OK returns — everything
    /// else throws. Never returns a nullable "did it fail" flag; callers that
    /// want to handle specific codes catch the specific exception type.
    /// </summary>
    internal static void ThrowIfError(int code, string operation);
}
```

### Exception hierarchy

Rooted at `ComputeException` in `Hartonomous.Core.Compute`:

| Exception | Native code |
|---|---|
| `ComputeInitException` | `HTNS_ERR_NOT_INIT`, `HTNS_ERR_ISA`, `HTNS_ERR_DETERMINISM` |
| `ComputeConvergenceException` | `HTNS_ERR_CONVERGE`, `HTNS_ERR_ILL_COND` |
| `UnsupportedDtypeException` | `HTNS_ERR_UNSUPPORTED` |
| `ComputeArgumentException` | `HTNS_ERR_NULL`, `HTNS_ERR_SIZE`, `HTNS_ERR_OVERFLOW` |
| `ComputeAllocationException` | `HTNS_ERR_ALLOC` |

Per CLAUDE.md: no catch-and-log. Callers either let these propagate (the common case) or handle a specific typed exception at a documented substrate boundary.

---

## Prohibited dependencies

The facade and its callers are audited at build time. The following references, anywhere outside `Hartonomous.Core.Compute.*`, fail the build:

- `Microsoft.ML.OnnxRuntime` — not used; would introduce its own BLAS
- `MKL.NET`, `MathNet.Numerics.MKL.*` — MKL routing lives in the native library only
- `HNSWLib`, `Annoy.Net`, any ANN package — approximation, prohibited by Law #6
- `Accord.Math.Random`, `MathNet.Numerics.Random` outside seeded-interface usage — no hidden entropy
- Any package whose description mentions "approximate", "randomized", "stochastic", "probabilistic NN"

A CI step greps every `.csproj` and the `.deps.json` of the built binaries for these names.

---

## Where callers change

Migration path, as other tasks land:

- Decomposers (`Hartonomous.Decomposers.*`) currently call `Blake3Native.Blake3(...)` directly. They migrate to `Hartonomous.Core.Compute.Common.Blake3.Hash(...)`. The old `Blake3Native` moves under `Compute.Internal` and is made `internal`.
- `LaplacianEigenmap.cs` currently implements its own Lanczos in managed code. It will be reduced to a thin wrapper that calls `Compute.Ingestion.KnnCosineGraph.BuildF64` then `Compute.Ingestion.SparseSymEigs.F64`, then `Compute.Common.GramSchmidt.OrthonormalizeInPlace`. No MKL / Spectra references in managed code.
- Any decomposer that today does array math in managed loops (`SafetensorsDecomposer.ProjectTensorToFireflies`, etc.) migrates its tight loops into facade calls — the facade's chunked GEMM replaces nested-for-loop matrix multiplies.

---

## Cross-references

- `CLAUDE.md` § *Compute Facade*, § *Determinism & Exact Math* — policy this spec implements.
- `docs/specs/native/compute-library.md` — the native artifact this facade calls.
- `docs/specs/csharp/interfaces.md` — the interfaces that will consume facade primitives.
- `docs/specs/csharp/base-classes.md` — `BaseDecomposer` migration: Blake3 via facade.
- `docs/specs/engine/embedding-physicality.md` — Track 1 caller of SparseSymEigs + GramSchmidt.
- `docs/architecture.md` Law #6 — determinism contract this facade enforces across the managed boundary.
