#include "hartonomous.h"

#define _USE_MATH_DEFINES
#include <math.h>
#include <stddef.h>

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
