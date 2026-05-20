# Native Compute Library

**Status**: 🔜 M1 (expanded scope — supersedes shared-library.md for ingestion-side primitives)

The native compute layer for Hartonomous. One C ABI, two linkage targets, one determinism policy. Called exclusively by the C# `Hartonomous.Core.Compute` facade (`CLAUDE.md` § *Compute Facade*). No other project links these libraries.

This spec extends `shared-library.md` — BLAKE3, S3, Super-Fibonacci, Hilbert are still public surface; this document adds the ingestion-time numerical primitives (SVD, sparse Lanczos eigensolve, chunked GEMM, sparse matvec, k-NN construction, tensor dtype decode) and the cross-cutting policies (ISA dispatch, init contract, two-artifact split, exact-math guarantees).

---

## Two-artifact split

One repository, one source tree, one C ABI, two linked outputs. Deliberate. Each artifact exists because the other cannot satisfy its constraints.

| Artifact | Purpose | Integer model | Consumers | Linked against |
|---|---|---|---|---|
| `hartonomous_ingest.{dll,so,dylib}` | All ingestion-time compute: SVD, sparse eigensolve, chunked GEMM, sparse matvec, k-NN, tensor decode, BLAKE3, S3, Hilbert, Super-Fibonacci | **ILP64** (MKL `*_ilp64`) | C# `Hartonomous.Core.Compute.Ingestion.*` via P/Invoke | Intel MKL 2025.3+ (ILP64, TBB threading layer), Intel TBB (composable parallel-for, task-group, `tbb::global_control` thread lock), Intel TCM (`tbbmalloc` scalable allocator), Eigen 3.4 (header-only, `EIGEN_USE_MKL_ALL`), Spectra 1.0+ (header-only), oneDPL (header-only, TBB execution policies), vendored BLAKE3; **optional**: oneDNN (gated behind `HTNS_ENABLE_ONEDNN`, off by default — reserved for analysis passes that need JIT-generated attention kernels) |
| `hartonomous_query.{dll,so,dylib}` | Substrate query-time compute: S3 distance, S3 centroid, Super-Fibonacci projection, Hilbert encode/decode, BLAKE3, Merkle roll-up, scalar geodesic ops | LP64 (no 64-bit array indices required) | PostgreSQL extension (statically linked into `hartonomous.so` PG module); C# `Hartonomous.Core.Compute.Inference.*` via P/Invoke | No MKL, no TBB, no BLAS — scalar math + SIMD intrinsics only. Must remain dependency-clean so it loads safely inside a PG backend. |

### Why split

- **ILP64 contagion.** MKL ILP64 defines `MKL_INT = int64_t` and is **not ABI-compatible** with any LP64 BLAS a host process may already link. PostgreSQL backends link LP64 libraries transitively (libc, OS BLAS, geos, proj, etc.). Loading an ILP64 MKL inside a PG backend crashes or silently corrupts integer arguments. Query-time code must be LP64-clean so it can be statically linked into the PG module.
- **Weight class.** MKL runtime is ~500 MB. Loading it into every PG backend is unacceptable; it has zero value at query time (queries do S3 distance and graph traversal, not SVD). Ingestion-time compute happens in one .NET process that can afford the footprint and loads MKL once.
- **Failure isolation.** The ingest artifact can crash in arbitrary ways inside Lanczos on an ill-conditioned 150K×150K sparse Laplacian without taking the database down. The query artifact must never crash — it runs inside `postgres.exe`.

Both artifacts share source files where identical (`blake3/`, `s3_geometry.c`, `super_fibonacci.c`, `hilbert.c`, `merkle.c`). Shared files are compiled twice under the two artifacts' build configurations — there is no third shared binary to avoid ABI drift.

---

## Public API

Header layout:

```
ext/native/include/
  hartonomous.h                ← top-level umbrella (includes everything)
  hartonomous_common.h         ← error codes, init/shutdown, version, build info
  hartonomous_hash.h           ← BLAKE3 (in both artifacts)
  hartonomous_geometry.h       ← S3, Super-Fibonacci, Hilbert (in both artifacts)
  hartonomous_ingest.h         ← ingest-only: SVD, Lanczos, GEMM, matvec, k-NN, decode
```

