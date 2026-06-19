using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;

namespace XivMediaPlayer.Server;

public class DjTimeoutService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DjTimeoutService> _logger;

    public DjTimeoutService(IServiceScopeFactory scopeFactory, ILogger<DjTimeoutService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Room] DJ timeout monitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(10);
                var staleDjs = await db.RoomStates
                    .Where(r => r.DjOwnerId != "" && r.DjHeartbeatUtc < cutoff)
                    .ToListAsync(stoppingToken);

                foreach (var room in staleDjs)
                {
                    var age = (DateTime.UtcNow - room.DjHeartbeatUtc).TotalSeconds;
                    _logger.LogWarning(
                        "[Room] DJ TIMEOUT: locationKey={Key}, ownerId={OwnerId}, version={Ver}, heartbeatAge={Age:F1}s, url={Url}, playing={Playing}",
                        room.LocationKey, room.DjOwnerId, room.StateVersion, age, room.CurrentUrl, room.IsPlaying);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Room] DJ timeout monitor error");
            }
        }

        _logger.LogInformation("[Room] DJ timeout monitor stopped");
    }
}
