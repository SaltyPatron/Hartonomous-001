#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <mkl.h>
#include <mkl_cblas.h>

#include "hartonomous.h"

/*
 * Symmetric sparse Lanczos eigensolver — top-k Ritz pairs with full
 * re-orthogonalization.
 *
 * API convention: caller stores the **upper triangle (including diagonal)** of
 * a symmetric matrix in CSR (row_ptr, col_idx, values). Callers that produce
 * full symmetric CSR (e.g. hartonomous_knn_cosine_graph_f64) must drop the
 * j < i entries before passing here.
 *
 * Heavy ops:
 *   - CSR matvec: hand-rolled symmetric-upper-CSR kernel below. Does NOT use
 *     the MKL sparse inspector-executor API (mkl_sparse_*) — that API has
 *     documented crashes on Windows with 64-bit indices in oneMKL 2025.x and
 *     in processes hosting MKL through a late-load / multi-OMP path (e.g.
 *     .NET testhost.exe). The kernel iterates upper entries, fuses the
 *     symmetric transpose contribution inline, and pins reduction order to
 *     (row_ptr, col_idx) for Law #6 determinism.
 *   - dot/axpy/nrm via cblas_ddot/daxpy/dnrm2 — proven reliable through the
 *     dense BLAS DLLs (mkl_intel_lp64/mkl_intel_thread/mkl_core).
 * CBWR=AUTO,STRICT is set globally in gemm.c; dense BLAS output is thus
 * bit-reproducible across runs in the same ISA class.
 */

/*
 * Symmetric upper-CSR matrix-vector multiply: y = A * x where A is symmetric
 * and (row_ptr, col_idx, values) stores only the upper triangle (j >= i). For
 * each stored (i, j, v):
 *   - diagonal (j == i): y[i] += v * x[i]
 *   - off-diag (j >  i): y[i] += v * x[j]   (upper contribution)
 *                        y[j] += v * x[i]   (symmetric transpose contribution)
 */
static void sym_upper_csr_mv_f64(
    int64_t n,
    const int64_t* row_ptr,
    const int64_t* col_idx,
    const double*  values,
    const double*  x,
    double* y
) {
    memset(y, 0, (size_t)n * sizeof(double));
    for (int64_t i = 0; i < n; ++i) {
        double yi = 0.0;
        const double xi = x[i];
        const int64_t r0 = row_ptr[i];
        const int64_t r1 = row_ptr[i + 1];
        for (int64_t p = r0; p < r1; ++p) {
            const int64_t j = col_idx[p];
            const double  v = values[p];
            if (j == i) {
                yi += v * xi;
            } else {
                yi   += v * x[j];
                y[j] += v * xi;
            }
        }
        y[i] += yi;
    }
}

static uint64_t xs64(uint64_t* state) {
    uint64_t x = *state;
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    *state = x;
    return x;
}

static double rand_unit(uint64_t* state) {
    uint64_t r = xs64(state) >> 11;
    double u = (double)r / (double)(1ULL << 53);
    return 2.0 * u - 1.0;
}

/* Symmetric Jacobi eigendecomposition for the small tridiagonal projection.
 * a (in/out) — n×n row-major symmetric. On exit: diagonal holds eigenvalues.
 * v (out)    — n×n row-major; columns are eigenvectors.
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
    for (int64_t i = 0; i < n; ++i) V[i] = rand_unit(&state);
    double norm0 = cblas_dnrm2((MKL_INT)n, V, 1);
    if (norm0 == 0.0) {
        free(V); free(alpha); free(beta); free(w);
        return -6;
    }
    cblas_dscal((MKL_INT)n, 1.0 / norm0, V, 1);

    int64_t actual = m;
    for (int64_t j = 0; j < m; ++j) {
        double* vj = V + j * n;

        /* w = A · vj  (hand-rolled symmetric upper-CSR kernel) */
        sym_upper_csr_mv_f64(n, row_ptr, col_idx, values, vj, w);

        /* w -= beta_j · v_{j-1} */
        if (j > 0) {
            double* vjm1 = V + (j - 1) * n;
            cblas_daxpy((MKL_INT)n, -beta[j], vjm1, 1, w, 1);
        }

        /* alpha_j = <w, vj>; w -= alpha_j · vj */
        double a = cblas_ddot((MKL_INT)n, w, 1, vj, 1);
        alpha[j] = a;
        cblas_daxpy((MKL_INT)n, -a, vj, 1, w, 1);

        /* Full re-orthogonalization against V[0..j]. */
        for (int64_t s = 0; s <= j; ++s) {
            double* vs = V + s * n;
            double dot = cblas_ddot((MKL_INT)n, w, 1, vs, 1);
            if (dot != 0.0) {
                cblas_daxpy((MKL_INT)n, -dot, vs, 1, w, 1);
            }
        }

        double bnorm = cblas_dnrm2((MKL_INT)n, w, 1);
        beta[j + 1] = bnorm;

        if (bnorm < 1e-12) {
            actual = j + 1;
            break;
        }
        if (j + 1 < m) {
            double* vjp1 = V + (j + 1) * n;
            cblas_dcopy((MKL_INT)n, w, 1, vjp1, 1);
            cblas_dscal((MKL_INT)n, 1.0 / bnorm, vjp1, 1);
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
        /* eigenvec[e] = V^T · S[:,col], shape (n,).  V is (m × n) row-major;
         * so V^T is (n × m). cblas_dgemv treats V as (m × n) row-major and
         * with CblasTrans gives y = V^T · x = sum_r V[r,:] * S[r,col]. */
        double* Svec = (double*)malloc((size_t)mm * sizeof(double));
        if (!Svec) {
            free(order); free(V); free(alpha); free(beta); free(w); free(T); free(S);
            return -9;
        }
        for (int64_t r = 0; r < mm; ++r) Svec[r] = S[r * mm + col];
        cblas_dgemv(
            CblasRowMajor, CblasTrans,
            (MKL_INT)mm, (MKL_INT)n,
            1.0, V, (MKL_INT)n,
            Svec, 1,
            0.0, eigenvectors + e * n, 1);
        free(Svec);
    }
    for (int64_t e = want; e < k; ++e) {
        eigenvalues[e] = 0.0;
        memset(eigenvectors + e * n, 0, (size_t)n * sizeof(double));
    }

    free(order); free(V); free(alpha); free(beta); free(w); free(T); free(S);
    return (want < k) ? -6 : 0;
}
