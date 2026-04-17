#include <stddef.h>
#include <stdint.h>

#include "hartonomous.h"

/*
 * Cache-blocked f64 GEMM. Row-major matrices.
 *   C = alpha · op(A) · op(B) + beta · C
 * op_a / op_b: 0 = no-op, 1 = transpose. Leading dimensions are in elements
 * and refer to the *physical* (un-transposed) row stride.
 *
 * Pure scalar with compiler auto-vectorization. Deterministic: same inputs →
 * bit-identical output. The current implementation is single-threaded and
 * intentionally simple — the public ABI is what matters; the kernel can be
 * swapped for an MKL-backed one in a follow-up without source changes
 * elsewhere.
 *
 * Blocks: 64x64 over (m,n), inner k-block 64. These fit comfortably in L1 on
 * the target CPU (14900KS, 48 KiB L1d per core).
 */

#define BM 64
#define BN 64
#define BK 64

static void apply_beta(double* c, int64_t m, int64_t n, int64_t ldc, double beta) {
    if (beta == 1.0) return;
    if (beta == 0.0) {
        for (int64_t i = 0; i < m; ++i) {
            double* row = c + i * ldc;
            for (int64_t j = 0; j < n; ++j) row[j] = 0.0;
        }
    } else {
        for (int64_t i = 0; i < m; ++i) {
            double* row = c + i * ldc;
            for (int64_t j = 0; j < n; ++j) row[j] *= beta;
        }
    }
}

/* Inner block multiply-accumulate. Specialized over (op_a, op_b) at the call
 * site so the inner loop has no branches. */
#define INNER_BLOCK(A_REF, B_REF)                                              \
    for (int64_t i = i0; i < imax; ++i) {                                      \
        double* crow = c + i * ldc;                                            \
        for (int64_t j = j0; j < jmax; ++j) {                                  \
            double s = 0.0;                                                    \
            for (int64_t kk = k0; kk < kmax; ++kk) {                           \
                s += (A_REF) * (B_REF);                                        \
            }                                                                  \
            crow[j] += alpha * s;                                              \
        }                                                                      \
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

    apply_beta(c, m, n, ldc, beta);
    if (alpha == 0.0) return 0;

    for (int64_t i0 = 0; i0 < m; i0 += BM) {
        int64_t imax = (i0 + BM > m) ? m : i0 + BM;
        for (int64_t j0 = 0; j0 < n; j0 += BN) {
            int64_t jmax = (j0 + BN > n) ? n : j0 + BN;
            for (int64_t k0 = 0; k0 < k; k0 += BK) {
                int64_t kmax = (k0 + BK > k) ? k : k0 + BK;

                if (op_a == 0 && op_b == 0) {
                    INNER_BLOCK(a[i * lda + kk], b[kk * ldb + j])
                } else if (op_a == 1 && op_b == 0) {
                    INNER_BLOCK(a[kk * lda + i], b[kk * ldb + j])
                } else if (op_a == 0 && op_b == 1) {
                    INNER_BLOCK(a[i * lda + kk], b[j * ldb + kk])
                } else {
                    INNER_BLOCK(a[kk * lda + i], b[j * ldb + kk])
                }
            }
        }
    }

    return 0;
}
