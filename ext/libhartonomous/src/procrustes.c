/*
 * procrustes.c — orthogonal Procrustes alignment of two d×n point clouds.
 *
 * Given two configurations X, Y ∈ R^(d×n) row-major (each column is a
 * point in R^d), find the rotation R ∈ O(d) that minimizes
 *     ||R·X − Y||_F.
 * The solution (Schönemann 1966) is:
 *     M = Y · X^T ∈ R^(d×d)
 *     M = U · Σ · V^T  (full SVD)
 *     R = U · diag(1,…,1, det(U·V^T)) · V^T     (proper rotation)
 * The diagonal correction ensures det(R) = +1 so reflections are not
 * returned; this is the standard "Kabsch" variant.
 *
 * Inputs are row-major as the rest of the compute facade. The rotation
 * is written in row-major (R_ij at R[i*d + j]). The optional residual
 * returns ||R·X − Y||_F so callers can gate acceptance.
 *
 * Determinism. Every primitive used — MKL GEMM, MKL LAPACK dgesdd — is
 * CBWR=AUTO,STRICT deterministic. No PRNG, no tolerance-driven branching.
 *
 * This is the single orthogonal-alignment primitive used by the substrate.
 * Embedding alignment (Laplacian eigenmap across runs), attention-pattern
 * basis alignment, and feature-cluster axis alignment all route here.
 */

#include "hartonomous.h"

#include <math.h>
#include <stddef.h>
#include <stdint.h>

#include <mkl.h>
#include <mkl_lapacke.h>