The ingest and query builds define different preprocessor symbols (`HTNS_ARTIFACT_INGEST` / `HTNS_ARTIFACT_QUERY`) that gate exported symbols. `hartonomous_ingest.h` is `#ifdef`-guarded off in the query build — attempting to call an ingest primitive from the query library is a link-time error, not a runtime surprise.

### Common (both artifacts)

```c
/* Error codes — extended from shared-library.md */
typedef enum {
    HTNS_OK                 =  0,
    HTNS_ERR_NULL           = -1,  /* NULL pointer argument */
    HTNS_ERR_SIZE           = -2,  /* Invalid size / dimension */
    HTNS_ERR_OVERFLOW       = -3,  /* Buffer too small */
    HTNS_ERR_NOT_INIT       = -4,  /* hart_init not called */
    HTNS_ERR_ISA            = -5,  /* Required ISA level not present */
    HTNS_ERR_CONVERGE       = -6,  /* Iterative solver failed to converge */
    HTNS_ERR_ILL_COND       = -7,  /* Input numerically ill-conditioned */
    HTNS_ERR_UNSUPPORTED    = -8,  /* Unsupported dtype / shape / combination */
    HTNS_ERR_ALLOC          = -9,  /* Allocation failed */
    HTNS_ERR_DETERMINISM    = -10  /* Determinism contract violated (e.g. MKL_CBWR mis-set) */
} htns_error;

/* One-shot process initialization. MUST be called before any compute primitive.
 * Idempotent. Thread-safe. Sets MKL_CBWR=AUTO,STRICT, locks ISA dispatch, verifies
 * determinism contracts. Returns HTNS_ERR_DETERMINISM if any contract fails.
 */
HTNS_API htns_error htns_init(void);

/* Process teardown — releases MKL thread pool, frees dispatch state.
 * Optional; process exit is safe without it.
 */
HTNS_API void htns_shutdown(void);

/* Build info — version, ISA ceiling actually compiled in, MKL interface, BLAKE3 commit.
 * Caller supplies buffer; NUL-terminated. Never truncates silently — returns
 * HTNS_ERR_OVERFLOW if buffer too small.
 */
HTNS_API htns_error htns_build_info(char* buf, size_t buf_len);
```

### Tensor decode (ingest only)

Dtype decode is lossless widening, never quantization.

```c
typedef enum {
    HTNS_DTYPE_F64 = 0,
    HTNS_DTYPE_F32 = 1,
    HTNS_DTYPE_F16 = 2,
    HTNS_DTYPE_BF16 = 3,
    HTNS_DTYPE_I8  = 4,
    HTNS_DTYPE_U8  = 5,
    HTNS_DTYPE_I16 = 6,
    HTNS_DTYPE_I32 = 7,
    HTNS_DTYPE_I64 = 8,
    HTNS_DTYPE_U16 = 9,
    HTNS_DTYPE_U32 = 10,
    HTNS_DTYPE_U64 = 11,
    HTNS_DTYPE_BOOL = 12,
    HTNS_DTYPE_F8_E4M3 = 13,
    HTNS_DTYPE_F8_E5M2 = 14
} htns_dtype;

/* Decode a packed little-endian tensor buffer of `src_dtype` into an f64 array.
 * Width contract: decode is exact for every integer dtype and for F16/BF16/F32/F64.
 * F8 dtypes widen via their defined mantissa/exponent unpack — lossless to f64
 * because f64 covers the entire representable set of f8.
 * Never normalizes, never clamps, never quantizes.
 */
HTNS_API htns_error htns_tensor_decode_f64(
    const void* src, size_t src_bytes,
    htns_dtype src_dtype,
    double* dst, int64_t dst_count);

/* Same, but target is f32 (for primitives that run in single precision for throughput
 * while preserving bit-identity across runs — gated on MKL_CBWR=AUTO,STRICT).
 * Only defined where widening from src → f32 is lossless (F16, BF16, F32, I8/I16, U8/U16, BOOL).
 * Returns HTNS_ERR_UNSUPPORTED for F64, I32/I64, U32/U64, F8 dtypes.
 */
HTNS_API htns_error htns_tensor_decode_f32(
    const void* src, size_t src_bytes,
    htns_dtype src_dtype,
    float* dst, int64_t dst_count);
```

### Dense linear algebra (ingest only)

