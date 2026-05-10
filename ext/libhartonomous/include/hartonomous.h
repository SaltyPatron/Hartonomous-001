#ifndef HARTONOMOUS_H
#define HARTONOMOUS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef HARTONOMOUS_BUILD
    #define HARTONOMOUS_API __declspec(dllexport)
  #else
    #define HARTONOMOUS_API __declspec(dllimport)
  #endif
#else
  #define HARTONOMOUS_API __attribute__((visibility("default")))
#endif

#define HARTONOMOUS_VERSION_MAJOR 0
#define HARTONOMOUS_VERSION_MINOR 1
#define HARTONOMOUS_VERSION_PATCH 0

#define HARTONOMOUS_HASH_LEN 32

HARTONOMOUS_API const char* hartonomous_version(void);

/* ── Runtime information ──────────────────────────────────────
 *
 * Populated by hartonomous_runtime_info(). Lets callers assert at runtime
 * that the intended acceleration is actually linked in — MKL version + max
 * threads, OpenMP max threads, the CBWR branch the library resolved to for
 * MKL strict reproducibility. No allocation; caller owns the struct.
 *
 * Fields:
 *   has_mkl           — 1 if MKL is linked (always 1 in this build), else 0.
 *   mkl_version       — MKL version string from mkl_get_version_string(),
 *                       NUL-terminated.
 *   mkl_max_threads   — mkl_get_max_threads().
 *   omp_max_threads   — omp_get_max_threads().
 *   cbwr_branch       — mkl_cbwr_get() — the active conditional-bitwise-
 *                       reproducibility branch. Non-negative on success.
 */
typedef struct hartonomous_runtime_info {
    int  has_mkl;
    char mkl_version[128];
    int  mkl_max_threads;
    int  omp_max_threads;
    int  cbwr_branch;
} hartonomous_runtime_info_t;

HARTONOMOUS_API void hartonomous_runtime_info(hartonomous_runtime_info_t* out);

/*
 * Force MKL CBWR=AUTO|STRICT and return the resolved branch (>= 0).
 * Returns -1 if MKL refuses the request (typically because compute was
 * performed before this call — a deterministic-init violation). Idempotent
 * when called repeatedly with the same setting.
 */
HARTONOMOUS_API int hartonomous_init_determinism(void);

/*
 * One-shot MKL initializer with a process-local atomic guard. Safe to call
 * from every MKL-using entry point; only the first invocation per process
 * pays the actual mkl_cbwr_set cost. Subsequent calls return immediately.
 * This is what should be used inside SQL function entry points instead of
 * calling MKL eagerly at extension load.
 */
HARTONOMOUS_API int hartonomous_ensure_mkl_initialized(void);

/*
 * BLAKE3 one-shot hash. Computes a 32-byte digest over `len` bytes at `data`.
 * `out` must point to at least HARTONOMOUS_HASH_LEN (32) bytes.
 * Thread-safe. No allocations.
 */
HARTONOMOUS_API void hartonomous_blake3(
    const uint8_t* data,
    size_t len,
    uint8_t out[HARTONOMOUS_HASH_LEN]
);

/*
 * BLAKE3 incremental hashing. Opaque state, owned by the caller.
 * Usage:
 *   hartonomous_blake3_state st;
 *   hartonomous_blake3_init(&st);
 *   hartonomous_blake3_update(&st, chunk_a, a_len);
 *   hartonomous_blake3_update(&st, chunk_b, b_len);
 *   hartonomous_blake3_finalize(&st, out);
 */
typedef struct hartonomous_blake3_state {
    /* Opaque; sized to hold a blake3_hasher (1912 bytes on LP64). */
    uint8_t _opaque[2048];
} hartonomous_blake3_state;

HARTONOMOUS_API void hartonomous_blake3_init(hartonomous_blake3_state* state);
HARTONOMOUS_API void hartonomous_blake3_update(
    hartonomous_blake3_state* state,
    const uint8_t* data,
    size_t len
);
HARTONOMOUS_API void hartonomous_blake3_finalize(
    const hartonomous_blake3_state* state,
    uint8_t out[HARTONOMOUS_HASH_LEN]
);

/*
 * BLAKE3 batched one-shot hash. Computes N independent BLAKE3 digests in a
 * single FFI call. Each input is hashed by the AVX-512 single-shot path;
 * the iteration over inputs is OpenMP-parallel so a 14900KS hashes ~10K
 * 1KB records in ~1ms wall time vs ~10ms for the serial per-record FFI.
 *
 * Eliminates per-record P/Invoke trampoline cost — the streaming sink
 * batches 4096 records per chunk and calls this once per chunk.
 *
 * Inputs:
 *   inputs       — array of N pointers to raw byte buffers.
 *   input_lens   — array of N lengths matching `inputs`.
 *   n            — number of inputs (>= 0).
 * Output:
 *   output       — caller-allocated, n * HARTONOMOUS_HASH_LEN contiguous
 *                  bytes; row i is the hash of inputs[i].
 *
 * Returns 0 on success, -1 on null arg, -2 on n < 0.
 */
