#include <math.h>
#include <stddef.h>
#include <stdint.h>

#include <mkl_cblas.h>

#include "hartonomous.h"

/*
 * Modified Gram-Schmidt orthonormalization on `k` row vectors of length `n`.
 * Row stride is `ld` doubles. Operates in-place. MKL Level-1 BLAS backs the
 * dot / axpy / nrm2 inner ops for AVX-512 throughput and deterministic
 * reduction order under CBWR=AUTO,STRICT.
 *
 * Zero-norm rows are left as zeros and the loop continues — for Lanczos basis
 * vectors a zero residual is convergence, not failure.
 */
int hartonomous_gram_schmidt_f64(
    int64_t k, int64_t n,
    double* vectors, int64_t ld
) {
    if (vectors == NULL) return -1;
    if (k <= 0 || n <= 0 || ld < n) return -2;

    const double tiny = 1e-300;

    for (int64_t i = 0; i < k; ++i) {
        double* vi = vectors + i * ld;

        for (int64_t j = 0; j < i; ++j) {
            const double* vj = vectors + j * ld;
            double dot = cblas_ddot((MKL_INT)n, vi, 1, vj, 1);
            if (dot != 0.0) {
                cblas_daxpy((MKL_INT)n, -dot, vj, 1, vi, 1);
            }
        }

        double nrm = cblas_dnrm2((MKL_INT)n, vi, 1);
        if (nrm * nrm <= tiny) {
            for (int64_t t = 0; t < n; ++t) vi[t] = 0.0;
            continue;
        }
        cblas_dscal((MKL_INT)n, 1.0 / nrm, vi, 1);
    }

    return 0;
}