```c
/* Chunked GEMM for matrices that exceed available RAM. Computes C = α·op(A)·op(B) + β·C
 * by tiling A and B across the k dimension. Tile size chosen internally to fit L3.
 * Thin wrapper over MKL cblas_dgemm / sgemm with deterministic tile schedule.
 * Same inputs → bitwise-identical output across repeated runs.
 */
typedef enum { HTNS_OP_N = 0, HTNS_OP_T = 1 } htns_op;

HTNS_API htns_error htns_gemm_f64(
    htns_op op_a, htns_op op_b,
    int64_t m, int64_t n, int64_t k,
    double alpha,
    const double* a, int64_t lda,
    const double* b, int64_t ldb,
    double beta,
    double* c, int64_t ldc);

HTNS_API htns_error htns_gemm_f32(
    htns_op op_a, htns_op op_b,
    int64_t m, int64_t n, int64_t k,
    float alpha,
    const float* a, int64_t lda,
    const float* b, int64_t ldb,
    float beta,
    float* c, int64_t ldc);

/* Full SVD of a dense M×N matrix. Uses MKL LAPACKE_dgesdd (divide-and-conquer).
 * Writes U (M×min), Σ (min-length vector), Vᵀ (min×N) where min = min(M, N).
 * Deterministic across runs: MKL's dgesdd is deterministic under CBWR=STRICT.
 */
HTNS_API htns_error htns_svd_f64(
    int64_t m, int64_t n,
    const double* a, int64_t lda,
    double* u, int64_t ldu,
    double* s,
    double* vt, int64_t ldvt);
```

### Sparse linear algebra (ingest only)

The k-NN Laplacian graph is sparse by construction — O(N·k) nonzeros in a 150K×150K matrix means ~1.5M nonzeros, not 22B. Sparse representation is mandatory; no dense Laplacian is ever materialized.

```c
/* CSR sparse matrix descriptor. Ownership: caller retains all arrays. */
typedef struct {
    int64_t n_rows;
    int64_t n_cols;
    int64_t nnz;
    const int64_t* row_ptr;    /* length n_rows + 1 */
    const int64_t* col_idx;    /* length nnz */
    const double*  values;     /* length nnz */
} htns_csr_f64;

/* Sparse matrix × dense vector: y = α·A·x + β·y.
 * Uses MKL Sparse BLAS with MKL_CBWR=AUTO,STRICT for reduction-order determinism.
 */
HTNS_API htns_error htns_csr_matvec_f64(
    const htns_csr_f64* a,
    double alpha, const double* x,
    double beta,  double* y);

/* Sparse symmetric eigensolver (Lanczos via Spectra SymEigsSolver over
 * MKL-backed Sparse BLAS). Extracts the top-k algebraic eigenvalues/vectors
 * of a symmetric CSR matrix. Deterministic: caller supplies starting vector
 * seed; Spectra never draws entropy on its own.
 *
 * For Laplacian eigenmaps we pass the normalized-Laplacian shift-and-invert
 * form (M = 2I − L) and extract the top eigenvalues of M, which correspond
 * to the bottom (smallest) eigenvalues of L — including the trivial λ=0
 * constant eigenvector — without ever forming L explicitly.
 */
HTNS_API htns_error htns_sparse_sym_eigs_f64(
    const htns_csr_f64* a,
    int64_t k,                    /* number of eigenpairs to return */
    int64_t max_iter,             /* Lanczos iteration cap */
    double  tol,                  /* convergence tolerance */
    uint64_t seed,                /* deterministic starting vector */
    double* eigenvalues,          /* length k */
    double* eigenvectors,         /* column-major n_rows × k */
    int64_t* out_iters);          /* actual iterations used — reported for diagnostics */

/* Exact k-nearest-neighbors graph construction (NOT ANN).
 * Input: row-major N×D matrix of L2-normalized vectors (cosine = dot product).
 * Output: CSR of the symmetrized k-NN graph with cosine-affinity weights.
 * Implementation: chunked MKL GEMM computes similarity blocks; per-row partial
 * sort selects top-k; symmetrize via lo/hi pair-key dictionary; deterministic
 * tie-break on (neighbor index, similarity) with stable ordering.
 * No HNSW, no IVF, no LSH, no random projection. Quadratic in N by design.
 */
HTNS_API htns_error htns_knn_cosine_graph_f64(
    int64_t n, int64_t d,
    const double* rows_normalized, int64_t ld,
    int64_t k,
    int64_t* out_row_ptr,         /* caller allocates n + 1 */
    int64_t* out_col_idx,         /* caller allocates up to 2·n·k */
    double*  out_values,          /* caller allocates up to 2·n·k */
    int64_t* out_nnz);            /* actual nnz after symmetrization */
```