HARTONOMOUS_API int hartonomous_blake3_many(
    const uint8_t* const* inputs,
    const size_t* input_lens,
    int64_t n,
    uint8_t* output
);

/* ── S3 Geometry ─────────────────────────────────────────── */

/*
 * Geodesic distance between two unit 4-vectors on S^3.
 * Returns arccos(clamp(dot(p1, p2), -1, 1)) in [0, pi].
 * Inputs are assumed unit-length; no normalization is performed.
 */
HARTONOMOUS_API double hartonomous_s3_distance(
    const double p1[4],
    const double p2[4]
);

/*
 * Chordal centroid of `point_count` points on S^3.
 * Computes the vector mean of the input points and renormalizes onto S^3.
 * Writes the 4D unit vector to `out`.
 * Returns 0 on success, -1 on null argument or zero points,
 * -2 when the vector sum has negligible magnitude (antipodal cancellation).
 */
HARTONOMOUS_API int hartonomous_s3_centroid(
    const double* points,
    size_t point_count,
    double out[4]
);

/*
 * Karcher (Fréchet) mean on the unit 3-sphere.
 *
 * Returns the point μ ∈ S³ minimizing (1/n) Σ dist_S³(μ, p_i)², where
 * dist_S³(a,b) = arccos(clamp(⟨a,b⟩, -1, 1)). Distinct from
 * hartonomous_s3_centroid, which returns the *chordal* mean (renormalize
 * the Euclidean sum) — fine as a seed, biased for widely-spread sets.
 *
 * Iteration: projected-gradient on S³ via Log/Exp maps. Seeded from the
 * chordal mean; converges in 3–8 iters for angular spreads under π/2.
 *
 * Inputs:
 *   points       — packed 4-double-per-point array, length 4·point_count.
 *                  Each point must be S³-unit (||p|| = 1) within 1e-9.
 *   point_count  — number of points; must be >= 1.
 *   max_iter     — iteration cap; <= 0 selects the default (64).
 *   tol          — stop when ||tangent-space update|| < tol; <= 0 → 1e-12.
 *
 * Output:
 *   out[4]       — S³-unit 4-vector (||out|| = 1 within rounding).
 *
 * Returns:
 *    0 on success,
 *   -1 on null argument or zero-count,
 *   -2 if the chordal seed cannot be computed (antipodal cancellation in
 *      the Euclidean mean),
 *   -3 if any point is antipodal to the current iterate (Log map undefined).
 *
 * Deterministic: same inputs → bit-identical output, Law #6.
 */
HARTONOMOUS_API int hartonomous_karcher_mean_s3(
    const double* points,
    size_t        point_count,
    int           max_iter,
    double        tol,
    double        out[4]
);

/* ── Super-Fibonacci ─────────────────────────────────────── */

/*
 * Project a sample index onto S^3 using the Super-Fibonacci lattice
 * (Alexa, CVPR 2022). Produces a deterministic, quasi-uniform unit
 * 4-vector in [-1, 1]^4 on the unit 3-sphere.
 *
 * params[0] = index i in [0, N)
 * params[1] = total sample count N (must be > 0)
 * ndims     = number of valid entries in params; must be >= 2
 *
 * Writes 4D unit vector to `out`. Returns 0 on success, -1 on null
 * argument, -2 when ndims < 2 or N <= 0 or i not in [0, N).
 */
HARTONOMOUS_API int hartonomous_super_fibonacci(
    const double* params,
    size_t ndims,
    double out[4]
);

/* Batched Super-Fibonacci: project N indices in [0, total) to N S³ points
 * in a single FFI call. OpenMP-parallel; SVML-vectorizable trig pair.
 * UCD codepoint projection's primary caller (1.1M codepoints in 1 call).
 * Returns 0 success, -1 null, -2 bad shape, -3 index OOB. */
HARTONOMOUS_API int hartonomous_super_fibonacci_many(
    const double* indices,
    int64_t n,
    double total,
    double* out
);

/* ── Hilbert Curve (4D) ──────────────────────────────────── */

/*
 * Compute the Hilbert curve index for a 4D point in [0, 1]^4.
 * `order` is the number of bits per dimension (1..16). Output fits in uint64_t
 * for order <= 16 (4 * 16 = 64 bits).
 * Returns the Hilbert index. Out-of-range inputs are clamped to [0, 1].
 */
HARTONOMOUS_API uint64_t hartonomous_hilbert_index(
    const double point[4],
    int order
);

