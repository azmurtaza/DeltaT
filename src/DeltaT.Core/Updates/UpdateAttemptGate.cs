namespace DeltaT.Core.Updates;

/// <summary>What the updater should do about a release it has already tried to install.</summary>
public enum UpdateAttempt
{
    /// <summary>Nothing stands in the way: download it and hand off to the installer.</summary>
    Proceed,

    /// <summary>This exact version was already attempted and the machine is still not
    /// running it, so the install is failing. Stop, and tell the user instead.</summary>
    Failing,
}

/// <summary>Decides whether to try installing a release again after a previous attempt did
/// not take. Pure, so the loop-breaker is unit-testable without a network or an installer.
///
/// This exists because the updater shuts the app down to hand off to the setup, and the
/// helper relaunches it whether or not the install worked. Without a memory of what was
/// already tried, an install that cannot succeed (UAC declined, antivirus quarantine, a
/// locked file) turns into an endless download, restart, download loop: the app is up for
/// seconds at a time and the release asset is re-fetched every cycle. A retry that has
/// already failed twice is not going to work on the third launch either, so DeltaT stops
/// and hands the problem to the user.</summary>
public static class UpdateAttemptGate
{
    /// <summary>How many times one version may be handed to the installer before DeltaT
    /// treats it as failing. Two, so a genuinely transient failure (a file still locked by
    /// the outgoing process) gets its second chance and nothing more.</summary>
    public const int MaxAttempts = 2;

    /// <summary>How long a failing version is left alone before one more attempt. Long
    /// enough that the user is not fighting a restart loop, short enough that a machine
    /// whose blocker was cleared (antivirus exclusion added, admin rights restored) heals
    /// itself without anyone touching Settings.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);

    /// <summary>Whether <paramref name="release"/> may be handed to the installer now.
    /// <paramref name="attempted"/>/<paramref name="attemptCount"/>/<paramref name="lastAttemptUtc"/>
    /// are what was recorded the last time an install was started, and are only meaningful
    /// while the machine is still not running that version (the caller clears the record
    /// once the running version catches up, see <see cref="Succeeded"/>).</summary>
    public static UpdateAttempt Evaluate(
        Version? attempted, int attemptCount, DateTimeOffset? lastAttemptUtc,
        Version release, DateTimeOffset now)
    {
        if (attempted is null || attempted != release)
            return UpdateAttempt.Proceed;   // a version we have not tried yet
        if (attemptCount < MaxAttempts)
            return UpdateAttempt.Proceed;   // second chance for a transient failure
        if (lastAttemptUtc is { } last && now - last >= RetryAfter)
            return UpdateAttempt.Proceed;   // long enough that the blocker may be gone
        return UpdateAttempt.Failing;
    }

    /// <summary>True when a recorded attempt has been vindicated: the machine is now
    /// running that version (or better), so the record should be cleared. Compared on
    /// Major.Minor.Build, since a release tag carries no revision.</summary>
    public static bool Succeeded(Version? attempted, Version running) =>
        attempted is not null && Normalize(running) >= Normalize(attempted);

    /// <summary>Drops the revision component so "2.4.0" and "2.4.0.0" compare equal.
    /// Version treats an absent revision as -1, which makes the tag look OLDER than the
    /// assembly version it names.</summary>
    public static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build));
}
