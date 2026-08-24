using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NexClipServer;

[ApiController]
[Route("api/devices")]
public class DevicesController(AppDbContext db, ClipboardService svc) : ControllerBase
{
    /// 设备列表(online = LastSeenAt 在阈值内)
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var threshold = DateTime.UtcNow.AddSeconds(-svc.Options.OnlineThresholdSeconds);
        var list = await db.Devices.OrderByDescending(d => d.LastSeenAt).ToListAsync();
        return Ok(list.Select(d => new
        {
            d.Id, d.Name, d.Platform, d.Ip, d.Version,
            online = d.LastSeenAt >= threshold,
            lastSeenAt = d.LastSeenAt.ToString("O"),
        }));
    }

    /// 重命名
    [HttpPut("{id}")]
    public async Task<IActionResult> Rename(string id, [FromBody] RenameDeviceRequest req)
    {
        var d = await db.Devices.FindAsync(id);
        if (d is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Name)) d.Name = req.Name.Trim();
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// 移除设备
    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        var d = await db.Devices.FindAsync(id);
        if (d is null) return NotFound();
        db.Devices.Remove(d);
        db.Activities.Add(new ActivityLog { Action = "delete", DeviceName = d.Name, Content = "移除了设备", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return NoContent();
    }
}