/*
 * Inverse of hartonomous_hilbert_index. Writes the 4D point corresponding to
 * `index` at resolution `order` into `out` (each coordinate in [0, 1]).
 * Returns 0 on success, -1 on null argument, -2 on order out of range.
 */
HARTONOMOUS_API int hartonomous_hilbert_inverse(
    uint64_t index,
    int order,
    double out[4]
);

/* ── Tensor decode (ingest) ──────────────────────────────────
 *
 * Lossless dtype widening to f64. Never normalizes, never clamps, never
 * quantizes. Reads `src` as a packed little-endian buffer of `src_dtype`
 * elements and writes `dst_count` f64 values to `dst`.
 *
 * Supported source dtypes — minimal viable set for safetensors today:
 *   0 = f64, 1 = f32, 2 = f16, 3 = bf16, 4 = i8, 5 = u8, 6 = i16,
 *   7 = i32, 8 = i64, 9 = u16, 10 = u32, 11 = u64, 12 = bool.
 * Returns 0 on success, -1 on null arg, -2 on size mismatch, -8 on
 * unsupported dtype.
 */
HARTONOMOUS_API int hartonomous_tensor_decode_f64(
    const void* src, size_t src_bytes,
    int src_dtype,
    double* dst, int64_t dst_count
);

/* ── Dense GEMM (ingest) ─────────────────────────────────────
 *
 * Computes C = α·op(A)·op(B) + β·C for f64 row-major matrices. op_a / op_b
 * are 0 (no-op) or 1 (transpose). Cache-blocked, single-threaded, scalar +
 * compiler-vectorized. Same inputs → bit-identical output across runs.
 *
 * Returns 0 on success, -1 on null arg, -2 on size <= 0.
 */
HARTONOMOUS_API int hartonomous_gemm_f64(
    int op_a, int op_b,
    int64_t m, int64_t n, int64_t k,
    double alpha,
    const double* a, int64_t lda,
    const double* b, int64_t ldb,
    double beta,
    double* c, int64_t ldc
);

/* ── Dense SVD (ingest) ──────────────────────────────────────
 *
 * Thin singular value decomposition of a row-major m×n f64 matrix A via
 * MKL dgesdd (divide-and-conquer). Let k = min(m, n). Writes:
 *   u   — row-major m×k, left singular vectors (orthogonal columns)
 *   s   — length k, singular values descending
 *   vt  — row-major k×n, right singular vectors transposed
 *
 * A is preserved (copied internally). Deterministic under CBWR=AUTO,STRICT:
 * same inputs → bit-identical output across repeated runs.
 *
 * Returns 0 on success, -1 on null arg, -2 on size <= 0 or invalid arg,
 * -6 on non-convergence of the bidiagonal solver, -9 on alloc failure.
 */
HARTONOMOUS_API int hartonomous_svd_f64(
    int64_t m, int64_t n,
    const double* a,
    double* u,
    double* s,
    double* vt
);

/* ── Orthogonal Procrustes alignment (ingest) ────────────────
 *
 * Given two d×n row-major configurations X, Y, compute the proper rotation
 * R ∈ SO(d) minimizing ||R·X − Y||_F. Uses SVD of Y·X^T (Kabsch). Always
 * returns det(R) = +1 — reflections are corrected via sign flip of the
 * last column of U.
 *
 * Inputs:
 *   d, n           — dimension and point count
 *   x, y           — row-major d×n, each column is a point
 * Outputs:
 *   rotation       — row-major d×d
 *   out_residual   — optional (may be NULL); ||R·X − Y||_F
 *
 * Returns 0 on success, -1 on null arg, -2 on bad shape, -3 if U·V^T is
 * singular (degenerate input), -6 on SVD non-convergence, -9 on alloc.
 */
HARTONOMOUS_API int hartonomous_procrustes_f64(
    int64_t d, int64_t n,
    const double* x,
    const double* y,
    double* rotation,
    double* out_residual
);

/* ── Exact k-nearest-neighbour query (ingest) ────────────────
 *
 * For each of `nq` queries, return the k nearest corpus points by squared
 * Euclidean distance ascending. Uses MKL GEMM for cross-term, per-query
 * heap selection. Deterministic under CBWR=AUTO,STRICT.
 *
 * Inputs:
 *   nq, nc, d      — query count, corpus count, dimension
 *   queries        — row-major nq × d
 *   corpus         — row-major nc × d
 *   k              — neighbours per query, 1 ≤ k ≤ nc
 * Outputs (caller-allocated, row-major nq × k):
 *   out_indices    — corpus index of each neighbour
 *   out_distances  — squared Euclidean distances (floored at 0)
 *
 * Returns 0 on success, -1 on null arg, -2 on bad shape, -9 on alloc.
 */