int hartonomous_procrustes_f64(
    int64_t d, int64_t n,
    const double* x,
    const double* y,
    double* rotation,
    double* out_residual
) {
    if (x == NULL || y == NULL || rotation == NULL) {
        return -1;
    }
    if (d <= 0 || n <= 0) {
        return -2;
    }

    /* M = Y · X^T, M ∈ R^(d×d). X is d×n row-major so X^T is n×d;
     * cblas_dgemm with transB=T computes Y(d×n) · X^T(n×d) directly. */
    double* m_mat = (double*)mkl_malloc((size_t)d * (size_t)d * sizeof(double), 64);
    if (m_mat == NULL) {
        return -9;
    }

    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasTrans,
        (MKL_INT)d, (MKL_INT)d, (MKL_INT)n,
        1.0,
        y, (MKL_INT)n,
        x, (MKL_INT)n,
        0.0,
        m_mat, (MKL_INT)d
    );

    /* Full SVD of M: M = U Σ V^T with U ∈ R^(d×d), V^T ∈ R^(d×d). */
    double* u_mat  = (double*)mkl_malloc((size_t)d * (size_t)d * sizeof(double), 64);
    double* vt_mat = (double*)mkl_malloc((size_t)d * (size_t)d * sizeof(double), 64);
    double* sigma  = (double*)mkl_malloc((size_t)d * sizeof(double), 64);
    if (u_mat == NULL || vt_mat == NULL || sigma == NULL) {
        mkl_free(m_mat);
        if (u_mat != NULL)  { mkl_free(u_mat); }
        if (vt_mat != NULL) { mkl_free(vt_mat); }
        if (sigma != NULL)  { mkl_free(sigma); }
        return -9;
    }

    lapack_int info = LAPACKE_dgesdd(
        LAPACK_ROW_MAJOR, 'A',
        (lapack_int)d, (lapack_int)d,
        m_mat, (lapack_int)d,
        sigma,
        u_mat, (lapack_int)d,
        vt_mat, (lapack_int)d
    );

    if (info != 0) {
        mkl_free(m_mat);
        mkl_free(u_mat);
        mkl_free(vt_mat);
        mkl_free(sigma);
        return info > 0 ? -6 : -2;
    }

    /* R_unscaled = U · V^T, computed into `rotation`. */
    cblas_dgemm(
        CblasRowMajor, CblasNoTrans, CblasNoTrans,
        (MKL_INT)d, (MKL_INT)d, (MKL_INT)d,
        1.0,
        u_mat, (MKL_INT)d,
        vt_mat, (MKL_INT)d,
        0.0,
        rotation, (MKL_INT)d
    );

    /* Compute det(U·V^T) by LU factorization on a scratch copy of the
     * rotation. det = product of diag(L) × product of diag(U) × sign
     * from pivots — LAPACK LU gives only U on the diagonal times the
     * pivot sign, so we track the sign explicitly. */
    double* det_scratch = (double*)mkl_malloc((size_t)d * (size_t)d * sizeof(double), 64);
    lapack_int* ipiv = (lapack_int*)mkl_malloc((size_t)d * sizeof(lapack_int), 64);
    if (det_scratch == NULL || ipiv == NULL) {
        mkl_free(m_mat);
        mkl_free(u_mat);
        mkl_free(vt_mat);
        mkl_free(sigma);
        if (det_scratch != NULL) { mkl_free(det_scratch); }
        if (ipiv != NULL)        { mkl_free(ipiv); }
        return -9;
    }
    for (int64_t i = 0; i < d * d; ++i) { det_scratch[i] = rotation[i]; }

    lapack_int lu_info = LAPACKE_dgetrf(
        LAPACK_ROW_MAJOR,
        (lapack_int)d, (lapack_int)d,
        det_scratch, (lapack_int)d,
        ipiv
    );

    double det = 1.0;
    if (lu_info == 0) {
        for (int64_t i = 0; i < d; ++i) {
            det *= det_scratch[i * d + i];
        }
        /* Pivot sign flips for each non-identity pivot. */
        for (int64_t i = 0; i < d; ++i) {
            if (ipiv[i] != (lapack_int)(i + 1)) {
                det = -det;
            }
        }
    } else {
        /* Singular U·V^T — should not occur because U, V are orthogonal.
         * Treat as degenerate geometry. */
        mkl_free(m_mat);
        mkl_free(u_mat);
        mkl_free(vt_mat);
        mkl_free(sigma);
        mkl_free(det_scratch);
        mkl_free(ipiv);
        return -3;
    }

    /* If det < 0, flip sign of last column of U before recomputing R. This
     * yields det(R) = +1. Equivalent formulation: R = U · diag(1,…,1, sign(det)) · V^T. */
    if (det < 0.0) {
        for (int64_t i = 0; i < d; ++i) {
            u_mat[i * d + (d - 1)] = -u_mat[i * d + (d - 1)];
        }
        cblas_dgemm(
            CblasRowMajor, CblasNoTrans, CblasNoTrans,
            (MKL_INT)d, (MKL_INT)d, (MKL_INT)d,
            1.0,
            u_mat, (MKL_INT)d,
            vt_mat, (MKL_INT)d,
            0.0,
            rotation, (MKL_INT)d
        );
    }

    /* Optional residual: ||R·X − Y||_F. */
    if (out_residual != NULL) {
        double* rx = (double*)mkl_malloc((size_t)d * (size_t)n * sizeof(double), 64);
        if (rx == NULL) {
            mkl_free(m_mat);
            mkl_free(u_mat);
            mkl_free(vt_mat);
            mkl_free(sigma);
            mkl_free(det_scratch);
            mkl_free(ipiv);
            return -9;
        }
        cblas_dgemm(
            CblasRowMajor, CblasNoTrans, CblasNoTrans,
            (MKL_INT)d, (MKL_INT)n, (MKL_INT)d,
            1.0,
            rotation, (MKL_INT)d,
            x, (MKL_INT)n,
            0.0,
            rx, (MKL_INT)n
        );
        double sum_sq = 0.0;
        for (int64_t i = 0; i < d * n; ++i) {
            double diff = rx[i] - y[i];
            sum_sq += diff * diff;
        }
        *out_residual = sqrt(sum_sq);
        mkl_free(rx);
    }

    mkl_free(m_mat);
    mkl_free(u_mat);
    mkl_free(vt_mat);
    mkl_free(sigma);
    mkl_free(det_scratch);
    mkl_free(ipiv);
    return 0;
}
