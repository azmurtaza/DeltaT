namespace DeltaT.Core.Scoring;

/// <summary>How a component's die-to-ambient rise actually answers power on THIS machine,
/// learned from its own cells: a constant offset plus a slope on its own package watts, plus
/// a slope on the neighbour's watts where one heatpipe stack is shared between CPU and GPU.
///
/// <para>This exists because the older model, ΔT = P × R with a single constant thermal
/// resistance, has no room for an offset and no room for a second heat source. DeltaT scores
/// rise over the OUTDOOR temperature, so every reading carries the indoor-over-outdoor gap
/// plus board and VRM heat the package sensor never reports, none of which scales with package
/// watts. Fitted over 18,948 minutes of real history from the dev laptop, the proportional
/// model scores R² = -1.14 (worse than predicting the mean) against R² = 0.47 for
/// ΔT = 17.6 + 0.41·W_self + 0.19·W_co. Scaling a rise by the watt ratio multiplies the offset
/// and the neighbour's heat along with the signal, which fabricates excess when watts fall and
/// hides it when they rise. <see cref="DetectionBenchmark.RunSharedCooling"/> measures both.</para>
///
/// <para>Pure and deterministic like everything else in scoring: observations in, coefficients
/// out, no clocks or I/O.</para></summary>
public sealed record ThermalResponse(
    double InterceptC,
    double SelfSlopeCPerW,
    // Null when the observations carried no neighbour watts, or too little spread in them to
    // identify a slope. Never guessed: an unfitted co-slope means the co-heat difference is
    // reported as un-judgeable, not silently assumed to be zero.
    double? CoSlopeCPerW,
    double MinSelfW,
    double MaxSelfW,
    double ResidualRmseC,
    int Points)
{
    /// <summary>Fewest cells that can pin a slope. Three gives one degree of freedom left over,
    /// so <see cref="ResidualRmseC"/> is a real (if noisy) quality signal rather than zero by
    /// construction.</summary>
    public const int MinPointsSelf = 3;

    /// <summary>The co-slope adds a third coefficient, so it needs a fourth observation before
    /// the fit stops being exactly determined.</summary>
    public const int MinPointsCo = 4;

    /// <summary>Watts of spread the observations must cover before a slope is identifiable.
    /// Below this the cells all sit at one operating point and the "slope" is noise divided by
    /// a small number.</summary>
    public const double MinSpanW = 3.0;

    /// <summary>Physically admissible slope range (°C per watt). More watts cannot cool a die,
    /// so a non-positive slope is a bad fit, and a laptop cooler that ran hotter than about
    /// 6 °C/W would be inoperable. A fit landing outside gets clamped rather than discarded:
    /// clamping toward the low end degrades to "barely correct for power", which is the
    /// conservative failure, while discarding would silently restore the old behaviour.</summary>
    public const double MinSlopeCPerW = 0.05;
    public const double MaxSlopeCPerW = 6.0;

    /// <summary>Co-slope is bounded below at zero (a neighbour's heat cannot cool this die) and
    /// well under the self-slope: heat reaching the die through a shared heatpipe crosses more
    /// thermal resistance than heat generated in it. Measured at 0.19 against a self-slope of
    /// 0.41 on the dev laptop.</summary>
    public const double MaxCoSlopeCPerW = 3.0;

    public double SelfSpanW => MaxSelfW - MinSelfW;

    /// <summary>Rise predicted at an operating point.</summary>
    public double Predict(double selfW, double? coW) =>
        InterceptC + SelfSlopeCPerW * selfW
        + (CoSlopeCPerW is { } c && coW is { } w ? c * w : 0);

    /// <summary>The rise difference attributable to moving from one operating point to another.
    ///
    /// <para>This is what scoring uses, and it deliberately does NOT replace the learned cell
    /// with the fitted line. The cell stays the anchor and this only transports it to the
    /// operating point actually measured, so per-bucket idiosyncrasy the line can't express (a
    /// fan curve stepping up at heavy load) is preserved. At equal power it returns exactly
    /// zero and the comparison reduces to the plain rise difference it always was.</para>
    ///
    /// <para>Additive, not multiplicative, which is the whole point: an offset common to both
    /// operating points cancels instead of being scaled.</para></summary>
    public double Transport(double fromSelfW, double toSelfW, double? fromCoW, double? toCoW)
    {
        double d = SelfSlopeCPerW * (toSelfW - fromSelfW);
        if (CoSlopeCPerW is { } c && fromCoW is { } f && toCoW is { } t)
            d += c * (t - f);
        return d;
    }

    /// <summary>How far an operating point sits outside the power range the fit was learned
    /// over, as a fraction of that range. Zero inside it. Scoring uses this to taper its
    /// confidence smoothly instead of trusting a fit right up to a cliff and then dropping the
    /// comparison entirely: the old code applied a full correction at a power ratio of 1.95 and
    /// none at 2.01, and the dev laptop landed on 1.951.</summary>
    public double Extrapolation(double selfW)
    {
        double span = Math.Max(SelfSpanW, MinSpanW);
        if (selfW < MinSelfW) return (MinSelfW - selfW) / span;
        if (selfW > MaxSelfW) return (selfW - MaxSelfW) / span;
        return 0;
    }

    /// <summary>One observation to fit: a learned rise at a known operating point.</summary>
    public readonly record struct Point(double RiseC, double SelfW, double? CoW, double Weight);

    /// <summary>Weighted least-squares fit. Returns null when the observations can't pin a
    /// slope (too few, or all at one operating point), which callers must treat as "this
    /// comparison can't be power-corrected", never as "no correction needed".</summary>
    public static ThermalResponse? Fit(IReadOnlyList<Point> points)
    {
        var usable = points.Where(p => p.Weight > 0 && p.SelfW > 0 && double.IsFinite(p.RiseC)).ToList();
        if (usable.Count < MinPointsSelf)
            return null;

        double minW = usable.Min(p => p.SelfW), maxW = usable.Max(p => p.SelfW);
        if (maxW - minW < MinSpanW)
            return null;

        // Try the co-heat model first: it only wins if the neighbour's watts are present on
        // enough observations AND actually vary, otherwise its slope is unidentifiable and the
        // solver would hand back an arbitrary number that happens to fit the noise.
        var withCo = usable.Where(p => p.CoW is not null).ToList();
        if (withCo.Count >= MinPointsCo)
        {
            double coMin = withCo.Min(p => p.CoW!.Value), coMax = withCo.Max(p => p.CoW!.Value);
            if (coMax - coMin >= MinSpanW)
            {
                double[]? c = Solve(
                    withCo.Select(p => new[] { 1.0, p.SelfW, p.CoW!.Value }).ToList(),
                    withCo.Select(p => p.RiseC).ToList(),
                    withCo.Select(p => p.Weight).ToList());
                if (c is not null)
                {
                    double self = Math.Clamp(c[1], MinSlopeCPerW, MaxSlopeCPerW);
                    double co = Math.Clamp(c[2], 0, MaxCoSlopeCPerW);
                    double rmse = Rmse(withCo, p => c[0] + self * p.SelfW + co * p.CoW!.Value);
                    return new ThermalResponse(c[0], self, co,
                        withCo.Min(p => p.SelfW), withCo.Max(p => p.SelfW), rmse, withCo.Count);
                }
            }
        }

        double[]? s = Solve(
            usable.Select(p => new[] { 1.0, p.SelfW }).ToList(),
            usable.Select(p => p.RiseC).ToList(),
            usable.Select(p => p.Weight).ToList());
        if (s is null)
            return null;

        double slope = Math.Clamp(s[1], MinSlopeCPerW, MaxSlopeCPerW);
        return new ThermalResponse(s[0], slope, null, minW, maxW,
            Rmse(usable, p => s[0] + slope * p.SelfW), usable.Count);
    }

    private static double Rmse(List<Point> pts, Func<Point, double> predict)
    {
        double num = 0, den = 0;
        foreach (Point p in pts)
        {
            double r = p.RiseC - predict(p);
            num += p.Weight * r * r;
            den += p.Weight;
        }
        return den > 0 ? Math.Sqrt(num / den) : 0;
    }

    /// <summary>Weighted normal equations solved by Gaussian elimination with partial pivoting.
    /// Returns null on a singular system (collinear observations).</summary>
    private static double[]? Solve(List<double[]> design, List<double> y, List<double> w)
    {
        int k = design[0].Length;
        var a = new double[k, k];
        var b = new double[k];
        for (int r = 0; r < design.Count; r++)
        {
            double[] x = design[r];
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k; j++)
                    a[i, j] += w[r] * x[i] * x[j];
                b[i] += w[r] * x[i] * y[r];
            }
        }

        for (int i = 0; i < k; i++)
        {
            int pivot = i;
            for (int r = i + 1; r < k; r++)
                if (Math.Abs(a[r, i]) > Math.Abs(a[pivot, i])) pivot = r;
            if (Math.Abs(a[pivot, i]) < 1e-9)
                return null;
            if (pivot != i)
            {
                for (int j = 0; j < k; j++) (a[i, j], a[pivot, j]) = (a[pivot, j], a[i, j]);
                (b[i], b[pivot]) = (b[pivot], b[i]);
            }
            for (int r = i + 1; r < k; r++)
            {
                double f = a[r, i] / a[i, i];
                if (f == 0) continue;
                for (int j = i; j < k; j++) a[r, j] -= f * a[i, j];
                b[r] -= f * b[i];
            }
        }

        var sol = new double[k];
        for (int i = k - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < k; j++) sum -= a[i, j] * sol[j];
            sol[i] = sum / a[i, i];
        }
        return sol.All(double.IsFinite) ? sol : null;
    }
}