HARTONOMOUS_API int hartonomous_knearest_exact_f64(
    int64_t nq, int64_t nc, int64_t d,
    const double* queries,
    const double* corpus,
    int64_t k,
    int64_t* out_indices,
    double* out_distances
);

/* ── Laplacian eigenmap (ingest) ─────────────────────────────
 *
 * Given the full symmetric CSR adjacency A (nonneg weights, both triangles
 * stored), compute the k smallest-algebraic eigenpairs of the normalized
 * symmetric Laplacian L_sym = I − D^{-1/2} · A · D^{-1/2}. Uses Spectra
 * Lanczos on the spectrum-flipped matrix (c·I − L_sym) so smallest
 * eigenvalues of L_sym correspond to largest of the flipped matrix,
 * where Lanczos converges fastest.
 *
 * The trivial λ₀ ≈ 0 IS included in the output. Callers filter it.
 *
 * Inputs:
 *   n, nnz           — node count, A-nnz
 *   row_ptr/col_idx/values — full symmetric CSR
 *   k                — eigenpairs, 1 ≤ k < n
 *   max_iter         — Lanczos ncv, must satisfy k < ncv ≤ n
 *   seed             — starting vector seed
 * Outputs (caller-allocated):
 *   out_eigenvalues  — length k, ascending
 *   out_eigenvectors — row-major k × n
 *   out_iters        — iterations performed
 *
 * Returns 0, -1 null, -2 shape, -3 negative weight or bad column index,
 * -6 non-convergence, -9 alloc.
 */
HARTONOMOUS_API int hartonomous_laplacian_eigenmap_f64(
    int64_t n, int64_t nnz,
    const int64_t* row_ptr,
    const int64_t* col_idx,
    const double*  values,
    int64_t k,
    int64_t max_iter,
    uint64_t seed,
    double* out_eigenvalues,
    double* out_eigenvectors,
    int64_t* out_iters
);

/* ── k-means++ (ingest) ───────────────────────────────
 * Deterministic k-means++ seeding + Lloyd iterations on row-major f64
 * points. Tie-break: lowest-index center wins on equal squared distance.
 * Empty clusters are re-seeded from the farthest point of the largest
 * cluster. Converges when assignments stabilize or max_iter reached.
 *
 * Inputs:
 *   n, d, k   — points, dimension, clusters; 1 ≤ k ≤ n.
 *   points    — row-major n × d.
 *   max_iter  — maximum Lloyd iterations.
 *   seed      — RNG seed for deterministic k-means++ picks.
 * Outputs:
 *   out_assignments — length n.
 *   out_centers     — row-major k × d.
 *   out_iters       — Lloyd iterations actually performed.
 * Returns 0, -1 null, -2 shape, -9 alloc.
 */
HARTONOMOUS_API int hartonomous_kmeans_plusplus_f64(
    int64_t n, int64_t d, int64_t k,
    const double* points,
    int64_t max_iter,
    uint64_t seed,
    int64_t* out_assignments,
    double*  out_centers,
    int64_t* out_iters
);

/* ── Delaunay 4D (ingest) ──────────────────────────────
 * Bowyer-Watson incremental 4D Delaunay tetrahedralization. Every output
 * simplex is a 4-simplex with 5 vertex indices into the input point list.
 *
 * Inputs:
 *   n           — number of points (≥ 5 required for a non-degenerate hull)
 *   points      — row-major n × 4 (f64 xyzw)
 *   out_capacity— size of out_simplices / 5. Pass 0 with out_simplices=NULL
 *                 to query the count via out_simplex_count.
 * Outputs:
 *   out_simplex_count — number of 4-simplices produced
 *   out_simplices     — row-major count × 5 vertex indices (sorted ascending
 *                       within each simplex; simplices ordered
 *                       lexicographically for deterministic output)
 * Returns 0 on success, -1 null, -2 shape/insufficient capacity,
 * -6 numerical failure, -9 alloc.
 */
HARTONOMOUS_API int hartonomous_delaunay_4d_f64(
    int64_t n,
    const double* points,
    int64_t* out_simplex_count,
    int64_t* out_simplices,
    int64_t  out_capacity
);

/* ── k-NN cosine graph (ingest) ──────────────────────────────
 *
 * Exact (not approximate) symmetric k-NN graph from row-L2-normalized
 * vectors. Uses chunked GEMM internally; per-row partial-sort selects
 * top-k; symmetrization de-duplicates the (lo, hi) pair set.
 *
 * Inputs:
 *   n              — number of rows
 *   d              — embedding dimension
 *   rows_normalized— row-major n × d, each row L2-normalized by caller
 *   k              — neighbors per row, must be >= 1 and < n
 *
 * Outputs (caller-allocated):
 *   out_row_ptr    — length n+1, CSR row pointer
 *   out_col_idx    — length up to 2·n·k, column indices
 *   out_values     — length up to 2·n·k, cosine-affinity weights (clamped to [0, 1])
 *   out_nnz        — actual nnz after symmetrization
 *
 * Returns 0 on success, -1 on null arg, -2 on bad shape, -9 on alloc fail.
 */