### Geometry (both artifacts)

Unchanged from `shared-library.md`: `htns_s3_distance`, `htns_s3_centroid`, `htns_super_fibonacci`, `htns_hilbert_index`, `htns_hilbert_inverse`, plus Gram-Schmidt orthonormalization (query-side needs it for candidate path comparison as well):

```c
/* In-place Gram-Schmidt orthonormalization of K row-major vectors of length N.
 * Stable modified-GS, never classical GS. Deterministic column order.
 */
HTNS_API htns_error htns_gram_schmidt_f64(
    int64_t k, int64_t n,
    double* vectors, int64_t ld);

/* Deterministic top-k with stable tie-break on (value, secondary_key).
 * Exposed so the facade's "deterministic top-k" primitive has a single
 * implementation used by both ingest (neighbor selection) and query
 * (candidate path ranking).
 */
HTNS_API htns_error htns_top_k_stable_f64(
    const double* values, int64_t n,
    const int64_t* secondary_key,    /* may be NULL → index-as-key */
    int64_t k,
    int64_t* out_indices,            /* length k */
    double*  out_values);            /* length k */
```

### Hash (both artifacts)

BLAKE3 surface unchanged from `shared-library.md`. Merkle roll-up moved here:

```c
/* Merkle hash: ordered array of 32-byte child hashes → 32-byte parent hash.
 * Order is part of the content — caller is responsible for canonical ordering.
 * Empty child array (child_count = 0) hashes an empty input.
 */
HTNS_API htns_error htns_blake3_merkle(
    const uint8_t* child_hashes, size_t child_count,
    uint8_t output[32]);
```

---

## ISA dispatch

Hard constraint: the deployment target is a 14900KS with AVX-512 fused off at the microcode level. AVX-512 is not available to us. The ISA ceiling is **AVX2 + FMA3 + AVX-VNNI + BMI2**. The library must not emit AVX-512 intrinsics or AVX-512 assembly.

| ISA path | Status | Used for |
|---|---|---|
| AVX2 + FMA3 + AVX-VNNI + BMI2 | **Primary** | All SIMD work on x86_64 |
| AVX2 + FMA3 (no VNNI) | Fallback | Older x86_64 (Haswell+) |
| SSE4.2 | Compatibility | Ancient x86_64 — slow path, warning logged |
| NEON | Macro-guarded | AArch64 dev machines only |
| Portable C | Always present | Verification (test harness picks this to confirm SIMD answers match scalar bitwise) |

`simd_dispatch.c` runs once at `htns_init` time, selects the function-pointer set, and **locks the selection**. Subsequent calls never re-dispatch. `htns_init` returns `HTNS_ERR_ISA` if the required minimum (AVX2 + FMA3) is absent; the library refuses to run on machines below the floor rather than silently downgrading accuracy or throughput.

MKL dispatch is similarly constrained: the process sets `MKL_ENABLE_INSTRUCTIONS=AVX2` at `htns_init` before any MKL call, preventing MKL from probing for AVX-512 on CPUs that advertise it but run it fused off (the probe can still emit an AVX-512 instruction that faults).

---

## Determinism contract

Hartonomous Law #6 — same input + same decomposer version = same substrate state, byte for byte — is absolute. `htns_init` enforces the conditions that make this true. A process that cannot satisfy the contract refuses to start.

### What `htns_init` sets and verifies

