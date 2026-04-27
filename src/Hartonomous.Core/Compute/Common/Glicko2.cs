using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Glicko-2 rating update primitive (Glickman 2012). Determined-by-spec,
/// bitwise-reproducible — no PRNG, all double arithmetic in IEEE 754
/// round-to-nearest-even.
///
/// The substrate uses Glicko-2 to rate trustworthiness of entities and
/// edges within an arena (`substrate.significance`, four junctions
/// (`entity_pos`, `entity_sense`, `pattern_deprel`)). The SQL functions
/// in migration 0053 (`substrate.record_corroboration`,
/// `substrate.glicko2_update`) implement the same algorithm server-side
/// for high-throughput ingest paths; this managed mirror is the canonical
/// reference for unit tests and any C# callers that need a per-comparison
/// update outside a SQL transaction.
///
/// Spec: Glickman, M. E. (2012). "Example of the Glicko-2 system."
/// http://www.glicko.net/glicko/glicko2.pdf
/// </summary>
public static class Glicko2
{
    /// <summary>The Glicko-2 internal scaling constant (Glickman 2012).</summary>
    public const double Scale = 173.7178;

    /// <summary>Default initial rating in the public scale (the "1500" anchor).</summary>
    public const double DefaultRating = 1500.0;

    /// <summary>Default initial rating deviation in the public scale.</summary>
    public const double DefaultDeviation = 350.0;

    /// <summary>Default volatility (Glickman 2012 recommendation 0.06).</summary>
    public const double DefaultVolatility = 0.06;

    /// <summary>System constant τ — controls how quickly volatility changes. 0.5 is the spec default.</summary>
    public const double SystemTau = 0.5;

    /// <summary>Convergence tolerance for the Illinois iteration in <see cref="UpdateVolatility"/>.</summary>
    public const double ConvergenceEpsilon = 1e-6;

    /// <summary>An opponent-result triple in the public (1500-anchored) rating scale.</summary>
    public readonly record struct Opponent(double Rating, double Deviation, double Score);

    /// <summary>The post-update rating, deviation, and volatility in the public scale.</summary>
    public readonly record struct Result(double Rating, double Deviation, double Volatility);

    /// <summary>
    /// Update a player's (entity's, edge's) rating after a list of comparisons.
    /// All inputs and outputs are in the public scale anchored at 1500.
    /// </summary>
    public static Result Update(
        double rating,
        double deviation,
        double volatility,
        IReadOnlyList<Opponent> opponents,
        double tau = SystemTau)
    {
        // 1. Convert to internal scale.
        double mu = (rating - DefaultRating) / Scale;
        double phi = deviation / Scale;

        if (opponents.Count == 0)
        {
            // Step 6 of the spec: no comparisons → only φ inflates by √(φ² + σ²).
            double phiPrime0 = Math.Sqrt(phi * phi + volatility * volatility);
            return new Result(rating, phiPrime0 * Scale, volatility);
        }

        // 2. Compute g(φ_j) and E(μ, μ_j, φ_j) for each opponent (internal scale).
        int n = opponents.Count;
        double[] g = new double[n];
        double[] e = new double[n];
        for (int j = 0; j < n; j++)
        {
            double muJ = (opponents[j].Rating - DefaultRating) / Scale;
            double phiJ = opponents[j].Deviation / Scale;
            g[j] = Glicko2G(phiJ);
            e[j] = Glicko2E(mu, muJ, g[j]);
        }

        // 3. Variance v.
        double vInv = 0;
        for (int j = 0; j < n; j++)
        {
            vInv += g[j] * g[j] * e[j] * (1.0 - e[j]);
        }
        double v = 1.0 / vInv;

        // 4. Estimated improvement Δ.
        double sumGSE = 0;
        for (int j = 0; j < n; j++)
        {
            sumGSE += g[j] * (opponents[j].Score - e[j]);
        }
        double delta = v * sumGSE;

        // 5. New volatility via Illinois iteration on f(x) per spec.
        double sigmaPrime = UpdateVolatility(volatility, phi, v, delta, tau);

        // 6. Pre-rating-period RD inflation.
        double phiStar = Math.Sqrt(phi * phi + sigmaPrime * sigmaPrime);

        // 7. New φ' and new μ'.
        double phiPrime = 1.0 / Math.Sqrt(1.0 / (phiStar * phiStar) + 1.0 / v);
        double muPrime = mu + phiPrime * phiPrime * sumGSE;

        // 8. Convert back to public scale.
        return new Result(
            Rating: muPrime * Scale + DefaultRating,
            Deviation: phiPrime * Scale,
            Volatility: sigmaPrime);
    }

    /// <summary>
    /// Re-encounter / corroboration on identical content: a draw against an
    /// opponent at the same rating with the same deviation. Algebraically
    /// specialized for the SQL pipeline — same result as
    /// <see cref="Update(double, double, double, IReadOnlyList&lt;Opponent&gt;, double)"/>
    /// with one Opponent(rating, deviation, 0.5).
    /// </summary>
    public static Result UpdateOnSelfDraw(
        double rating,
        double deviation,
        double volatility,
        double tau = SystemTau)
    {
        return Update(
            rating, deviation, volatility,
            [new Opponent(rating, deviation, 0.5)],
            tau);
    }

    private static double Glicko2G(double phi)
    {
        return 1.0 / Math.Sqrt(1.0 + 3.0 * phi * phi / (Math.PI * Math.PI));
    }

    private static double Glicko2E(double mu, double muJ, double g)
    {
        return 1.0 / (1.0 + Math.Exp(-g * (mu - muJ)));
    }

    /// <summary>
    /// Step 5 of the spec — update σ via the Illinois algorithm on
    /// f(x) = (e^x · (Δ² − φ² − v − e^x)) / (2(φ² + v + e^x)²) − (x − a)/τ²
    /// where a = ln(σ²). Returns the new σ in linear scale.
    /// </summary>
    private static double UpdateVolatility(
        double sigma, double phi, double v, double delta, double tau)
    {
        double a = Math.Log(sigma * sigma);
        double tauSq = tau * tau;

        double F(double x)
        {
            double ex = Math.Exp(x);
            double numerator = ex * (delta * delta - phi * phi - v - ex);
            double denominator = 2.0 * (phi * phi + v + ex) * (phi * phi + v + ex);
            return (numerator / denominator) - (x - a) / tauSq;
        }

        double A = a;
        double B;
        if (delta * delta > phi * phi + v)
        {
            B = Math.Log(delta * delta - phi * phi - v);
        }
        else
        {
            int k = 1;
            while (F(a - k * tau) < 0)
            {
                k++;
                if (k > 1000)
                {
                    throw new InvalidOperationException("Glicko-2 volatility iteration failed to bracket root.");
                }
            }
            B = a - k * tau;
        }

        double fA = F(A);
        double fB = F(B);
        int iter = 0;
        while (Math.Abs(B - A) > ConvergenceEpsilon)
        {
            double C = A + (A - B) * fA / (fB - fA);
            double fC = F(C);
            if (fC * fB <= 0)
            {
                A = B;
                fA = fB;
            }
            else
            {
                fA /= 2.0;
            }
            B = C;
            fB = fC;
            if (++iter > 1000)
            {
                break;
            }
        }
        return Math.Exp(A / 2.0);
    }
}
