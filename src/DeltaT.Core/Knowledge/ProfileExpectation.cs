namespace DeltaT.Core.Knowledge;

/// <summary>Turns a profile's typical rise-over-ambient figures into expected die
/// temperatures at the current outside reference. Profile numbers are anchored at a
/// normal ~25 °C room; a colder outside HOLDS the expectation instead of lowering it,
/// because silicon idles against its own heat floor (an idle die sits ~40 °C even at
/// 0 °C outside) — "+20° over ambient" read literally in winter would flag a perfectly
/// healthy machine, the same loophole the scoring engine closes with its cross-band
/// ceiling (ScoringEngine.CrossBandExcess). Warmer weather raises the expectation 1:1
/// (a real, expected rise), capped at the chassis concern threshold — past that the
/// silicon throttles rather than climbs, so promising a higher number would be
/// nonsense.</summary>
public static class ProfileExpectation
{
    /// <summary>The room ambient the knowledge-profile deltas are anchored to.</summary>
    public const double ReferenceAmbientC = 25.0;

    public static double ExpectedTempC(double typicalDeltaC, double? ambientC, double concernC)
    {
        double anchored = Math.Max(ambientC ?? ReferenceAmbientC, ReferenceAmbientC);
        return Math.Min(anchored + typicalDeltaC, concernC);
    }
}
