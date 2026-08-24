using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace NexClipServer;

/// 实时推送通道:连接需带 token(query access_token 或 header),可选 deviceId 用于设备心跳。
/// HubHeartbeatService 每 45s 刷新在线设备的 LastSeenAt,保证连接存活期间设备保持在线。
public class ClipboardHub(AppDbContext db, ILogger<ClipboardHub> log) : Hub
{
    /// connectionId -> deviceId(连接存活期间视为设备在线)
    public static readonly ConcurrentDictionary<string, string> ActiveDevices = new();

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var deviceId = http?.Request.Query["deviceId"].ToString();
        if (string.IsNullOrEmpty(deviceId)) deviceId = http?.Request.Headers["X-Device-Id"].ToString();

        var deviceName = http?.Request.Query["deviceName"].ToString();
        if (string.IsNullOrEmpty(deviceName)) deviceName = http?.Request.Headers["X-Device-Name"].ToString();

        var platform = http?.Request.Query["platform"].ToString();
        if (string.IsNullOrEmpty(platform)) platform = http?.Request.Headers["X-Platform"].ToString();

        if (!string.IsNullOrEmpty(deviceId))
        {
            ActiveDevices[Context.ConnectionId] = deviceId;
            var d = await db.Devices.FindAsync(deviceId);
            if (d is null)
            {
                d = new Device
                {
                    Id = deviceId,
                    Name = string.IsNullOrEmpty(deviceName) ? "未知设备" : deviceName,
                    Platform = string.IsNullOrEmpty(platform) ? "Unknown" : platform,
                    LastSeenAt = DateTime.UtcNow
                };
                db.Devices.Add(d);
                db.Activities.Add(new ActivityLog { Action = "connect", DeviceName = d.Name, CreatedAt = DateTime.UtcNow });
            }
            else
            {
                if (!string.IsNullOrEmpty(deviceName)) d.Name = deviceName;
                if (!string.IsNullOrEmpty(platform)) d.Platform = platform;
                d.LastSeenAt = DateTime.UtcNow;
                db.Activities.Add(new ActivityLog { Action = "connect", DeviceName = d.Name, CreatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        log.LogInformation("Hub 连接: {ConnectionId} (DeviceId: {DeviceId}, DeviceName: {DeviceName})", Context.ConnectionId, deviceId, deviceName);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        ActiveDevices.TryRemove(Context.ConnectionId, out var deviceId);
        log.LogInformation("Hub 断开: {ConnectionId} (DeviceId: {DeviceId})", Context.ConnectionId, deviceId);
        return base.OnDisconnectedAsync(exception);
    }
}
