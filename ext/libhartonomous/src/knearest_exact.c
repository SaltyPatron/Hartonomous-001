/*
 * knearest_exact.c — exact per-query k-nearest-neighbour search in
 * Euclidean (L2) space, f64, row-major. For each of nq queries in R^d,
 * returns the k nearest corpus points by squared-distance ascending.
 *
 * Algorithm:
 *     ||q - c||² = ||q||² + ||c||² − 2·⟨q, c⟩
 * Compute the cross term C = Q · C^T via MKL GEMM, subtract per-row q-norms
 * and per-column c-norms, then per-query partial heap select top-k (min-heap
 * sized to k, evict weakest when a better candidate arrives).
 *
 * Determinism: MKL GEMM under CBWR=AUTO,STRICT has fixed reduction order.
 * Per-query heap has deterministic tie-break (distance ascending, corpus
 * index ascending).
 *
 * This complements the cosine-similarity graph API (knn.c): this one takes
 * arbitrary unnormalized vectors and supports queries ≠ corpus.
 */

#include "hartonomous.h"

#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>

#include <mkl.h>

typedef struct {
    double d2;   /* squared distance */
    int64_t idx; /* corpus index */
} nb_t;

/* Max-heap keyed by (d2 desc, idx desc) so the WORST candidate (largest
 * distance, or on ties the larger index) is at the root and gets evicted. */
static inline int worse(const nb_t a, const nb_t b) {
    if (a.d2 != b.d2) return a.d2 > b.d2;
    return a.idx > b.idx;
}

static inline void sift_up(nb_t* h, int64_t i) {
    while (i > 0) {
        int64_t p = (i - 1) / 2;
        if (!worse(h[i], h[p])) break;
        nb_t t = h[i]; h[i] = h[p]; h[p] = t;
        i = p;
    }
}

static inline void sift_down(nb_t* h, int64_t n, int64_t i) {
    while (1) {
        int64_t l = 2 * i + 1, r = 2 * i + 2, m = i;
        if (l < n && worse(h[l], h[m])) m = l;
        if (r < n && worse(h[r], h[m])) m = r;
        if (m == i) break;
        nb_t t = h[i]; h[i] = h[m]; h[m] = t;
        i = m;
    }
}

static int mul_size_overflows(int64_t a, int64_t b, size_t elem_size) {
    if (a < 0 || b < 0) return 1;
    if ((uint64_t)a > SIZE_MAX / (uint64_t)b) return 1;
    uint64_t ab = (uint64_t)a * (uint64_t)b;
    return ab > SIZE_MAX / elem_size;
}

int hartonomous_knearest_exact_f64(
    int64_t nq, int64_t nc, int64_t d,
    const double* queries,
    const double* corpus,
    int64_t k,
    int64_t* out_indices,
    double* out_distances
) {
    if (queries == NULL || corpus == NULL || out_indices == NULL || out_distances == NULL) {
        return -1;
    }
    if (nq <= 0 || nc <= 0 || d <= 0 || k <= 0 || k > nc) {
        return -2;
    }
    if (mul_size_overflows(nc, d, sizeof(double)) ||
        mul_size_overflows(nq, d, sizeof(double)) ||
        mul_size_overflows(nq, k, sizeof(int64_t)) ||
        mul_size_overflows(nq, k, sizeof(double))) {
        return -3;
    }

    /* Precompute squared row norms for corpus and queries. */
    double* cn2 = (double*)mkl_malloc((size_t)nc * sizeof(double), 64);
    double* qn2 = (double*)mkl_malloc((size_t)nq * sizeof(double), 64);
    if (cn2 == NULL || qn2 == NULL) {
        if (cn2 != NULL) { mkl_free(cn2); }
        if (qn2 != NULL) { mkl_free(qn2); }
        return -9;
    }
    for (int64_t i = 0; i < nc; ++i) {
        double s = 0.0;
        const double* row = corpus + i * d;
        for (int64_t t = 0; t < d; ++t) { s += row[t] * row[t]; }
        cn2[i] = s;
    }
    for (int64_t i = 0; i < nq; ++i) {
        double s = 0.0;
        const double* row = queries + i * d;
        for (int64_t t = 0; t < d; ++t) { s += row[t] * row[t]; }
        qn2[i] = s;
    }

    const int64_t max_block_bytes = 256LL * 1024LL * 1024LL;
    int64_t q_chunk = max_block_bytes / ((int64_t)sizeof(double) * nc);
    if (q_chunk < 1) q_chunk = 1;
    if (q_chunk > nq) q_chunk = nq;

    double* g = (double*)mkl_malloc((size_t)q_chunk * (size_t)nc * sizeof(double), 64);
    nb_t* heap = (nb_t*)mkl_malloc((size_t)k * sizeof(nb_t), 64);
    if (g == NULL) {
        if (heap != NULL) { mkl_free(heap); }
        mkl_free(cn2); mkl_free(qn2);
        return -9;
    }
    if (heap == NULL) {
        mkl_free(g); mkl_free(cn2); mkl_free(qn2);
        return -9;
    }

    for (int64_t q0 = 0; q0 < nq; q0 += q_chunk) {
        int64_t bs = (q0 + q_chunk > nq) ? (nq - q0) : q_chunk;
        int gemm_rc = hartonomous_gemm_f64(
            0, 1,
            bs, nc, d,
            1.0,
            queries + q0 * d, d,
            corpus, d,
            0.0,
            g, nc);
        if (gemm_rc != 0) {
            mkl_free(heap); mkl_free(g); mkl_free(cn2); mkl_free(qn2);
            return gemm_rc;
        }

        for (int64_t qr = 0; qr < bs; ++qr) {
            int64_t q = q0 + qr;
            const double* gq = g + qr * nc;
            double qnorm = qn2[q];
            /* Build heap of first k candidates. */
            int64_t h_size = 0;
            for (int64_t c = 0; c < nc; ++c) {
                double d2 = qnorm + cn2[c] - 2.0 * gq[c];
                /* Numerical floor: self-matches can produce tiny negatives. */
                if (d2 < 0.0) { d2 = 0.0; }
                nb_t cand = { d2, c };
                if (h_size < k) {
                    heap[h_size] = cand;
                    sift_up(heap, h_size);
                    h_size++;
                } else if (worse(heap[0], cand)) {
                    heap[0] = cand;
                    sift_down(heap, h_size, 0);
                }
            }
            /* Extract into the output sorted ascending. */
            int64_t count = h_size;
            /* heap sort: repeatedly swap root with last, shrink, sift. */
            for (int64_t e = count - 1; e > 0; --e) {
                nb_t t = heap[0]; heap[0] = heap[e]; heap[e] = t;
                sift_down(heap, e, 0);
            }
            /* After that inverse heap-sort, heap[0..count-1] is ascending by
             * (d2, idx). Copy out. */
            int64_t* oi = out_indices + q * k;
            double* od = out_distances + q * k;
            for (int64_t e = 0; e < count; ++e) {
                oi[e] = heap[e].idx;
                od[e] = heap[e].d2;
            }
        }
    }

    mkl_free(heap);
    mkl_free(g);
    mkl_free(cn2);
    mkl_free(qn2);
    return 0;
}
