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
        new WhatsNewRelease(new Version(2, 4, 0),
            "This release is driven by user reports. It adds cooling setups so a laptop cooler pad turned down stops reading as failing paste, makes the GPU stress test work the whole card instead of a fraction of it, and stops DeltaT from learning its own stress test as if it were something your machine normally does.",
            new WhatsNewItem[]
            {
                // --- New features ---
                new("New: cooling setups, for anyone using a cooler pad",
                    "If you use a laptop cooler, or take a side panel off, or switch fan profiles depending on what you are doing, DeltaT used to punish you for it. It cannot see airflow it has no sensor for, so turning a cooler down just looks like the machine running hotter, and a machine running hotter at the same load and the same weather is what degrading paste looks like. One user with an adjustable sealed cooler was being told the liquid metal on both his CPU and GPU was failing, when all he had done was turn the cooler's fans down. Settings now has a Cooling setup card: name each arrangement you actually use, and DeltaT learns a completely separate normal for each one. Switching between them is instant, from Settings or from a new submenu on the tray icon, and it never relearns or throws anything away, so you can flip back and forth as often as you like. A new setup does have to calibrate from scratch the first time, since it has no history yet. Recalibrate is still the right button for a one-off permanent change like new fans or a clean-out; this is for the changes you make and undo."),

                // --- Fixes ---
                new("Fixed: DeltaT's own stress test was being learned as if it were normal use",
                    "The built-in stress test is a measuring tool, not something your machine actually does, but its minutes were being recorded into the learned history alongside your real workloads. That matters most on the GPU, where the test's compute load reaches roughly 120 W on a card that pulls 139 W in FurMark, because pinning the card to 100% usage puts those minutes in the full-load bucket at a wattage no game ever produces. On a machine that had not gamed recently, the test became the only full-load reading DeltaT had, so the power column reported the card drawing around 11% under its own baseline. Running a real load for ten minutes made it read MATCHED again with nothing else changed, which is exactly the symptom one user reported. The test's minutes are now kept out of the learned history entirely, including the few minutes afterwards while the chip is still cooling, and it no longer manufactures its own heat-soak, cooldown or throttle readings. The run still appears on the temperature charts and still records its result in fingerprint history as before. Measured on a simulated 140 W card: the power state was misreported on 357 of 400 healthy weeks before this and 2 of 400 after, with no loss of detection on a machine whose paste really is degrading."),

                new("Fixed: the GPU stress test now works the whole card, not just its maths units",
                    "The built-in GPU test was leaving power on the table. Its workload was pure arithmetic running out of the chip's registers, which left the memory system completely idle, and it waited for each batch of work to finish before sending the next, so the card kept draining and sitting still between them. On a 140 W laptop RTX 3060 that came out around 120 W where FurMark v2 reaches 139 W, and a test that only ever loads a card to 86% is measuring a machine you never actually run. The test now streams a 64 MB buffer alongside the arithmetic so the memory system draws too, runs eight independent calculation chains instead of four, and keeps four batches of work queued so the card never goes idle waiting on the app. Each individual batch is unchanged in length, so this is no closer to the Windows graphics watchdog than before. If you want to check it on your own card, run the app's GPU burn diagnostic and it will now report the sustained wattage it reached, which you can compare against FurMark or a game."),

                new("Fixed: the power column now says '--' instead of guessing from an idle machine",
                    "The power column compares what your chip is drawing against what its baseline was learned at. It was reading that from every load level including idle, where a chip draws about the same in any configuration, so a week with no real load behind it could still show a confident percentage. It now reports a number only when there has been genuine load to measure, and shows '--' otherwise, with a tooltip explaining that it fills in once you have spent time in a game, a render or a build. DeltaT's own stress test deliberately does not count toward it."),
            }),
        new WhatsNewRelease(new Version(2, 3, 0),
            "This release rebuilds the thermal diagnosis panel to show the readings behind its verdict, fixes calibration getting stuck at 80%, lets a repaste reset just the CPU or just the GPU, adds a RAM usage card, and adds an Intel power-budget reading that tells a machine held back by heat from one simply set to run cooler.",
            new WhatsNewItem[]
            {
                // --- New features ---
                new("New: the thermal diagnosis panel now shows its working",
                    "The top of the dashboard used to assert a verdict and keep the evidence in tooltips. It now leads with what the verdict was actually measured from: how many hours of load, up to which load level, in which weather band, over which window. Each readout underneath carries the raw pair it came from, so the temperature difference reads '43.4° vs 36.9°' rather than a bare '+10.8°' you have to take on trust, the power correction shows the two wattages, and the fan correction shows the two fan speeds. A power readout is now always present, saying MATCHED with its watts when the machine drew what its baseline was learned at, instead of going silent. And when a component has been hitting its thermal limit, the count of those hits appears as its own reading rather than only inside a tooltip."),
                new("New: the readout row pages sideways instead of being cut off",
                    "The row of readings grows when the machine throttles or is still calibrating, so on a smaller window it can run past the edge. It now stays on one line and pages one reading at a time with a pair of arrows, with a fade at the cut edge so a paged-off reading never looks like a missing one. Maximize the window and everything fits, with the arrows hidden."),
                new("New: a component still calibrating gets its own readout",
                    "The CPU and GPU lock their baselines independently, so one can be scoring while the other is still learning. That used to be a sentence tucked under the other component's verdict. It is now its own strip with the component named, a confidence meter, the percentage, and the specific load and weather it still needs."),
                new("New: repaste or recalibrate one component, not both",
                    "Repasting a laptop CPU without touching the GPU is the normal case, but DeltaT used to reset both baselines, throwing away days of learning for no reason. Both buttons now ask whether it was the CPU, the GPU, or both. Whatever you leave out keeps its baseline, its score and its verdict untouched."),
                new("New: Intel power-budget reading tells heat apart from a power setting",
                    "On Intel CPUs, DeltaT now reads the chip's configured power budget (PL1 and PL2) and which limit is actually holding it back. That answers what temperature alone cannot: is cooling the ceiling, or is the machine deliberately set to run cooler? A machine only counts as thermally constrained when the chip itself says heat is the active limit, so a boost-off or quiet-power-plan setup is never mistaken for a fault. Shown on the Device page under Silicon limits, and the fingerprint test now gives a day-one verdict with no baseline needed. Intel only for now."),
                new("New: RAM usage card",
                    "The dashboard now shows memory usage between the SSD and the battery, with used and total gigabytes and its own icon. It is a live readout only. Memory has no thermal paste, so it is never scored."),
                new("New: calibration tells you what it is waiting for",
                    "While calibrating, DeltaT now names the specific load and weather it still needs more of, and how close that reading is to being tight enough, instead of a vague 'a few more sessions'."),
                new("New: the fingerprint test no longer locks up the app",
                    "The fingerprint window used to hold the rest of DeltaT hostage for the two to four minutes it ran. It is now an ordinary window: leave it open and keep using the app. Pressing the button again brings the running test to the front instead of starting a second one."),
                new("New: your contact details are remembered on feedback reports",
                    "Fill in an email or handle on a bug report or idea and DeltaT keeps it for the next one. It stays on your machine and is only sent with a report you choose to send."),

                // --- Fixes ---
                new("Fixed: a machine running at its own baseline could still be told its paste was failing",
                    "This is the biggest correctness fix in the release. DeltaT compares a temperature rise against the wattage it was measured at, because a chip drawing fewer watts runs cooler for reasons that have nothing to do with its paste. It did that by scaling the rise by the ratio of the two wattages, which quietly assumes the whole rise is produced by that chip's own power. It is not. A large part of any rise is the room sitting above the outdoor temperature DeltaT scores against, plus board and VRM heat, plus, on a laptop, the heat coming from the neighbouring chip through the shared heatpipes. Scaling the rise scaled all of that too. On a real machine running with CPU boost switched off while gaming, the measured rise was 30.0° against a learned baseline of 30.0°, identical, and the score still read 55 with the paste meter at 0. DeltaT now learns how this machine's rise actually answers power, from its own baseline cells, and moves the reference to the operating point the reading was taken at instead of stretching the reading. Fitted against 18,948 minutes of real history, the old assumption predicted worse than simply guessing the average; the new one is a genuine fit. It also removes a hard edge where a reading at 1.95 times the baseline wattage was corrected in full and one at 2.01 times was thrown away entirely."),
                new("Fixed: the GPU's heat no longer counts against the CPU's paste, or the other way round",
                    "CPU and GPU in a laptop share one heatpipe stack, so a loaded GPU raises the CPU's temperature without the CPU drawing a single extra watt. Measured on the development machine, holding CPU power steady, the CPU's rise still climbed 22.1° to 30.6° as the GPU went from 5 W to 35 W. DeltaT now records what the other chip was drawing alongside every reading and accounts for it, so a gaming session cannot read as the CPU's paste degrading. The same mistake ran in reverse: it could hide real degradation behind a neighbour that happened to be quiet."),
                new("Fixed: fans were judged against one chip's watts instead of the whole cooling module",
                    "One fan curve serves both chips and follows whichever is hotter. DeltaT used to expect the fan to slow down in proportion to a single chip's power, so a CPU with boost turned off next to a busy GPU looked like it had fans spinning faster than its wattage justified, which was read as extra airflow flattering the reading and added several degrees of imaginary excess. Fan speed is now judged against total heat through the module, which matches the measurements far better. The reason line could also contradict itself, reporting 'fans averaged 3672 rpm against a 3663 rpm baseline' and then calling that extra airflow."),
                new("Fixed: recalibrating one component stopped the other from scoring",
                    "Recalibrating just the GPU (or just the CPU) dropped the other one to 'waiting for a comparable load', even when it had been sitting on a locked, healthy score. Its baseline was never actually lost: the reset moved the start of its measuring window forward to that moment, so it had nothing on either side of the comparison to work with and had to wait for a fresh session. The untouched component now keeps the window start it already had, and carries on scoring without a pause. This applies to both buttons, scoped either way."),
                new("Fixed: calibration stuck at 80%",
                    "Two causes, both fixed. A load bucket the machine visited only once counted as heavily as one backed by weeks of data, so a single thin reading could hold a well-learned baseline below the finish line forever; readings are now weighted by how much evidence backs them. And the confidence check compared raw temperatures, which made a GPU's game-to-game wattage swings look like noise; it now compares them adjusted for power draw, the same way the score already did. A stable machine now locks in about three sessions."),
                new("Fixed: the RAM card was missing or blank on some machines",
                    "The memory card could fail to appear at all, or show the Windows pagefile instead of your actual RAM, depending on how the sensor library named its readings on that machine. DeltaT now identifies physical RAM by measuring it against what Windows reports installed, and falls back to asking Windows directly if the sensor library offers nothing usable. That last path needs no driver and no admin rights, so the card now works on every machine."),
                new("Fixed: the headline verdict is now colored, and stops naming one chip when both agree",
                    "The big verdict line was plain white, which made 'Excellent' look as urgent as 'Degraded'. It now carries the health dials' color, green through red. It also used to name whichever chip scored worse, so a healthy machine read 'GPU: Excellent' and left you wondering if the CPU was checked at all. It now names a component only when that is the actual information."),

                // --- Notes ---
                new("Note: on a laptop, the paste fix gets better once your baseline relearns",
                    "The two fixes above that account for the neighbouring chip's heat need a reading DeltaT was not recording before this version: what the other chip was drawing at the same moment. Your existing history does not have it, so on a laptop where the CPU and GPU share heatpipes those fixes work only partly until enough new data builds up. Nothing is lost and nothing is required of you. The score is already better than it was and improves on its own over the following days. If your score has been reading low and you are confident your cooling is fine, Recalibrate under Settings rebuilds the reference straight away with the new readings, and your history, trends and repaste log are kept either way. Desktops and single-chip machines are unaffected, since there is no neighbour sharing the cooler."),
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
