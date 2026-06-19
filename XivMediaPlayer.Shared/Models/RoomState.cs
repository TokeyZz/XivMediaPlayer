namespace XivMediaPlayer.Shared.Models;

/// <summary>
/// Core room state model used by the v2 confirm-then-push protocol.
/// Represents the authoritative state of a room's media playback.
/// </summary>
public class RoomState
{
    public string LocationKey { get; set; } = string.Empty;
    public string CurrentUrl { get; set; } = string.Empty;
    public long TimecodeMs { get; set; }
    public bool IsPlaying { get; set; }
    public float SpeedRate { get; set; } = 1.0f;
    public string QueueJson { get; set; } = "[]";
    public string DjOwnerId { get; set; } = string.Empty;
    public DateTime DjHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public long StateVersion { get; set; }
}
