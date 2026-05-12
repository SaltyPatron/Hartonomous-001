using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Tests.Native;

/// <summary>
/// Native Glicko-2 guard. There is no managed mirror; C in
/// <c>ext/libhartonomous/src/glicko_bulk.c</c> is the implementation surface
/// reached through centralized <see cref="NativeCompute"/>.
/// </summary>
public sealed class Glicko2NativeTests
{
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

        int rc = NativeCompute.Glicko2BulkUpdate(
            1, muIn, sigmaIn, volIn, oppMuIn, oppSigmaIn, scoreIn,
            newMu, newSigma, newVol);
        Assert.Equal(0, rc);
        return (newMu[0], newSigma[0], newVol[0]);
    }

    [Fact]
    public void DefaultPlayerWinsAgainstWeakOpponent_IncreasesRating()
    {
        const double mu = 1500.0, sigma = 350.0, vol = 0.06;
        const double oppMu = 1200.0, oppSigma = 30.0, score = 1.0;

        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.True(nMu > mu);
        Assert.True(nSigma < sigma);
        Assert.True(nVol > 0.0);
        Assert.True(double.IsFinite(nMu));
        Assert.True(double.IsFinite(nSigma));
        Assert.True(double.IsFinite(nVol));
    }

    [Fact]
    public void EqualPlayerDraw_DoesNotMoveRating()
    {
        const double mu = 1500.0, sigma = 200.0, vol = 0.06;
        const double oppMu = 1500.0, oppSigma = 200.0, score = 0.5;

        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.InRange(nMu, mu - 1e-12, mu + 1e-12);
        Assert.True(nSigma < sigma);
        Assert.True(nVol > 0.0);
    }

    [Fact]
    public void LossToStrongOpponent_DecreasesRating()
    {
        const double mu = 1500.0, sigma = 200.0, vol = 0.06;
        const double oppMu = 2000.0, oppSigma = 30.0, score = 0.0;

        (double nMu, double nSigma, double nVol) = RunNative(mu, sigma, vol, oppMu, oppSigma, score);

        Assert.True(nMu < mu);
        Assert.True(nSigma < sigma);
        Assert.True(nVol > 0.0);
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
