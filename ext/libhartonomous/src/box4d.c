#include "hartonomous.h"

#include <math.h>
#include <stddef.h>

/*
 * box4d layout: 8 doubles, [min[0..3], max[0..3]].
 *
 * Used as the GiST key type for `point4d` columns. Operations are intentionally
 * scalar/branch-free where possible because the GiST consistent/penalty
 * functions are called once per inner-page entry per query.
 */

static double dmin(double a, double b) { return a < b ? a : b; }
static double dmax(double a, double b) { return a > b ? a : b; }

void hartonomous_bbox_init_point(const double p[4], double box[8])
{
    box[0] = p[0]; box[1] = p[1]; box[2] = p[2]; box[3] = p[3];
    box[4] = p[0]; box[5] = p[1]; box[6] = p[2]; box[7] = p[3];
}

void hartonomous_bbox_expand_point(double box[8], const double p[4])
{
    box[0] = dmin(box[0], p[0]);
    box[1] = dmin(box[1], p[1]);
    box[2] = dmin(box[2], p[2]);
    box[3] = dmin(box[3], p[3]);
    box[4] = dmax(box[4], p[0]);
    box[5] = dmax(box[5], p[1]);
    box[6] = dmax(box[6], p[2]);
    box[7] = dmax(box[7], p[3]);
}

void hartonomous_bbox_union(const double a[8], const double b[8], double out[8])
{
    /* Aliasing-safe: read all of a/b into locals before writing out. */
    double mn0 = dmin(a[0], b[0]);
    double mn1 = dmin(a[1], b[1]);
    double mn2 = dmin(a[2], b[2]);
    double mn3 = dmin(a[3], b[3]);
    double mx0 = dmax(a[4], b[4]);
    double mx1 = dmax(a[5], b[5]);
    double mx2 = dmax(a[6], b[6]);
    double mx3 = dmax(a[7], b[7]);
    out[0] = mn0; out[1] = mn1; out[2] = mn2; out[3] = mn3;
    out[4] = mx0; out[5] = mx1; out[6] = mx2; out[7] = mx3;
}

int hartonomous_bbox_overlaps(const double a[8], const double b[8])
{
    /* Closed intervals: separation requires a.max < b.min OR b.max < a.min on
     * SOME axis. Overlap is the negation across all 4 axes. */
    if (a[4] < b[0] || b[4] < a[0]) return 0;
    if (a[5] < b[1] || b[5] < a[1]) return 0;
    if (a[6] < b[2] || b[6] < a[2]) return 0;
    if (a[7] < b[3] || b[7] < a[3]) return 0;
    return 1;
}

int hartonomous_bbox_contains_point(const double box[8], const double p[4])
{
    if (p[0] < box[0] || p[0] > box[4]) return 0;
    if (p[1] < box[1] || p[1] > box[5]) return 0;
    if (p[2] < box[2] || p[2] > box[6]) return 0;
    if (p[3] < box[3] || p[3] > box[7]) return 0;
    return 1;
}

int hartonomous_bbox_contains_box(const double outer[8], const double inner[8])
{
    if (inner[0] < outer[0] || inner[4] > outer[4]) return 0;
    if (inner[1] < outer[1] || inner[5] > outer[5]) return 0;
    if (inner[2] < outer[2] || inner[6] > outer[6]) return 0;
    if (inner[3] < outer[3] || inner[7] > outer[7]) return 0;
    return 1;
}

int hartonomous_bbox_equals(const double a[8], const double b[8])
{
    for (int i = 0; i < 8; ++i) {
        if (a[i] != b[i]) return 0;
    }
    return 1;
}

double hartonomous_bbox_volume(const double box[8])
{
    double e0 = box[4] - box[0];
    double e1 = box[5] - box[1];
    double e2 = box[6] - box[2];
    double e3 = box[7] - box[3];
    /* Degenerate axes (min==max) contribute factor 0 in true volume; for GiST
     * penalty purposes a tiny epsilon would matter, but PG opclasses handle
     * degeneracy at the support-function level. Return raw product. */
    return e0 * e1 * e2 * e3;
}

double hartonomous_bbox_min_distance_4d(const double box[8], const double p[4])
{
    /* Per-axis clamp: if p inside [min, max], distance contribution is 0;
     * otherwise it's |p - nearer face|. */
    double d0 = 0.0, d1 = 0.0, d2 = 0.0, d3 = 0.0;
    if (p[0] < box[0])      d0 = box[0] - p[0];
    else if (p[0] > box[4]) d0 = p[0] - box[4];
    if (p[1] < box[1])      d1 = box[1] - p[1];
    else if (p[1] > box[5]) d1 = p[1] - box[5];
    if (p[2] < box[2])      d2 = box[2] - p[2];
    else if (p[2] > box[6]) d2 = p[2] - box[6];
    if (p[3] < box[3])      d3 = box[3] - p[3];
    else if (p[3] > box[7]) d3 = p[3] - box[7];
    return sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3);
}
