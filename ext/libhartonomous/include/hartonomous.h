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

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_H */
