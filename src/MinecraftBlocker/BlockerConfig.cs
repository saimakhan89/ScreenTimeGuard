namespace MinecraftBlocker;

/// <summary>
/// Root configuration object — bound from the "BlockerConfig" section of appsettings.json.
/// Reload-on-change is supported: edit the file while the service is running and the new
/// values take effect within one poll interval.
/// </summary>
public sealed class BlockerConfig
{
    /// <summary>
    /// Days on which blocked apps are killed unconditionally (unless an AllowedTimeWindow applies).
    /// Default: Monday - Friday.
    /// </summary>
    public List<DayOfWeek> BlockedDays { get; set; } =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    ];

    /// <summary>
    /// Optional time windows within a blocked day when apps ARE allowed.
    /// Leave empty to block the entire day.
    /// Example: [{ "Start": "17:00:00", "End": "21:00:00" }] allows use after 5 pm.
    /// </summary>
    public List<TimeWindow> AllowedTimeWindows { get; set; } = [];

    /// <summary>
    /// Substrings matched (case-insensitive) against the javaw.exe command line.
    /// Used to identify Java-based games (Minecraft, etc.) without killing unrelated
    /// Java processes such as IDEs.
    /// </summary>
    public List<string> JavaProcessKeywords { get; set; } =
    [
        ".minecraft",
        ".lunarclient",
        "minecraft"
    ];

    /// <summary>
    /// Substrings matched (case-insensitive) against a process's ExecutablePath via WMI.
    /// Catches launchers and native game executables regardless of how many helper
    /// processes the app spawns. Add any folder or exe name fragment here.
    /// </summary>
    public List<string> ProcessPathKeywords { get; set; } =
    [
        // Minecraft launchers
        "Lunar Client",
        "MinecraftLauncher",
        // Fortnite (Epic Games)
        "FortniteClient-Win64-Shipping",
        "EpicGamesLauncher",
        // Roblox
        "RobloxPlayerBeta",
        "RobloxPlayer",
        "RobloxPlayerLauncher"
    ];

    /// <summary>
    /// Daily entertainment time limit in minutes on free days (weekend / days not in BlockedDays).
    /// Shared across ALL blocked apps — 30 min Roblox + 30 min Fortnite = 60 min total.
    /// Counter resets at midnight. Set to 0 for unlimited play on free days.
    /// Default: 60 minutes.
    /// </summary>
    public int FreeDayDailyLimitMinutes { get; set; } = 60;

    /// <summary>
    /// Windows timezone ID used for all schedule decisions.
    /// Pinning to a specific timezone prevents circumvention by changing the machine clock.
    /// Run `tzutil /l` to list valid IDs. Default: Eastern Standard Time (covers EST/EDT).
    /// </summary>
    public string TimeZoneId { get; set; } = "Eastern Standard Time";

    /// <summary>How often the service checks for running processes (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Write blocked-attempt entries to the Windows Event Log.</summary>
    public bool LogBlockedAttempts { get; set; } = true;

    /// <summary>Custom Windows Event Log source name.</summary>
    public string EventLogSource { get; set; } = "ScreenTimeGuard";
}

/// <summary>A half-open time interval [Start, End) within a single day.</summary>
public sealed class TimeWindow
{
    /// <summary>Window start, e.g. "17:00:00".</summary>
    public TimeSpan Start { get; set; }

    /// <summary>Window end (exclusive), e.g. "21:00:00".</summary>
    public TimeSpan End { get; set; }

    public bool Contains(TimeSpan time) => time >= Start && time < End;
}
