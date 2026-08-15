using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace SyncClipboardServer;

/// 实时推送通道:连接需带 token(query access_token 或 header),可选 deviceId 用于设备心跳。
/// HubHeartbeatService 每 45s 刷新在线设备的 LastSeenAt,保证连接存活期间设备保持在线。
public class ClipboardHub(AppDbContext db, ILogger<ClipboardHub> log) : Hub
{
    /// connectionId -> deviceId(连接存活期间视为设备在线)
    public static readonly ConcurrentDictionary<string, string> ActiveDevices = new();

    public override async Task OnConnectedAsync()
    {
        var deviceId = Context.GetHttpContext()?.Request.Query["deviceId"].ToString();
        if (!string.IsNullOrEmpty(deviceId))
        {
            ActiveDevices[Context.ConnectionId] = deviceId;
            var d = await db.Devices.FindAsync(deviceId);
            if (d is null)
            {
                d = new Device { Id = deviceId, Name = "未知设备", Platform = "Web" };
                db.Devices.Add(d);
                db.Activities.Add(new ActivityLog { Action = "connect", DeviceName = d.Name, CreatedAt = DateTime.UtcNow });
            }
            else
            {
                d.LastSeenAt = DateTime.UtcNow;
                db.Activities.Add(new ActivityLog { Action = "connect", DeviceName = d.Name, CreatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        log.LogInformation("Hub 连接: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        ActiveDevices.TryRemove(Context.ConnectionId, out _);
        log.LogInformation("Hub 断开: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
