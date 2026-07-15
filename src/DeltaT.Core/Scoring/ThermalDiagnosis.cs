using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Scoring;

/// <summary>What is actually driving a machine's temperatures. DeltaT is a thermal
/// diagnostician, not a paste salesman: a hot machine can be dust, a dying fan, a bad
/// mount, a deliberate overclock, or genuinely the paste, and each wants a different
/// fix. The engine weighs the evidence and names the cause it can defend, so the app
/// never reflexively blames the paste.</summary>
public enum ThermalCause
{
    /// <summary>Nothing is wrong: behaving like its own healthy baseline.</summary>
    Healthy,
    /// <summary>Thermal paste conducting worse than it did: load-dependent excess,
    /// faster heat-soak, sluggish cooldown.</summary>
    Paste,
    /// <summary>Airflow starved (dust, clogged filters, blocked intake): a broad, steady
    /// offset across every load, with fans working harder to hold the line.</summary>
    Airflow,
    /// <summary>A fan turning slower than it used to at the same load: a failing bearing,
    /// a stalled fan, or a profile change it can't be told apart from without asking.</summary>
    FanFault,
    /// <summary>Uneven mount or pumped-out paste: a hotspot running far above the edge
    /// sensor, heat trapped at one point instead of spreading.</summary>
    Mount,
    /// <summary>Temperatures moved because power did (overclock, undervolt, a changed
    /// power limit), not because cooling changed. Reassurance, not a fault.</summary>
    PowerConfig,
    /// <summary>Running hot because the room is hot. DeltaT already corrects for it; this
    /// says so out loud so a heatwave isn't mistaken for a hardware problem.</summary>
    HighAmbient,
    /// <summary>Out of thermal headroom: touching the silicon limit or throttling,
    /// whatever the upstream cause.</summary>
    CoolingHeadroom,
}

/// <summary>One weighed cause, with the plain-language evidence behind it.</summary>
public sealed record CauseFinding(ThermalCause Cause, double Confidence, string Evidence);

/// <summary>The ranked read on why a component runs the way it does. The first finding
/// is the leading explanation; there may be more than one (dust AND a tired fan is a
/// real combination). Empty findings, or a lone <see cref="ThermalCause.Healthy"/>,
/// means nothing needs attention.</summary>
public sealed record ThermalDiagnosis(IReadOnlyList<CauseFinding> Findings)
{
    public static readonly ThermalDiagnosis Healthy =
        new(new[] { new CauseFinding(ThermalCause.Healthy, 1.0, "Running like its own healthy baseline.") });

    public CauseFinding Primary => Findings.Count > 0 ? Findings[0] : Healthy.Findings[0];

    public bool IsHealthy => Primary.Cause == ThermalCause.Healthy;

    /// <summary>A short headline for the leading cause, for tight UI spots.</summary>
    public string Headline => Primary.Cause switch
    {
        ThermalCause.Healthy => "Healthy",
        ThermalCause.Paste => "Paste",
        ThermalCause.Airflow => "Airflow",
        ThermalCause.FanFault => "Fan",
        ThermalCause.Mount => "Mount",
        ThermalCause.PowerConfig => "Power",
        ThermalCause.HighAmbient => "Ambient",
        _ => "Headroom",
    };
}

/// <summary>The characteristics DeltaT can put an individual health number on. The
/// per-cause diagnosis names the leading problem; the aspect readout shows the state
/// of EVERY subsystem side by side (paste 96, airflow 42, fans 100 ...), so a healthy
/// aspect is visible as healthy instead of silently absent.</summary>
public enum HealthAspect
{
    /// <summary>The die-to-heatsink joint: load-dependent rise, soak and cooldown rates.</summary>
    Paste,
    /// <summary>The fin-stack-to-air path: dust, filters, intakes.</summary>
    Airflow,
    /// <summary>The fans themselves reaching their own baseline speed at the same load.</summary>
    Fans,
    /// <summary>Mount evenness: hotspot-to-edge gap against this card's learned gap.</summary>
    Mount,
    /// <summary>Distance from the silicon wall: throttle events and near-limit peaks.</summary>
    Headroom,
    /// <summary>Power configuration relative to baseline: stock, overclock, undervolt.
    /// A state readout, not a health score; a power change is a choice, not a fault.</summary>
    Power,
}

