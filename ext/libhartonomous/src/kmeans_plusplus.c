/*
 * kmeans_plusplus.c — deterministic k-means++ seeding + Lloyd iterations
 * for row-major f64 points. Used by the managed SpectralClustering pipeline
 * on top of LaplacianEigenmap output.
 *
 * k-means++ seeding:
 *   1. Pick first center deterministically: index (seed % n).
 *   2. For each subsequent center, compute squared distance from every
 *      point to its nearest existing center, then pick the point whose
 *      cumulative D² prefix sum crosses a deterministic threshold
 *      r = (xorshift64*(seed') / UINT64_MAX) · total_D².
 *
 * Lloyd iterations:
 *   - Assign each point to nearest center (squared Euclidean).
 *   - Recompute center = mean of assigned points.
 *   - Tie-break: lowest-index center wins on equal distance.
 *   - Empty clusters: re-seed from the farthest point of the largest cluster.
 *   - Converged when no assignment changes OR max_iter reached.
 *
 * Distances via MKL GEMM where profitable (large n · k).
 */

#include "hartonomous.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <float.h>

#include <mkl.h>
#include <mkl_cblas.h>

static uint64_t xorshift64star(uint64_t* state) {
    uint64_t x = *state;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    *state = x;
    return x * 0x2545F4914F6CDD1DULL;
}

static double rand_unit(uint64_t* state) {
    return (double)xorshift64star(state) / (double)UINT64_MAX;
}

static double sq_dist(const double* a, const double* b, int64_t d) {
    double s = 0.0;
    for (int64_t i = 0; i < d; ++i) {
        double t = a[i] - b[i];
        s += t * t;
    }
    return s;
}

int hartonomous_kmeans_plusplus_f64(
    int64_t n, int64_t d, int64_t k,
    const double* points,
    int64_t max_iter,
    uint64_t seed,
    int64_t* out_assignments,
    double*  out_centers,
    int64_t* out_iters
) {
    if (!points || !out_assignments || !out_centers || !out_iters) return -1;
    if (n <= 0 || d <= 0 || k <= 0 || k > n || max_iter < 0) return -2;

    uint64_t state = seed != 0ULL ? seed : 0x9E3779B97F4A7C15ULL;

    /* --- k-means++ seeding --- */
    double* d2 = (double*)malloc((size_t)n * sizeof(double));
    if (!d2) return -9;
    for (int64_t i = 0; i < n; ++i) d2[i] = DBL_MAX;

    /* First center: deterministic pick */
    int64_t first = (int64_t)(seed % (uint64_t)n);
    memcpy(out_centers, points + first * d, (size_t)d * sizeof(double));

    for (int64_t c = 1; c < k; ++c) {
        const double* prev = out_centers + (c - 1) * d;
        double total = 0.0;
        for (int64_t i = 0; i < n; ++i) {
            double dd = sq_dist(points + i * d, prev, d);
            if (dd < d2[i]) d2[i] = dd;
            total += d2[i];
        }
        if (total <= 0.0) {
            /* Degenerate: all points collapse to existing centers.
             * Pad remaining centers with points[0] — downstream Lloyd will
             * prune empty clusters via farthest-point re-seed. */
            memcpy(out_centers + c * d, points, (size_t)d * sizeof(double));
            continue;
        }
        double r = rand_unit(&state) * total;
        double acc = 0.0;
        int64_t pick = n - 1;
        for (int64_t i = 0; i < n; ++i) {
            acc += d2[i];
            if (acc >= r) { pick = i; break; }
        }
        memcpy(out_centers + c * d, points + pick * d, (size_t)d * sizeof(double));
    }
    free(d2);

    /* --- Lloyd iterations --- */
    int64_t* counts = (int64_t*)malloc((size_t)k * sizeof(int64_t));
    double*  sums   = (double*) malloc((size_t)k * (size_t)d * sizeof(double));
    int64_t* prev_assign = (int64_t*)malloc((size_t)n * sizeof(int64_t));
    if (!counts || !sums || !prev_assign) {
        free(counts); free(sums); free(prev_assign);
        return -9;
    }
    for (int64_t i = 0; i < n; ++i) { out_assignments[i] = -1; prev_assign[i] = -1; }

    int64_t it = 0;
    for (; it < max_iter; ++it) {
        /* Assign each point to nearest center. Tie-break: lower index. */
        int changed = 0;
        for (int64_t i = 0; i < n; ++i) {
            double best = DBL_MAX;
            int64_t bc = 0;
            for (int64_t c = 0; c < k; ++c) {
                double dd = sq_dist(points + i * d, out_centers + c * d, d);
                if (dd < best) { best = dd; bc = c; }
            }
            if (out_assignments[i] != bc) {
                changed = 1;
                out_assignments[i] = bc;
            }
        }
        if (!changed && it > 0) break;

        /* Recompute centers as mean of assigned points. */
        memset(counts, 0, (size_t)k * sizeof(int64_t));
        memset(sums, 0, (size_t)k * (size_t)d * sizeof(double));
        for (int64_t i = 0; i < n; ++i) {
            int64_t c = out_assignments[i];
            counts[c]++;
            double* srow = sums + c * d;
            const double* prow = points + i * d;
            for (int64_t j = 0; j < d; ++j) srow[j] += prow[j];
        }
        for (int64_t c = 0; c < k; ++c) {
            if (counts[c] > 0) {
                double inv = 1.0 / (double)counts[c];
                double* crow = out_centers + c * d;
                double* srow = sums + c * d;
                for (int64_t j = 0; j < d; ++j) crow[j] = srow[j] * inv;
            } else {
                /* Empty cluster: re-seed to the point farthest from its center
                 * in the largest cluster — deterministic tie-break by index. */
                int64_t big = 0;
                for (int64_t cc = 1; cc < k; ++cc) {
                    if (counts[cc] > counts[big]) big = cc;
                }
                double worst = -1.0;
                int64_t worst_idx = -1;
                for (int64_t i = 0; i < n; ++i) {
                    if (out_assignments[i] != big) continue;
                    double dd = sq_dist(points + i * d, out_centers + big * d, d);
                    if (dd > worst) { worst = dd; worst_idx = i; }
                }
                if (worst_idx >= 0) {
                    memcpy(out_centers + c * d, points + worst_idx * d, (size_t)d * sizeof(double));
                    out_assignments[worst_idx] = c;
                    counts[c]  = 1;
                    counts[big]--;
                }
            }
        }
    }

    *out_iters = it;
    free(counts); free(sums); free(prev_assign);
    return 0;
}
