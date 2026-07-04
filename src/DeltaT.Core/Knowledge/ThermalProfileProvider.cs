using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaT.Core.Machine;

namespace DeltaT.Core.Knowledge;

public sealed record ComponentProfile(
    [property: JsonPropertyName("typicalIdleDeltaC")] double TypicalIdleDeltaC,
    [property: JsonPropertyName("typicalHeavyDeltaC")] double TypicalHeavyDeltaC,
    [property: JsonPropertyName("sustainedNormC")] double SustainedNormC,
    [property: JsonPropertyName("concernC")] double ConcernC);

public sealed record MatchRule(
    [property: JsonPropertyName("manufacturerContains")] string[]? ManufacturerContains,
    [property: JsonPropertyName("modelContains")] string[]? ModelContains,
    [property: JsonPropertyName("isLaptop")] bool? IsLaptop);

public sealed record ThermalProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("match")] MatchRule? Match,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("cpu")] ComponentProfile? Cpu,
    [property: JsonPropertyName("gpu")] ComponentProfile? Gpu);

/// <summary>Resolves the machine's thermal personality: exact model → brand
/// series → category. The data ships embedded so lookups work offline forever.</summary>
public static class ThermalProfileProvider
{
    private sealed record ProfileFile([property: JsonPropertyName("profiles")] List<ThermalProfile> Profiles);

    private static readonly Lazy<IReadOnlyList<ThermalProfile>> All = new(Load);

    private static IReadOnlyList<ThermalProfile> Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("thermal-profiles.json")
            ?? throw new InvalidOperationException("thermal-profiles.json missing from embedded resources");
        return JsonSerializer.Deserialize<ProfileFile>(stream)!.Profiles;
    }

    public static ThermalProfile Resolve(MachineIdentity machine, bool hasDiscreteGpu)
    {
        string haystackModel = $"{machine.Model} {machine.SystemFamily}".ToLowerInvariant();
        string manufacturer = machine.Manufacturer.ToLowerInvariant();

        ThermalProfile? best = All.Value
            .Where(p => Matches(p.Match, manufacturer, haystackModel, machine.IsLaptop))
            .Where(p => p.Match?.ModelContains is not null || p.Match?.ManufacturerContains is not null) // brand/model rules only here
            .OrderByDescending(p => p.Priority)
            .FirstOrDefault();

        if (best is not null)
            return best;

        // Category fallbacks.
        string fallbackId = machine.IsLaptop
            ? hasDiscreteGpu ? "generic-gaming-laptop" : "thin-light-laptop"
            : "generic-desktop";
        return All.Value.First(p => p.Id == fallbackId);
    }

    private static bool Matches(MatchRule? rule, string manufacturer, string model, bool isLaptop)
    {
        if (rule is null)
            return false;
        if (rule.IsLaptop is { } laptop && laptop != isLaptop)
            return false;
        if (rule.ManufacturerContains is { Length: > 0 } mans && !mans.Any(manufacturer.Contains))
            return false;
        if (rule.ModelContains is { Length: > 0 } models && !models.Any(model.Contains))
            return false;
        return true;
    }
}