/// <summary>One aspect's readout. <see cref="Score"/> is 0–100 health (null when this
/// machine exposes no sensor for it, or for <see cref="HealthAspect.Power"/> which is a
/// state, not a health). <see cref="Status"/> is the short instrument word; for unknown
/// aspects it is "--" so the UI never fakes a zero.</summary>
public sealed record AspectHealth(HealthAspect Aspect, int? Score, string Status, string Detail, bool Unmeasurable = false)
{
    public bool Known => Status != "--";
}

/// <summary>Everything the diagnosis reads, gathered once by the scoring engine so the
/// attribution logic stays pure and testable.</summary>
public sealed record DiagnosisInputs(
    ComponentKind Kind,
    double? WeightedExcessC,     // fan+power-normalized excess over baseline
    double? HeavyExcessC,        // excess in the heavy/full buckets
    double? IdleExcessC,         // excess at idle (a steady offset shows here too)
    bool BroadExcess,            // excess present across three or more load buckets
    double? SoakRatio,           // recent soak rate / baseline (>1 = heats faster)
    double? CooldownRatio,       // recent cooldown rate / baseline (<1 = sheds slower)
    bool FanUndershoot,          // fan running well below baseline at the same load
    double? FanRatio,            // recent fan / baseline fan at load (>1 = working harder)
    double PowerCorrectionC,     // °C the power normalization moved the comparison
    double? GapC,                // current hotspot-to-edge gap under load
    double? GapBaselineC,        // this card's own learned healthy gap
    int ThrottleEvents,
    double RecentWindowHours,
    bool NearLimit,              // peaks within a couple °C of the silicon limit
    bool BeyondChassisNorm,      // heavy-load average past what the chassis sustains
    double? PowerRatio = null);  // recent W / baseline W when both sides carry power

/// <summary>Turns the gathered evidence into a ranked list of causes. Pure and
/// deterministic: same inputs, same diagnosis, no clocks or I/O.</summary>
public static class ThermalDiagnostician
{
    /// <summary>Confidence below this is too weak to show as a cause.</summary>
    public const double SurfaceFloor = 0.30;

    /// <summary>Power ratio beyond ±8% of baseline reads as a deliberate configuration
    /// change (overclock / raised limit / undervolt) rather than run-to-run variance.</summary>
    public const double PowerStateDeadband = 0.08;

