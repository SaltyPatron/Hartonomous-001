#include "hartonomous.h"

#include <math.h>
#include <stddef.h>

static double dot4(const double a[4], const double b[4])
{
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
}

double hartonomous_s3_distance(const double p1[4], const double p2[4])
{
    double d = dot4(p1, p2);
    if (d > 1.0) d = 1.0;
    if (d < -1.0) d = -1.0;
    return acos(d);
}

int hartonomous_s3_centroid(const double* points, size_t point_count, double out[4])
{
    if (points == NULL || out == NULL) return -1;
    if (point_count == 0) return -1;

    double sx = 0.0, sy = 0.0, sz = 0.0, sw = 0.0;
    for (size_t i = 0; i < point_count; ++i) {
        const double* p = points + i * 4;
        sx += p[0];
        sy += p[1];
        sz += p[2];
        sw += p[3];
    }

    double norm = sqrt(sx * sx + sy * sy + sz * sz + sw * sw);
    if (norm < 1e-12) return -2;

    out[0] = sx / norm;
    out[1] = sy / norm;
    out[2] = sz / norm;
    out[3] = sw / norm;
    return 0;
}
