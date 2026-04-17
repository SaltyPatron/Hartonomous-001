#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <omp.h>

#include "hartonomous.h"

/*
 * Symmetric cosine-affinity k-NN graph over L2-normalized rows.
 *
 * Strategy:
 *   1. Per-row top-k selection (the expensive step) is done via a chunked
 *      MKL-backed GEMM producing similarity blocks, then a per-row partial
 *      sort. Both the GEMM (MKL internal threads) and the per-row heap pass
 *      (OpenMP across rows of the chunk) are parallel.
 *   2. Symmetrization is a serial dedup pass that walks per-row top-k lists
 *      and emits each undirected edge once into the output CSR.
 *
 * Determinism: GEMM reduction order is pinned by CBWR=AUTO,STRICT (set in
 * gemm.c). Per-row heap selection is a pure function of the row's similarity
 * vector with deterministic tie-break (similarity descending, column index
 * ascending). Cross-row parallelism does not affect output ordering because
 * each row writes only to its own slot in the topk array.
 */

typedef struct {
    double s;
    int64_t j;
} kn_t;

/* Min-heap keyed by (s asc, j desc) so the weakest neighbour (smallest s, or
 * on ties the LARGER col index) is at the top and gets evicted first. That way
 * after all candidates are processed the heap contains the top-k strongest,
 * and ties are resolved in favour of the SMALLER column index. */
static inline void heap_sift_up(kn_t* h, int64_t i) {
    while (i > 0) {
        int64_t p = (i - 1) / 2;
        if (h[p].s < h[i].s) break;
        if (h[p].s == h[i].s && h[p].j >= h[i].j) break;
        kn_t tmp = h[i]; h[i] = h[p]; h[p] = tmp;
        i = p;
    }
}

static inline void heap_sift_down(kn_t* h, int64_t n, int64_t i) {
    while (1) {
        int64_t l = 2 * i + 1, r = 2 * i + 2, m = i;
        if (l < n) {
            if (h[l].s < h[m].s) m = l;
            else if (h[l].s == h[m].s && h[l].j > h[m].j) m = l;
        }
        if (r < n) {
            if (h[r].s < h[m].s) m = r;
            else if (h[r].s == h[m].s && h[r].j > h[m].j) m = r;
        }
        if (m == i) break;
        kn_t tmp = h[i]; h[i] = h[m]; h[m] = tmp;
        i = m;
    }
}

static int compare_kn_desc(const void* a, const void* b) {
    const kn_t* x = (const kn_t*)a;
    const kn_t* y = (const kn_t*)b;
    if (x->s > y->s) return -1;
    if (x->s < y->s) return 1;
    if (x->j < y->j) return -1;
    if (x->j > y->j) return 1;
    return 0;
}

