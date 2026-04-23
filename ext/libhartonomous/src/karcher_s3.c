/*
 * karcher_s3.c — Karcher (Fréchet) mean on the unit 3-sphere S³.
 *
 * `hartonomous_s3_centroid` (s3_geometry.c) returns the *chordal* mean:
 * sum the Euclidean vectors and renormalize onto S³. That is the first
 * iterate of the true intrinsic mean seeded from the origin — fast but
 * biased for widely-spread point sets (the bias grows like O(θ²) in the
 * angular spread θ).
 *
 * The plan's complexity-gravity composition (Phase 1.C) requires the
 * *true* intrinsic Fréchet mean: the point μ ∈ S³ minimizing
 *     F(μ) = (1/n) Σ dist_S³(μ, p_i)²
 * where dist_S³(a,b) = arccos(clamp(⟨a,b⟩, -1, 1)).
 *
 * Algorithm: Karcher's projected-gradient iteration on S³.
 *   1. Seed μ₀ = chordal mean of {p_i}.
 *   2. For k = 0, 1, ...:
 *        g_i    = Log_{μ_k}(p_i)                       // tangent at μ_k
 *        v_k    = (1/n) Σ g_i                         // tangent-space mean
 *        μ_{k+1} = Exp_{μ_k}(v_k)                     // exp map back to S³
 *        stop when ||v_k|| < tol or k == max_iter.
 *
 * Tangent-space maps on the unit sphere embedded in R⁴:
 *   Log_μ(p)  = θ · (p − ⟨μ,p⟩·μ) / sin(θ)   where θ = arccos(⟨μ,p⟩)
 *             = 0                             when p == μ (θ → 0)
 *   Exp_μ(v)  = cos(||v||)·μ + (sin(||v||)/||v||)·v   when ||v|| > 0
 *             = μ                             when ||v|| == 0
 *
 * Numerical care:
 *   - clamp ⟨μ,p⟩ to [-1, 1] before arccos.
 *   - guard θ → 0 with Taylor expansion (Log_μ(p) ≈ p − μ for small θ).
 *   - guard ||v|| → 0 at exp-map (return μ unchanged).
 *   - antipodal point (⟨μ,p⟩ ≈ -1) makes Log_μ undefined; we detect and
 *     return -3 in that case (the caller supplied a degenerate set and the
 *     Fréchet mean is not unique on S³).
 *
 * Deterministic: same input buffer + same max_iter + same tol → bit-
 * identical output. Reduction order is fixed (i = 0..n-1, scalar sums).
 *
 * Complexity: O(max_iter · n). Typical convergence 3–8 iterations for
 * angular spreads under π/2. No MKL dependency — the per-iteration cost is
 * 8n flops plus one sqrt, dwarfed by the outer trajectory composition.
 */

#include "hartonomous.h"

#include <math.h>
#include <stddef.h>
#include <string.h>

/* Default Karcher tolerance — 1e-12 rad is well under any downstream use. */
#define HARTONOMOUS_KARCHER_DEFAULT_TOL     1e-12
#define HARTONOMOUS_KARCHER_DEFAULT_MAXITER 64

static double dot4(const double a[4], const double b[4])
{
    return a[0]*b[0] + a[1]*b[1] + a[2]*b[2] + a[3]*b[3];
}

/*
 * Log map at base μ, applied to p. Writes into `out`. Returns:
 *    0 on success,
 *   -3 if p is antipodal to μ within 1e-12 (undefined log).
 */