1. **MKL Conditional Numerical Reproducibility.** Calls `mkl_cbwr_set(MKL_CBWR_AUTO | MKL_CBWR_STRICT)`. Returns `HTNS_ERR_DETERMINISM` if MKL rejects (old MKL, unsupported ISA). This pins the reduction order for every MKL operation: same inputs → same floating-point output bit-for-bit across repeated runs on machines within the same ISA class.
2. **MKL threading — TBB backend.** The ingest artifact links `libmkl_tbb_thread` (not `libmkl_intel_thread`). `htns_init` calls `mkl_set_threading_layer(MKL_THREADING_TBB)` at entry, then `mkl_set_num_threads_local(N)` and `mkl_set_dynamic(0)` so the thread count is not negotiated at runtime. Thread count is **also** locked at the TBB level via a process-scoped `tbb::global_control(max_allowed_parallelism, N)` held for the process lifetime — this ensures any oneDPL parallel algorithm, any direct TBB `parallel_for` we issue from inside the artifact, and MKL share a single thread pool with a fixed size. N is configured via `HTNS_COMPUTE_THREADS` env var or defaults to physical core count. Reduction order is thread-count-dependent under STRICT CBWR regardless of threading backend; fixing the thread count fixes the order.
3. **MKL ISA ceiling.** Sets `mkl_enable_instructions(MKL_ENABLE_AVX2)` to prevent AVX-512 probe on fused-off parts.
4. **Denormal-as-zero / flush-to-zero.** `htns_init` reads the MXCSR register state at entry, records it, and forces DAZ/FTZ **off** (IEEE-strict). MKL is instructed not to override. Any denormal behavior is treated as part of the floating-point contract and must be reproducible.
5. **Process-wide PRNG.** No process-wide PRNG is initialized. Every primitive that needs randomness takes a `uint64_t seed` argument. No hidden entropy.

### Prohibited primitives

The library **does not** and will never expose:

- Randomized SVD, randomized range-finder SVD, randomized Nyström.
- Approximate nearest neighbor (HNSW, IVF, IVFPQ, LSH, PQ, OPQ, Annoy, NGT).
- Random projection (Gaussian, sparse, very-sparse, Achlioptas).
- Stochastic trace estimators (Hutchinson, Hutch++).
- Sampling-based approximate eigensolvers.
- Any primitive whose error bound is stated in probability rather than in ULPs.

These are conventional approximations. The substrate rejects them on principle (CLAUDE.md § *Determinism & Exact Math*). Callers who want them must build their own library; the compute facade will not route to them.

### Not prohibited — exact methods that happen to use randomness for tie-breaking

A deterministic procedure that consumes a seeded PRNG for tie-breaking or starting-vector initialization (e.g., Lanczos starting vector, Super-Fibonacci offset) is fine provided the seed is a fixed caller-supplied argument and the procedure converges to a mathematically defined answer. The randomness is bookkeeping, not approximation.

---

## Memory contract

Extends `shared-library.md`. Summary: the library never mallocs memory the caller must free. Every output buffer is caller-allocated. Sizes of caller-allocated buffers are documented per primitive and are either exact or a stated upper bound (k-NN graph nnz is bounded by 2·n·k before symmetrization collapses duplicates).

The sparse eigensolver and SVD internally allocate MKL workspace. That workspace is freed before the primitive returns — no cross-boundary allocations. MKL `mkl_malloc` / `mkl_free` are used to ensure 64-byte alignment without polluting the system allocator.

---

## Source structure

```
ext/native/
  include/
    hartonomous.h               ← umbrella
    hartonomous_common.h        ← init, errors, version, build info
    hartonomous_hash.h          ← BLAKE3
    hartonomous_geometry.h      ← S3, Super-Fibonacci, Hilbert, Gram-Schmidt, top-k
    hartonomous_ingest.h        ← SVD, Lanczos, GEMM, matvec, k-NN, decode
  src/
    common/
      init.c                    ← htns_init, MKL/ISA config, determinism checks
      build_info.c
      errors.c
    blake3/                     ← unchanged — vendored BLAKE3
    geometry/
      s3_geometry.c
      super_fibonacci.c
      hilbert.c
      gram_schmidt.c
      top_k.c
      merkle.c
    ingest/
      tensor_decode.c           ← dtype decode, lossless widening
      gemm.c                    ← chunked GEMM over MKL
      svd.c                     ← LAPACKE_dgesdd wrapper
      csr.c                     ← CSR matvec over MKL Sparse BLAS
      sparse_eigs.cpp           ← Spectra SymEigsSolver wrapper (C++ callable from C ABI)
      knn.c                     ← exact k-NN graph construction
    simd/
      dispatch.c                ← runtime ISA selection, locked at init
  tests/
    common/                     ← init contract, ISA dispatch, determinism harness
    geometry/                   ← unchanged
    ingest/
      test_decode.cpp           ← round-trip per dtype
      test_gemm.cpp             ← chunked vs reference bit-identity
      test_svd.cpp              ← LAPACKE reference
      test_csr.cpp              ← sparse matvec bit-identity across thread counts
      test_sparse_eigs.cpp      ← known-spectrum matrices
      test_knn.cpp              ← exact-brute-force reference for small N
    determinism/
      test_repeatability.cpp    ← run every primitive twice, bitwise compare
      test_no_prohibited.cpp    ← static-symbol check: no HNSW/ANN/randomized symbols linked
  CMakeLists.txt
  ingest-artifact.cmake         ← hartonomous_ingest target
  query-artifact.cmake          ← hartonomous_query target
```

