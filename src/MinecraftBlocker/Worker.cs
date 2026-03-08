using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Options;

namespace MinecraftBlocker;

/// <summary>
/// Background worker that polls every N seconds, checks whether the current
/// day/time is blocked, and kills any matching Minecraft processes.
/// </summary>
public sealed class BlockerWorker : BackgroundService
{
    private readonly ILogger<BlockerWorker> _logger;
    private readonly IOptionsMonitor<BlockerConfig> _config;

    // Track PIDs we have already logged in this enforcement window to avoid
    // flooding the Event Log when the process is slow to die.
    private readonly HashSet<int> _recentlyLogged = new();
    private DateTime _lastLogFlush = DateTime.UtcNow;

    public BlockerWorker(ILogger<BlockerWorker> logger, IOptionsMonitor<BlockerConfig> config)
    {
        _logger = logger;
        _config = config;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureEventSource();
        WriteEvent("MinecraftBlocker service started.", EventLogEntryType.Information);
        _logger.LogInformation("MinecraftBlocker service started.");
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

            // Flush the "recently logged" set every minute to allow re-logging
            // if a process is restarted after being killed.
            if (DateTime.UtcNow - _lastLogFlush > TimeSpan.FromMinutes(1))
            {
                _recentlyLogged.Clear();
                _lastLogFlush = DateTime.UtcNow;
            }

            try
            {
                if (ShouldBlockNow(cfg))
                {
                    EnforceBlock(cfg);
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
    // Schedule logic
    // -------------------------------------------------------------------------

    private static bool ShouldBlockNow(BlockerConfig cfg)
    {
        var now = DateTime.Now;

        if (!cfg.BlockedDays.Contains(now.DayOfWeek))
            return false; // Weekend / non-blocked day — let it run.

        // If allowed windows are configured, check whether we are inside one.
        if (cfg.AllowedTimeWindows.Count > 0)
        {
            var tod = now.TimeOfDay;
            foreach (var window in cfg.AllowedTimeWindows)
            {
                if (window.Contains(tod))
                    return false; // Inside an allowed window — let it run.
            }
        }

        return true; // Blocked day, outside any allowed window.
    }

    // -------------------------------------------------------------------------
    // Process enforcement
    // -------------------------------------------------------------------------

    private void EnforceBlock(BlockerConfig cfg)
    {
        KillMinecraftJvm(cfg);
        KillLauncherProcesses(cfg);
    }

    /// <summary>
    /// Finds javaw.exe processes whose command line references .minecraft and kills them.
    /// WMI is used because System.Diagnostics.Process does not expose CommandLine.
    /// </summary>
    private void KillMinecraftJvm(BlockerConfig cfg)
    {
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
                    KillProcessById(pid, $"javaw.exe (PID {pid})", cfg);
            }
        }
        catch (ManagementException mex)
        {
            _logger.LogError(mex, "WMI query failed while scanning javaw.exe processes.");
        }
    }

    /// <summary>
    /// Kills any running process whose name matches one of the configured launcher names.
    /// </summary>
    private void KillLauncherProcesses(BlockerConfig cfg)
    {
        foreach (var name in cfg.LauncherProcessNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                        KillProcessById(proc.Id, $"{name} (PID {proc.Id})", cfg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating launcher process '{Name}'.", name);
            }
        }
    }

    private void KillProcessById(int pid, string description, BlockerConfig cfg)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);

            string msg = $"Blocked: killed Minecraft process {description} at {DateTime.Now:T} on {DateTime.Now:dddd}.";
            _logger.LogWarning("{Message}", msg);

            if (cfg.LogBlockedAttempts && _recentlyLogged.Add(pid))
                WriteEvent(msg, EventLogEntryType.Warning);
        }
        catch (ArgumentException)
        {
            // Process already exited between enumeration and kill — harmless.
        }
        catch (InvalidOperationException)
        {
            // Process exited before we could call Kill().
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process {Description}.", description);
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
            // Non-fatal: may fail if the source was already created by another account.
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