HARTONOMOUS_API int hartonomous_knn_cosine_graph_f64(
    int64_t n, int64_t d,
    const double* rows_normalized,
    int64_t k,
    int64_t* out_row_ptr,
    int64_t* out_col_idx,
    double*  out_values,
    int64_t* out_nnz
);

/* ── Sparse symmetric Lanczos eigensolver (ingest) ───────────
 *
 * Top-k Ritz pairs of a symmetric CSR matrix via Lanczos with full
 * re-orthogonalization. Deterministic — caller supplies the seed used for
 * the starting vector. Single-threaded.
 *
 * Inputs:
 *   n            — matrix dimension (rows == cols)
 *   nnz          — non-zeros
 *   row_ptr      — length n+1
 *   col_idx      — length nnz
 *   values       — length nnz
 *   k            — number of eigenpairs requested
 *   max_iter     — Lanczos iteration cap (>= k+10 recommended)
 *   seed         — PRNG seed for starting vector
 *
 * Outputs (caller-allocated):
 *   eigenvalues  — length k (sorted descending by algebraic value)
 *   eigenvectors — column-major n × k
 *   out_iters    — actual Lanczos iterations used
 *
 * Returns 0 on success, -1 on null arg, -2 on bad shape, -6 on non-converge,
 * -9 on alloc fail.
 */
HARTONOMOUS_API int hartonomous_sparse_sym_eigs_f64(
    int64_t n, int64_t nnz,
    const int64_t* row_ptr,
    const int64_t* col_idx,
    const double* values,
    int64_t k,
    int64_t max_iter,
    uint64_t seed,
    double* eigenvalues,
    double* eigenvectors,
    int64_t* out_iters
);

/* ── Modified Gram–Schmidt (both) ────────────────────────────
 *
 * In-place modified Gram–Schmidt orthonormalization of `k` row-major
 * vectors of length `n`. Stable; deterministic column order.
 * `ld` is the row stride in elements (>= n).
 *
 * Returns 0 on success, -1 on null arg, -2 on bad shape.
 */
HARTONOMOUS_API int hartonomous_gram_schmidt_f64(
    int64_t k, int64_t n,
    double* vectors, int64_t ld
);

/* ── Merkle roll-up (both) ───────────────────────────────────
 *
 * BLAKE3 hash of an ordered concatenation of 32-byte child hashes.
 * `child_count` may be 0 (hashes the empty input).
 *
 * Returns 0 on success, -1 on null output.
 */
HARTONOMOUS_API int hartonomous_blake3_merkle(
    const uint8_t* child_hashes, size_t child_count,
    uint8_t output[HARTONOMOUS_HASH_LEN]
);

/* ── 4D primitives (point4d / box4d / linestring4d) ───────────
 *
 * The substrate is genuinely 4D. PostGIS POINTZM treats the M axis as an
 * out-of-band scalar attribute, so any distance through PostGIS drops it.
 * These functions operate on raw `double[4]` so the native side has zero
 * coupling to PostgreSQL's type machinery; the PG extension wraps each one
 * in a `point4d`/`box4d`/`linestring4d` SQL type.
 *
 * Coordinate semantics are application-defined (S³ unit-quaternion vs
 * Euclidean 4-space) and live on the row's physicality_type, not in the
 * function. For S³ operations the caller must pre-normalize.
 */

/* Euclidean 4D distance: sqrt(sum_i (a_i - b_i)^2). */
HARTONOMOUS_API double hartonomous_distance_4d(
    const double a[4], const double b[4]
);

/* Batched Euclidean 4D distance for N pairs. AVX2 inner kernel,
 * OpenMP-parallel across pairs. Caller-allocated `out` of length n.
 * Returns 0 success, -1 null, -2 n < 0. */
HARTONOMOUS_API int hartonomous_distance_4d_pairs(
    const double* a,
    const double* b,
    int64_t n,
    double* out
);

/* Batched 4D discrete Fréchet distance for N polyline pairs.
 * Each pair gets a thread-local DP-table allocation of size na[i]*nb[i].
 * NaN where either polyline is empty.
 * Returns 0 success, -1 null, -2 n < 0, -9 alloc failure for any pair. */
HARTONOMOUS_API int hartonomous_frechet_4d_pairs(
    const double* const* a_polylines,
    const size_t* na,
    const double* const* b_polylines,
    const size_t* nb,
    int64_t n,
    double* out_distances
);