    public static ThermalDiagnosis Diagnose(DiagnosisInputs e)
    {
        var findings = new List<CauseFinding>();
        Tells t = ReadTells(e);

        // --- Power / configuration: did the die move because power did? -------------
        // A large power-normalization correction means a chunk of the raw temperature
        // change is a wattage difference (overclock, undervolt, power limit), not cooling.
        double powerConf = PowerConfidence(e);
        if (powerConf >= SurfaceFloor)
        {
            string dir = e.PowerCorrectionC < 0
                ? "more power than the baseline learned (a boost or turbo mode switched on, a raised power limit, a faster power plan, or an overclock)"
                : "less power than the baseline learned (boost switched off, a lower power limit, a battery-saver plan, or an undervolt)";
            findings.Add(new CauseFinding(ThermalCause.PowerConfig, powerConf,
                $"About {Math.Abs(e.PowerCorrectionC):0.#}° of the difference is {dir}, not cooling. Judged at equal wattage, the cooler is treated fairly."));
        }

        // --- Mount / pump-out: hotspot far above the edge, widening vs own baseline --
        double mountConf = MountConfidence(e);
        if (mountConf >= SurfaceFloor)
            findings.Add(new CauseFinding(ThermalCause.Mount, mountConf, MountEvidence(e)));

        // --- Paste: load-dependent excess, corroborated by fast soak / slow cooldown --
        double pasteConf = PasteConfidence(e, t);
        if (pasteConf >= SurfaceFloor)
            findings.Add(new CauseFinding(ThermalCause.Paste, pasteConf, PasteEvidence(e, t)));

        // --- Airflow / dust: broad offset that reaches idle, fans harder, normal soak -
        double airflowConf = AirflowConfidence(e, t);
        if (airflowConf >= SurfaceFloor)
            findings.Add(new CauseFinding(ThermalCause.Airflow, airflowConf, AirflowEvidence(e, t)));

        // --- Fan fault: turning slower than baseline at the same load ----------------
        double fanConf = FanFaultConfidence(e, t);
        if (fanConf >= SurfaceFloor)
            findings.Add(new CauseFinding(ThermalCause.FanFault, fanConf, FanFaultEvidence()));

        // --- Cooling headroom: throttling / at the silicon wall ----------------------
        double headroomConf = HeadroomConfidence(e);
        if (headroomConf > 0)
            findings.Add(new CauseFinding(ThermalCause.CoolingHeadroom, headroomConf, HeadroomEvidence(e)));

        // --- High ambient: hot, but it's the room -----------------------------------
        if (e.BeyondChassisNorm && Math.Abs(t.Excess) < 2 && findings.All(f => f.Cause != ThermalCause.Paste))
        {
            findings.Add(new CauseFinding(ThermalCause.HighAmbient, 0.35,
                "Absolute temperatures are high, but the rise over the room matches baseline: this is the ambient, which DeltaT already corrects for, not a cooling fault."));
        }

        if (findings.Count == 0)
            return ThermalDiagnosis.Healthy;

        var ranked = findings
            .Where(f => f.Confidence >= SurfaceFloor)
            .OrderByDescending(f => f.Confidence)
            .Take(3)
            .ToList();

        return ranked.Count > 0 ? new ThermalDiagnosis(ranked) : ThermalDiagnosis.Healthy;
    }

