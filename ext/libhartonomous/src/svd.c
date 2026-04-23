/*
 * svd.c — dense f64 singular value decomposition via MKL dgesdd (divide-
 *          and-conquer). Row-major throughout so callers stay in C/C#
 *          conventions. Computes the thin SVD: for A in R^(m×n) with
 *          k = min(m, n),
 *             A = U · diag(S) · V^T,
 *          U  ∈ R^(m×k), S ∈ R^k (descending), V^T ∈ R^(k×n).
 *
 * Determinism. MKL dgesdd is deterministic under CBWR=AUTO,STRICT. Reduction
 * order is fixed, no PRNG is involved, and LAPACK's row-major wrapper is a
 * pure argument marshalling layer — same inputs produce bit-identical
 * outputs across repeated runs on the same ISA.
 *
 * This is the only SVD used by the substrate (Procrustes alignment,
 * attention-pattern right-singular vectors, tensor-energy spectra, feature
 * clustering preprocessing). No randomized / truncated / quantized variants
 * are offered. Law #6 forbids approximation; if the substrate needs a
 * sparse Lanczos SVD later, it will be a separately-named primitive
 * against an explicit sparse matrix.
 */

#include "hartonomous.h"

#include <stddef.h>
#include <stdint.h>

#include <mkl.h>
#include <mkl_lapacke.h>

int hartonomous_svd_f64(
    int64_t m, int64_t n,
    const double* a,
    double* u,
    double* s,
    double* vt
) {
    if (a == NULL || u == NULL || s == NULL || vt == NULL) {
        return -1;
    }
    if (m <= 0 || n <= 0) {
        return -2;
    }

    const int64_t k = (m < n) ? m : n;

    /* dgesdd overwrites the input matrix when jobz='O'; with jobz='S' it
     * leaves A intact and writes the thin U and V^T. We want A preserved,
     * so copy into a scratch buffer sized m·n. */
    double* a_copy = (double*)mkl_malloc((size_t)m * (size_t)n * sizeof(double), 64);
    if (a_copy == NULL) {
        return -9;
    }
    /* Plain copy — memcpy is fine; MKL's cblas_dcopy could be used but adds
     * no value at this size. */
    for (int64_t i = 0; i < m * n; ++i) {
        a_copy[i] = a[i];
    }

    /* LAPACKE row-major:
     *   jobz = 'S'  → thin U (m × k) and thin V^T (k × n)
     *   lda = n     (row-major stride of A)
     *   ldu = k     (row-major stride of U when thin)
     *   ldvt = n    (row-major stride of V^T)
     */
    lapack_int info = LAPACKE_dgesdd(
        LAPACK_ROW_MAJOR,
        'S',
        (lapack_int)m,
        (lapack_int)n,
        a_copy,
        (lapack_int)n,
        s,
        u,
        (lapack_int)k,
        vt,
        (lapack_int)n
    );

    mkl_free(a_copy);

    if (info > 0) {
        /* DBDSDC did not converge. Treat identically to Lanczos non-convergence
         * (error code -6) so the facade can surface a uniform diagnostic. */
        return -6;
    }
    if (info < 0) {
        /* Invalid argument — the argument marshalling above should prevent
         * this, so treat as a shape error for the caller's contract. */
        return -2;
    }

    (void)k;
    return 0;
}
