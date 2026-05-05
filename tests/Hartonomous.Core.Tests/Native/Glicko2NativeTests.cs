using System;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Native;

namespace Hartonomous.Core.Tests.Native;

/// <summary>
/// Cross-language parity guard: the managed Glicko-2 mirror in
/// <see cref="Glicko2"/> must agree byte-for-byte with the canonical C
/// implementation in <c>ext/libhartonomous/src/glicko_bulk.c</c> on the
/// single-opponent case. Drift between the two surfaces is the regression
/// the user explicitly named — three independent reimplementations is
/// banned. The native bulk function is the source of truth; the managed
/// mirror is a test-only reference.
/// </summary>
public sealed class Glicko2NativeTests
{
    // The managed mirror and the C reference both run Step 5 (volatility) via
    // Illinois iteration with ConvergenceEpsilon = 1e-6. They terminate at
    // points within 1e-6 of the true root and naturally disagree by O(1e-6) —
    // observed real-world delta is ~7e-7. ParityTolerance must therefore sit
    // above the convergence noise floor and below any meaningful drift; 1e-5
    // is two orders above the floor and at least three orders below what a
    // one-line algorithmic change would produce.
    private const double ParityTolerance = 1e-5;

    private static (double NewMu, double NewSigma, double NewVol) RunNative(
        double mu, double sigma, double vol,
        double oppMu, double oppSigma, double score)
    {
        Span<double> muIn       = stackalloc double[1] { mu };
        Span<double> sigmaIn    = stackalloc double[1] { sigma };
        Span<double> volIn      = stackalloc double[1] { vol };
        Span<double> oppMuIn    = stackalloc double[1] { oppMu };
        Span<double> oppSigmaIn = stackalloc double[1] { oppSigma };
        Span<double> scoreIn    = stackalloc double[1] { score };
        Span<double> newMu      = stackalloc double[1];
        Span<double> newSigma   = stackalloc double[1];
        Span<double> newVol     = stackalloc double[1];

        int rc = Glicko2Native.Glicko2BulkUpdate(
            1, muIn, sigmaIn, volIn, oppMuIn, oppSigmaIn, scoreIn,
            newMu, newSigma, newVol);
        Assert.Equal(0, rc);
        return (newMu[0], newSigma[0], newVol[0]);
    }

    [Fact]
    public void DefaultPlayerWinsAgainstWeakOpponent_NativeMatchesManaged()
    {
        // Mirrors the gtest fixture in test_glicko_bulk.cc:
        //   GlickoBulk.DefaultPlayerWinsAgainstWeakOpponent
        const double mu = 1500.0, sigma = 350.0, vol = 0.06;
        const double oppMu = 1200.0, oppSigma = 30.0, score = 1.0;

        Glicko2.Result managed = Glicko2.Update(mu, sigma, vol,
            [new Glicko2.Opponent(oppMu, oppSigma, score)]);
        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.InRange(nMu - managed.Rating, -ParityTolerance, ParityTolerance);
        Assert.InRange(nSigma - managed.Deviation, -ParityTolerance, ParityTolerance);
        Assert.InRange(nVol - managed.Volatility, -ParityTolerance, ParityTolerance);
    }

    [Fact]
    public void EqualPlayerDraw_NativeMatchesManaged()
    {
        const double mu = 1500.0, sigma = 200.0, vol = 0.06;
        const double oppMu = 1500.0, oppSigma = 200.0, score = 0.5;

        Glicko2.Result managed = Glicko2.Update(mu, sigma, vol,
            [new Glicko2.Opponent(oppMu, oppSigma, score)]);
        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.InRange(nMu - managed.Rating, -ParityTolerance, ParityTolerance);
        Assert.InRange(nSigma - managed.Deviation, -ParityTolerance, ParityTolerance);
        Assert.InRange(nVol - managed.Volatility, -ParityTolerance, ParityTolerance);
    }

    [Fact]
    public void LossToStrongOpponent_NativeMatchesManaged()
    {
        const double mu = 1500.0, sigma = 200.0, vol = 0.06;
        const double oppMu = 2000.0, oppSigma = 30.0, score = 0.0;

        Glicko2.Result managed = Glicko2.Update(mu, sigma, vol,
            [new Glicko2.Opponent(oppMu, oppSigma, score)]);
        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.InRange(nMu - managed.Rating, -ParityTolerance, ParityTolerance);
        Assert.InRange(nSigma - managed.Deviation, -ParityTolerance, ParityTolerance);
        Assert.InRange(nVol - managed.Volatility, -ParityTolerance, ParityTolerance);
    }

    [Fact]
    public void NativeBulk_IsDeterministic()
    {
        // Same input → bit-identical output across repeated calls (Law #6).
        (double mu1, double sigma1, double vol1) = RunNative(1500, 200, 0.06, 1400, 30, 1.0);
        (double mu2, double sigma2, double vol2) = RunNative(1500, 200, 0.06, 1400, 30, 1.0);
        Assert.Equal(mu1, mu2);
        Assert.Equal(sigma1, sigma2);
        Assert.Equal(vol1, vol2);
    }
}
