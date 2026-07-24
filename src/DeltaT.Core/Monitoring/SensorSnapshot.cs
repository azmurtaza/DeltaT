namespace DeltaT.Core.Monitoring;

/// <summary>Intel-only absolute power-budget telemetry, read live from the CPU's own MSRs
/// (never cached: BIOS/EC/DPTF can move the limits at runtime). It answers "how much of the
/// platform's own configured power budget is the CPU actually reaching, and if it is short,
/// WHY". This is what lets DeltaT tell a genuinely thermally-constrained machine from one that
/// is deliberately power-limited (boost off, a low power plan) without false-alarming the
/// latter. Null on AMD (a different register set) and when the kernel driver is absent.</summary>
public sealed record CpuPowerLimitInfo(
    // Configured RAPL limits (MSR 0x610): PL1 sustained watts, PL2 short-duration turbo
    // watts, and Tau (the PL2 time window). Null when a field can't be decoded.
    double? Pl1W,
    double? Pl2W,
    double? TauSeconds,
    // Which limiter, if any, is asserting RIGHT NOW (MSR 0x64F status bits). These are
    // mutually non-exclusive; more than one can be set. The whole point of the feature is
    // that a power deficit only means "cooling" when ThermalActive is the reason.
    bool ThermalActive,   // PROCHOT / thermal / RATL / VR-thermal: heat is the limiter
    bool PowerLimitActive, // RAPL PL1/PL2: the configured power budget is the limiter (by design)
    bool CurrentLimitActive); // EDP / VR TDC: a current/VRM limit, not heat

/// <summary>One component's state at a single sampling instant. Nullable fields
/// mean "this hardware doesn't expose that sensor" — never guess a value.</summary>
public sealed record ComponentReading(
    ComponentKind Kind,
    string Name,
    double? TemperatureC,
    double? HotspotC,
    double? LoadPercent,
    double? FanRpm,
    double? PowerW,
    double? WearPercent,
    bool IsThrottling,
    double? ThrottleLimitC,
    // Battery charge/discharge cycle count, when the firmware exposes it (most do
    // not — then it stays null and is shown as "--", never faked as 0).
    double? BatteryCycles = null,
    // System memory (RAM) usage, for the display-only RAM card. Used/total gibibytes,
    // when the component is memory; null on every other component.
    double? MemUsedGb = null,
    double? MemTotalGb = null,
    // Intel-only CPU power-budget telemetry (PL1/PL2 + active-limit reason). Null on AMD,
    // on non-CPU components, and when the kernel driver can't read the MSRs.
    CpuPowerLimitInfo? PowerLimit = null)
{
    /// <summary>Stable identity for storage/UI (a machine can have several drives).
    /// Materialized once — it gets used as a dictionary key on every sample.</summary>
    public string Id { get; } = $"{Kind}:{Name}";

    public LoadBucket? Bucket => LoadPercent is { } p ? LoadBuckets.FromPercent(p) : null;
}

public sealed record SensorSnapshot(
    DateTimeOffset TimestampUtc,
    bool OnAcPower,
    IReadOnlyList<ComponentReading> Components)
{
    public ComponentReading? Find(ComponentKind kind)
    {
        for (int i = 0; i < Components.Count; i++)
        {
            if (Components[i].Kind == kind)
                return Components[i];
        }
        return null;
    }
}

/// <summary>The only doorway to sensors. The hardware implementation wraps
/// LibreHardwareMonitor; the simulated one powers dev/demo/tests.</summary>
public interface ISensorSource : IDisposable
{
    SensorSnapshot Read();
}