---

## Build configuration

### CMake — ingest artifact

```cmake
add_library(hartonomous_ingest SHARED
    ${HTNS_COMMON_SOURCES}
    ${HTNS_GEOMETRY_SOURCES}
    ${HTNS_HASH_SOURCES}
    ${HTNS_INGEST_SOURCES}
    ${HTNS_SIMD_SOURCES})

target_compile_definitions(hartonomous_ingest PRIVATE
    HTNS_ARTIFACT_INGEST=1
    HTNS_BUILD_DLL
    MKL_ILP64                    # MKL_INT = int64_t
    EIGEN_USE_MKL_ALL            # Eigen routes BLAS/LAPACK through MKL
    EIGEN_NO_DEBUG
    SPECTRA_USE_MKL              # Spectra matvec → MKL Sparse BLAS
    ONEDPL_USE_TBB_BACKEND=1)    # oneDPL parallel algorithms share the TBB pool

target_include_directories(hartonomous_ingest PRIVATE
    ${MKL_INCLUDE_DIR}
    ${TBB_INCLUDE_DIR}
    ${ONEDPL_INCLUDE_DIR}
    ${EIGEN_INCLUDE_DIR}
    ${SPECTRA_INCLUDE_DIR})

target_link_libraries(hartonomous_ingest PRIVATE
    MKL::MKL                     # ILP64 + TBB threading layer; static-link preferred
    TBB::tbb                     # task-based parallelism; MKL's threading backend
    TBB::tbbmalloc               # Intel TCM scalable allocator
    ${CMAKE_DL_LIBS}
    Threads::Threads)

# ISA ceiling. AVX-512 is NOT in this list — 14900KS has it fused off.
target_compile_options(hartonomous_ingest PRIVATE
    $<$<CXX_COMPILER_ID:GNU,Clang>:-mavx2 -mfma -mavxvnni -mbmi2>
    $<$<CXX_COMPILER_ID:MSVC>:/arch:AVX2>)
```

### CMake — query artifact

```cmake
add_library(hartonomous_query SHARED
    ${HTNS_COMMON_SOURCES}
    ${HTNS_GEOMETRY_SOURCES}
    ${HTNS_HASH_SOURCES}
    ${HTNS_SIMD_SOURCES})         # no ingest sources

target_compile_definitions(hartonomous_query PRIVATE
    HTNS_ARTIFACT_QUERY=1
    HTNS_BUILD_DLL)               # no MKL_ILP64, no MKL macros

target_compile_options(hartonomous_query PRIVATE
    $<$<CXX_COMPILER_ID:GNU,Clang>:-mavx2 -mfma -mavxvnni -mbmi2>
    $<$<CXX_COMPILER_ID:MSVC>:/arch:AVX2>)

# No MKL link. No Eigen link. No Spectra link. Pure scalar + SIMD intrinsics.
```

### MKL linkage — TBB threading layer

Intel MKL 2025.3+ is linked statically where possible (reproducibility) and loaded dynamically (`mkl_rt`) only when static link is impractical. The ingest artifact uses the **TBB threading layer**, not Intel OpenMP. Rationale: TBB is composable with our own `tbb::parallel_for` loops in k-NN neighbor selection and deterministic top-k, shares a single thread pool across MKL + oneDPL + our code, and avoids the OpenMP↔TBB thread-pool contention that would otherwise exist.

Linux ingest link line:

```
-Wl,--start-group
  ${MKL_ROOT}/lib/intel64/libmkl_intel_ilp64.a
  ${MKL_ROOT}/lib/intel64/libmkl_tbb_thread.a    # TBB, not intel_thread (OpenMP)
  ${MKL_ROOT}/lib/intel64/libmkl_core.a
-Wl,--end-group
-ltbb -ltbbmalloc                                 # TBB runtime + TCM scalable allocator
-lpthread -lm -ldl
```