/* 4D inner product. */
HARTONOMOUS_API double hartonomous_dot_4d(
    const double a[4], const double b[4]
);

/* L2 norm: sqrt(sum_i x_i^2). */
HARTONOMOUS_API double hartonomous_norm_4d(const double x[4]);

/* Unit-normalize. Returns 0 on success; -1 on null arg; -2 on near-zero norm
 * (||x|| < 1e-12). On error `out` is left untouched. */
HARTONOMOUS_API int hartonomous_normalize_4d(
    const double x[4], double out[4]
);

/* Spherical linear interpolation on S³. Inputs are assumed unit-length.
 * t in [0,1]; t=0 returns a, t=1 returns b. Returns 0 on success;
 * -1 on null; -2 if either input is not unit-length within 1e-9. */
HARTONOMOUS_API int hartonomous_slerp(
    const double a[4], const double b[4], double t, double out[4]
);

/* Antipode: out_i = -p_i. Returns 0 on success; -1 on null. */
HARTONOMOUS_API int hartonomous_antipode(
    const double p[4], double out[4]
);

/* Euclidean centroid of `point_count` 4D points. Output is the arithmetic
 * mean (NOT renormalized — use hartonomous_s3_centroid for spherical mean).
 * Returns 0 on success; -1 on null/zero-count. */
HARTONOMOUS_API int hartonomous_centroid_4d(
    const double* points, size_t point_count, double out[4]
);

/* Grouped centroid: for N points with group_ids in [0, group_count), compute
 * the per-group arithmetic mean. Streaming-sink pattern for emitting
 * composition centroids in one FFI call instead of one-call-per-composition.
 * Empty groups are zero-filled. Deterministic per Law #6.
 * Returns 0 success, -1 null, -2 bad shape/alloc, -3 group_id OOB. */
HARTONOMOUS_API int hartonomous_centroid_4d_grouped(
    const double* points,
    const int64_t* group_ids,
    int64_t n,
    int64_t group_count,
    double* centroids
);

/* ── box4d: axis-aligned bounding box ─────────────────────────
 * Layout: 8 doubles laid out as [min[0..3], max[0..3]].
 * Used as the GiST key type for point4d columns.
 */

/* Initialize box from a single point: min = max = p. */
HARTONOMOUS_API void hartonomous_bbox_init_point(
    const double p[4], double box[8]
);

/* Expand `box` in-place to include `p`. */
HARTONOMOUS_API void hartonomous_bbox_expand_point(
    double box[8], const double p[4]
);

/* Compute union of two boxes into `out` (which may alias either input). */
HARTONOMOUS_API void hartonomous_bbox_union(
    const double a[8], const double b[8], double out[8]
);

/* Box-box overlap predicate: closed intervals on every axis. */
HARTONOMOUS_API int hartonomous_bbox_overlaps(
    const double a[8], const double b[8]
);

/* Point-in-box predicate (closed). */
HARTONOMOUS_API int hartonomous_bbox_contains_point(
    const double box[8], const double p[4]
);

/* Box-in-box predicate (closed): every axis of `inner` ⊆ axis of `outer`. */
HARTONOMOUS_API int hartonomous_bbox_contains_box(
    const double outer[8], const double inner[8]
);

/* Box-box equality (exact double comparison). */
HARTONOMOUS_API int hartonomous_bbox_equals(
    const double a[8], const double b[8]
);

/* 4D volume of a box (product of axis extents). */
HARTONOMOUS_API double hartonomous_bbox_volume(const double box[8]);

/* Lower-bound distance from `p` to the nearest point of `box` (Euclidean 4D).
 * Zero when `p` is inside `box`. Used by GiST `distance` support function for
 * <-> kNN ordering. */
HARTONOMOUS_API double hartonomous_bbox_min_distance_4d(
    const double box[8], const double p[4]
);

/* ── linestring4d: trajectories ───────────────────────────────
 * Stored as a contiguous packed array of `point_count` 4D points,
 * total length 4*point_count doubles. */

/* 4D discrete Fréchet distance between two polylines.
 *   a, b           — packed 4-double-per-vertex arrays
 *   na, nb         — vertex counts (>= 1 each)
 *   workspace      — caller-allocated double[na * nb] scratch buffer
 * Returns Fréchet distance in the Euclidean 4D metric. Returns NaN on
 * null arg or zero-vertex input. Deterministic, O(na*nb). */
HARTONOMOUS_API double hartonomous_frechet_4d(
    const double* a, size_t na,
    const double* b, size_t nb,
    double* workspace
);

/* 4D Hausdorff distance: max(sup_a inf_b ||a-b||, sup_b inf_a ||a-b||). */
HARTONOMOUS_API double hartonomous_hausdorff_4d(
    const double* a, size_t na,
    const double* b, size_t nb
);

