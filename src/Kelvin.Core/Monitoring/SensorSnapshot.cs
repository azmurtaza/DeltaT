namespace Kelvin.Core.Monitoring;

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
    double? ThrottleLimitC)
{
    /// <summary>Stable identity for storage/UI (a machine can have several drives).</summary>
    public string Id => $"{Kind}:{Name}";

    public LoadBucket? Bucket => LoadPercent is { } p ? LoadBuckets.FromPercent(p) : null;
}

public sealed record SensorSnapshot(
    DateTimeOffset TimestampUtc,
    bool OnAcPower,
    IReadOnlyList<ComponentReading> Components)
{
    public ComponentReading? Find(ComponentKind kind) =>
        Components.FirstOrDefault(c => c.Kind == kind);
}

/// <summary>The only doorway to sensors. The hardware implementation wraps
/// LibreHardwareMonitor; the simulated one powers dev/demo/tests.</summary>
public interface ISensorSource : IDisposable
{
    SensorSnapshot Read();
}