Windows equivalent uses `mkl_intel_ilp64.lib` + `mkl_tbb_thread.lib` + `mkl_core.lib` + `tbb12.lib` + `tbbmalloc.lib`. No `libiomp5md.lib`.

### Intel toolchain

| Tool | Role | Policy |
|---|---|---|
| **Intel icx** (`icx` / `icx-cl` / `icpx`) | Primary compiler for ingest artifact on Linux and Windows | Preferred. icx outperforms MSVC and gcc on AVX2+FMA+AVX-VNNI auto-vectorization, respects `/QxCORE-AVX2` for deterministic codegen, and integrates cleanly with MKL and TBB. CMake detects via `CMAKE_C_COMPILER=icx` / `CMAKE_CXX_COMPILER=icpx` (or `icx-cl` on Windows). Fallback: MSVC 14.50 or GCC 13+ — acceptable but vectorizes fewer ingest hot loops. |
| **Intel VTune Profiler** | Hotspot / memory-access / microarchitecture profiling of ingest runs | Dev-time only. Not shipped. Used to identify Lanczos reduction bottlenecks, k-NN tile sizes, CSR matvec cache behavior. |
| **Intel Advisor** | Vectorization advisor + roofline analysis | Dev-time only. Used to prove we are hitting AVX2 + FMA peak on the hot primitives (GEMM tiles, k-NN similarity blocks, tensor decode loops). |
| **MSVC / GCC / Clang** | Query artifact + cross-platform fallback | Query artifact uses MSVC on Windows, GCC/Clang on Linux. Query artifact does NOT depend on icx or any Intel runtime — it must build with the system compiler so it can be packaged into the PG extension without pulling Intel redistributables into `postgres.exe`. |

### Intel oneAPI component inventory