    /// <summary>Health of every subsystem, from the same weighed evidence as the ranked
    /// diagnosis: 100 = behaving exactly like this machine's own baseline, lower = the
    /// evidence against it. Aspects the hardware can't measure (no hotspot sensor, no
    /// fan sensor, no package power) come back with a null score and "--", never a fake
    /// number. Pure and deterministic like everything else here.</summary>
    public static IReadOnlyList<AspectHealth> AssessAspects(DiagnosisInputs e)
    {
        Tells t = ReadTells(e);
        bool comparable = e.WeightedExcessC is not null || e.HeavyExcessC is not null;
        var list = new List<AspectHealth>(6);

        // Paste: judgeable once any like-for-like comparison exists.
        if (comparable)
        {
            double conf = PasteConfidence(e, t);
            list.Add(Meter(HealthAspect.Paste, conf, conf >= SurfaceFloor
                ? PasteEvidence(e, t)
                : "No load-dependent excess against this machine's own baseline. Heat crosses the joint like it always has."));
        }
        else
        {
            list.Add(Unknown(HealthAspect.Paste, "Not enough comparable load yet to judge the paste."));
        }

        // Airflow: same comparability requirement.
        if (comparable)
        {
            double conf = AirflowConfidence(e, t);
            list.Add(Meter(HealthAspect.Airflow, conf, conf >= SurfaceFloor
                ? AirflowEvidence(e, t)
                : "No broad, all-loads offset against baseline. The path from fins to air looks clear."));
        }
        else
        {
            list.Add(Unknown(HealthAspect.Airflow, "Not enough comparable load yet to judge airflow."));
        }

        // Fans: needs a fan sensor with baseline context.
        if (e.FanRatio is not null || e.FanUndershoot)
        {
            double conf = FanFaultConfidence(e, t);
            list.Add(Meter(HealthAspect.Fans, conf, conf >= SurfaceFloor
                ? FanFaultEvidence()
                : "Fans reach their usual speed for the same load."));
        }
        else
        {
            list.Add(NoSensor(HealthAspect.Fans, "No fan speed sensor exposed on this machine."));
        }

        // Mount: needs a hotspot sensor (GPUs, typically).
        if (e.GapC is { } gap)
        {
            double conf = MountConfidence(e);
            list.Add(Meter(HealthAspect.Mount, conf, conf >= SurfaceFloor
                ? MountEvidence(e)
                : e.GapBaselineC is { } bg
                    ? $"Hotspot-to-edge gap is {gap:0.#}°, at this card's own learned {bg:0.#}°. Heat spreads evenly across the mount."
                    : $"Hotspot-to-edge gap is {gap:0.#}° under load, within the healthy range."));
        }
        else
        {
            list.Add(NoSensor(HealthAspect.Mount, "No hotspot sensor exposed, so mount evenness can't be read. CPUs report a single die temperature, so this reads only on GPUs."));
        }

        // Headroom: always judgeable (throttle events are counted from day one).
        {
            double conf = HeadroomConfidence(e);
            list.Add(Meter(HealthAspect.Headroom, conf, conf > 0
                ? HeadroomEvidence(e)
                : "No throttle events recently; peaks stay clear of the silicon limit."));
        }

        // Power: a state, not a health. Stock / overclock / undervolt vs baseline watts.
        if (e.PowerRatio is { } pr)
        {
            // A watt reading, not a verdict, and not an accusation: the common reason a
            // machine draws more or less than its baseline is a boost/turbo mode or power
            // plan change, not an overclock. So the cell states the measured difference and
            // lets the tooltip list the reasons it could have.
            if (pr >= 1 + PowerStateDeadband)
                list.Add(new AspectHealth(HealthAspect.Power, null, $"+{(pr - 1) * 100:0}%",
                    $"Drawing about {(pr - 1) * 100:0}% more power than the baseline learned. Usually a boost or turbo mode turned on, a raised power limit, or a faster power plan; an overclock does it too. Comparisons are corrected to equal wattage, so this never counts against the cooling."));
            else if (pr <= 1 - PowerStateDeadband)
                list.Add(new AspectHealth(HealthAspect.Power, null, $"-{(1 - pr) * 100:0}%",
                    $"Drawing about {(1 - pr) * 100:0}% less power than the baseline learned. Usually boost turned off, a lower power limit, or a battery-saver plan; an undervolt does it too. Comparisons are corrected to equal wattage, so real degradation can't hide behind the lower heat."));
            else
                list.Add(new AspectHealth(HealthAspect.Power, null, "MATCHED",
                    "Package power sits where the baseline learned it, so the comparison needs no wattage correction."));
        }
        else
        {
            list.Add(NoSensor(HealthAspect.Power, "No package power sensor exposed, so comparisons use the raw rise."));
        }

        return list;
    }

    private static AspectHealth Meter(HealthAspect aspect, double conf, string detail)
    {
        // Align the meter's healthy zone with the diagnosis's surface floor. A confidence
        // too weak to name as a cause (< SurfaceFloor) must read Clear, not a "Watch" that
        // contradicts an Excellent overall verdict and a healthy tooltip on the same cell.
        // Below the floor the meter eases 100 → 85; at and above it, it spans the graded
        // 85 → 0 problem range. Continuous at the floor (both branches give 85 there), so
        // there is no jump. This is what makes the matrix and the ranked diagnosis agree
        // cell-for-cell: a sub-85 cell exists exactly when a cause would be surfaced. The
        // old unfloored 100·(1−conf) let a below-floor signal (e.g. the small residual a
        // power-state change leaves after normalization) render as a spurious "Watch"
        // while the score stayed 100 and no cause was named.
        conf = Clamp01(conf);
        double raw = conf <= SurfaceFloor
            ? 100 - 15 * (conf / SurfaceFloor)
            : 85 * (1 - conf) / (1 - SurfaceFloor);
        int score = (int)Math.Round(Math.Clamp(raw, 0, 100));
        string status = score >= 85 ? "Clear" : score >= 60 ? "Watch" : score >= 35 ? "Suspect" : "Failing";
        return new AspectHealth(aspect, score, status, detail);
    }

