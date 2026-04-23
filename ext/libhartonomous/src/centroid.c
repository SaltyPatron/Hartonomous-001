#include "hartonomous.h"

#include <math.h>
#include <stddef.h>

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
