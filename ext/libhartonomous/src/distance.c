#include "hartonomous.h"

#define _USE_MATH_DEFINES
#include <math.h>
#include <stddef.h>

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
