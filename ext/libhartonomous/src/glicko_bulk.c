#include "hartonomous.h"

#define _USE_MATH_DEFINES
#include <math.h>
#include <stddef.h>
#include <stdint.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

/*
 * Glicko-2 single-game bulk update.
 *
 * Reference: Mark E. Glickman, "Example of the Glicko-2 system" (2013).
 * System constant tau (volatility damping) = 0.5 (Glickman's recommended
 * starting value). Convergence tolerance for the volatility iteration =
 * 1e-6. Iteration uses the Illinois variant of regula falsi as described
 * in §1, step 5.
 *
 * Each row is an independent (player vs single opponent) update; bulk just
 * means we process N rows in one call so the SQL `record_comparisons_bulk`
 * function can pipe `unnest($1, $2, ...)` straight in.
 *
 * Determinism (Law #6): all math is IEEE-754 double, in fixed evaluation
 * order, with no PRNG. Same inputs → bit-identical outputs.
 */

static const double GLICKO_SCALE = 173.7178;
static const double GLICKO_BASE_RATING = 1500.0;
static const double GLICKO_TAU = 0.5;
static const double GLICKO_EPS = 1e-6;
static const int    GLICKO_MAX_ITER = 100;

static double g_factor(double phi)
{
    return 1.0 / sqrt(1.0 + 3.0 * phi * phi / (M_PI * M_PI));
}

static double e_expectation(double mu, double mu_j, double phi_j)
{
    return 1.0 / (1.0 + exp(-g_factor(phi_j) * (mu - mu_j)));
}

/* f(x) for the volatility iteration; A = ln(sigma^2). */
static double f_vol(double x, double delta_sq, double phi_sq, double v, double a, double tau_sq)
{
    double ex = exp(x);
    double num = ex * (delta_sq - phi_sq - v - ex);
    double den = 2.0 * (phi_sq + v + ex);
    den = den * den;
    return (num / den) - ((x - a) / tau_sq);
}

/* Glickman's Illinois algorithm (step 5.5). */
static double iterate_volatility(double sigma, double delta, double phi, double v)
{
    double a = log(sigma * sigma);
    double delta_sq = delta * delta;
    double phi_sq = phi * phi;
    double tau_sq = GLICKO_TAU * GLICKO_TAU;

    double A = a;
    double B;
    if (delta_sq > phi_sq + v) {
        B = log(delta_sq - phi_sq - v);
    } else {
        int k = 1;
        while (f_vol(a - (double)k * GLICKO_TAU, delta_sq, phi_sq, v, a, tau_sq) < 0.0) {
            ++k;
            if (k > GLICKO_MAX_ITER) break;
        }
        B = a - (double)k * GLICKO_TAU;
    }

    double fA = f_vol(A, delta_sq, phi_sq, v, a, tau_sq);
    double fB = f_vol(B, delta_sq, phi_sq, v, a, tau_sq);

    int iter = 0;
    while (fabs(B - A) > GLICKO_EPS && iter < GLICKO_MAX_ITER) {
        double C = A + (A - B) * fA / (fB - fA);
        double fC = f_vol(C, delta_sq, phi_sq, v, a, tau_sq);
        if (fC * fB <= 0.0) {
            A = B;
            fA = fB;
        } else {
            fA = fA / 2.0;
        }
        B = C;
        fB = fC;
        ++iter;
    }

    return exp(A / 2.0);
}

int hartonomous_glicko2_bulk_update(
    int64_t n,
    const double* mu,
    const double* sigma,
    const double* volatility,
    const double* opp_mu,
    const double* opp_sigma,
    const double* score,
    double* new_mu,
    double* new_sigma,
    double* new_volatility)
{
    if (mu == NULL || sigma == NULL || volatility == NULL) return -1;
    if (opp_mu == NULL || opp_sigma == NULL || score == NULL) return -1;
    if (new_mu == NULL || new_sigma == NULL || new_volatility == NULL) return -1;
    if (n < 0) return -2;

    for (int64_t i = 0; i < n; ++i) {
        /* Step 2: convert player + opponent to Glicko-2 internal scale. */
        double mu_g     = (mu[i]     - GLICKO_BASE_RATING) / GLICKO_SCALE;
        double phi      = sigma[i]   / GLICKO_SCALE;
        double mu_jg    = (opp_mu[i] - GLICKO_BASE_RATING) / GLICKO_SCALE;
        double phi_j    = opp_sigma[i] / GLICKO_SCALE;

        /* Step 3: compute v (estimated variance from this game alone). */
        double g  = g_factor(phi_j);
        double E  = 1.0 / (1.0 + exp(-g * (mu_g - mu_jg)));
        double v_inv = g * g * E * (1.0 - E);
        double v  = (v_inv > 0.0) ? (1.0 / v_inv) : 0.0;

        /* Step 4: estimated improvement in rating. */
        double delta = v * g * (score[i] - E);

        /* Step 5: new volatility. */
        double sigma_prime = iterate_volatility(volatility[i], delta, phi, v);

        /* Step 6: pre-rating period deviation. */
        double phi_star = sqrt(phi * phi + sigma_prime * sigma_prime);

        /* Step 7: new rating deviation and rating. */
        double phi_prime = 1.0 / sqrt(1.0 / (phi_star * phi_star) + 1.0 / v);
        double mu_prime  = mu_g + phi_prime * phi_prime * g * (score[i] - E);

        /* Step 8: convert back to display scale. */
        new_mu[i]         = GLICKO_SCALE * mu_prime + GLICKO_BASE_RATING;
        new_sigma[i]      = GLICKO_SCALE * phi_prime;
        new_volatility[i] = sigma_prime;
    }

    return 0;
}

/* Suppress unused-when-static helper warnings on builds that fold the function
 * (some MSVC configurations strip e_expectation when the loop body inlines).
 * We leave the symbol available for tests that want to probe E directly. */
double hartonomous_glicko2_e_for_test(double mu_disp, double mu_j_disp, double sigma_j_disp);
double hartonomous_glicko2_e_for_test(double mu_disp, double mu_j_disp, double sigma_j_disp)
{
    double mu_g  = (mu_disp - GLICKO_BASE_RATING) / GLICKO_SCALE;
    double mu_jg = (mu_j_disp - GLICKO_BASE_RATING) / GLICKO_SCALE;
    double phi_j = sigma_j_disp / GLICKO_SCALE;
    return e_expectation(mu_g, mu_jg, phi_j);
}
