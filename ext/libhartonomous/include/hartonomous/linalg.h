/* libhartonomous — linalg.h
 *
 * Dense + sparse linear algebra primitives used by the ingestion path.
 * MKL-backed; deterministic under CBWR=AUTO,STRICT.
 */

#ifndef HARTONOMOUS_LINALG_H
#define HARTONOMOUS_LINALG_H

#include <stddef.h>
#include <stdint.h>

#include "hartonomous/version.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Dense GEMM: C = α·op(A)·op(B) + β·C, row-major f64.
 * op_a/op_b: 0 = no-op, 1 = transpose. */
HARTONOMOUS_API int hartonomous_gemm_f64(
    int op_a, int op_b,
    int64_t m, int64_t n, int64_t k,
    double alpha,
    const double* a, int64_t lda,
    const double* b, int64_t ldb,
    double beta,
    double* c, int64_t ldc);

/* Thin SVD via MKL dgesdd. */
HARTONOMOUS_API int hartonomous_svd_f64(
    int64_t m, int64_t n,
    const double* a,
    double* u,
    double* s,
    double* vt);

/* Orthogonal Procrustes (Kabsch). */
HARTONOMOUS_API int hartonomous_procrustes_f64(
    int64_t d, int64_t n,
    const double* x,
    const double* y,
    double* rotation,
    double* out_residual);

/* Exact k-NN by squared Euclidean distance. */
HARTONOMOUS_API int hartonomous_knearest_exact_f64(
    int64_t nq, int64_t nc, int64_t d,
    const double* queries,
    const double* corpus,
    int64_t k,
    int64_t* out_indices,
    double* out_distances);

/* Symmetric k-NN cosine graph (chunked GEMM + per-row top-k). */
HARTONOMOUS_API int hartonomous_knn_cosine_graph_f64(
    int64_t n, int64_t d,
    const double* rows_normalized,
    int64_t k,
    int64_t* out_row_ptr,
    int64_t* out_col_idx,
    double*  out_values,
    int64_t* out_nnz);

/* Sparse symmetric Lanczos eigensolver (CSR, top-k). */
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
    int64_t* out_iters);

/* Smallest-algebraic eigenpairs of L_sym = I − D^{-1/2}·A·D^{-1/2}. */
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
    int64_t* out_iters);

/* Deterministic k-means++ + Lloyd. */
HARTONOMOUS_API int hartonomous_kmeans_plusplus_f64(
    int64_t n, int64_t d, int64_t k,
    const double* points,
    int64_t max_iter,
    uint64_t seed,
    int64_t* out_assignments,
    double*  out_centers,
    int64_t* out_iters);

/* Bowyer-Watson 4D Delaunay tetrahedralization. */
HARTONOMOUS_API int hartonomous_delaunay_4d_f64(
    int64_t n,
    const double* points,
    int64_t* out_simplex_count,
    int64_t* out_simplices,
    int64_t  out_capacity);

/* Modified Gram-Schmidt, in-place. */
HARTONOMOUS_API int hartonomous_gram_schmidt_f64(
    int64_t k, int64_t n,
    double* vectors, int64_t ld);

/* Solve A·X = B via Moore-Penrose pseudoinverse. */
HARTONOMOUS_API int hartonomous_linear_system_solve_f64(
    int64_t m, int64_t n, int64_t p,
    const double* a,
    const double* b,
    double* x,
    double tolerance,
    int64_t* rank_out);

#ifdef __cplusplus
}
#endif

#endif /* HARTONOMOUS_LINALG_H */
