/*
 * Incremental MKL sparse-API probes — each exported function exercises ONE
 * more MKL sparse call than the previous. Called from C# to pinpoint the
 * exact MKL inspector-executor step that faults under the .NET test host.
 *
 * Return convention (all probes):
 *     0 = success
 *   <0  = MKL status or local failure code
 *
 * The probes use a fixed 3x3 identity matrix so no Lanczos / heap pressure
 * confounds the signal — if a probe crashes, the crash is caused by the
 * highest-indexed MKL sparse call the probe invoked, not by size or data.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>

#include <mkl.h>
#include <mkl_spblas.h>

#include "hartonomous.h"

/* Fill a 3x3 upper-CSR identity: rp=[0,1,2,3], ci=[0,1,2], v=[1,1,1]. */
static void fill_identity3(MKL_INT* rp, MKL_INT* ci, double* v) {
    rp[0] = 0; rp[1] = 1; rp[2] = 2; rp[3] = 3;
    ci[0] = 0; ci[1] = 1; ci[2] = 2;
    v[0] = 1.0; v[1] = 1.0; v[2] = 1.0;
}

/* P1: mkl_sparse_d_create_csr + mkl_sparse_destroy only. */
HARTONOMOUS_API int hartonomous_probe_sparse_create_destroy(void) {
    MKL_INT rp[4];
    MKL_INT ci[3];
    double  v[3];
    fill_identity3(rp, ci, v);

    sparse_matrix_t A;
    sparse_status_t st = mkl_sparse_d_create_csr(
        &A, SPARSE_INDEX_BASE_ZERO, 3, 3,
        rp, rp + 1, ci, v);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-100 - (int)st);
    st = mkl_sparse_destroy(A);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-200 - (int)st);
    return 0;
}

/* P2: + mkl_sparse_set_mv_hint. */
HARTONOMOUS_API int hartonomous_probe_sparse_set_hint(void) {
    MKL_INT rp[4];
    MKL_INT ci[3];
    double  v[3];
    fill_identity3(rp, ci, v);

    sparse_matrix_t A;
    sparse_status_t st = mkl_sparse_d_create_csr(
        &A, SPARSE_INDEX_BASE_ZERO, 3, 3,
        rp, rp + 1, ci, v);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-100 - (int)st);

    struct matrix_descr descr;
    descr.type = SPARSE_MATRIX_TYPE_SYMMETRIC;
    descr.mode = SPARSE_FILL_MODE_UPPER;
    descr.diag = SPARSE_DIAG_NON_UNIT;
    st = mkl_sparse_set_mv_hint(A, SPARSE_OPERATION_NON_TRANSPOSE, descr, 8);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-300 - (int)st);
    }
    mkl_sparse_destroy(A);
    return 0;
}

/* P3: + mkl_sparse_optimize. */
HARTONOMOUS_API int hartonomous_probe_sparse_optimize(void) {
    MKL_INT rp[4];
    MKL_INT ci[3];
    double  v[3];
    fill_identity3(rp, ci, v);

    sparse_matrix_t A;
    sparse_status_t st = mkl_sparse_d_create_csr(
        &A, SPARSE_INDEX_BASE_ZERO, 3, 3,
        rp, rp + 1, ci, v);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-100 - (int)st);

    struct matrix_descr descr;
    descr.type = SPARSE_MATRIX_TYPE_SYMMETRIC;
    descr.mode = SPARSE_FILL_MODE_UPPER;
    descr.diag = SPARSE_DIAG_NON_UNIT;
    st = mkl_sparse_set_mv_hint(A, SPARSE_OPERATION_NON_TRANSPOSE, descr, 8);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-300 - (int)st);
    }
    st = mkl_sparse_optimize(A);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-400 - (int)st);
    }
    mkl_sparse_destroy(A);
    return 0;
}

/* P4: + one mkl_sparse_d_mv call. */
HARTONOMOUS_API int hartonomous_probe_sparse_mv(void) {
    MKL_INT rp[4];
    MKL_INT ci[3];
    double  v[3];
    double  x[3] = {1.0, 2.0, 3.0};
    double  y[3] = {0.0, 0.0, 0.0};
    fill_identity3(rp, ci, v);

    sparse_matrix_t A;
    sparse_status_t st = mkl_sparse_d_create_csr(
        &A, SPARSE_INDEX_BASE_ZERO, 3, 3,
        rp, rp + 1, ci, v);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-100 - (int)st);

    struct matrix_descr descr;
    descr.type = SPARSE_MATRIX_TYPE_SYMMETRIC;
    descr.mode = SPARSE_FILL_MODE_UPPER;
    descr.diag = SPARSE_DIAG_NON_UNIT;
    st = mkl_sparse_set_mv_hint(A, SPARSE_OPERATION_NON_TRANSPOSE, descr, 8);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-300 - (int)st);
    }
    st = mkl_sparse_optimize(A);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-400 - (int)st);
    }
    st = mkl_sparse_d_mv(
        SPARSE_OPERATION_NON_TRANSPOSE, 1.0, A, descr,
        x, 0.0, y);
    if (st != SPARSE_STATUS_SUCCESS) {
        mkl_sparse_destroy(A);
        return (int)(-500 - (int)st);
    }
    mkl_sparse_destroy(A);
    if (y[0] != 1.0 || y[1] != 2.0 || y[2] != 3.0) return -700;
    return 0;
}

/* P5: full inspector-executor chain (same sequence as sparse_eigs inner loop)
 * repeated 8 times to surface any setup-only-once vs per-call bug. */
HARTONOMOUS_API int hartonomous_probe_sparse_mv_loop(int32_t iters) {
    MKL_INT rp[4];
    MKL_INT ci[3];
    double  v[3];
    double  x[3] = {1.0, 2.0, 3.0};
    double  y[3];
    fill_identity3(rp, ci, v);

    sparse_matrix_t A;
    sparse_status_t st = mkl_sparse_d_create_csr(
        &A, SPARSE_INDEX_BASE_ZERO, 3, 3,
        rp, rp + 1, ci, v);
    if (st != SPARSE_STATUS_SUCCESS) return (int)(-100 - (int)st);

    struct matrix_descr descr;
    descr.type = SPARSE_MATRIX_TYPE_SYMMETRIC;
    descr.mode = SPARSE_FILL_MODE_UPPER;
    descr.diag = SPARSE_DIAG_NON_UNIT;
    st = mkl_sparse_set_mv_hint(A, SPARSE_OPERATION_NON_TRANSPOSE, descr, iters);
    if (st != SPARSE_STATUS_SUCCESS) { mkl_sparse_destroy(A); return (int)(-300 - (int)st); }
    st = mkl_sparse_optimize(A);
    if (st != SPARSE_STATUS_SUCCESS) { mkl_sparse_destroy(A); return (int)(-400 - (int)st); }

    for (int32_t i = 0; i < iters; ++i) {
        y[0] = y[1] = y[2] = 0.0;
        st = mkl_sparse_d_mv(
            SPARSE_OPERATION_NON_TRANSPOSE, 1.0, A, descr,
            x, 0.0, y);
        if (st != SPARSE_STATUS_SUCCESS) { mkl_sparse_destroy(A); return (int)(-500 - (int)st); }
        if (y[0] != 1.0 || y[1] != 2.0 || y[2] != 3.0) { mkl_sparse_destroy(A); return -700; }
    }
    mkl_sparse_destroy(A);
    return 0;
}
