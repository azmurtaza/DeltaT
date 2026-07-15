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

    // Order is audition order. Acer first: it is the one verified on real hardware, so a
    // machine that has it never spends a sample on a probe that has to be gated dark.
    public LaptopFanReader()
        : this(new ILaptopFanProbe[]
        {
            new AcerWmiFanReader(),
            new LenovoWmiFanReader(),
            new AsusWmiFanReader(),
            new HpWmiFanReader(),
            // Last: the only EC-based probe. It auditions after HP's business WMI (so a business
            // HP never reaches the EC path) and self-gates dark on anything but an OMEN/Victus.
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
            return _winner.Read();

        // Still auditioning: try each probe that hasn't ruled itself out. The first to
        // return real airflow wins the machine for good. Parked fans (a valid 0) don't
        // win — they read as no-data-yet, so a probe answering "0 rpm, fan idle" keeps
        // its turn without being mistaken for the wrong vendor.
        foreach (ILaptopFanProbe p in _probes)
        {
            if (p.IsDead)
                continue;
            LaptopFanSample sample = p.Read();
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

    public void Dispose()
    {
        foreach (ILaptopFanProbe p in _probes)
            p.Dispose();
    }
}
