namespace DeltaT.App.Services;

/// <summary>App-wide clock preference: 12-hour (AM/PM) or 24-hour, mirroring the °C/°F unit
/// toggle. Held statically so the custom drawing controls (the trend charts) can format a
/// timestamp without carrying a <see cref="Core.Storage.SettingsStore"/> reference, the same
/// way the view models read the unit setting live. Set once at startup from the stored setting
/// and updated the moment the Settings toggle flips, so the next chart redraw or feed refresh
/// picks it up. All patterns are time-of-day only; date-only labels are unaffected.</summary>
public static class TimeFormat
{
    /// <summary>True = 12-hour clock with AM/PM. Default 24-hour, matching the previous behaviour.</summary>
    public static bool Use12Hour { get; set; }

    /// <summary>Just the time, e.g. "14:30" or "2:30 PM".</summary>
    public static string TimeOnly => Use12Hour ? "h:mm tt" : "HH:mm";

    /// <summary>Weekday plus time, e.g. "Mon 14:30" or "Mon 2:30 PM" (wide chart axis).</summary>
    public static string DayAndTime => Use12Hour ? "ddd h:mm tt" : "ddd HH:mm";

    /// <summary>Date plus time, e.g. "Jul 22 14:30" or "Jul 22 2:30 PM" (chart tooltip).</summary>
    public static string DateAndTime => Use12Hour ? "MMM d h:mm tt" : "MMM d HH:mm";

    /// <summary>Date and time joined by the feed's middot, e.g. "Jul 22 · 2:30 PM".</summary>
    public static string DateDotTime => Use12Hour ? "MMM d · h:mm tt" : "MMM d · HH:mm";
}
