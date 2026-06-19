namespace XivMediaPlayer.Shared.Models;

/// <summary>
/// Request to claim DJ ownership of a room.
/// </summary>
public class ClaimDjRequest
{
    public string OwnerId { get; set; } = string.Empty;
    public long ExpectedVersion { get; set; }
}

/// <summary>
/// Response to a claim-dj request.
/// </summary>
public class ClaimDjResponse
{
    public bool Success { get; set; }
    public RoomState? RoomState { get; set; }
    public long CurrentVersion { get; set; }
    public string? DjOwnerId { get; set; }
}

/// <summary>
/// Heartbeat payload sent by the active DJ to keep ownership and sync state.
/// </summary>
public class HeartbeatRequest
{
    public string OwnerId { get; set; } = string.Empty;
    public long StateVersion { get; set; }
    public string CurrentUrl { get; set; } = string.Empty;
    public long TimecodeMs { get; set; }
    public bool IsPlaying { get; set; }
    public float SpeedRate { get; set; } = 1.0f;
    public List<string> Queue { get; set; } = new();
}

/// <summary>
/// Response to a heartbeat request.
/// </summary>
public class HeartbeatResponse
{
    public bool Accepted { get; set; }
    public long AcceptedVersion { get; set; }
    public long CurrentVersion { get; set; }
}

/// <summary>
/// Request to release DJ ownership.
/// </summary>
public class ReleaseDjRequest
{
    public string OwnerId { get; set; } = string.Empty;
}

/// <summary>
/// Room state as returned by the public GET /state endpoint.
/// </summary>
public class RoomStateResponse
{
    public string CurrentUrl { get; set; } = string.Empty;
    public long TimecodeMs { get; set; }
    public bool IsPlaying { get; set; }
    public float SpeedRate { get; set; }
    public List<string> Queue { get; set; } = new();
    public string DjOwnerId { get; set; } = string.Empty;
    public double DjHeartbeatAgeSeconds { get; set; }
    public long StateVersion { get; set; }
    public bool DjDisconnected { get; set; }
}
