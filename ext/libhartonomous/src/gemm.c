#include <stddef.h>
#include <stdint.h>

#include <mkl.h>
#include <mkl_cblas.h>
#include <mkl_service.h>

#include "hartonomous.h"

/*
 * f64 GEMM via Intel MKL cblas_dgemm.
 *   C = alpha · op(A) · op(B) + beta · C
 * Multi-threaded through MKL's own thread pool (iomp5). Bit-reproducible
 * across runs within an ISA class via CBWR=AUTO,STRICT.
 *
 * op_a / op_b: 0 = no-op, 1 = transpose. Row-major throughout.
 */

static void ensure_cbwr_set(void) {
    static int set_done = 0;
    if (!set_done) {
        mkl_cbwr_set(MKL_CBWR_AUTO | MKL_CBWR_STRICT);
        set_done = 1;
    }
}

int hartonomous_gemm_f64(
    int op_a, int op_b,
    int64_t m, int64_t n, int64_t k,
    double alpha,
    const double* a, int64_t lda,
    const double* b, int64_t ldb,
    double beta,
    double* c, int64_t ldc
) {
    if (a == NULL || b == NULL || c == NULL) return -1;
    if (m <= 0 || n <= 0 || k <= 0) return -2;
    if ((op_a != 0 && op_a != 1) || (op_b != 0 && op_b != 1)) return -2;
    if (lda <= 0 || ldb <= 0 || ldc <= 0) return -2;

    ensure_cbwr_set();

    CBLAS_TRANSPOSE ta = op_a ? CblasTrans : CblasNoTrans;
    CBLAS_TRANSPOSE tb = op_b ? CblasTrans : CblasNoTrans;

    cblas_dgemm(
        CblasRowMajor, ta, tb,
        (MKL_INT)m, (MKL_INT)n, (MKL_INT)k,
        alpha,
        a, (MKL_INT)lda,
        b, (MKL_INT)ldb,
        beta,
        c, (MKL_INT)ldc
    );

    return 0;
}
