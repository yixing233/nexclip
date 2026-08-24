using Microsoft.AspNetCore.Mvc;

namespace NexClipServer;

/// 兼容旧协议:GET/PUT /SyncClipboard.json(仅文本,过渡用)
[ApiController]
public class LegacyController(ClipboardService svc) : ControllerBase
{
    [HttpGet("/SyncClipboard.json")]
    public async Task<IActionResult> Get()
    {
        var e = await svc.GetCurrentAsync();
        if (e is null) return Ok(new { text = "", deviceId = "", deviceName = "", createdAt = "" });
        return Ok(new { text = e.Text ?? "", deviceId = e.DeviceId, deviceName = e.DeviceName ?? "", createdAt = e.CreatedAt.ToString("O") });
    }

    [HttpPut("/SyncClipboard.json")]
    public async Task<IActionResult> Put([FromBody] LegacyUpload body)
    {
        if (string.IsNullOrEmpty(body.Text)) return BadRequest(new { error = "text 不能为空" });
        var (_, _) = await svc.UploadTextAsync(body.Text, body.DeviceId ?? "legacy", body.DeviceName ?? "Legacy Client", null, null, IpUtil.Normalize(HttpContext.Connection.RemoteIpAddress?.ToString()));
        return Ok(new { ok = true });
    }

    public record LegacyUpload(string? Text, string? DeviceId, string? DeviceName);
}