/* ── Text decomposition (in-process, native) ────────────────────────
 *
 * Replaces the per-text round-trip to substrate.text_decompose with an
 * in-process call. The native pipeline does the entire UAX#29 + BLAKE3 +
 * S^3-centroid walk against the embedded UCD 17.0.0 tables (compiled into
 * libhartonomous at build time) and emits records via a callback. No
 * Postgres handshake, no parse/plan/execute per text.
 *
 * Determinism: Law #6. Same UTF-8 input + same UCD blob = byte-identical
 * hash output. Algorithm matches pg_text_decompose's substrate-side
 * version exactly.
 *
 * Atom blob: must be loaded once per process via hartonomous_ucd_load
 * before calling hartonomous_text_decompose. The blob path must contain
 *   hartonomous-ucd-<ver>.idx
 *   hartonomous-ucd-<ver>.reverse.bin
 *   blocks/<startHex>-<name>.bin
 * (same on-disk layout the PG extension consumes).
 */

/* Record kinds emitted by the pipeline. */
#define HARTONOMOUS_REC_ENTITY        1   /* a unique content hash */
#define HARTONOMOUS_REC_CLASSIFICATION 2  /* (entity_hash, kind) */
#define HARTONOMOUS_REC_PHYSICALITY   3   /* (entity_hash, content_hash, kind, wkb) */
#define HARTONOMOUS_REC_SEQUENCE      4   /* (parent_hash, ordinal, child_hash) */
#define HARTONOMOUS_REC_SIGNIFICANCE  5   /* (entity_hash, context_kind, mu) */

/* Entity-kind tags passed in record.subkind / out_root_kind. The integers
 * match substrate.entity_type ids 1..3 + 9 in the project schema, but the
 * native side does NOT depend on that mapping — callers translate kind →
 * substrate.entity_type code. */
#define HARTONOMOUS_KIND_CODEPOINT         1
#define HARTONOMOUS_KIND_GRAPHEME_CLUSTER  2
#define HARTONOMOUS_KIND_WORD_FORM         3
#define HARTONOMOUS_KIND_TEXT_COMPOSITION  9

/* Physicality-kind tags. */
#define HARTONOMOUS_PHYS_S3_POSITION  1   /* POINTZM (atom) */
#define HARTONOMOUS_PHYS_CONTOUR      2   /* LINESTRINGZM (composition) */

/* Significance-kind tags. */
#define HARTONOMOUS_SIG_SOURCE_AUTHORITY  1

typedef struct hartonomous_text_record {
    int             kind;        /* HARTONOMOUS_REC_* */
    int             subkind;     /* entity-kind / phys-kind / sig-kind / 0 */
    const uint8_t*  hash_a;      /* entity_hash | parent_hash */
    const uint8_t*  hash_b;      /* content_hash | child_hash | 0 */
    int             int_param;   /* ordinal | rle_count | 0 */
    double          double_param;/* mu | 0 */
    const uint8_t*  wkb;         /* EWKB bytes | 0 */
    size_t          wkb_len;     /* 0 when wkb is 0 */
} hartonomous_text_record_t;

/* Callback fires once per emitted record. Return 0 to continue, non-zero to
 * abort the walk (the function returns that value). The callback runs on the
 * same thread that called hartonomous_text_decompose. */
typedef int (*hartonomous_text_emit_cb)(
    void* ctx,
    const hartonomous_text_record_t* rec
);

/*
 * Load the UCD per-block blob from `dir`. Idempotent — calling repeatedly
 * is a no-op after the first success. Must be called before
 * hartonomous_text_decompose. Returns 0 on success, -1 if any required
 * file is missing or malformed. Thread-safe via internal mutex.
 */
HARTONOMOUS_API int hartonomous_ucd_load(const char* dir);

/*
 * Release any mmap'd UCD pages. Optional; the OS reclaims on process exit.
 */
HARTONOMOUS_API void hartonomous_ucd_unload(void);

/*
 * Returns 1 if hartonomous_ucd_load has succeeded since the last unload,
 * 0 otherwise.
 */
HARTONOMOUS_API int hartonomous_ucd_loaded_state(void);

/*
 * Per-codepoint atom accessors. All return -1 on failure
 * (out-of-range or block file missing); 0 on success.
 *
 * hartonomous_ucd_cp_centroid: copies 4 doubles (S^3 X,Y,Z,M) into out[].
 * hartonomous_ucd_cp_hash:     copies 32 bytes BLAKE3 into out[].
 * hartonomous_ucd_cp_hilbert:  returns the 64-bit Hilbert code via *out (0 on miss).
 *
 * The centroid is computed at blob-build time as
 * super_fibonacci_4d(uca_index[cp], 0x110000) — UCA-collation-rank ordered,
 * NOT raw codepoint ordered. Same case/accent pairs cluster on S^3.
 *
 * Caller must have called hartonomous_ucd_load first.
 */
