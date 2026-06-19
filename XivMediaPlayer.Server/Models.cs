using XivMediaPlayer.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace XivMediaPlayer.Server.Models
{
    public class AppDbContext : DbContext
    {
        public DbSet<TvPlacement> TvPlacements { get; set; } = null!;
        public DbSet<RoomMediaStateSync> RoomMediaStates { get; set; } = null!;
        public DbSet<MediaTrackRecord> MediaTrackRecords { get; set; } = null!;
        public DbSet<RoomState> RoomStates { get; set; } = null!;

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TvPlacement>(entity =>
            {
                entity.HasKey(t => t.LocationKey);
                // BypassLock is a runtime-only field, not persisted
                entity.Ignore(t => t.BypassLock);
            });

            modelBuilder.Entity<RoomMediaStateSync>(entity =>
            {
                entity.HasKey(m => m.LocationKey);
                // These fields are runtime-only, not persisted
                entity.Ignore(m => m.BypassLock);
                entity.Ignore(m => m.IsBackgroundSync);
                entity.Ignore(m => m.DataAgeMs);
                entity.Ignore(m => m.IdleTimeMs);
            });

            modelBuilder.Entity<MediaTrackRecord>(entity =>
            {
                entity.HasKey(r => r.Id);
            });

            modelBuilder.Entity<RoomState>(entity =>
            {
                entity.HasKey(r => r.LocationKey);
            });
        }
    }
}