    private static AspectHealth Unknown(HealthAspect aspect, string detail) =>
        new(aspect, null, "--", detail);

    /// <summary>Like <see cref="Unknown"/>, but the reading is missing because this hardware
    /// exposes no sensor for it (a CPU has no hotspot, a chassis no fan tach) — a permanent
    /// fact of the machine, not a transient "still learning". Flagged so a consumer can tell
    /// the two apart, rather than treating every "--" as work-in-progress.</summary>
    private static AspectHealth NoSensor(HealthAspect aspect, string detail) =>
        new(aspect, null, "--", detail, Unmeasurable: true);

    // ------------------------------------------------------------------ evidence math
    // One set of tells and confidence formulas feeds BOTH the ranked diagnosis and the
    // per-aspect readout, so the two can never disagree about the same machine.

    private readonly record struct Tells(
        double Excess, double Heavy, double Idle, bool HaveIdle,
        bool FastSoak, bool SlowCool, bool NormalSoak, bool FansHarder, bool IdleLifted, bool LoadDependent);

    private static Tells ReadTells(DiagnosisInputs e)
    {
        double excess = e.WeightedExcessC ?? 0;
        double heavy = e.HeavyExcessC ?? 0;
        double idle = e.IdleExcessC ?? double.NaN;
        bool haveIdle = e.IdleExcessC is not null;

        // Discriminators that separate a paste joint (die → heatsink base) from an
        // airflow problem (heatsink fins → air). They fail in opposite ways: bad paste
        // makes the die race ahead of the base, so it heat-soaks FAST and barely moves
        // idle; lost airflow keeps the soak rate NORMAL but lifts EVERY load (idle too)
        // and makes the fans spin HARDER to hold the line.
        bool fastSoak = e.SoakRatio is { } srx && srx > 1.15;
        bool slowCool = e.CooldownRatio is { } crx && crx < ScoringEngine.CooldownSlowdownRatio;
        bool fansHarder = e.FanRatio is { } frx && frx > 1.12;
        bool idleLifted = haveIdle && idle > 2;
        bool loadDependent = heavy > 2 && (!haveIdle || heavy - idle > 3);

        return new Tells(excess, heavy, idle, haveIdle, fastSoak, slowCool, !fastSoak, fansHarder, idleLifted, loadDependent);
    }

    private static double PowerConfidence(DiagnosisInputs e) =>
        Clamp01((Math.Abs(e.PowerCorrectionC) - 2.0) / 8.0);

    private static double MountConfidence(DiagnosisInputs e)
    {
        if (e.GapC is not { } gap)
            return 0;
        double gapDrift = e.GapBaselineC is { } bg ? gap - bg : 0;
        if (e.GapBaselineC is not null && gapDrift > ScoringEngine.HotspotDriftDeadbandC)
            return Clamp01((gapDrift - ScoringEngine.HotspotDriftDeadbandC) / 10.0);
        if (e.GapBaselineC is null && gap >= ScoringEngine.HotspotGapPenaltyC)
            return Clamp01((gap - ScoringEngine.HotspotGapPenaltyC) / 12.0) * 0.8;
        return 0;
    }

    private static string MountEvidence(DiagnosisInputs e) =>
        e.GapBaselineC is { } bb
            ? $"Hotspot gap widened to {e.GapC:0.#}° from this card's own {bb:0.#}°: heat trapped at one point, the signature of pump-out or an uneven mount."
            : $"Hotspot runs {e.GapC:0.#}° above the edge under load: heat pooling at one spot rather than spreading across the mount.";