The user directive is "every optimization under the sun to ensure GPU isn't required." This table is the standing policy for every Intel oneAPI component encountered at `C:\Program Files (x86)\Intel\oneAPI\`.

| Component | Role in Hartonomous | Status | Artifact(s) |
|---|---|---|---|
| **MKL** | BLAS, LAPACK, Sparse BLAS, FFT | **Committed** | ingest |
| **TBB** | Task-based parallelism, thread-pool coordination, `global_control` for determinism | **Committed** | ingest |
| **TCM** (`tbbmalloc`) | Scalable allocator for the ingest process | **Committed** | ingest |
| **oneDPL** | C++17 parallel STL algorithms running on TBB execution policies — used for k-NN partial-sort, top-k stable-sort, deterministic unique-by-key during dedup | **Committed** | ingest |
| **Intel icx compiler** | Best x86_64 auto-vectorization on AVX2+FMA+AVX-VNNI; deterministic codegen under `/QxCORE-AVX2` | **Committed** | ingest (Windows + Linux) |
| **VTune Profiler** | Dev-time performance profiling | **Committed** | none (tooling) |
| **Advisor** | Dev-time vectorization + roofline analysis | **Committed** | none (tooling) |
| **IPP** (Integrated Performance Primitives) | FFT, DCT, MFCC, image resize, color conversion, audio resampling — needed by M6 runtime decomposers for audio/image/video | **Planned — M6** | `hartonomous_ingest` extended (or separate `hartonomous_signal` artifact TBD at M6 entry) |
| **oneDNN** | JIT-generated attention / conv / matmul kernels with shape-specific unrolling | **Optional, off by default** | ingest (when `HTNS_ENABLE_ONEDNN=1`) — reserved for attention-archetype analysis pass if we decide to run forward-pass probes on attention heads |
| **Intel MPI** | Inter-process message passing for distributed compute | **Deferred — decentralized mode** | — (not M1–M11). Relevant when the decentralized-substrate roadmap lands (substrate split across user hardware for usage credits). Tracked here so we do not dismiss it as out-of-scope or paint corners that would block its eventual use. |
| **oneDAL** | Data-analytics primitives (PCA, k-means, etc.) | **Skip** | — Redundant with MKL + our exact-math policy; oneDAL's algorithms overlap with what we implement atop MKL and offer no primitive we can't get with more control from raw LAPACK. |
| **ocloc** | OpenCL offline compiler for GPU kernels | **Skip** | — GPU-adjacent; violates CPU-only mandate. |
| **ippcp** | Cryptographic primitives (AES, SHA-2 families) | **Skip** | — BLAKE3 is the sole hash; no other crypto is used in the substrate. |
| **UMF** (Unified Memory Framework) | Heterogeneous-memory allocator (GPU/CPU) | **Skip** | — TCM covers CPU allocation; we have no heterogeneous memory targets. |

Components marked **Committed** are load-bearing dependencies of the ingest artifact. Components marked **Planned** or **Deferred** must remain unreferenced by M1–M11 code but are documented here so future work has a settled classification rather than a fresh argument.

### Decentralized-substrate compatibility

A planned (not-yet-scheduled) mode of Hartonomous splits the substrate across user hardware in exchange for usage credits. Four points of this spec exist because of that roadmap and must not be compromised during M1–M11:

1. **Content addressing everywhere.** Every entity keyed by BLAKE3 of content alone. Never hash placement, publisher, revision, filename, or ordinal. This is the substrate's sharding key — arbitrary partitioning across nodes is safe because identity is content-derived, not location-derived.
2. **Merkle roll-up is public surface.** `htns_blake3_merkle` is in the query artifact (cheap to verify on every node) and in the ingest artifact (used during dedup). A remote node can verify a claimed subtree hash without trusting its neighbor.
3. **ILP64 stays in ingest only.** Query-time compute is LP64 and MKL-free. A PG backend running on a contributor's laptop must not need Intel MKL runtime installed. That constraint directly enables the decentralized deployment model.
4. **Intel MPI is deferred, not rejected.** The link line above has no MPI symbols and `htns_init` has no MPI init, but the ABI leaves room: primitives that would eventually become distributed (global dedup, cross-node Merkle reconciliation) are expressed in terms of CSR + content hashes, both of which serialize cleanly for MPI transport without re-architecture.

---

## Version coordination

| Component | Source of truth | Constraint |
|---|---|---|
| `hartonomous_ingest` API | `hartonomous_*.h` | Pinned to C# `Hartonomous.Core.Compute` facade version |
| `hartonomous_query` API | `hartonomous_*.h` (subset) | Pinned to PG extension SQL-callable function set |
| MKL | Build-time version check in `htns_init` | ≥ 2024.0 (CBWR STRICT on AVX2 required) |
| Eigen | Header-only, vendored commit | 3.4+ |
| Spectra | Header-only, vendored commit | 1.0+ |
| BLAKE3 | Vendored, pinned commit | Official reference impl |

`htns_build_info` returns all five versions in a single string so the C# facade can assert at startup that its expectations match the artifact it loaded.

---

## Test matrix

- **Determinism.** Every primitive is invoked twice back-to-back; outputs are bitwise-compared. Failure is a hard CI stop.
- **Thread-count invariance.** Matvec / eigs / SVD run at 1, 2, 4, 8, 16 MKL threads; CBWR STRICT requires bitwise-identical output at all counts. Failure indicates CBWR is not engaged.
- **ISA invariance.** The SIMD geometry primitives (S3 distance, Super-Fibonacci, Hilbert) run on AVX2 and on the portable-C fallback; results must be bitwise-identical (these primitives are deterministic under any ordering because they are element-wise).
- **Prohibited-symbol check.** A linker-step test greps the final `.so`/`.dll` for forbidden symbols: `hnsw`, `ivf`, `lsh`, `random_projection`, `rsvd`, `hutchinson`. Any hit fails the build.
- **Cross-artifact audit.** The `hartonomous_query` artifact is scanned for MKL symbols (`MKL_`, `mkl_`) and Eigen symbols (`Eigen::`); any hit fails the build — the query library must be MKL-free.

---

## Cross-references

- `CLAUDE.md` § *Compute Facade* — C# caller policy.
- `CLAUDE.md` § *Determinism & Exact Math* — policy this spec implements.
- `docs/specs/native/shared-library.md` — BLAKE3 / S3 / Super-Fibonacci / Hilbert surface (still authoritative for those primitives).
- `docs/specs/native/build-system.md` — CMake / PGXS build flows (extended here with two-artifact split).
- `docs/specs/native/pg-extension.md` — PG extension static-links the query artifact only.
- `docs/specs/engine/embedding-physicality.md` — caller of `htns_sparse_sym_eigs_f64` via the ingest facade.
- `docs/specs/csharp/compute-facade.md` — (to be written, task #30) C# surface that P/Invokes this ABI.
- `docs/architecture.md` Law #6 — the determinism contract this library implements.
