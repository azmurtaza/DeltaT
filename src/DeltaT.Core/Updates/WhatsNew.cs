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
        new WhatsNewRelease(new Version(2, 3, 0),
            "This release fixes calibration getting stuck at 80%, adds a RAM usage card, and adds an Intel power-budget reading that can tell a machine held back by heat from one that is simply set to run cooler.",
            new WhatsNewItem[]
            {
                // --- Fixes ---
                new("Fixed: calibration stuck at 80%",
                    "Several people reported calibration sitting at 80% and never finishing, especially for the GPU. There were two causes and both are fixed. First, a load bucket the machine only visited once (say it briefly crossed into a different weather band) counted just as heavily as one backed by weeks of data, so a single thin reading could hold a well-learned baseline below the finish line forever. DeltaT now weights each reading by how much evidence actually backs it, so a rare one-off can no longer veto the rest. Second, the confidence check compared raw temperature readings, but a GPU pulls very different wattage from game to game, which made its readings look noisier than they are. It now compares them adjusted for power draw, the same way the score itself already did, so genuine consistency is finally recognised. Measured result: a stable machine now locks in about three sessions, and a scattered GPU that used to be stuck now calibrates reliably."),
                new("New: calibration tells you exactly what it is waiting for",
                    "While calibrating, DeltaT now names the specific load and weather it still needs more of, and how close that reading is to being tight enough, instead of a vague 'a few more sessions'. If your usage or a workout never fully loads a component, you can now see that is why it is waiting."),

                // --- New features ---
                new("New: RAM usage card",
                    "The dashboard now shows a memory card beneath the battery: current RAM usage with a used and total gigabytes readout and its own icon, matching the other components. It is a live readout only. Memory has no thermal paste, so it is never scored or mixed into any thermal verdict."),
                new("New: Intel power-budget reading tells heat apart from a power setting",
                    "On Intel CPUs, DeltaT now reads the chip's own configured power budget (PL1 and PL2) and, crucially, which limit is holding it back at any moment. This answers a question raw temperature cannot: when a CPU is not reaching its full power, is that because cooling is the ceiling, or because it is deliberately set to run cooler (boost off, a battery or quiet power plan)? DeltaT only calls a machine 'thermally constrained' when the chip itself reports that heat, not a power setting, is the active limit while it draws below its budget. That reading sharpens the fan, airflow, and mount diagnoses when heat really is the cause, and it never counts a deliberately power-limited machine as a fault. You can see the budget on the Device page under Silicon limits, and a fingerprint test now gives a day-one verdict on whether cooling is your ceiling, no baseline required. This is Intel-only for now; AMD exposes its limits differently."),
                new("New: the fingerprint test no longer locks up the app",
                    "The fingerprint window used to hold the rest of DeltaT hostage for the two to four minutes it ran, so you could not check the dashboard or your history while the test was going. It is now an ordinary window: leave it open and keep using the app. Clicking the button again while a test is running brings that window back to the front instead of kicking off a second test on top of the first. The feedback window works the same way now."),
                new("New: your contact details are remembered on feedback reports",
                    "If you fill in an email or handle when you send a bug report or idea, DeltaT remembers it for the next one, so following up on a report does not mean typing it in again. It stays on your machine and is only ever sent with a report you choose to send."),

                // --- Refinements ---
                new("Fixed: the headline verdict is now colored, and stops naming one chip when both agree",
                    "The big verdict line at the top of the dashboard was plain white text, which made 'Excellent' look exactly as urgent as 'Degraded'. It now carries the same color as the health dials, green when healthy through to red when it needs attention, so the state reads at a glance. It also used to name whichever of your CPU and GPU scored worse, so a perfectly healthy machine read 'GPU: Excellent' and left you wondering whether the CPU had even been checked. When both agree it now simply says the verdict once, and only names a specific component when that is the actual information, meaning that one needs attention and the other does not."),
            }),
        new WhatsNewRelease(new Version(2, 2, 1),
            "This release brings fan speed readings to Lenovo Legion laptops, adds a 12-hour clock option and proper branded notifications, and fixes a GPU test crash, the auto-updater, and a clipped window.",
            new WhatsNewItem[]
            {
                // --- New features ---
                new("New: fan speed readings on Lenovo Legion and LOQ laptops",
                    "DeltaT can now read CPU and GPU fan RPM on Legion and LOQ machines. Fan speed is what lets DeltaT tell 'cooler because the fans are working harder' apart from 'genuinely cooling better', so without it the score falls back to raw temperature differences. Lenovo has moved this reading across three firmware interfaces over the years and left the old ones answering zero, which made a modern Legion look like it had no fans at all. DeltaT now tries all three and uses whichever actually responds. Verified on a Legion 7i Gen 9, and strictly read only, so your fan curves are never touched."),
                new("New: 12-hour clock option",
                    "Settings now has a toggle for 12-hour times with AM/PM instead of 24-hour. It applies everywhere a time appears: the history graphs and their hover readout, the remarks feed, and the dashboard. Takes effect straight away, no restart needed."),
                new("New: notifications now carry the DeltaT mark",
                    "Notifications now show the DeltaT logo as their main icon, which Windows 10 and 11 render as a proper toast card rather than a plain system balloon, so it is clear at a glance which app is talking. The tray icon also shows the logo during startup, before the first temperature reading arrives."),

                // --- Fixes ---
                new("Fixed: the GPU test could crash DeltaT on some newer graphics drivers",
                    "The GPU fingerprint now runs its load in a separate background process. On some early drivers for the newest NVIDIA cards (seen on an RTX 50-series laptop whose GPU also drives the display), that load could fault at the driver level, and a fault of that kind cannot be caught from inside the app, so it took DeltaT down mid-test. Isolated in its own process, it now kills only the helper and the test reports the failure instead."),
                new("Fixed: update failing with a 'cannot find the file specified' error",
                    "On some machines, especially DeltaT installed outside the default Program Files location, applying an update failed with 'The system cannot find the file specified'. DeltaT was launching its update helper by short name from its own install folder, which some Windows setups couldn't resolve. It now uses the helper's full system path from a location that always exists."),
                new("Fixed: buttons cut off at the bottom of this window",
                    "On some screen sizes and display scaling settings, the buttons along the bottom of this what's new window were clipped by the window edge and could not be clicked. It now sizes itself so they always fit."),

                // --- Notes ---
                new("Note: DeltaT now has a written privacy policy",
                    "There is now a full privacy policy on the project page. Nothing has changed, it just writes down what was always true: your readings, baselines and history stay in a database on your own machine. The only things that leave are the weather lookup, the version check, and a feedback report if you send one."),
            }),
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
