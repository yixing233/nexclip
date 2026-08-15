using Microsoft.EntityFrameworkCore;

namespace SyncClipboardServer;

/// 定时刷新在线设备心跳:hub 连接存续期间设备保持 online
public class HubHeartbeatService(IServiceScopeFactory scopeFactory, ILogger<HubHeartbeatService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var ids = ClipboardHub.ActiveDevices.Values.Distinct().ToList();
                if (ids.Count == 0) continue;
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                foreach (var id in ids)
                {
                    var d = await db.Devices.FindAsync(id);
                    if (d != null) d.LastSeenAt = now;
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "设备心跳刷新失败");
            }
        }
    }
}
