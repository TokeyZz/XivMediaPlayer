namespace XivMediaPlayer.Shared.Models;

/// <summary>
/// Television placement in a game room / zone.
/// Shared between client and server. BypassLock is a runtime-only field;
/// the server ignores it for persistence via Fluent API.
/// </summary>
public class TvPlacement
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string LocationKey { get; set; } = string.Empty;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float ScaleX { get; set; }
    public float ScaleY { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public bool IsLocked { get; set; } = false;
    public bool BypassLock { get; set; } = false;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Legacy room claim request model.
/// </summary>
public class RoomClaimRequest
{
    public string LocationKey { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
}

/// <summary>
/// Legacy media state sync model shared between client and server.
/// Some fields (BypassLock, IsBackgroundSync, DataAgeMs, IdleTimeMs)
/// are runtime-only and not persisted; the server ignores them via Fluent API.
/// </summary>
public class RoomMediaStateSync
{
    public string LocationKey { get; set; } = string.Empty;
    public string CurrentUrl { get; set; } = string.Empty;
    public long TimecodeMs { get; set; }
    public bool IsPlaying { get; set; } = true;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string PlaylistJson { get; set; } = "[]";
    public string OwnerId { get; set; } = string.Empty;
    public bool BypassLock { get; set; } = false;
    public bool IsBackgroundSync { get; set; } = false;
    public double DataAgeMs { get; set; } = 0;
    public double IdleTimeMs { get; set; } = 0;
    public double? DurationMs { get; set; } = null;
}

/// <summary>
/// Media track history record.
/// </summary>
public class MediaTrackRecord
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string LocationKey { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
}
