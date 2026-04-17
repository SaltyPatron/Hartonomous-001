#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "hartonomous.h"

/* xorshift64 PRNG. State must be non-zero. */
static uint64_t xs64(uint64_t* state) {
    uint64_t x = *state;
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    *state = x;
    return x;
}

/* Uniform double in [-1, 1]. */
static double rand_unit(uint64_t* state) {
    uint64_t r = xs64(state) >> 11;
    double u = (double)r / (double)(1ULL << 53);
    return 2.0 * u - 1.0;
}

/* y = A*x for symmetric CSR A. */
static void csr_matvec(
    int64_t n,
    const int64_t* row_ptr,
    const int64_t* col_idx,
    const double* values,
    const double* x,
    double* y
) {
    for (int64_t i = 0; i < n; ++i) {
        double s = 0.0;
        int64_t a = row_ptr[i], b = row_ptr[i + 1];
        for (int64_t p = a; p < b; ++p) {
            s += values[p] * x[col_idx[p]];
        }
        y[i] = s;
    }
}

/* Symmetric Jacobi eigendecomposition for small dense matrices.
 * a (in/out) — n×n row-major symmetric. On exit: diagonal holds eigenvalues,
 *   off-diagonal entries are ~0.
 * v (out)    — n×n row-major; columns are eigenvectors.
 * Sweep order is fixed (p ascending, then q ascending) for determinism.
 */
static int jacobi_eig(int64_t n, double* a, double* v) {
    for (int64_t i = 0; i < n; ++i)
        for (int64_t j = 0; j < n; ++j)
            v[i * n + j] = (i == j) ? 1.0 : 0.0;

    const int max_sweeps = 100;
    const double conv_tol = 1e-28;

    for (int sw = 0; sw < max_sweeps; ++sw) {
        double off = 0.0;
        for (int64_t i = 0; i < n; ++i)
            for (int64_t j = i + 1; j < n; ++j)
                off += a[i * n + j] * a[i * n + j];
        if (off < conv_tol) return 0;

        for (int64_t p = 0; p < n - 1; ++p) {
            for (int64_t q = p + 1; q < n; ++q) {
                double apq = a[p * n + q];
                if (apq == 0.0) continue;
                double app = a[p * n + p];
                double aqq = a[q * n + q];

                double theta = (aqq - app) / (2.0 * apq);
                double t;
                if (fabs(theta) > 1e15) {
                    t = 0.5 / theta;
                } else {
                    double sgn = (theta >= 0.0) ? 1.0 : -1.0;
                    t = sgn / (fabs(theta) + sqrt(theta * theta + 1.0));
                }
                double c = 1.0 / sqrt(1.0 + t * t);
                double s = t * c;

                double new_app = c * c * app - 2.0 * c * s * apq + s * s * aqq;
                double new_aqq = s * s * app + 2.0 * c * s * apq + c * c * aqq;
                a[p * n + p] = new_app;
                a[q * n + q] = new_aqq;
                a[p * n + q] = 0.0;
                a[q * n + p] = 0.0;

                for (int64_t r = 0; r < n; ++r) {
                    if (r != p && r != q) {
                        double arp = a[r * n + p];
                        double arq = a[r * n + q];
                        a[r * n + p] = c * arp - s * arq;
                        a[r * n + q] = s * arp + c * arq;
                        a[p * n + r] = a[r * n + p];
                        a[q * n + r] = a[r * n + q];
                    }
                    double vrp = v[r * n + p];
                    double vrq = v[r * n + q];
                    v[r * n + p] = c * vrp - s * vrq;
                    v[r * n + q] = s * vrp + c * vrq;
                }
            }
        }
    }
    return -6;
}

