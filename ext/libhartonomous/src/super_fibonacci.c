#include "hartonomous.h"

#define _USE_MATH_DEFINES
#include <math.h>
#include <stddef.h>
#include <stdint.h>

#include <omp.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

/*
 * Super-Fibonacci spirals on SO(3)/S^3 (Marc Alexa, CVPR 2022).
 * Deterministic quasi-uniform sampler. For index i in [0, n):
 *   s = i + 0.5
 *   t = s / n
 *   d = 2 * pi * s
 *   r = sqrt(t), R = sqrt(1 - t)
 *   alpha = d / PHI       (PHI = golden ratio)
 *   beta  = d / PSI       (PSI = sqrt(2))
 *   out = (r*sin(alpha), r*cos(alpha), R*sin(beta), R*cos(beta))
 * Unit-length by construction.
 */

static const double PHI = 1.6180339887498949;   /* golden ratio */
static const double PSI = 1.4142135623730951;   /* sqrt(2) */

int hartonomous_super_fibonacci(const double* params, size_t ndims, double out[4])
{
    if (params == NULL || out == NULL) return -1;
    if (ndims < 2) return -2;

    double i = params[0];
    double n = params[1];
    if (!(n > 0.0)) return -2;
    if (i < 0.0 || i >= n) return -2;

    double s = i + 0.5;
    double t = s / n;
    double d = 2.0 * M_PI * s;

    double r = sqrt(t);
    double R = sqrt(1.0 - t);
    double alpha = d / PHI;
    double beta  = d / PSI;

    out[0] = r * sin(alpha);
    out[1] = r * cos(alpha);
    out[2] = R * sin(beta);
    out[3] = R * cos(beta);
    return 0;
}

/*
 * Batched Super-Fibonacci projection: project N indices in [0, total) to
 * N points on S³. UCD codepoint S3 projection is the canonical caller —
 * 1.1M codepoints → 1.1M projections in one FFI call instead of 1.1M.
 *
 * Inputs:
 *   indices    — length-n array of double-encoded indices (each in [0, total))
 *   n          — number of points to project
 *   total      — sample-count denominator (the N in i/N)
 * Output:
 *   out        — caller-allocated, n × 4 doubles
 *
 * Returns 0 on success, -1 on null arg, -2 on n < 0 or total <= 0,
 * -3 on any indices[i] out of [0, total).
 *
 * OpenMP-parallel across indices. icx auto-vectorizes the trig pair via
 * SVML __svml_sincos_d8 if available — 8x scalar throughput on the trig.
 */
int hartonomous_super_fibonacci_many(
    const double* indices,
    int64_t n,
    double total,
    double* out
) {
    if (indices == NULL || out == NULL) return -1;
    if (n < 0 || !(total > 0.0)) return -2;
    if (n == 0) return 0;

    int range_err = 0;
    int64_t i;
    #pragma omp parallel for schedule(static) private(i)
    for (i = 0; i < n; ++i) {
        double idx = indices[i];
        if (idx < 0.0 || idx >= total) {
            #pragma omp atomic write
            range_err = 1;
            continue;
        }
        double s = idx + 0.5;
        double t = s / total;
        double d = 2.0 * M_PI * s;

        double r = sqrt(t);
        double R = sqrt(1.0 - t);
        double alpha = d / PHI;
        double beta  = d / PSI;

        double* p = out + i * 4;
        p[0] = r * sin(alpha);
        p[1] = r * cos(alpha);
        p[2] = R * sin(beta);
        p[3] = R * cos(beta);
    }
    return range_err ? -3 : 0;
}