    private static double PasteConfidence(DiagnosisInputs e, Tells t)
    {
        double pasteConf = 0;
        if (t.LoadDependent)
        {
            pasteConf = Clamp01((t.Heavy - 2.0) / 9.0);
            int corroborations = (t.FastSoak ? 1 : 0) + (t.SlowCool ? 1 : 0);
            pasteConf = Clamp01(pasteConf * (1.0 + 0.35 * corroborations));
            // A load-independent offset with a normal soak rate and fans straining is the
            // fin stack losing airflow, not the die-to-base joint: pull paste down so dust
            // can lead. Bad paste does the opposite (fast soak, fans normal, idle flat).
            if (t.NormalSoak && t.FansHarder && t.IdleLifted) pasteConf *= 0.30;
            else if (t.NormalSoak && (t.FansHarder || t.IdleLifted)) pasteConf *= 0.55;
        }
        else if (t.FastSoak && t.SlowCool)
        {
            // Even without a clear excess yet, a fast soak AND a slow cooldown together
            // are a paste-conduction signature worth naming early.
            pasteConf = 0.4;
        }
        return pasteConf;
    }

    private static string PasteEvidence(DiagnosisInputs e, Tells t)
    {
        var bits = new List<string>();
        if (t.LoadDependent) bits.Add($"runs {t.Heavy:0.#}° hotter than baseline under load but not at idle");
        if (t.FastSoak && e.SoakRatio is { } s) bits.Add($"heat-soaks {(s - 1) * 100:0}% faster");
        if (t.SlowCool && e.CooldownRatio is { } c) bits.Add($"sheds heat {(1 - c) * 100:0}% slower");
        return Capitalize(string.Join(", ", bits)) + ". Heat is struggling to cross the paste.";
    }

    private static double AirflowConfidence(DiagnosisInputs e, Tells t)
    {
        // Requires a genuine dust tell (fans working harder, or a lifted idle, or an
        // across-the-board offset with a normal soak). Broad excess with a FAST soak is
        // paste, not dust, so it is deliberately not enough on its own.
        bool dustTell = t.FansHarder || t.IdleLifted;
        if (e.BroadExcess && t.Excess > 2 && (dustTell || (t.NormalSoak && t.Excess > 3)))
        {
            double airflowConf = Clamp01((t.Excess - 2.0) / 8.0);
            if (t.FansHarder) airflowConf += 0.25;
            if (t.IdleLifted) airflowConf += 0.15;
            if (t.NormalSoak) airflowConf += 0.10;
            return Clamp01(airflowConf);
        }
        if (t.FansHarder && t.Excess > 1.5)
            return 0.45; // fans clearly straining to hold temps: airflow is being lost
        return 0;
    }

    private static string AirflowEvidence(DiagnosisInputs e, Tells t)
    {
        string fanBit = t.FansHarder ? " while the fans spin harder than they used to for the same result" : "";
        string idleBit = t.IdleLifted ? " (idle included)" : "";
        return $"A steady {t.Excess:0.#}° offset across the load range{idleBit}{fanBit}. That broad, soak-normal pattern is dust or blocked airflow, not the paste.";
    }

    private static double FanFaultConfidence(DiagnosisInputs e, Tells t)
    {
        if (!e.FanUndershoot)
            return 0;
        return t.Excess > 3 ? 0.6 : 0.45; // slower fan AND hotter = it's costing real degrees
    }

    private static string FanFaultEvidence() =>
        "A fan is running slower than this machine's own baseline at the same load. If you didn't set a quieter profile, that points at a failing fan or a blocked intake, not the paste.";

    private static double HeadroomConfidence(DiagnosisInputs e)
    {
        if (e.ThrottleEvents > 0)
        {
            double perDay = e.ThrottleEvents / Math.Max(1.0, e.RecentWindowHours / 24.0);
            return Clamp01(0.4 + perDay * 0.15);
        }
        return e.NearLimit ? 0.35 : 0;
    }

    private static string HeadroomEvidence(DiagnosisInputs e) =>
        e.ThrottleEvents > 0
            ? $"Hit the thermal limit {e.ThrottleEvents}× recently and pulled clocks back. Whatever the root cause, there's no headroom left."
            : "Peaks are right at the silicon limit with no headroom to spare.";

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