int hartonomous_sparse_sym_eigs_f64(
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
) {
    if (!row_ptr || !col_idx || !values || !eigenvalues || !eigenvectors || !out_iters)
        return -1;
    if (n <= 0 || nnz < 0 || k <= 0 || max_iter < k + 4) return -2;
    if (k >= n) return -2;

    int64_t m = max_iter;
    if (m > n) m = n;

    double* V = (double*)calloc((size_t)m * (size_t)n, sizeof(double));
    double* alpha = (double*)calloc((size_t)m, sizeof(double));
    double* beta = (double*)calloc((size_t)(m + 1), sizeof(double));
    double* w = (double*)malloc((size_t)n * sizeof(double));
    if (!V || !alpha || !beta || !w) {
        free(V); free(alpha); free(beta); free(w);
        return -9;
    }

    uint64_t state = seed ? seed : 0x9E3779B97F4A7C15ULL;
    double norm0 = 0.0;
    for (int64_t i = 0; i < n; ++i) {
        double r = rand_unit(&state);
        V[i] = r;
        norm0 += r * r;
    }
    norm0 = sqrt(norm0);
    if (norm0 == 0.0) {
        free(V); free(alpha); free(beta); free(w);
        return -6;
    }
    double inv0 = 1.0 / norm0;
    for (int64_t i = 0; i < n; ++i) V[i] *= inv0;

    int64_t actual = m;
    for (int64_t j = 0; j < m; ++j) {
        const double* vj = V + j * n;

        csr_matvec(n, row_ptr, col_idx, values, vj, w);

        if (j > 0) {
            const double* vjm1 = V + (j - 1) * n;
            double bj = beta[j];
            for (int64_t i = 0; i < n; ++i) w[i] -= bj * vjm1[i];
        }

        double a = 0.0;
        for (int64_t i = 0; i < n; ++i) a += w[i] * vj[i];
        alpha[j] = a;
        for (int64_t i = 0; i < n; ++i) w[i] -= a * vj[i];

        for (int64_t s = 0; s <= j; ++s) {
            const double* vs = V + s * n;
            double dot = 0.0;
            for (int64_t i = 0; i < n; ++i) dot += w[i] * vs[i];
            if (dot != 0.0) {
                for (int64_t i = 0; i < n; ++i) w[i] -= dot * vs[i];
            }
        }

        double bnorm = 0.0;
        for (int64_t i = 0; i < n; ++i) bnorm += w[i] * w[i];
        bnorm = sqrt(bnorm);
        beta[j + 1] = bnorm;

        if (bnorm < 1e-12) {
            actual = j + 1;
            break;
        }
        if (j + 1 < m) {
            double inv = 1.0 / bnorm;
            double* vjp1 = V + (j + 1) * n;
            for (int64_t i = 0; i < n; ++i) vjp1[i] = w[i] * inv;
        }
    }

    int64_t mm = actual;
    *out_iters = mm;

    double* T = (double*)calloc((size_t)mm * (size_t)mm, sizeof(double));
    double* S = (double*)malloc((size_t)mm * (size_t)mm * sizeof(double));
    if (!T || !S) {
        free(V); free(alpha); free(beta); free(w); free(T); free(S);
        return -9;
    }
    for (int64_t i = 0; i < mm; ++i) {
        T[i * mm + i] = alpha[i];
        if (i + 1 < mm) {
            T[i * mm + (i + 1)] = beta[i + 1];
            T[(i + 1) * mm + i] = beta[i + 1];
        }
    }

    int rc = jacobi_eig(mm, T, S);
    if (rc != 0) {
        free(V); free(alpha); free(beta); free(w); free(T); free(S);
        return rc;
    }

    int64_t* order = (int64_t*)malloc((size_t)mm * sizeof(int64_t));
    if (!order) {
        free(V); free(alpha); free(beta); free(w); free(T); free(S);
        return -9;
    }
    for (int64_t i = 0; i < mm; ++i) order[i] = i;
    /* Insertion sort by eigenvalue descending; ties broken by lower column index. */
    for (int64_t i = 1; i < mm; ++i) {
        int64_t cur = order[i];
        double cur_eig = T[cur * mm + cur];
        int64_t j = i - 1;
        while (j >= 0) {
            double prev_eig = T[order[j] * mm + order[j]];
            int swap = 0;
            if (prev_eig < cur_eig) swap = 1;
            else if (prev_eig == cur_eig && order[j] > cur) swap = 1;
            if (!swap) break;
            order[j + 1] = order[j];
            j--;
        }
        order[j + 1] = cur;
    }

    int64_t want = (k < mm) ? k : mm;
    for (int64_t e = 0; e < want; ++e) {
        int64_t col = order[e];
        eigenvalues[e] = T[col * mm + col];
        for (int64_t i = 0; i < n; ++i) {
            double s = 0.0;
            for (int64_t r = 0; r < mm; ++r) {
                s += V[r * n + i] * S[r * mm + col];
            }
            eigenvectors[e * n + i] = s;
        }
    }
    for (int64_t e = want; e < k; ++e) {
        eigenvalues[e] = 0.0;
        for (int64_t i = 0; i < n; ++i) eigenvectors[e * n + i] = 0.0;
    }

    free(order); free(V); free(alpha); free(beta); free(w); free(T); free(S);
    return (want < k) ? -6 : 0;
}
