#include <math.h>
#include <stddef.h>
#include <stdint.h>

#include "hartonomous.h"

/*
 * Modified Gram-Schmidt orthonormalization on `k` row vectors of length `n`.
 * Row stride is `ld` doubles. Operates in-place. Deterministic ordering:
 * row 0 is normalized first, row i is then orthogonalized against rows 0..i-1
 * in increasing index order, then normalized.
 *
 * If a row reduces to (numerically) zero norm during orthogonalization, it is
 * left as zeros and the next row continues — the caller can detect rank
 * deficiency by inspecting the output. We do not return an error for this
 * since for Lanczos basis vectors a zero residual means convergence, not
 * failure.
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
            double dot = 0.0;
            for (int64_t t = 0; t < n; ++t) dot += vi[t] * vj[t];
            if (dot != 0.0) {
                for (int64_t t = 0; t < n; ++t) vi[t] -= dot * vj[t];
            }
        }

        double norm2 = 0.0;
        for (int64_t t = 0; t < n; ++t) norm2 += vi[t] * vi[t];
        if (norm2 <= tiny) {
            for (int64_t t = 0; t < n; ++t) vi[t] = 0.0;
            continue;
        }
        double inv = 1.0 / sqrt(norm2);
        for (int64_t t = 0; t < n; ++t) vi[t] *= inv;
    }

    return 0;
}