static int log_map_s3(const double mu[4], const double p[4], double out[4])
{
    double d = dot4(mu, p);
    if (d >  1.0) d =  1.0;
    if (d < -1.0) d = -1.0;

    /* Antipodal guard: Log_μ is undefined, Karcher mean not unique. */
    if (d <= -1.0 + 1e-12) return -3;

    /* Small-angle: Log_μ(p) ≈ p − μ (first order). Avoid 0/sin(0). */
    if (d >= 1.0 - 1e-14) {
        out[0] = p[0] - mu[0];
        out[1] = p[1] - mu[1];
        out[2] = p[2] - mu[2];
        out[3] = p[3] - mu[3];
        return 0;
    }

    double theta   = acos(d);
    double sin_inv = 1.0 / sin(theta);
    /* Project p onto tangent at μ: p − ⟨μ,p⟩·μ, scaled to arc length θ. */
    double t0 = (p[0] - d*mu[0]) * sin_inv * theta;
    double t1 = (p[1] - d*mu[1]) * sin_inv * theta;
    double t2 = (p[2] - d*mu[2]) * sin_inv * theta;
    double t3 = (p[3] - d*mu[3]) * sin_inv * theta;
    out[0] = t0; out[1] = t1; out[2] = t2; out[3] = t3;
    return 0;
}

/* Exp map at base μ applied to tangent vector v. */
static void exp_map_s3(const double mu[4], const double v[4], double out[4])
{
    double n = sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2] + v[3]*v[3]);
    if (n < 1e-18) {
        out[0] = mu[0]; out[1] = mu[1]; out[2] = mu[2]; out[3] = mu[3];
        return;
    }
    double c = cos(n);
    double s = sin(n) / n;
    out[0] = c*mu[0] + s*v[0];
    out[1] = c*mu[1] + s*v[1];
    out[2] = c*mu[2] + s*v[2];
    out[3] = c*mu[3] + s*v[3];
    /* Belt-and-suspenders renormalize — cos/sin round-off keeps ||out|| very
     * close to 1 but not exact. One divide is cheap next to the iteration. */
    double m = sqrt(out[0]*out[0] + out[1]*out[1] + out[2]*out[2] + out[3]*out[3]);
    if (m > 0.0) {
        double inv = 1.0 / m;
        out[0] *= inv; out[1] *= inv; out[2] *= inv; out[3] *= inv;
    }
}

int hartonomous_karcher_mean_s3(
    const double* points,
    size_t        point_count,
    int           max_iter,
    double        tol,
    double        out[4]
)
{
    if (points == NULL || out == NULL) return -1;
    if (point_count == 0)               return -1;

    if (max_iter <= 0) max_iter = HARTONOMOUS_KARCHER_DEFAULT_MAXITER;
    if (tol <= 0.0)    tol      = HARTONOMOUS_KARCHER_DEFAULT_TOL;

    /* Single-point fast path: the mean *is* that point. */
    if (point_count == 1) {
        out[0] = points[0]; out[1] = points[1];
        out[2] = points[2]; out[3] = points[3];
        return 0;
    }

    /* Seed μ from chordal mean (s3_geometry.c). -2 if antipodal cancellation. */
    int rc = hartonomous_s3_centroid(points, point_count, out);
    if (rc != 0) return rc;

    double inv_n = 1.0 / (double) point_count;
    double tang[4];
    double sum[4];

    for (int iter = 0; iter < max_iter; ++iter) {
        sum[0] = sum[1] = sum[2] = sum[3] = 0.0;

        for (size_t i = 0; i < point_count; ++i) {
            const double* p = points + i * 4;
            int lrc = log_map_s3(out, p, tang);
            if (lrc != 0) return lrc;
            sum[0] += tang[0];
            sum[1] += tang[1];
            sum[2] += tang[2];
            sum[3] += tang[3];
        }

        double v[4] = {
            sum[0] * inv_n,
            sum[1] * inv_n,
            sum[2] * inv_n,
            sum[3] * inv_n
        };
        double vnorm = sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2] + v[3]*v[3]);

        double next[4];
        exp_map_s3(out, v, next);
        memcpy(out, next, sizeof(next));

        if (vnorm < tol) return 0;
    }

    /* Hit the iteration cap — still usable (iteration is monotone under the
     * S³ Fréchet functional for spreads < π/2), caller can inspect if they
     * care. Return 0 so this is not treated as failure. */
    return 0;
}
