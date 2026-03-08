using System.Diagnostics;
using System.Management;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MinecraftBlocker;

/// <summary>
/// Background worker that polls every N seconds, checks whether the current
/// day/time is blocked or over the free-day limit, and kills matching processes.
/// </summary>
public sealed class BlockerWorker : BackgroundService
{
    private readonly ILogger<BlockerWorker> _logger;
    private readonly IOptionsMonitor<BlockerConfig> _config;

    // Track PIDs we have already logged to avoid Event Log flooding when a
    // process is slow to die. Flushed every minute.
    private readonly HashSet<int> _recentlyLogged = new();
    private DateTime _lastLogFlush = DateTime.UtcNow;

    // Cached TimeZoneInfo resolved from config. Rebuilt whenever the config reloads.
    private TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private string _loadedTimeZoneId = string.Empty;

    // Per-day play-time state, persisted to disk so a service restart mid-day
    // doesn't reset the counter.
    private PlayState _playState = new();
    private static readonly string StateFilePath =
        Path.Combine(AppContext.BaseDirectory, "play-state.json");

    public BlockerWorker(ILogger<BlockerWorker> logger, IOptionsMonitor<BlockerConfig> config)
    {
        _logger = logger;
        _config = config;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureEventSource();
        _playState = LoadPlayState();
        var cfg = _config.CurrentValue;
        RefreshTimeZone(cfg);
        var now = GetNow(cfg);
        WriteEvent($"MinecraftBlocker service started. Schedule timezone: {_timeZone.DisplayName}. Current time: {now:ddd yyyy-MM-dd HH:mm:ss zzz}.", EventLogEntryType.Information);
        _logger.LogInformation("MinecraftBlocker service started. Timezone: {Tz}. Now: {Now:ddd HH:mm:ss}.",
            _timeZone.Id, now);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        WriteEvent("MinecraftBlocker service stopped.", EventLogEntryType.Information);
        _logger.LogInformation("MinecraftBlocker service stopped.");
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config.CurrentValue;
            var interval = TimeSpan.FromSeconds(Math.Clamp(cfg.PollIntervalSeconds, 1, 60));

            RefreshTimeZone(cfg);
            var now = GetNow(cfg);
            var today = DateOnly.FromDateTime(now);

            // Flush event-log dedup set every minute so re-launched processes get logged.
            if (DateTime.UtcNow - _lastLogFlush > TimeSpan.FromMinutes(1))
            {
                _recentlyLogged.Clear();
                _lastLogFlush = DateTime.UtcNow;
            }

            // Reset the play-time counter at midnight (Eastern time).
            if (_playState.Date != today)
            {
                _logger.LogInformation(
                    "New day ({Date} Eastern). Resetting play-time counter (was {Played:F1} min).",
                    today, _playState.PlayedMinutes);
                _playState = new PlayState { Date = today };
                SavePlayState();
            }

            try
            {
                // Single scan — result is reused by both paths to avoid double WMI query.
                var matches = FindMinecraftProcesses(cfg);

                if (ShouldBlockNow(now, cfg))
                {
                    // Weekday (outside allowed windows): kill unconditionally.
                    foreach (var m in matches)
                        KillProcessById(m.Pid, m.Description, cfg);
                }
                else if (cfg.FreeDayDailyLimitMinutes > 0
                         && !cfg.BlockedDays.Contains(now.DayOfWeek))
                {
                    // Free day with a configured daily limit.
                    EnforceFreeDayLimit(matches, cfg, interval);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in monitoring loop.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    // -------------------------------------------------------------------------
    // Timezone helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the current time in the configured timezone (default: Eastern Standard Time).
    /// All schedule comparisons use this value — not DateTime.Now — so the schedule
    /// cannot be bypassed by changing the machine's local timezone.
    /// </summary>
    private DateTime GetNow(BlockerConfig cfg)
    {
        RefreshTimeZone(cfg);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
    }

    /// <summary>Rebuilds the cached TimeZoneInfo if the config TimeZoneId changed.</summary>
    private void RefreshTimeZone(BlockerConfig cfg)
    {
        if (cfg.TimeZoneId == _loadedTimeZoneId)
            return;

        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(cfg.TimeZoneId);
            _loadedTimeZoneId = cfg.TimeZoneId;
            _logger.LogInformation("Schedule timezone set to '{Id}' ({Display}).",
                _timeZone.Id, _timeZone.DisplayName);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning(
                "TimeZoneId '{Id}' not found — falling back to local time. " +
                "Run 'tzutil /l' for valid IDs.", cfg.TimeZoneId);
            _timeZone = TimeZoneInfo.Local;
            _loadedTimeZoneId = cfg.TimeZoneId; // Don't retry every poll.
        }
    }

    // -------------------------------------------------------------------------
    // Schedule logic
    // -------------------------------------------------------------------------

    private static bool ShouldBlockNow(DateTime now, BlockerConfig cfg)
    {
        if (!cfg.BlockedDays.Contains(now.DayOfWeek))
            return false;

        if (cfg.AllowedTimeWindows.Count > 0)
        {
            var tod = now.TimeOfDay;
            foreach (var window in cfg.AllowedTimeWindows)
            {
                if (window.Contains(tod))
                    return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Free-day time-limit enforcement
    // -------------------------------------------------------------------------

    private void EnforceFreeDayLimit(
        List<ProcessMatch> matches, BlockerConfig cfg, TimeSpan pollInterval)
    {
        if (matches.Count == 0)
            return; // Nothing running — nothing to track or kill.

        double limitSeconds = cfg.FreeDayDailyLimitMinutes * 60.0;

        if (!_playState.LimitReached)
        {
            // Accumulate: add one poll interval worth of play time.
            _playState.PlayedSeconds += pollInterval.TotalSeconds;

            // One-time "5 minutes remaining" warning.
            double remaining = limitSeconds - _playState.PlayedSeconds;
            if (!_playState.WarningSent && remaining is <= 300 and > 0)
            {
                int minsLeft = (int)Math.Ceiling(remaining / 60.0);
                string warn = $"Weekend play time: ~{minsLeft} minute(s) remaining today.";
                _logger.LogInformation("{Message}", warn);
                if (cfg.LogBlockedAttempts)
                    WriteEvent(warn, EventLogEntryType.Information);
                _playState.WarningSent = true;
            }

            if (_playState.PlayedSeconds >= limitSeconds)
            {
                _playState.LimitReached = true;
                string msg =
                    $"Weekend daily limit of {cfg.FreeDayDailyLimitMinutes} min reached " +
                    $"({_playState.PlayedMinutes:F1} min played). " +
                    "Blocking Minecraft for the rest of today.";
                _logger.LogWarning("{Message}", msg);
                if (cfg.LogBlockedAttempts)
                    WriteEvent(msg, EventLogEntryType.Warning);
            }

            SavePlayState();
        }

        // Kill only once the limit is actually reached (LimitReached may have just
        // been set above, or was already set from a previous poll/service restart).
        if (_playState.LimitReached)
        {
            foreach (var m in matches)
                KillProcessById(m.Pid, m.Description, cfg);
        }
    }

    // -------------------------------------------------------------------------
    // Process detection  (find-first, kill-separately)
    // -------------------------------------------------------------------------

    private record ProcessMatch(int Pid, string Description);

    /// <summary>
    /// Returns all currently running Minecraft processes without killing them.
    /// One WMI scan per poll cycle, result shared between detection and kill paths.
    /// </summary>
    private List<ProcessMatch> FindMinecraftProcesses(BlockerConfig cfg)
    {
        var found = new List<ProcessMatch>();

        // javaw.exe whose command line contains a MinecraftKeyword.
        const string query =
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'javaw.exe'";
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                int pid = Convert.ToInt32(obj["ProcessId"]);
                string cmdLine = obj["CommandLine"]?.ToString() ?? string.Empty;

                bool isMinecraft = cfg.MinecraftKeywords.Any(kw =>
                    cmdLine.Contains(kw, StringComparison.OrdinalIgnoreCase));

                if (isMinecraft)
                    found.Add(new ProcessMatch(pid, $"javaw.exe (PID {pid})"));
            }
        }
        catch (ManagementException mex)
        {
            _logger.LogError(mex, "WMI query failed while scanning javaw.exe processes.");
        }

        // Minecraft launcher by process name.
        foreach (var name in cfg.LauncherProcessNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                        found.Add(new ProcessMatch(proc.Id, $"{name} (PID {proc.Id})"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating launcher process '{Name}'.", name);
            }
        }

        return found;
    }

    private void KillProcessById(int pid, string description, BlockerConfig cfg)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);

            var nowEst = GetNow(_config.CurrentValue);
            string msg = $"Blocked: killed Minecraft process {description} at {nowEst:T} on {nowEst:dddd} (Eastern).";
            _logger.LogWarning("{Message}", msg);

            if (cfg.LogBlockedAttempts && _recentlyLogged.Add(pid))
                WriteEvent(msg, EventLogEntryType.Warning);
        }
        catch (ArgumentException) { /* Process already exited. */ }
        catch (InvalidOperationException) { /* Process exited before Kill(). */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process {Description}.", description);
        }
    }

    // -------------------------------------------------------------------------
    // Play-state persistence
    // -------------------------------------------------------------------------

    private static PlayState LoadPlayState()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                var loaded = JsonSerializer.Deserialize<PlayState>(json);
                // Only reuse state if it's for today — otherwise start fresh.
                if (loaded?.Date == DateOnly.FromDateTime(DateTime.Today))
                    return loaded;
            }
        }
        catch { /* Start fresh on any deserialization error. */ }

        return new PlayState { Date = DateOnly.FromDateTime(DateTime.Today) };
    }

    private void SavePlayState()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _playState, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save play-state to '{Path}'.", StateFilePath);
        }
    }

    // -------------------------------------------------------------------------
    // Windows Event Log helpers
    // -------------------------------------------------------------------------

    private void EnsureEventSource()
    {
        var source = _config.CurrentValue.EventLogSource;
        try
        {
            if (!EventLog.SourceExists(source))
            {
                EventLog.CreateEventSource(source, "Application");
                _logger.LogInformation("Created Event Log source '{Source}'.", source);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create Event Log source '{Source}'.", source);
        }
    }

    private void WriteEvent(string message, EventLogEntryType type)
    {
        var source = _config.CurrentValue.EventLogSource;
        try
        {
            EventLog.WriteEntry(source, message, type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write to Event Log source '{Source}'.", source);
        }
    }
}
