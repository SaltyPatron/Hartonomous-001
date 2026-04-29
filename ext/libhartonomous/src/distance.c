#include "hartonomous.h"

#define _USE_MATH_DEFINES
#include <math.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#include <omp.h>

#if defined(__AVX2__) || defined(_MSC_VER)
#include <immintrin.h>
#endif

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

double hartonomous_distance_4d(const double a[4], const double b[4])
{
    double d0 = a[0] - b[0];
    double d1 = a[1] - b[1];
    double d2 = a[2] - b[2];
    double d3 = a[3] - b[3];
    return sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3);
}

/*
 * Batched Euclidean 4D distance for N pairs.
 *
 * Inputs:
 *   a, b  — packed n × 4 doubles (row i = pair i's left/right point).
 *   n     — number of pairs (>= 0).
 * Output:
 *   out   — length-n distances.
 *
 * AVX2 path: one 4D point fits in a YMM register (4 × float64 = 256 bits).
 * The naive layout maps one pair to one YMM subtract + one YMM multiply +
 * a 4-lane horizontal sum + a scalar sqrt. ~10x scalar throughput on hot
 * loops because the FMA + sqrtpd issue rate is the bottleneck, not memory.
 *
 * OpenMP-parallel across pairs since each pair is independent.
 *
 * Returns 0 on success, -1 on null arg, -2 on n < 0.
 */
int hartonomous_distance_4d_pairs(
    const double* a,
    const double* b,
    int64_t n,
    double* out
) {
    if (a == NULL || b == NULL || out == NULL) return -1;
    if (n < 0) return -2;

    int64_t i;
    #pragma omp parallel for schedule(static) private(i)
    for (i = 0; i < n; ++i) {
#if defined(__AVX2__)
        __m256d va = _mm256_loadu_pd(a + i * 4);
        __m256d vb = _mm256_loadu_pd(b + i * 4);
        __m256d vd = _mm256_sub_pd(va, vb);
        __m256d vsq = _mm256_mul_pd(vd, vd);
        /* Horizontal sum of 4 doubles in YMM. */
        __m128d hi = _mm256_extractf128_pd(vsq, 1);
        __m128d lo = _mm256_castpd256_pd128(vsq);
        __m128d sum2 = _mm_add_pd(lo, hi);
        __m128d sum1 = _mm_add_sd(sum2, _mm_unpackhi_pd(sum2, sum2));
        out[i] = sqrt(_mm_cvtsd_f64(sum1));
#else
        double d0 = a[i*4+0] - b[i*4+0];
        double d1 = a[i*4+1] - b[i*4+1];
        double d2 = a[i*4+2] - b[i*4+2];
        double d3 = a[i*4+3] - b[i*4+3];
        out[i] = sqrt(d0*d0 + d1*d1 + d2*d2 + d3*d3);
#endif
    }
    return 0;
}

/*
 * Batched 4D discrete Fréchet distance for N pairs of polylines.
 *
 * The scalar kernel (hartonomous_frechet_4d) is O(na · nb) per pair and
 * needs a workspace double[na · nb]. For batching, OpenMP-parallel across
 * pairs; each pair gets its own thread-local workspace allocation.
 *
 * Inputs (parallel arrays):
 *   a_polylines    — array of n pointers, each to a packed na_i × 4 buffer
 *   na             — array of n vertex counts for the a-side
 *   b_polylines    — array of n pointers, each to a packed nb_i × 4 buffer
 *   nb             — array of n vertex counts for the b-side
 *   n              — number of pairs
 * Output:
 *   out_distances  — length-n
 *
 * Returns 0 on success, -1 on null arg, -2 on n < 0, -9 on alloc fail
 * for any per-pair workspace.
 */
