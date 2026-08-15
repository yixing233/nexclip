using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace SyncClipboardServer;

[ApiController]
[Route("api")]
public class StatsController(AppDbContext db, ClipboardService svc, LatencyTracker latency) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var now = DateTime.UtcNow;
        var threshold = now.AddSeconds(-svc.Options.OnlineThresholdSeconds);
        var online = await db.Devices.CountAsync(d => d.LastSeenAt >= threshold);
        var total = await db.Devices.CountAsync();
        var totalEntries = await db.Entries.CountAsync();

        var todayStart = now.Date;
        var yesterdayStart = todayStart.AddDays(-1);
        var todayCount = await db.Entries.CountAsync(e => e.CreatedAt >= todayStart);
        var yesterdayCount = await db.Entries.CountAsync(e => e.CreatedAt >= yesterdayStart && e.CreatedAt < todayStart);
        var syncTrend = yesterdayCount == 0
            ? (todayCount > 0 ? 100 : 0)
            : (int)Math.Round((todayCount - yesterdayCount) * 100.0 / yesterdayCount);

        // 最近 12 小时同步分布(sparkline)
        var since = now.AddHours(-12);
        var hourly = await db.Entries
            .Where(e => e.CreatedAt >= since)
            .ToListAsync();
        var sync = new long[12];
        var hist = new long[12];
        long cum = await db.Entries.CountAsync(e => e.CreatedAt < since);
        for (var i = 0; i < 12; i++)
        {
            var s = now.AddHours(-12 + i);
            var e = now.AddHours(-11 + i);
            sync[i] = hourly.Count(x => x.CreatedAt >= s && x.CreatedAt < e);
            cum += sync[i];
            hist[i] = cum;
        }
        var devicesSpark = new long[12];
        var actSince = now.AddHours(-12);
        var connects = await db.Activities.Where(a => a.Action == "connect" && a.CreatedAt >= actSince).ToListAsync();
        for (var i = 0; i < 12; i++)
        {
            var s = now.AddHours(-12 + i);
            var e = now.AddHours(-11 + i);
            devicesSpark[i] = connects.Count(x => x.CreatedAt >= s && x.CreatedAt < e);
        }

        var uptime = now - svc.Options.StartedAt;
        var uptimeStr = uptime.TotalDays >= 1
            ? $"{uptime.Days} 天 {uptime.Hours} 小时"
            : uptime.TotalHours >= 1
                ? $"{uptime.Hours} 小时 {uptime.Minutes} 分钟"
                : $"{uptime.Minutes} 分钟";

        return Ok(new
        {
            onlineDevices = online,
            totalDevices = total,
            todaySyncCount = todayCount,
            syncTrend,
            totalClipboardCount = totalEntries,
            status = "running",
            uptime = uptimeStr,
            avgLatencyMs = latency.AvgMs,
            sparklines = new
            {
                devices = devicesSpark,
                sync,
                history = hist,
                latency = latency.Last12,
            },
        });
    }

    [HttpGet("activities")]
    public async Task<IActionResult> Activities([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);
        var list = await db.Activities.OrderByDescending(a => a.Id).Take(limit).ToListAsync();
        return Ok(list.Select(a => new
        {
            a.Id, a.Action, a.DeviceName, a.Content,
            createdAt = a.CreatedAt.ToString("O"),
        }));
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "ok", time = DateTime.UtcNow.ToString("O") });
}
