using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

public class ThermalResponseTests
{
    private static ThermalResponse.Point P(double rise, double selfW, double? coW = null, double weight = 1) =>
        new(rise, selfW, coW, weight);

    /// <summary>The dev laptop's own baseline cells (epoch 11, CPU, warm band, read out of
    /// deltat.db on 2026-07-31). Fitting them must recover a slope close to the one measured
    /// independently from 18,948 minutes of that machine's raw history (0.62 °C/W on an 18.3 °C
    /// intercept), because those cells ARE that machine.</summary>
    [Fact]
    public void Fit_RecoversTheDevLaptopsOwnResponse()
    {
        ThermalResponse? r = ThermalResponse.Fit(new[]
        {
            P(21.44, 7.66, weight: 79), P(22.97, 9.68, weight: 76), P(27.26, 13.53, weight: 10),
            P(34.22, 21.24, weight: 8), P(34.82, 26.22, weight: 8),
        });

        Assert.NotNull(r);
        Assert.InRange(r!.SelfSlopeCPerW, 0.45, 0.95);
        Assert.InRange(r.InterceptC, 12, 22);
        Assert.Null(r.CoSlopeCPerW);          // no neighbour watts on these cells
        Assert.Equal(7.66, r.MinSelfW, 2);
        Assert.Equal(26.22, r.MaxSelfW, 2);
    }

    [Fact]
    public void Fit_RecoversBothSlopes_WhenTheNeighboursWattsVary()
    {
        // dT = 17.6 + 0.40*self + 0.19*co, sampled across a spread of both.
        var pts = new List<ThermalResponse.Point>();
        foreach (double self in new[] { 8.0, 14.0, 20.0, 26.0, 32.0 })
            foreach (double co in new[] { 5.0, 20.0, 40.0 })
                pts.Add(P(17.6 + 0.40 * self + 0.19 * co, self, co));

        ThermalResponse? r = ThermalResponse.Fit(pts);

        Assert.NotNull(r);
        Assert.Equal(0.40, r!.SelfSlopeCPerW, 2);
        Assert.Equal(0.19, r.CoSlopeCPerW!.Value, 2);
        Assert.Equal(17.6, r.InterceptC, 1);
        Assert.True(r.ResidualRmseC < 0.01);
    }

    /// <summary>A neighbour that never moves cannot pin a co-slope, and inventing one would let
    /// the solver fit noise. The fit must fall back to the self-only model rather than return a
    /// confident-looking co-slope it has no evidence for.</summary>
    [Fact]
    public void Fit_LeavesCoSlopeUnfitted_WhenTheNeighbourNeverMoves()
    {
        var pts = new List<ThermalResponse.Point>();
        foreach (double self in new[] { 8.0, 14.0, 20.0, 26.0, 32.0 })
            pts.Add(P(17.6 + 0.40 * self + 0.19 * 6, self, coW: 6));

        ThermalResponse? r = ThermalResponse.Fit(pts);

        Assert.NotNull(r);
        Assert.Null(r!.CoSlopeCPerW);
        Assert.Equal(0.40, r.SelfSlopeCPerW, 2);
    }

    [Fact]
    public void Fit_ReturnsNull_WhenEveryCellSitsAtOneOperatingPoint()
    {
        Assert.Null(ThermalResponse.Fit(new[] { P(30, 20), P(30.4, 20.5), P(29.7, 21) }));
    }

    [Fact]
    public void Fit_ReturnsNull_BelowMinimumPoints()
    {
        Assert.Null(ThermalResponse.Fit(new[] { P(22, 8), P(34, 26) }));
    }

    /// <summary>More watts cannot cool a die. A fit that says otherwise is bad data, and the
    /// clamp must land on "barely correct for power", the conservative failure.</summary>
    [Fact]
    public void Fit_ClampsAPhysicallyImpossibleSlope()
    {
        ThermalResponse? r = ThermalResponse.Fit(new[] { P(40, 8), P(30, 16), P(20, 26), P(12, 34) });

        Assert.NotNull(r);
        Assert.Equal(ThermalResponse.MinSlopeCPerW, r!.SelfSlopeCPerW, 4);
    }

    /// <summary>Transport is the operation scoring actually performs, and its defining property
    /// is that it vanishes at equal power: the comparison then reduces to the plain rise
    /// difference it always was, so a machine that did not change its power draw is scored
    /// exactly as before this model existed.</summary>
    [Fact]
    public void Transport_IsZeroAtEqualPower()
    {
        var r = new ThermalResponse(17.6, 0.40, 0.19, 8, 32, 0.3, 5);

        Assert.Equal(0, r.Transport(20, 20, 10, 10), 9);
        Assert.Equal(0, r.Transport(20, 20, null, null), 9);
    }

    [Fact]
    public void Transport_MovesTheAnchorByTheLearnedSlopes()
    {
        var r = new ThermalResponse(17.6, 0.40, 0.19, 8, 32, 0.3, 5);

        // +10 W of own power, +20 W of neighbour: 0.40*10 + 0.19*20.
        Assert.Equal(4.0 + 3.8, r.Transport(10, 20, 5, 25), 6);
        // Neighbour unknown on one side: only the self term applies.
        Assert.Equal(4.0, r.Transport(10, 20, null, 25), 6);
    }

    /// <summary>The additive transport of an offset-carrying rise, against the multiplicative
    /// scaling it replaces. This is the whole defect in one assertion: on the dev laptop's real
    /// numbers, a machine running 5.5 °C COOLER than its reference was reported 10 °C hotter.</summary>
    [Fact]
    public void Transport_DoesNotScaleTheConstantOffset()
    {
        var r = new ThermalResponse(17.64, 0.405, null, 7.66, 26.22, 0.4, 5);

        // The dev laptop's full-load cell: 29.3 °C measured at 13.44 W against a 34.82 °C
        // baseline learned at 26.22 W.
        double transported = 34.82 + r.Transport(26.22, 13.44, null, null);
        double additiveExcess = 29.3 - transported;
        double multiplicativeExcess = 29.3 * (26.22 / 13.44) - 34.82;

        Assert.InRange(additiveExcess, -1.0, 3.0);        // near zero: the machine is fine
        Assert.InRange(multiplicativeExcess, 20, 30);     // what the old model reported
    }

    [Fact]
    public void Extrapolation_IsZeroInsideTheLearnedRangeAndGrowsOutside()
    {
        var r = new ThermalResponse(17.6, 0.40, null, 10, 30, 0.3, 5);

        Assert.Equal(0, r.Extrapolation(10));
        Assert.Equal(0, r.Extrapolation(20));
        Assert.Equal(0, r.Extrapolation(30));
        Assert.Equal(0.5, r.Extrapolation(40), 6);   // 10 W past a 20 W span
        Assert.Equal(0.25, r.Extrapolation(5), 6);
    }

    [Fact]
    public void Fit_WeightsHeavierCellsMore()
    {
        // One badly-off cell with almost no evidence behind it must not drag the fit.
        var trusted = new[] { P(20, 8, weight: 100), P(28, 20, weight: 100), P(36, 32, weight: 100) };
        var withOutlier = trusted.Append(P(80, 26, weight: 0.01)).ToList();

        ThermalResponse? a = ThermalResponse.Fit(trusted);
        ThermalResponse? b = ThermalResponse.Fit(withOutlier);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.SelfSlopeCPerW, b!.SelfSlopeCPerW, 2);
    }
}