int hartonomous_frechet_4d_pairs(
    const double* const* a_polylines,
    const size_t* na,
    const double* const* b_polylines,
    const size_t* nb,
    int64_t n,
    double* out_distances
) {
    if (a_polylines == NULL || na == NULL ||
        b_polylines == NULL || nb == NULL ||
        out_distances == NULL) return -1;
    if (n < 0) return -2;

    int alloc_failed = 0;
    int64_t i;
    #pragma omp parallel for schedule(dynamic) private(i)
    for (i = 0; i < n; ++i) {
        size_t ws_size = na[i] * nb[i];
        if (ws_size == 0) {
            out_distances[i] = (double)NAN;
            continue;
        }
        double* ws = (double*)malloc(ws_size * sizeof(double));
        if (ws == NULL) {
            #pragma omp atomic write
            alloc_failed = 1;
            out_distances[i] = (double)NAN;
            continue;
        }
        out_distances[i] = hartonomous_frechet_4d(
            a_polylines[i], na[i],
            b_polylines[i], nb[i],
            ws
        );
        free(ws);
    }
    return alloc_failed ? -9 : 0;
}

double hartonomous_dot_4d(const double a[4], const double b[4])
{
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
}

double hartonomous_norm_4d(const double x[4])
{
    return sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2] + x[3] * x[3]);
}

int hartonomous_normalize_4d(const double x[4], double out[4])
{
    if (x == NULL || out == NULL) return -1;
    double n = hartonomous_norm_4d(x);
    if (n < 1e-12) return -2;
    double inv = 1.0 / n;
    out[0] = x[0] * inv;
    out[1] = x[1] * inv;
    out[2] = x[2] * inv;
    out[3] = x[3] * inv;
    return 0;
}

int hartonomous_slerp(const double a[4], const double b[4], double t, double out[4])
{
    if (a == NULL || b == NULL || out == NULL) return -1;

    /* Inputs must be unit length; tolerance is loose since slerp degrades
     * gracefully near the unit sphere. */
    double na = hartonomous_norm_4d(a);
    double nb = hartonomous_norm_4d(b);
    if (fabs(na - 1.0) > 1e-9 || fabs(nb - 1.0) > 1e-9) return -2;

    double cos_omega = hartonomous_dot_4d(a, b);

    /* Choose the shortest arc on S^3 (treat antipodal quaternions as equiv). */
    double sign = 1.0;
    if (cos_omega < 0.0) {
        cos_omega = -cos_omega;
        sign = -1.0;
    }

    /* Numerical safety: clamp into [-1, 1]. */
    if (cos_omega > 1.0) cos_omega = 1.0;

    /* When endpoints are nearly identical, fall back to linear interpolation
     * to avoid division by sin(omega) ≈ 0. Threshold matches the IEEE-754
     * regime where sin(omega)/omega ≈ 1 to 1 ulp. */
    if (cos_omega > 1.0 - 1e-10) {
        out[0] = (1.0 - t) * a[0] + t * sign * b[0];
        out[1] = (1.0 - t) * a[1] + t * sign * b[1];
        out[2] = (1.0 - t) * a[2] + t * sign * b[2];
        out[3] = (1.0 - t) * a[3] + t * sign * b[3];
        /* Renormalize so successive slerps don't drift off S^3. */
        double n = hartonomous_norm_4d(out);
        if (n > 1e-12) {
            double inv = 1.0 / n;
            out[0] *= inv; out[1] *= inv; out[2] *= inv; out[3] *= inv;
        }
        return 0;
    }

    double omega = acos(cos_omega);
    double sin_omega = sin(omega);
    double inv_sin = 1.0 / sin_omega;
    double wa = sin((1.0 - t) * omega) * inv_sin;
    double wb = sin(t * omega) * inv_sin * sign;

    out[0] = wa * a[0] + wb * b[0];
    out[1] = wa * a[1] + wb * b[1];
    out[2] = wa * a[2] + wb * b[2];
    out[3] = wa * a[3] + wb * b[3];
    return 0;
}

int hartonomous_antipode(const double p[4], double out[4])
{
    if (p == NULL || out == NULL) return -1;
    out[0] = -p[0];
    out[1] = -p[1];
    out[2] = -p[2];
    out[3] = -p[3];
    return 0;
}
