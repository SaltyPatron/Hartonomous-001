#include "hartonomous.h"

#include <math.h>
#include <stddef.h>

static double dist_4d_inline(const double a[4], const double b[4])
{
    double d0 = a[0] - b[0];
    double d1 = a[1] - b[1];
    double d2 = a[2] - b[2];
    double d3 = a[3] - b[3];
    return sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3);
}

static double dmax(double a, double b) { return a > b ? a : b; }
static double dmin(double a, double b) { return a < b ? a : b; }

/*
 * 4D discrete Fréchet distance — Eiter & Mannila, 1994.
 *
 * Bottom-up DP fill of the coupling-distance matrix:
 *   ca[i][j] = max( d(a_i, b_j), min( ca[i-1][j], ca[i-1][j-1], ca[i][j-1] ) )
 * with ca[0][0] = d(a_0, b_0). Result is ca[na-1][nb-1].
 *
 * Caller supplies a workspace of size na*nb so this function does not allocate.
 * Stored row-major: ca[i][j] at workspace[i*nb + j].
 */
double hartonomous_frechet_4d(
    const double* a, size_t na,
    const double* b, size_t nb,
    double* workspace)
{
    if (a == NULL || b == NULL || workspace == NULL) return NAN;
    if (na == 0 || nb == 0) return NAN;

    workspace[0] = dist_4d_inline(a, b);

    for (size_t j = 1; j < nb; ++j) {
        double d = dist_4d_inline(a, b + j * 4);
        workspace[j] = dmax(workspace[j - 1], d);
    }

    for (size_t i = 1; i < na; ++i) {
        double* row     = workspace + i * nb;
        const double* prev_row = workspace + (i - 1) * nb;
        const double* ai = a + i * 4;

        double d = dist_4d_inline(ai, b);
        row[0] = dmax(prev_row[0], d);

        for (size_t j = 1; j < nb; ++j) {
            double dij = dist_4d_inline(ai, b + j * 4);
            double m = dmin(dmin(prev_row[j], prev_row[j - 1]), row[j - 1]);
            row[j] = dmax(dij, m);
        }
    }

    return workspace[(na - 1) * nb + (nb - 1)];
}

/*
 * 4D Hausdorff distance: max(directed(A→B), directed(B→A)) where
 *   directed(A→B) = max_{a in A} min_{b in B} ||a - b||
 * O(na * nb) — for substrate trajectories that's bounded.
 */
double hartonomous_hausdorff_4d(
    const double* a, size_t na,
    const double* b, size_t nb)
{
    if (a == NULL || b == NULL) return NAN;
    if (na == 0 || nb == 0) return NAN;

    double a_to_b = 0.0;
    for (size_t i = 0; i < na; ++i) {
        const double* ai = a + i * 4;
        double min_d = INFINITY;
        for (size_t j = 0; j < nb; ++j) {
            double d = dist_4d_inline(ai, b + j * 4);
            if (d < min_d) min_d = d;
        }
        if (min_d > a_to_b) a_to_b = min_d;
    }

    double b_to_a = 0.0;
    for (size_t j = 0; j < nb; ++j) {
        const double* bj = b + j * 4;
        double min_d = INFINITY;
        for (size_t i = 0; i < na; ++i) {
            double d = dist_4d_inline(a + i * 4, bj);
            if (d < min_d) min_d = d;
        }
        if (min_d > b_to_a) b_to_a = min_d;
    }

    return dmax(a_to_b, b_to_a);
}
