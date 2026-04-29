#include "hartonomous.h"

#include <math.h>
#include <stddef.h>
#include <string.h>

#include <omp.h>

/*
 * Euclidean centroid: arithmetic mean of N 4D points.
 *
 * Deliberately separate from `hartonomous_s3_centroid` (in s3_geometry.c)
 * which renormalizes onto S^3. This one returns the raw mean, suitable for
 * Euclidean physicality (R^4 firefly embeddings) and as the SFUNC backing
 * for the SQL `centroid_4d` aggregate.
 *
 * Sums in row-pair stride order to keep accumulation deterministic across
 * runs. Same inputs → bit-identical output (Law #6).
 */
int hartonomous_centroid_4d(const double* points, size_t point_count, double out[4])
{
    if (points == NULL || out == NULL) return -1;
    if (point_count == 0) return -1;

    double s0 = 0.0, s1 = 0.0, s2 = 0.0, s3 = 0.0;
    for (size_t i = 0; i < point_count; ++i) {
        const double* p = points + i * 4;
        s0 += p[0];
        s1 += p[1];
        s2 += p[2];
        s3 += p[3];
    }

    double inv = 1.0 / (double)point_count;
    out[0] = s0 * inv;
    out[1] = s1 * inv;
    out[2] = s2 * inv;
    out[3] = s3 * inv;
    return 0;
}

/*
 * Grouped centroid reduction: for N points labelled with group_ids in
 * [0, group_count), compute the mean per group. Streaming sink uses this
 * to emit per-composition LINESTRINGZM centroids in one FFI call instead
 * of N per-composition scalar loops.
 *
 * Single-pass accumulation; each output centroid is the arithmetic mean of
 * its group's points. Empty groups (count == 0) are zero-filled.
 *
 * Inputs:
 *   points       — packed n × 4 doubles.
 *   group_ids    — length-n array; group_ids[i] in [0, group_count).
 *   n            — point count.
 *   group_count  — number of groups.
 * Output:
 *   centroids    — caller-allocated, group_count × 4 doubles.
 *
 * Returns 0 on success, -1 on null arg, -2 on n < 0 or group_count <= 0,
 * -3 if any group_ids[i] is out of [0, group_count).
 *
 * Determinism: same inputs → bit-identical output (Law #6). Sums in
 * input-order; no parallel reduction across groups (each group's sum is
 * sequential, but groups are independent so the per-group accumulator
 * loop is OpenMP-parallel after a single bucketization pass).
 */
int hartonomous_centroid_4d_grouped(
    const double* points,
    const int64_t* group_ids,
    int64_t n,
    int64_t group_count,
    double* centroids
) {
    if (points == NULL || group_ids == NULL || centroids == NULL) return -1;
    if (n < 0 || group_count <= 0) return -2;

    /* Zero output. */
    memset(centroids, 0, (size_t)group_count * 4 * sizeof(double));

    /* Per-group counts; accumulated alongside the sums. */
    int64_t* counts = (int64_t*)calloc((size_t)group_count, sizeof(int64_t));
    if (counts == NULL) return -2;

    /* Single-pass scatter. Sequential to preserve deterministic accumulation
     * order — parallel scatter would need atomic adds whose ordering is
     * non-deterministic, breaking Law #6. */
    for (int64_t i = 0; i < n; ++i) {
        int64_t g = group_ids[i];
        if (g < 0 || g >= group_count) {
            free(counts);
            return -3;
        }
        const double* p = points + i * 4;
        double* c = centroids + g * 4;
        c[0] += p[0];
        c[1] += p[1];
        c[2] += p[2];
        c[3] += p[3];
        counts[g] += 1;
    }

    /* Per-group division — independent across groups, parallelizable. */
    int64_t g;
    #pragma omp parallel for schedule(static) private(g)
    for (g = 0; g < group_count; ++g) {
        if (counts[g] > 0) {
            double inv = 1.0 / (double)counts[g];
            double* c = centroids + g * 4;
            c[0] *= inv;
            c[1] *= inv;
            c[2] *= inv;
            c[3] *= inv;
        }
        /* Empty groups stay zero-filled from memset above. */
    }

    free(counts);
    return 0;
}
