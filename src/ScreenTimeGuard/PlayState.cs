using System.Text.Json.Serialization;

namespace ScreenTimeGuard;

/// <summary>
/// Per-day play-time state persisted to play-state.json in the service install directory.
/// The file is ACL-protected (Admins + SYSTEM only), so a standard user cannot tamper
/// with it to reset their daily counter.
/// </summary>
internal sealed class PlayState
{
    /// <summary>The calendar date this record belongs to.</summary>
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Accumulated Minecraft play time in seconds for this date.</summary>
    public double PlayedSeconds { get; set; }

    /// <summary>
    /// True once the daily limit has been reached. Stays true for the rest of the day
    /// so any re-launch attempt is immediately killed, even if the config limit is raised
    /// mid-day.
    /// </summary>
    public bool LimitReached { get; set; }

    /// <summary>True once the "5 minutes left" warning has been written to the Event Log.</summary>
    public bool WarningSent { get; set; }

    [JsonIgnore]
    public double PlayedMinutes => PlayedSeconds / 60.0;
}
