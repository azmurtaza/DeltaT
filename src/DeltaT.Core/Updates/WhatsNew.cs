namespace DeltaT.Core.Updates;

/// <summary>One highlight in a release's what's-new popup: a short bold title and a
/// plain-English line on why it matters. Keep the voice the release notes use.</summary>
public sealed record WhatsNewItem(string Title, string Body);

/// <summary>The curated highlights for one release, shown once when a user upgrades INTO it.</summary>
public sealed record WhatsNewRelease(Version Version, string Intro, IReadOnlyList<WhatsNewItem> Items);

/// <summary>The static, per-release what's-new copy. Add one entry each release you want to
/// announce on upgrade, keyed to the exact <c>Major.Minor.Build</c> you ship in
/// <c>DeltaT.App.csproj</c> &lt;Version&gt;. A release with no entry here simply shows no
/// popup (the version is still recorded, so nothing pops later). This is deliberately data,
/// not logic, so updating it each release is a one-line edit.</summary>
public static class WhatsNewNotes
{
    public static readonly IReadOnlyList<WhatsNewRelease> Releases = new[]
    {
        // NOTE: bump this Version to match the shipped <Version> when these features release.
        // Style: this is a technical app, so cover EVERY new thing in real detail (see the
        // "What's-new content" rule in CLAUDE.md). One item per genuinely new capability, each
        // body explaining what it does, how it works, and why it matters. No "a few small changes".
        new WhatsNewRelease(new Version(2, 2, 0),
            "This release adds a fixed-temperature scoring mode and a tip jar, and fixes GPU detection, OmenMon coexistence, and a few unit and settings issues.",
            new WhatsNewItem[]
            {
                // --- New features ---
                new("New: score against a fixed indoor temperature",
                    "For a climate-controlled room, set your actual indoor temperature (Settings, Location & weather) and DeltaT scores your CPU/GPU rise over that instead of the outside weather. It keeps a separate baseline for this mode, so switching back and forth never mixes the two, and detection accuracy is unchanged."),
                new("New: Support DeltaT",
                    "You can now tip in crypto if DeltaT earned its keep. Totally optional. DeltaT stays free, no account, nothing collected."),
                new("New: tunable weather refresh",
                    "You can now set how often the outside temperature refreshes (1, 2, or 3 hours) instead of a fixed 3 hours. Handy near the equator where it swings hour to hour. Applies immediately."),
                new("New: warning notifications you can click",
                    "A warning toast now opens straight to the Remarks feed, where the full explanation and suggested fix live, instead of leaving you to find it."),
                new("New: this what's-new screen",
                    "DeltaT now shows a one-time summary like this after an update, so a new version never changes behaviour on you silently. Never on a fresh install, and only once per version."),

                // --- Fixes and refinements ---
                new("Fixed: wrong GPU on hybrid AMD laptops",
                    "On laptops with an AMD Ryzen chip (with built-in Radeon graphics) plus a separate gaming GPU, DeltaT could latch onto the sensorless integrated Radeon, so the dashboard or GPU fingerprint read the wrong chip or showed no sensor. It was worst when the gaming GPU had powered itself down to save battery (common on ASUS ROG and TUF laptops): with the real card asleep and invisible, DeltaT mistook the built-in Radeon for it, and because that Radeon shares a chip with the CPU, a GPU fingerprint could even record the CPU's temperature. DeltaT now asks Windows which cards physically exist, so it knows your gaming GPU is there even while it sleeps and never mixes the two up. An APU-only machine (no separate GPU) still keeps its readout."),
                new("Fixed: clearer message when the GPU fingerprint can't run",
                    "If your dedicated GPU is idle or switched off when you start a GPU fingerprint, DeltaT now tells you to wake it and try again, right away, instead of running the full load for a minute and a half and then failing with a confusing 'not enough sensor samples' error."),
                new("Fixed: OmenMon no longer clashes with DeltaT",
                    "On HP OMEN and Victus laptops, DeltaT reading fan RPM from the embedded controller could knock out OmenMon's readings (and the reverse). DeltaT now detects OmenMon and steps aside, reading fan speeds from its feed instead, so neither loses data."),
                new("Fixed: clearer indoor-mode reference",
                    "In fixed mode the Device view now reads 'indoor reference (fixed)' instead of calling it the outside reference, and no longer double-applies the weather-mode display offset."),
            }),
    };

    /// <summary>The highlights for a version, matched on Major.Minor.Build, or null when the
    /// release has no curated notes.</summary>
    public static WhatsNewRelease? For(Version v) =>
        Releases.FirstOrDefault(r =>
            r.Version.Major == v.Major && r.Version.Minor == v.Minor && r.Version.Build == v.Build);
}

/// <summary>Pure decision for whether to pop the what's-new window this launch. Separated from
/// the app shell so the once-per-upgrade rule (and the "never on a fresh install" rule) is
/// unit-tested rather than trusted. The caller always records the running version afterwards,
/// whatever this returns, so the popup can only ever fire once per version.</summary>
public static class WhatsNewGate
{
    /// <summary>Normalize to the three-part version the store records ("M.m.b"), dropping the
    /// build's revision field.</summary>
    public static string VersionKey(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Returns the notes to show now, or null. Shows only on a genuine upgrade into a
    /// version that has curated notes: never on a first-ever run (onboarding covers that), never
    /// for a version already shown, never on a downgrade.</summary>
    public static WhatsNewRelease? Evaluate(Version running, string? lastShown, bool firstRun)
    {
        if (firstRun)
            return null; // fresh install: onboarding is the welcome, not a changelog
        var run = new Version(running.Major, running.Minor, Math.Max(0, running.Build));
        if (Version.TryParse(lastShown, out Version? shown) && shown >= run)
            return null; // already shown for this version (or newer), or a downgrade
        return WhatsNewNotes.For(run);
    }
}
