#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>

#include "hartonomous.h"

/* GEMM is in gemm.c — declared in hartonomous.h. */

typedef struct {
    double s;
    int64_t j;
} kn_t;

/* Min-heap by similarity (ties broken by larger col index → so the row with
 * the SMALLER col index is preferred when popped: the smaller col index
 * stays in the heap longer). For the public ordering inside a row, we
 * post-sort the surviving k entries by (s desc, j asc) for determinism. */
static void heap_sift_up(kn_t* h, int64_t i) {
    while (i > 0) {
        int64_t p = (i - 1) / 2;
        if (h[p].s < h[i].s) break;
        if (h[p].s == h[i].s && h[p].j >= h[i].j) break;
        kn_t tmp = h[i]; h[i] = h[p]; h[p] = tmp;
        i = p;
    }
}

static void heap_sift_down(kn_t* h, int64_t n, int64_t i) {
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

/* Open-addressing hash set keyed by lo*n + hi (n is the row count). */
typedef struct {
    int64_t* keys;
    double* vals;
    uint64_t cap;
    uint64_t mask;
} pair_table_t;

static int pair_table_init(pair_table_t* t, uint64_t want) {
    uint64_t p = 16;
    while (p < want) p <<= 1;
    t->cap = p;
    t->mask = p - 1;
    t->keys = (int64_t*)malloc((size_t)p * sizeof(int64_t));
    t->vals = (double*)malloc((size_t)p * sizeof(double));
    if (!t->keys || !t->vals) {
        free(t->keys); free(t->vals);
        t->keys = NULL; t->vals = NULL;
        return -1;
    }
    for (uint64_t i = 0; i < p; ++i) t->keys[i] = -1;
    return 0;
}

static void pair_table_free(pair_table_t* t) {
    free(t->keys); free(t->vals);
    t->keys = NULL; t->vals = NULL;
}

static uint64_t splitmix64(uint64_t x) {
    x += 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
    return x ^ (x >> 31);
}

static void pair_table_insert(pair_table_t* t, int64_t key, double val) {
    uint64_t h = splitmix64((uint64_t)key) & t->mask;
    while (t->keys[h] != -1) {
        if (t->keys[h] == key) return;
        h = (h + 1) & t->mask;
    }
    t->keys[h] = key;
    t->vals[h] = val;
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
    double* sim = (double*)malloc((size_t)CHUNK * (size_t)n * sizeof(double));
    kn_t* heap = (kn_t*)malloc((size_t)k * sizeof(kn_t));
    if (!sim || !heap) { free(sim); free(heap); return -9; }

    pair_table_t pairs;
    if (pair_table_init(&pairs, (uint64_t)(n * k * 4)) != 0) {
        free(sim); free(heap); return -9;
    }

    for (int64_t i0 = 0; i0 < n; i0 += CHUNK) {
        int64_t bs = (i0 + CHUNK > n) ? (n - i0) : CHUNK;
        int rc = hartonomous_gemm_f64(
            0, 1,
            bs, n, d,
            1.0,
            rows_normalized + i0 * d, d,
            rows_normalized, d,
            0.0,
            sim, n
        );
        if (rc != 0) {
            pair_table_free(&pairs); free(sim); free(heap);
            return rc;
        }

        for (int64_t r = 0; r < bs; ++r) {
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
            for (int64_t t = 0; t < hsize; ++t) {
                int64_t j = heap[t].j;
                double w = heap[t].s;
                if (w < 0.0) w = 0.0;
                if (w > 1.0) w = 1.0;
                int64_t lo = (row_i < j) ? row_i : j;
                int64_t hi = (row_i < j) ? j : row_i;
                int64_t key = lo * n + hi;
                pair_table_insert(&pairs, key, w);
            }
        }
    }

    free(sim); free(heap);

    for (int64_t i = 0; i <= n; ++i) out_row_ptr[i] = 0;
    for (uint64_t s = 0; s < pairs.cap; ++s) {
        if (pairs.keys[s] == -1) continue;
        int64_t key = pairs.keys[s];
        int64_t lo = key / n;
        int64_t hi = key % n;
        out_row_ptr[lo + 1]++;
        out_row_ptr[hi + 1]++;
    }
    for (int64_t i = 1; i <= n; ++i) out_row_ptr[i] += out_row_ptr[i - 1];

    int64_t total = out_row_ptr[n];
    int64_t* cursor = (int64_t*)malloc((size_t)n * sizeof(int64_t));
    if (!cursor) { pair_table_free(&pairs); return -9; }
    for (int64_t i = 0; i < n; ++i) cursor[i] = out_row_ptr[i];

    for (uint64_t s = 0; s < pairs.cap; ++s) {
        if (pairs.keys[s] == -1) continue;
        int64_t key = pairs.keys[s];
        int64_t lo = key / n;
        int64_t hi = key % n;
        double w = pairs.vals[s];
        int64_t pos;
        pos = cursor[lo]++; out_col_idx[pos] = hi; out_values[pos] = w;
        pos = cursor[hi]++; out_col_idx[pos] = lo; out_values[pos] = w;
    }
    free(cursor);
    pair_table_free(&pairs);

    /* Sort each row by col_idx ascending (degree per row is small ≤ 2k). */
    for (int64_t i = 0; i < n; ++i) {
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
