namespace DeltaT.Core.Monitoring;

/// <summary>What a laptop's embedded controller will tell us through its vendor WMI
/// interface: fan RPM, and (where the firmware publishes it) the EC's own CPU temperature.
/// A null field means "not exposed / not trustworthy" and is rendered as "--" upstream —
/// never faked as 0.
///
/// The temperature is a **fallback**, never the primary reading: it is whole-degree and
/// EC-paced, where the MSR path is precise and instant. It earns its place by needing no
/// kernel driver at all — the same firmware channel NitroSense uses — so a machine without
/// PawnIO still shows a real CPU temperature instead of falling all the way back to the
/// coarse ACPI thermal zone.</summary>
public readonly record struct LaptopFanSample(double? CpuRpm, double? GpuRpm, double? CpuTempC = null)
{
    /// <summary>Airflow data specifically: this is what latches a vendor probe, so a probe
    /// answering only a temperature can never win the machine for the wrong vendor.</summary>
    public bool HasAny => CpuRpm is not null || GpuRpm is not null;
}

/// <summary>One vendor's read-only laptop-fan probe. Implementations must never invoke
/// a firmware method that changes state (no fan curves, profiles, LEDs) — they only
/// read. A probe self-marks <see cref="IsDead"/> once it's certain this isn't its
/// hardware (its WMI class is absent, or it never once answered), so the coordinator
/// can stop calling it.</summary>
public interface ILaptopFanProbe : IDisposable
{
    /// <summary>Short vendor tag for diagnostics, e.g. "Acer", "Lenovo".</summary>
    string Vendor { get; }

    /// <summary>True once this probe has ruled itself out for this machine.</summary>
    bool IsDead { get; }

    LaptopFanSample Read();
}

/// <summary>Picks the one laptop-fan probe that fits this machine and sticks with it.
/// Laptop fans hide behind the EC where LibreHardwareMonitor sees nothing; each supported
/// vendor (Acer Nitro/Predator, Lenovo Legion/LOQ, ASUS ROG/TUF, HP business-class)
/// exposes them through its own read-only WMI interface instead. A machine is exactly one
/// vendor, so on the first sample that yields real RPM the winning probe is latched and
/// the rest disposed; if none ever answers (unsupported laptop, desktop, non-elevated) the
/// reader simply stays dark and every FanRpm remains null.
///
/// Adding a vendor is one new <see cref="ILaptopFanProbe"/> in the constructor list.
/// HP's consumer gaming line (Omen/Victus) has no WMI RPM getter, so its fans are read from
/// the Embedded Controller through <see cref="HpOmenEcFanReader"/> — but via LHM 0.9.6's
/// signed-PawnIO, mutex-arbitrated EC path, not a bare port poke, and gated hard to HP
/// OMEN/Victus hardware. Still uncovered: MSI, whose fans need a different EC access again.</summary>
public sealed class LaptopFanReader : IDisposable
{
    private readonly List<ILaptopFanProbe> _probes;
    private ILaptopFanProbe? _winner;

    // Vendor-neutral sanity ceiling. No laptop EC/WMI fan tachometer legitimately reports above
    // this; a higher value is a torn or mis-decoded read (an EC updates its 16-bit fan word
    // non-atomically, so a poll landing mid-update can splice a stale byte onto a fresh one and
    // produce a wild number). Each probe also range-checks in its own units; this is the final
    // net that catches any vendor, so a bogus spike surfaces as "--" for that tick instead of a
    // fake RPM. A real 0 (a parked fan that a latched probe has confirmed) passes untouched.
    private const double MaxPlausibleRpm = 8000;

    // Order is audition order. Acer first: it is the one verified on real hardware, so a
    // machine that has it never spends a sample on a probe that has to be gated dark.
    public LaptopFanReader()
        : this(new ILaptopFanProbe[]
        {
            new AcerWmiFanReader(),
            new LenovoWmiFanReader(),
            new AsusWmiFanReader(),
            new HpWmiFanReader(),
            // Cooperative source for HP's consumer gaming line: if OmenMon is running it owns the
            // EC and publishes fan RPM on a named pipe, so we read that and never open the EC
            // ourselves. Auditions immediately before the EC probe, so when the pipe answers the
            // EC path is never reached and OmenMon keeps working (issue #1).
            new OmenMonPipeFanReader(),
            // Last: the only EC-based probe. It auditions after HP's business WMI (so a business
            // HP never reaches the EC path) and self-gates dark on anything but an OMEN/Victus,
            // and yields the EC entirely when OmenMon is running.
            new HpOmenEcFanReader(),
        })
    {
    }

    // Test/diagnostic seam.
    public LaptopFanReader(IEnumerable<ILaptopFanProbe> probes) => _probes = probes.ToList();

    /// <summary>Vendor tag of the latched probe, or null until one answers.</summary>
    public string? ActiveVendor => _winner?.Vendor;

    public LaptopFanSample Read()
    {
        if (_winner is not null)
            return Sanitize(_winner.Read());

        // Still auditioning: try each probe that hasn't ruled itself out. The first to
        // return real airflow wins the machine for good. Parked fans (a valid 0) don't
        // win — they read as no-data-yet, so a probe answering "0 rpm, fan idle" keeps
        // its turn without being mistaken for the wrong vendor.
        foreach (ILaptopFanProbe p in _probes)
        {
            if (p.IsDead)
                continue;
            LaptopFanSample sample = Sanitize(p.Read());
            if (sample.HasAny)
            {
                _winner = p;
                foreach (ILaptopFanProbe other in _probes)
                {
                    if (!ReferenceEquals(other, p))
                        other.Dispose();
                }
                return sample;
            }
        }
        return default;
    }

    /// <summary>Drop any RPM outside the plausible laptop-fan range to null, so a torn or
    /// mis-decoded spike shows "--" for that tick rather than a fake number. A confirmed 0
    /// (parked fan) and any absent (null) reading pass through unchanged.</summary>
    private static LaptopFanSample Sanitize(LaptopFanSample s) =>
        s with { CpuRpm = Plausible(s.CpuRpm), GpuRpm = Plausible(s.GpuRpm) };

    private static double? Plausible(double? rpm) =>
        rpm is { } r && r >= 0 && r <= MaxPlausibleRpm ? r : null;

    public void Dispose()
    {
        foreach (ILaptopFanProbe p in _probes)
            p.Dispose();
    }
}