HARTONOMOUS_API int hartonomous_ucd_cp_centroid(int32_t cp, double out[4]);
HARTONOMOUS_API int hartonomous_ucd_cp_hash(int32_t cp, uint8_t out[32]);
HARTONOMOUS_API int hartonomous_ucd_cp_hilbert(int32_t cp, uint64_t* out);

/*
 * Reverse hash → codepoint over the global sorted table.
 * Returns the codepoint, or -1 if not found.
 */
HARTONOMOUS_API int32_t hartonomous_ucd_cp_from_hash(const uint8_t hash32[32]);

/*
 * Decompose UTF-8 bytes into the substrate's text DAG.
 *   utf8 / utf8_len    — input document
 *   top_kind           — HARTONOMOUS_KIND_* for the root composition
 *   trust_mu           — initial μ for source_authority significance rows
 *   emit / ctx         — callback fired once per emitted record
 *   out_root_hash      — 32-byte buffer; receives the root composition hash
 *   out_root_kind      — receives the resolved top kind (== top_kind)
 *   out_root_centroid  — optional 4-double buffer; receives root POINTZM
 *                        centroid coordinates
 *
 * Returns:
 *    0 on success,
 *   -1 on null required arg,
 *   -2 if hartonomous_ucd_load was not called or failed,
 *   -3 if utf8_len is 0 (callers should check before calling),
 *   any non-zero value returned by `emit` to abort the walk.
 */
HARTONOMOUS_API int hartonomous_text_decompose(
    const uint8_t* utf8,
    size_t utf8_len,
    int top_kind,
    double trust_mu,
    hartonomous_text_emit_cb emit,
    void* ctx,
    uint8_t out_root_hash[HARTONOMOUS_HASH_LEN],
    int* out_root_kind,
    double out_root_centroid[4]
);

/* ── Glicko-2 bulk update ───────────────────────────────────── */
HARTONOMOUS_API int hartonomous_glicko2_bulk_update(
    int64_t n,
    const double* mu,
    const double* sigma,
    const double* volatility,
    const double* opp_mu,
    const double* opp_sigma,
    const double* score,
    double* new_mu,
    double* new_sigma,
    double* new_volatility
);

/* ── Phase A.0.4 synthesis primitives (2026-05-09) ────────────
 *
 * Stub entrypoints for the recomposer's exact synthesis surface.
 * Native implementation is scheduled for Phase B.1; current returns
 * -99 (HARTONOMOUS_ERR_NOT_IMPLEMENTED). C# callers translate that
 * to a ComputeException via NativeError with the entrypoint name.
 *
 * Spec docs:
 *   docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md
 *   docs/specs/recomposers/algorithms/ffn-kv-inversion.md
 *   docs/specs/recomposers/algorithms/lottery-ticket-foundations.md
 */

/* Solve A·X = B for X via Moore-Penrose pseudoinverse (SVD-based).
 * All matrices row-major f64. Returns numerical rank used in *rank_out. */
HARTONOMOUS_API int hartonomous_linear_system_solve_f64(
    int64_t m, int64_t n, int64_t p,
    const double* a,
    const double* b,
    double* x,
    double tolerance,
    int64_t* rank_out
);

/* Construct (W_gate, W_up, W_down) from sparse token-pair attestations.
 * See SparseFfnInversion.cs for argument semantics. */
HARTONOMOUS_API int hartonomous_sparse_ffn_invert_f64(
    int64_t vocab_size, int64_t hidden_dim, int64_t intermediate_dim,
    const double* token_embeddings,
    int64_t nnz,
    const int64_t* input_token_idx,
    const int64_t* output_token_idx,
    const double* strength,
    double coverage_min,
    double* w_gate_out,
    double* w_up_out,
    double* w_down_out,
    double* coverage_out
);

/* Reverse-project firefly POINTZM centroids (XYZM) back to hidden_dim
 * using stored eigenvectors and the model's native embedding. */
HARTONOMOUS_API int hartonomous_inverse_eigenmap_f64(
    int64_t vocab_size, int64_t hidden_dim,
    const double* eigenvectors,
    const double* embeddings,
    int64_t centroid_count,
    const double* centroids_xyzm,
    double* hidden_out
);

/* Mask cells whose coverage is below threshold to exact zero (in-place
 * on weights). Writes per-row mean coverage to row_coverage_out and
 * returns the aggregate matrix coverage in *aggregate_coverage_out. */
HARTONOMOUS_API int hartonomous_honest_abstention_f64(
    int64_t rows, int64_t cols,
    double* weights,
    const double* coverage,
    double cell_threshold,
    double* row_coverage_out,
    double* aggregate_coverage_out
);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_H */
