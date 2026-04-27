using System.Collections.Generic;
using Hartonomous.Core.Compute.Common;
using Xunit;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Validates the managed Glicko-2 mirror against the Glickman 2012 worked
/// example and against the symmetric properties the substrate's
/// corroboration path relies on.
/// </summary>
public sealed class Glicko2Tests
{
    [Fact]
    public void GlickmanWorkedExample_ProducesSpecExpectedRatingAndRd()
    {
        // Glickman 2012 § "Example calculation": a player at 1500 (RD 200,
        // vol 0.06) plays three opponents and ends near (1464.06, 151.52).
        Glicko2.Result result = Glicko2.Update(
            rating: 1500.0,
            deviation: 200.0,
            volatility: 0.06,
            opponents:
            [
                new Glicko2.Opponent(1400.0, 30.0, 1.0),
                new Glicko2.Opponent(1550.0, 100.0, 0.0),
                new Glicko2.Opponent(1700.0, 300.0, 0.0),
            ]);

        Assert.InRange(result.Rating, 1463.5, 1464.5);
        Assert.InRange(result.Deviation, 151.0, 152.0);
        Assert.InRange(result.Volatility, 0.0599, 0.0600001);
    }

    [Fact]
    public void NoOpponents_OnlyInflatesDeviationByVolatility()
    {
        // Step 6 of the spec: with no comparisons, only φ inflates by
        // sqrt(φ² + σ²); rating and volatility stay put.
        const double r = 1500;
        const double rd = 200;
        const double vol = 0.06;

        Glicko2.Result result = Glicko2.Update(r, rd, vol, []);

        Assert.Equal(r, result.Rating);
        Assert.Equal(vol, result.Volatility);
        // Inflated φ in internal scale: sqrt(φ² + σ²); back to public scale.
        double phiInternal = rd / Glicko2.Scale;
        double inflated = System.Math.Sqrt(phiInternal * phiInternal + vol * vol);
        Assert.InRange(result.Deviation, inflated * Glicko2.Scale - 0.5, inflated * Glicko2.Scale + 0.5);
    }

    [Fact]
    public void SelfDraw_TightensDeviation_RatingUnchanged()
    {
        // Drawing against a clone of yourself should not move your rating
        // (no expected-vs-actual gap), but it should reduce uncertainty
        // because the comparison provides corroborating evidence.
        Glicko2.Result result = Glicko2.UpdateOnSelfDraw(1500.0, 350.0, 0.06);

        Assert.InRange(result.Rating, 1499.99, 1500.01);
        Assert.True(result.Deviation < 350.0,
            $"Self-draw should reduce RD; got {result.Deviation}");
    }

    [Fact]
    public void IsDeterministic_RepeatedCallsProduceIdenticalResults()
    {
        IReadOnlyList<Glicko2.Opponent> opponents =
        [
            new(1400.0, 30.0, 1.0),
            new(1550.0, 100.0, 0.0),
        ];
        Glicko2.Result a = Glicko2.Update(1500.0, 200.0, 0.06, opponents);
        Glicko2.Result b = Glicko2.Update(1500.0, 200.0, 0.06, opponents);

        Assert.Equal(a.Rating, b.Rating);
        Assert.Equal(a.Deviation, b.Deviation);
        Assert.Equal(a.Volatility, b.Volatility);
    }

    [Fact]
    public void HigherDeviation_MovesRatingMore()
    {
        // A loss to an equally-rated opponent moves a high-uncertainty
        // (high RD) player further than a low-uncertainty player.
        Glicko2.Result moveable = Glicko2.Update(1500.0, 350.0, 0.06,
            [new Glicko2.Opponent(1500.0, 30.0, 0.0)]);
        Glicko2.Result fixed_ = Glicko2.Update(1500.0, 50.0, 0.06,
            [new Glicko2.Opponent(1500.0, 30.0, 0.0)]);

        double moveableDelta = 1500.0 - moveable.Rating;
        double fixedDelta = 1500.0 - fixed_.Rating;
        Assert.True(moveableDelta > fixedDelta,
            $"high-RD player should drop more on a loss; got {moveableDelta} vs {fixedDelta}");
    }
}