int hartonomous_knn_cosine_graph_f64(
    int64_t n, int64_t d,
    const double* rows_normalized,
    int64_t k,
    int64_t* out_row_ptr,
    int64_t* out_col_idx,
    double*  out_values,
    int64_t* out_nnz
) {
    if (rows_normalized == NULL || out_row_ptr == NULL || out_col_idx == NULL ||
        out_values == NULL || out_nnz == NULL) return -1;
    if (n <= 0 || d <= 0 || k <= 0 || k >= n) return -2;

    const int64_t CHUNK = 64;

    /* Per-row top-k storage: n rows × k neighbours. Written by the parallel
     * chunk loop, read serially in the symmetrization pass. */
    kn_t* topk = (kn_t*)malloc((size_t)n * (size_t)k * sizeof(kn_t));
    if (!topk) return -9;
    for (int64_t i = 0; i < n * k; ++i) {
        topk[i].s = -2.0;  /* sentinel < -1 so any real similarity wins */
        topk[i].j = -1;
    }

    /* One similarity-block buffer per thread so the inner loop is thread-local. */
    int max_threads = omp_get_max_threads();
    double* sim_all = (double*)malloc((size_t)max_threads * (size_t)CHUNK * (size_t)n * sizeof(double));
    kn_t* heap_all = (kn_t*)malloc((size_t)max_threads * (size_t)k * sizeof(kn_t));
    if (!sim_all || !heap_all) {
        free(sim_all); free(heap_all); free(topk);
        return -9;
    }

    int outer_rc = 0;

    /* Outer chunk loop. Each iteration runs a multi-threaded MKL GEMM on the
     * current (bs × d) × (d × n)T block, then parallelizes the per-row heap
     * reduction across rows of the block. */
    for (int64_t i0 = 0; i0 < n; i0 += CHUNK) {
        int64_t bs = (i0 + CHUNK > n) ? (n - i0) : CHUNK;
        /* Use thread 0's sim slab for the GEMM output; safe because GEMM
         * executes before the parallel region below. */
        double* sim = sim_all;
        int rc = hartonomous_gemm_f64(
            0, 1,
            bs, n, d,
            1.0,
            rows_normalized + i0 * d, d,
            rows_normalized, d,
            0.0,
            sim, n
        );
        if (rc != 0) { outer_rc = rc; break; }

        int64_t r;
        #pragma omp parallel for schedule(static) private(r)
        for (r = 0; r < bs; ++r) {
            int tid = omp_get_thread_num();
            kn_t* heap = heap_all + (size_t)tid * (size_t)k;
            int64_t row_i = i0 + r;
            const double* srow = sim + r * n;

            int64_t hsize = 0;
            for (int64_t j = 0; j < n; ++j) {
                if (j == row_i) continue;
                double s = srow[j];
                if (hsize < k) {
                    heap[hsize].s = s;
                    heap[hsize].j = j;
                    hsize++;
                    heap_sift_up(heap, hsize - 1);
                } else {
                    int better = 0;
                    if (s > heap[0].s) better = 1;
                    else if (s == heap[0].s && j < heap[0].j) better = 1;
                    if (better) {
                        heap[0].s = s;
                        heap[0].j = j;
                        heap_sift_down(heap, hsize, 0);
                    }
                }
            }

            /* Clamp to [0, 1] and stable-sort for deterministic output. */
            for (int64_t t = 0; t < hsize; ++t) {
                double w = heap[t].s;
                if (w < 0.0) w = 0.0;
                if (w > 1.0) w = 1.0;
                heap[t].s = w;
            }
            qsort(heap, (size_t)hsize, sizeof(kn_t), compare_kn_desc);
            memcpy(topk + (size_t)row_i * (size_t)k, heap, (size_t)hsize * sizeof(kn_t));
        }
    }

    free(sim_all);
    free(heap_all);

    if (outer_rc != 0) { free(topk); return outer_rc; }

    /* Symmetrize: emit every undirected edge once. Use a linear scan over
     * per-row top-k with a "seen" check: edge (lo,hi) is emitted only when
     * encountered from row lo. For rows i < j where j ∈ top-k(i), the edge
     * is emitted; rows where i > j rely on row j's scan picking it up or on
     * the mirror pass below.
     *
     * To avoid missing asymmetric pairs (j ∈ top-k(i) but i ∉ top-k(j)) we
     * collect all (min(i,j), max(i,j), w) tuples into a dedup hash, then
     * expand to CSR.
     */
    const uint64_t pair_cap_want = (uint64_t)n * (uint64_t)k * 4;
    uint64_t cap = 16;
    while (cap < pair_cap_want) cap <<= 1;
    uint64_t mask = cap - 1;
    int64_t* pkeys = (int64_t*)malloc((size_t)cap * sizeof(int64_t));
    double* pvals = (double*)malloc((size_t)cap * sizeof(double));
    if (!pkeys || !pvals) { free(pkeys); free(pvals); free(topk); return -9; }
    for (uint64_t i = 0; i < cap; ++i) pkeys[i] = -1;

    for (int64_t i = 0; i < n; ++i) {
        const kn_t* row = topk + (size_t)i * (size_t)k;
        for (int64_t t = 0; t < k; ++t) {
            int64_t j = row[t].j;
            if (j < 0) continue;
            int64_t lo = (i < j) ? i : j;
            int64_t hi = (i < j) ? j : i;
            int64_t key = lo * n + hi;
            uint64_t h = (uint64_t)key;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDULL;
            h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53ULL;
            h ^= h >> 33;
            h &= mask;
            while (pkeys[h] != -1) {
                if (pkeys[h] == key) break;
                h = (h + 1) & mask;
            }
            if (pkeys[h] == -1) {
                pkeys[h] = key;
                pvals[h] = row[t].s;
            }
        }
    }

    for (int64_t i = 0; i <= n; ++i) out_row_ptr[i] = 0;
    for (uint64_t s = 0; s < cap; ++s) {
        if (pkeys[s] == -1) continue;
        int64_t key = pkeys[s];
        int64_t lo = key / n;
        int64_t hi = key % n;
        out_row_ptr[lo + 1]++;
        out_row_ptr[hi + 1]++;
    }
    for (int64_t i = 1; i <= n; ++i) out_row_ptr[i] += out_row_ptr[i - 1];

    int64_t total = out_row_ptr[n];
    int64_t* cursor = (int64_t*)malloc((size_t)n * sizeof(int64_t));
    if (!cursor) { free(pkeys); free(pvals); free(topk); return -9; }
    for (int64_t i = 0; i < n; ++i) cursor[i] = out_row_ptr[i];

    for (uint64_t s = 0; s < cap; ++s) {
        if (pkeys[s] == -1) continue;
        int64_t key = pkeys[s];
        int64_t lo = key / n;
        int64_t hi = key % n;
        double w = pvals[s];
        int64_t pos;
        pos = cursor[lo]++; out_col_idx[pos] = hi; out_values[pos] = w;
        pos = cursor[hi]++; out_col_idx[pos] = lo; out_values[pos] = w;
    }
    free(cursor);
    free(pkeys);
    free(pvals);
    free(topk);

    /* Sort each row by col_idx ascending. */
    int64_t isort;
    #pragma omp parallel for schedule(static) private(isort)
    for (isort = 0; isort < n; ++isort) {
        int64_t i = isort;
        int64_t a = out_row_ptr[i], b = out_row_ptr[i + 1];
        for (int64_t x = a + 1; x < b; ++x) {
            int64_t cv = out_col_idx[x];
            double wv = out_values[x];
            int64_t y = x - 1;
            while (y >= a && out_col_idx[y] > cv) {
                out_col_idx[y + 1] = out_col_idx[y];
                out_values[y + 1] = out_values[y];
                y--;
            }
            out_col_idx[y + 1] = cv;
            out_values[y + 1] = wv;
        }
    }

    *out_nnz = total;
    return 0;
}
