using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;

namespace XivMediaPlayer.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RoomsController : ControllerBase
    {
        private readonly AppDbContext _db;

        public RoomsController(AppDbContext db)
        {
            _db = db;
        }

        [HttpGet("{locationKey}/tvs")]
        public async Task<IActionResult> GetTvs(string locationKey)
        {
            var tvs = await _db.TvPlacements
                .Where(t => t.LocationKey == locationKey)
                .ToListAsync();
                
            return Ok(tvs);
        }

        [HttpPost("{locationKey}/tvs")]
        public async Task<IActionResult> RegisterTv(string locationKey, [FromBody] TvPlacement placement)
        {
            placement.LocationKey = locationKey;
            placement.LastUpdated = DateTime.UtcNow;

            var existing = await _db.TvPlacements.FindAsync(locationKey);
            if (existing != null)
            {
                if (existing.IsLocked && existing.OwnerId != placement.OwnerId && !placement.BypassLock)
                {
                    return Forbid();
                }

                // Update existing TV
                existing.PositionX = placement.PositionX;
                existing.PositionY = placement.PositionY;
                existing.PositionZ = placement.PositionZ;
                existing.RotationX = placement.RotationX;
                existing.RotationY = placement.RotationY;
                existing.RotationZ = placement.RotationZ;
                existing.ScaleX = placement.ScaleX;
                existing.ScaleY = placement.ScaleY;
                existing.IsLocked = placement.IsLocked;
                // We do NOT update the OwnerId of an existing TV unless they were already the owner, 
                // but if it wasn't locked they can technically steal it right now.
                existing.OwnerId = placement.OwnerId;
                existing.LastUpdated = placement.LastUpdated;
                _db.TvPlacements.Update(existing);
            }
            else
            {
                // Add new TV
                _db.TvPlacements.Add(placement);
            }

            await _db.SaveChangesAsync();

            return Ok(placement);
        }

        [HttpDelete("{locationKey}/tvs/{tvId}")]
        public async Task<IActionResult> RemoveTv(string locationKey, string tvId)
        {
            var tv = await _db.TvPlacements.FirstOrDefaultAsync(t => t.LocationKey == locationKey && t.Id == tvId);
            if (tv != null)
            {
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
            
            // Calculate exactly how many milliseconds have passed since the HOST pushed this data.
            // By doing this on the server, we completely eliminate client clock drift issues!
            state.DataAgeMs = (DateTime.UtcNow - state.TimestampUtc).TotalMilliseconds;
            
            return Ok(state);
        }

        [HttpPost("{locationKey}/media")]
        public async Task<IActionResult> UpdateMediaState(string locationKey, [FromBody] RoomMediaStateSync state)
        {
            state.LocationKey = locationKey;
            
            // Check if the TV is locked
            var tv = await _db.TvPlacements.FindAsync(locationKey);
            if (tv != null && tv.IsLocked && tv.OwnerId != state.OwnerId && !state.BypassLock)
            {
                return Forbid();
            }

            // Always stamp with the server's exact current time to prevent client drift
            state.TimestampUtc = DateTime.UtcNow;

            var existing = await _db.RoomMediaStates.FindAsync(locationKey);
            if (existing != null)
            {
                existing.CurrentUrl = state.CurrentUrl;
                existing.TimecodeMs = state.TimecodeMs;
                existing.IsPlaying = state.IsPlaying;
                existing.TimestampUtc = state.TimestampUtc;
                existing.PlaylistJson = state.PlaylistJson;
                existing.OwnerId = state.OwnerId;
                _db.RoomMediaStates.Update(existing);
            }
            else
            {
                _db.RoomMediaStates.Add(state);
            }

            await _db.SaveChangesAsync();
            return Ok(state);
        }
    }
}
