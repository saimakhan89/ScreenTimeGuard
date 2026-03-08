namespace MinecraftBlocker;

/// <summary>
/// Root configuration object — bound from the "BlockerConfig" section of appsettings.json.
/// Reload-on-change is supported: edit the file while the service is running and the new
/// values take effect within one poll interval.
/// </summary>
public sealed class BlockerConfig
{
    /// <summary>
    /// Days on which Minecraft is blocked all day (unless an AllowedTimeWindow applies).
    /// Default: Monday – Friday.
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
    /// Optional override windows within a blocked day when Minecraft IS allowed.
    /// Leave empty to block the entire day.
    /// Example: [{ "Start": "17:00:00", "End": "21:00:00" }] allows play after 5 pm.
    /// </summary>
    public List<TimeWindow> AllowedTimeWindows { get; set; } = [];

    /// <summary>
    /// Substrings checked against javaw.exe command-line to identify a Minecraft JVM.
    /// Case-insensitive. Add extra paths here if you use a custom launcher profile.
    /// </summary>
    public List<string> MinecraftKeywords { get; set; } =
    [
        ".minecraft",
        "minecraft"
    ];

    /// <summary>
    /// Process names (without .exe) of the Minecraft launcher itself.
    /// The service kills these unconditionally on a blocked day/time.
    /// </summary>
    public List<string> LauncherProcessNames { get; set; } =
    [
        "MinecraftLauncher",
        "minecraft-launcher",
        "Minecraft Launcher"
    ];

    /// <summary>How often the service checks for running processes (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Write blocked-attempt entries to the Windows Event Log.</summary>
    public bool LogBlockedAttempts { get; set; } = true;

    /// <summary>Custom Windows Event Log source name.</summary>
    public string EventLogSource { get; set; } = "MinecraftBlocker";
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
