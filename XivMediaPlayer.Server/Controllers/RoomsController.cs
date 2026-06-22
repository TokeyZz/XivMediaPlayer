using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;
using XivMediaPlayer.Shared.Models;
using XivMediaPlayer.Shared;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace XivMediaPlayer.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RoomsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<RoomsController> _logger;
        private readonly IConfiguration _config;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastFetchTimes = new();

        public RoomsController(AppDbContext db, ILogger<RoomsController> logger, IConfiguration config)
        {
            _db = db;
            _logger = logger;
            _config = config;
        }

        [HttpGet("{locationKey}/tvs")]
        public async Task<IActionResult> GetTvs(string locationKey)
        {
            var tvs = await _db.TvPlacements
                .Where(t => t.LocationKey == locationKey)
                .ToListAsync();
            return Ok(tvs);
        }

        [HttpGet("time")]
        public IActionResult GetServerTime()
        {
            return Ok(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        [HttpPost("{locationKey}/tvs")]
        public async Task<IActionResult> RegisterTv(string locationKey, [FromBody] TvPlacement placement)
        {
            placement.LocationKey = locationKey;
            placement.LastUpdated = DateTime.UtcNow;
            var existing = await _db.TvPlacements.FirstOrDefaultAsync(t => t.LocationKey == locationKey);
            if (existing != null)
            {
                bool isForfeited = false;
                if (locationKey.StartsWith("zone_"))
                {
                    var lastFetch = _lastFetchTimes.TryGetValue(locationKey, out var lf) ? lf : DateTime.MinValue;
                    if ((DateTime.UtcNow - lastFetch).TotalMinutes >= 2)
                        isForfeited = true;
                }
                if (!isForfeited && existing.IsLocked && existing.OwnerId != placement.OwnerId && !placement.BypassLock)
                    return Forbid();
                if (isForfeited) existing.IsLocked = false;
                existing.PositionX = placement.PositionX;
                existing.PositionY = placement.PositionY;
                existing.PositionZ = placement.PositionZ;
                existing.RotationX = placement.RotationX;
                existing.RotationY = placement.RotationY;
                existing.RotationZ = placement.RotationZ;
                existing.ScaleX = placement.ScaleX;
                existing.ScaleY = placement.ScaleY;
                existing.Opacity = placement.Opacity;
                existing.IsProjectorMode = placement.IsProjectorMode;
                existing.ScreensaverColorR = placement.ScreensaverColorR;
                existing.ScreensaverColorG = placement.ScreensaverColorG;
                existing.ScreensaverColorB = placement.ScreensaverColorB;
                existing.ScreensaverStyle = placement.ScreensaverStyle;
                existing.IsLocked = placement.IsLocked;
                existing.OwnerId = placement.OwnerId;
                existing.LastUpdated = placement.LastUpdated;
                _db.TvPlacements.Update(existing);
            }
            else
            {
                _db.TvPlacements.Add(placement);
            }
            await _db.SaveChangesAsync();
            return Ok(placement);
        }

        [HttpDelete("{locationKey}/tvs/{tvId}")]
        public async Task<IActionResult> RemoveTv(string locationKey, string tvId, [FromQuery] string ownerId, [FromQuery] bool bypassLock = false)
        {
            var tv = await _db.TvPlacements.FirstOrDefaultAsync(t => t.LocationKey == locationKey && t.Id == tvId);
            if (tv != null)
            {
                if (tv.OwnerId != ownerId && !bypassLock)
                    return StatusCode(403);
                _db.TvPlacements.Remove(tv);
                await _db.SaveChangesAsync();
                return Ok();
            }
            return NotFound();
        }

        [HttpGet("{locationKey}/media")]
        public async Task<IActionResult> GetMediaState(string locationKey)
        {
            var state = await _db.RoomMediaStates.FindAsync(locationKey);
            if (state == null) return NotFound();
            state.DataAgeMs = (DateTime.UtcNow - state.TimestampUtc).TotalMilliseconds;
            var lastFetch = _lastFetchTimes.TryGetValue(locationKey, out var lf) ? lf : DateTime.MinValue;
            state.IdleTimeMs = lastFetch == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - lastFetch).TotalMilliseconds;
            _lastFetchTimes[locationKey] = DateTime.UtcNow;
            if (state.IsPlaying && state.DurationMs.HasValue && (state.DataAgeMs + state.TimecodeMs) >= state.DurationMs.Value)
            {
                var playlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(state.PlaylistJson);
                if (playlist != null && playlist.Count > 0)
                {
                    string nextUrl = null;
                    while (playlist.Count > 0)
                    {
                        var candidate = playlist[0];
                        playlist.RemoveAt(0);
                        if (!IsUrlBlacklisted(candidate))
                        {
                            nextUrl = candidate;
                            break;
                        }
                    }
                    if (nextUrl != null)
                    {
                        state.CurrentUrl = nextUrl;
                        state.PlaylistJson = System.Text.Json.JsonSerializer.Serialize(playlist);
                        state.TimecodeMs = 0;
                        state.TimestampUtc = DateTime.UtcNow;
                        state.DataAgeMs = 0;
                        state.DurationMs = null;
                        _db.RoomMediaStates.Update(state);
                        await RecordMediaPlay(state.CurrentUrl, locationKey, state.OwnerId);
                        await _db.SaveChangesAsync();
                    }
                    else
                    {
                        state.IsPlaying = false;
                        state.TimecodeMs = (long)state.DurationMs.Value;
                        _db.RoomMediaStates.Update(state);
                        await _db.SaveChangesAsync();
                    }
                }
                else
                {
                    state.IsPlaying = false;
                    state.TimecodeMs = (long)state.DurationMs.Value;
                    _db.RoomMediaStates.Update(state);
                    await _db.SaveChangesAsync();
                }
            }
            _logger.LogDebug(
                "[Media] GetState (legacy): locationKey={Key}, url={Url}, playing={Playing}, timecode={Time}ms, duration={Dur}ms, dataAge={Age}ms, queueRemaining={QRemain}",
                locationKey, state.CurrentUrl, state.IsPlaying, state.TimecodeMs, state.DurationMs, state.DataAgeMs, state.PlaylistJson);
            return Ok(state);
        }

        [HttpPost("{locationKey}/media")]
        public async Task<IActionResult> UpdateMediaState(string locationKey, [FromBody] RoomMediaStateSync state)
        {
            if (IsUrlBlacklisted(state.CurrentUrl))
            {
                _logger.LogWarning("[Media] UpdateMedia REJECTED: blacklisted URL, locationKey={Key}, url={Url}", locationKey, state.CurrentUrl);
                return BadRequest("The provided URL is blacklisted.");
            }
            if (!string.IsNullOrEmpty(state.PlaylistJson))
            {
                try
                {
                    var playlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(state.PlaylistJson);
                    if (playlist != null && playlist.Any(url => IsUrlBlacklisted(url)))
                        return BadRequest("One or more URLs in the queue are blacklisted.");
                }
                catch { }
            }
            state.LocationKey = locationKey;
            var tv = await _db.TvPlacements.FindAsync(locationKey);
            if (tv != null && tv.IsLocked && tv.OwnerId != state.OwnerId && !state.BypassLock)
                return StatusCode(403);
            state.TimestampUtc = DateTime.UtcNow;

            int retries = 3;
            bool isNewPlay = false;
            while (retries > 0)
            {
                try
                {
                    var existing = await _db.RoomMediaStates.FindAsync(locationKey);
                    isNewPlay = false;

                    if (existing != null)
                    {
                        if (state.IsBackgroundSync && existing.OwnerId != state.OwnerId)
                        {
                            return Conflict();
                        }

                        if (existing.CurrentUrl != state.CurrentUrl && !string.IsNullOrEmpty(state.CurrentUrl))
                        {
                            isNewPlay = true;
                        }

                        existing.CurrentUrl = state.CurrentUrl;
                        existing.TimecodeMs = state.TimecodeMs;
                        existing.IsPlaying = state.IsPlaying;
                        existing.TimestampUtc = state.TimestampUtc;
                        existing.PlaylistJson = state.PlaylistJson;
                        existing.OwnerId = state.OwnerId;
                        existing.DurationMs = state.DurationMs;
                        _db.RoomMediaStates.Update(existing);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(state.CurrentUrl))
                        {
                            isNewPlay = true;
                        }

                        var newState = new RoomMediaStateSync {
                            LocationKey = state.LocationKey,
                            CurrentUrl = state.CurrentUrl,
                            TimecodeMs = state.TimecodeMs,
                            IsPlaying = state.IsPlaying,
                            TimestampUtc = state.TimestampUtc,
                            PlaylistJson = state.PlaylistJson,
                            OwnerId = state.OwnerId,
                            DurationMs = state.DurationMs
                        };
                        _db.RoomMediaStates.Add(newState);
                    }

                    await _db.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException)
                {
                    retries--;
                    if (retries == 0) throw;
                    _db.ChangeTracker.Clear();
                }
            }

            if (isNewPlay)
            {
                await RecordMediaPlay(state.CurrentUrl, locationKey, state.OwnerId);
                await _db.SaveChangesAsync();
            }

            if (!state.IsBackgroundSync)
                _logger.LogInformation(
                    "[Media] UpdateMedia (legacy): locationKey={Key}, url={Url}, playing={Playing}, ownerId={OwnerId}, isNewPlay={New}, duration={Dur}ms, queueSize={QSize}",
                    locationKey, state.CurrentUrl, state.IsPlaying, state.OwnerId, isNewPlay, state.DurationMs, state.PlaylistJson);
            return Ok(state);
        }

        [HttpGet("health")]
        public async Task<IActionResult> Health()
        {
            var allRooms = await _db.RoomStates
                .Where(r => r.DjOwnerId != "")
                .Select(r => new { r.LocationKey, r.DjOwnerId, r.StateVersion, r.IsPlaying, r.DjHeartbeatUtc })
                .ToListAsync();

            var now = DateTime.UtcNow;
            var activeDjs = allRooms
                .Where(r => (now - r.DjHeartbeatUtc).TotalSeconds <= 10)
                .Select(r => new { r.LocationKey, r.DjOwnerId, r.StateVersion, r.IsPlaying })
                .ToList();
            var staleDjs = allRooms
                .Where(r => (now - r.DjHeartbeatUtc).TotalSeconds > 10)
                .Select(r => new { r.LocationKey, r.DjOwnerId, r.StateVersion, Age = (now - r.DjHeartbeatUtc).TotalSeconds })
                .ToList();

            _logger.LogInformation(
                "[Room] Health check: totalRooms={Total}, activeDjs={Active}, staleDjs={Stale}",
                allRooms.Count, activeDjs.Count, staleDjs.Count);

            return Ok(new
            {
                TotalRooms = allRooms.Count,
                ActiveDjs = activeDjs,
                StaleDjs = staleDjs,
                ServerTime = DateTime.UtcNow,
                Uptime = Environment.TickCount64 / 1000
            });
        }

        // Phase 1: confirm-then-push protocol endpoints

        [HttpPost("{locationKey}/claim-dj")]
        public async Task<IActionResult> ClaimDj(string locationKey, [FromBody] ClaimDjRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var state = await _db.RoomStates.FindAsync(locationKey);
            if (state != null && !string.IsNullOrEmpty(state.DjOwnerId))
            {
                var heartbeatAge = (DateTime.UtcNow - state.DjHeartbeatUtc).TotalSeconds;
                if (heartbeatAge <= 10 && state.DjOwnerId != request.OwnerId)
                {
                    _logger.LogWarning(
                        "[Room] Claim DJ REJECTED: active DJ exists, requestOwner={RequestOwner}, currentDj={CurrentDj}, djAge={Age:F1}s, locationKey={Key}, oldVersion={Ver}",
                        request.OwnerId, state.DjOwnerId, heartbeatAge, locationKey, state.StateVersion);
                    var rejectResult = ApiResult<ClaimDjResponse>.Fail("Room already has an active DJ");
                    rejectResult.Data = new ClaimDjResponse
                    {
                        Success = false,
                        CurrentVersion = state.StateVersion,
                        DjOwnerId = state.DjOwnerId,
                        RoomState = state
                    };
                    return Conflict(rejectResult);
                }
            }
            bool isNewRoom = state == null;
            bool isTakeover = state != null && !string.IsNullOrEmpty(state.DjOwnerId);
            string prevDj = state?.DjOwnerId ?? "";
            string inheritedUrl = state?.CurrentUrl ?? "";
            if (state == null)
            {
                state = new RoomState
                {
                    LocationKey = locationKey,
                    CurrentUrl = "",
                    IsPlaying = false,
                    SpeedRate = 1.0f,
                    QueueJson = "[]",
                    StateVersion = 1
                };
                _db.RoomStates.Add(state);
            }
            else
            {
                state.StateVersion++;
            }
            state.DjOwnerId = request.OwnerId;
            state.DjHeartbeatUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            sw.Stop();
            _logger.LogInformation(
                "[Room] Claim DJ OK: ownerId={OwnerId}, locationKey={Key}, version={Ver}, isNew={New}, isTakeover={Takeover}, prevDj={PrevDj}, inheritedUrl={Url}, elapsed={Elapsed}ms",
                request.OwnerId, locationKey, state.StateVersion, isNewRoom, isTakeover, prevDj, inheritedUrl, sw.ElapsedMilliseconds);
            return Ok(ApiResult<ClaimDjResponse>.Ok(new ClaimDjResponse
            {
                Success = true,
                RoomState = state,
                CurrentVersion = state.StateVersion,
                DjOwnerId = state.DjOwnerId
            }));
        }

        [HttpPost("{locationKey}/heartbeat")]
        public async Task<IActionResult> Heartbeat(string locationKey, [FromBody] HeartbeatRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var state = await _db.RoomStates.FindAsync(locationKey);
            if (state == null)
            {
                _logger.LogWarning("[Room] Heartbeat FAIL: no room state, locationKey={Key}", locationKey);
                return NotFound(ApiResult<HeartbeatResponse>.Fail("Room state not found"));
            }
            if (request.OwnerId != state.DjOwnerId)
            {
                _logger.LogWarning(
                    "[Room] Heartbeat REJECTED: owner mismatch, requestOwner={RequestOwner}, actualDj={ActualDj}, locationKey={Key}",
                    request.OwnerId, state.DjOwnerId, locationKey);
                return StatusCode(403, ApiResult<HeartbeatResponse>.Fail("Not the active DJ"));
            }
            if (request.StateVersion != state.StateVersion)
            {
                _logger.LogWarning(
                    "[Room] Heartbeat VERSION CONFLICT: locationKey={Key}, reqVersion={ReqVer}, curVersion={CurVer}, ownerId={OwnerId}",
                    locationKey, request.StateVersion, state.StateVersion, request.OwnerId);
                var conflictResult = ApiResult<HeartbeatResponse>.Fail("Version conflict");
                conflictResult.Data = new HeartbeatResponse
                {
                    Accepted = false,
                    CurrentVersion = state.StateVersion,
                    AcceptedVersion = state.StateVersion
                };
                return Conflict(conflictResult);
            }

            bool urlChanged = state.CurrentUrl != request.CurrentUrl;
            bool playingChanged = state.IsPlaying != request.IsPlaying;
            int queueCount = request.Queue?.Count ?? 0;
            long timeDelta = request.TimecodeMs - state.TimecodeMs;

            state.CurrentUrl = request.CurrentUrl;
            state.TimecodeMs = request.TimecodeMs;
            state.IsPlaying = request.IsPlaying;
            state.SpeedRate = request.SpeedRate;
            state.QueueJson = JsonSerializer.Serialize(request.Queue);
            state.DjHeartbeatUtc = DateTime.UtcNow;
            state.StateVersion++;
            await _db.SaveChangesAsync();
            sw.Stop();

            if (urlChanged || playingChanged)
            {
                _logger.LogInformation(
                    "[Room] Heartbeat OK (changed): locationKey={Key}, version={Ver}, url={Url}, playing={Playing}, time={Time}ms, queue={QCount}, urlChanged={UrlChg}, playingChanged={PlayChg}, elapsed={Elapsed}ms",
                    locationKey, state.StateVersion, request.CurrentUrl, request.IsPlaying, request.TimecodeMs, queueCount, urlChanged, playingChanged, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogDebug(
                    "[Room] Heartbeat OK: locationKey={Key}, version={Ver}, time={Time}ms, timeDelta={Delta}ms, queue={QCount}, elapsed={Elapsed}ms",
                    locationKey, state.StateVersion, request.TimecodeMs, timeDelta, queueCount, sw.ElapsedMilliseconds);
            }
            return Ok(ApiResult<HeartbeatResponse>.Ok(new HeartbeatResponse
            {
                Accepted = true,
                AcceptedVersion = state.StateVersion,
                CurrentVersion = state.StateVersion
            }));
        }

        [HttpPost("{locationKey}/release-dj")]
        public async Task<IActionResult> ReleaseDj(string locationKey, [FromBody] ReleaseDjRequest request)
        {
            var state = await _db.RoomStates.FindAsync(locationKey);
            if (state == null)
            {
                _logger.LogWarning("[Room] Release DJ FAIL: no room state, locationKey={Key}", locationKey);
                return NotFound(ApiResult<ClaimDjResponse>.Fail("Room state not found"));
            }
            if (request.OwnerId != state.DjOwnerId)
            {
                _logger.LogWarning(
                    "[Room] Release DJ REJECTED: owner mismatch, requestOwner={RequestOwner}, actualDj={ActualDj}, locationKey={Key}",
                    request.OwnerId, state.DjOwnerId, locationKey);
                return StatusCode(403, ApiResult<ClaimDjResponse>.Fail("Not the active DJ"));
            }
            string lastUrl = state.CurrentUrl;
            bool wasPlaying = state.IsPlaying;
            long lastVersion = state.StateVersion;
            state.CurrentUrl = "";
            state.IsPlaying = false;
            state.DjOwnerId = "";
            state.SpeedRate = 1.0f;
            state.StateVersion++;
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "[Room] Release DJ OK: ownerId={OwnerId}, locationKey={Key}, version={Ver}, lastUrl={Url}, wasPlaying={Playing}, prevVersion={PrevVer}",
                request.OwnerId, locationKey, state.StateVersion, lastUrl, wasPlaying, lastVersion);
            return Ok(ApiResult<ClaimDjResponse>.Ok(new ClaimDjResponse
            {
                Success = true,
                RoomState = state,
                CurrentVersion = state.StateVersion
            }));
        }

        [HttpGet("{locationKey}/state")]
        public async Task<IActionResult> GetState(string locationKey)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var state = await _db.RoomStates.FindAsync(locationKey);
            if (state == null)
            {
                _logger.LogDebug("[Room] GetState: no state for locationKey={Key}", locationKey);
                return NotFound();
            }
            var djHeartbeatAge = (DateTime.UtcNow - state.DjHeartbeatUtc).TotalSeconds;
            var djDisconnected = djHeartbeatAge > 10;
            List<string> queue;
            try
            {
                queue = JsonSerializer.Deserialize<List<string>>(state.QueueJson) ?? new List<string>();
            }
            catch
            {
                queue = new List<string>();
            }
            sw.Stop();
            _logger.LogDebug(
                "[Room] GetState: locationKey={Key}, version={Ver}, dj={DjId}, djAge={Age:F1}s, djOff={Offline}, url={Url}, playing={Playing}, queue={QCount}, elapsed={Elapsed}ms",
                locationKey, state.StateVersion, state.DjOwnerId, djHeartbeatAge, djDisconnected, state.CurrentUrl, state.IsPlaying, queue.Count, sw.ElapsedMilliseconds);
            var response = new RoomStateResponse
            {
                CurrentUrl = state.CurrentUrl,
                TimecodeMs = state.TimecodeMs,
                IsPlaying = state.IsPlaying,
                SpeedRate = state.SpeedRate,
                Queue = queue,
                DjOwnerId = state.DjOwnerId,
                DjHeartbeatAgeSeconds = djHeartbeatAge,
                StateVersion = state.StateVersion,
                DjDisconnected = djDisconnected
            };
            return Ok(response);
        }

        [HttpPost("batch/tvs")]
        public async Task<IActionResult> GetTvsBatch([FromBody] List<string> locationKeys)
        {
            if (locationKeys == null || !locationKeys.Any()) return BadRequest();
            var tvs = await _db.TvPlacements
                .Where(t => locationKeys.Contains(t.LocationKey))
                .ToListAsync();
            return Ok(tvs);
        }

        [HttpPost("batch/media")]
        public async Task<IActionResult> GetMediaStatesBatch([FromBody] List<string> locationKeys)
        {
            if (locationKeys == null || !locationKeys.Any()) return BadRequest();
            var states = await _db.RoomMediaStates
                .Where(s => locationKeys.Contains(s.LocationKey))
                .ToListAsync();
            foreach (var state in states)
            {
                state.DataAgeMs = (DateTime.UtcNow - state.TimestampUtc).TotalMilliseconds;
                var lastFetch = _lastFetchTimes.TryGetValue(state.LocationKey, out var lf) ? lf : DateTime.MinValue;
                state.IdleTimeMs = lastFetch == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - lastFetch).TotalMilliseconds;
                _lastFetchTimes[state.LocationKey] = DateTime.UtcNow;
            }
            return Ok(states);
        }

        [HttpGet("media/history")]
        public async Task<IActionResult> GetMediaHistory([FromQuery] int limit = 100)
        {
            var history = await _db.MediaTrackRecords
                .OrderByDescending(r => r.PlayedAtUtc)
                .Take(limit)
                .ToListAsync();
            return Ok(history);
        }

        [HttpGet("media/stats")]
        public async Task<IActionResult> GetMediaStats([FromQuery] int limit = 10)
        {
            var topUrls = await _db.MediaTrackRecords
                .GroupBy(r => r.Url)
                .Select(g => new { Url = g.Key, Count = g.Count(), LastPlayed = g.Max(r => r.PlayedAtUtc) })
                .OrderByDescending(x => x.Count)
                .Take(limit)
                .ToListAsync();
            var topDomains = await _db.MediaTrackRecords
                .GroupBy(r => r.Domain)
                .Select(g => new { Domain = g.Key, Count = g.Count(), LastPlayed = g.Max(r => r.PlayedAtUtc) })
                .OrderByDescending(x => x.Count)
                .Take(limit)
                .ToListAsync();
            return Ok(new { TopUrls = topUrls, TopDomains = topDomains });
        }

        private async Task RecordMediaPlay(string url, string locationKey, string ownerId)
        {
            if (string.IsNullOrEmpty(url)) return;
            string domain = string.Empty;
            try
            {
                var uri = new Uri(url);
                domain = uri.Host;
            }
            catch { }
            var record = new MediaTrackRecord
            {
                Url = url,
                Domain = domain,
                LocationKey = locationKey,
                OwnerId = ownerId,
                PlayedAtUtc = DateTime.UtcNow
            };
            _db.MediaTrackRecords.Add(record);
        }

        private bool IsUrlBlacklisted(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var blacklistedDomains = _config.GetSection("MediaBlacklist:Domains").Get<List<string>>() ?? new List<string>();
            var blacklistedUrls = _config.GetSection("MediaBlacklist:Urls").Get<List<string>>() ?? new List<string>();
            var hashedDomains = _config.GetSection("MediaBlacklist:HashedDomains").Get<List<string>>() ?? new List<string>();
            var hashedUrls = _config.GetSection("MediaBlacklist:HashedUrls").Get<List<string>>() ?? new List<string>();
            if (blacklistedUrls.Contains(url, StringComparer.OrdinalIgnoreCase)) return true;
            if (hashedUrls.Any())
            {
                var urlHash = ComputeSha256Hash(url.ToLowerInvariant());
                if (hashedUrls.Contains(urlHash, StringComparer.OrdinalIgnoreCase)) return true;
            }
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                if (blacklistedDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
                    return true;
                if (hashedDomains.Any())
                {
                    var parts = host.Split('.');
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        var domainToTest = string.Join(".", parts.Skip(i));
                        var domainHash = ComputeSha256Hash(domainToTest);
                        if (hashedDomains.Contains(domainHash, StringComparer.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private string ComputeSha256Hash(string rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                var builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                    builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}

