using Microsoft.AspNetCore.Mvc;

namespace NexClipServer;

[ApiController]
[Route("api/clipboard")]
public class ClipboardController(ClipboardService svc) : ControllerBase
{
    /// 当前剪贴板(共享模型 = 最新一条);无内容 204
    [HttpGet]
    public async Task<IActionResult> GetCurrent()
    {
        var e = await svc.GetCurrentAsync();
        if (e is null) return NoContent();
        return Ok(e);
    }

    /// 上传文本剪贴板;同内容去重返回当前条目
    [HttpPut]
    public async Task<IActionResult> UploadText([FromBody] UploadTextRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 500_000)
            return BadRequest(new { error = "text 不能为空且不超过 500KB" });
        var deviceId = string.IsNullOrEmpty(req.DeviceId) ? "web-" + Guid.NewGuid().ToString("N")[..8] : req.DeviceId;
        var deviceName = string.IsNullOrEmpty(req.DeviceName) ? deviceId : req.DeviceName;
        var ip = IpUtil.Normalize(HttpContext.Connection.RemoteIpAddress?.ToString());
        var (entry, unchanged) = await svc.UploadTextAsync(text, deviceId, deviceName, req.Platform, req.Version, ip, isManual: req.IsManual);
        // 扁平条目 + unchanged 标记:web 端直接取条目字段,桌面端读 unchanged 判断是否新内容
        return Ok(new { entry!.Id, entry.Type, entry.Text, entry.ImageRef, entry.DeviceId, entry.DeviceName, entry.IsManual, entry.CreatedAt, unchanged });
    }

    /// 上传图片剪贴板(multipart/form-data: file + deviceId + deviceName + isManual)
    [HttpPost("image")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<IActionResult> UploadImage(IFormFile file, [FromForm] string? deviceId, [FromForm] string? deviceName, [FromForm] bool isManual = false)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "缺少图片文件" });
        var max = svc.Options.MaxImageSizeBytes;
        if (file.Length > max)
            return BadRequest(new { error = $"图片超过大小限制({max / 1024 / 1024}MB)" });
        var did = string.IsNullOrEmpty(deviceId) ? "web-" + Guid.NewGuid().ToString("N")[..8] : deviceId;
        var dname = string.IsNullOrEmpty(deviceName) ? did : deviceName;
        await using var stream = file.OpenReadStream();
        var entry = await svc.UploadImageAsync(stream, file.FileName, did, dname, IpUtil.Normalize(HttpContext.Connection.RemoteIpAddress?.ToString()), isManual: isManual);
        return Ok(entry);
    }

    /// 历史列表(新→旧,分页)
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int offset = 0, [FromQuery] int limit = 20)
    {
        var (items, total) = await svc.GetHistoryAsync(offset, limit);
        return Ok(new { items, total });
    }

    /// 按 id 获取
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var e = await svc.GetByIdAsync(id);
        if (e is null) return NotFound();
        return Ok(e);
    }

    /// 清空全部历史(含图片文件),并广播 ClipboardCleared
    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistory()
    {
        await svc.ClearHistoryAsync();
        await svc.BroadcastClearedAsync();
        return NoContent();
    }

    /// 删除单条
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await svc.DeleteAsync(id);
        return NoContent();
    }

    /// 发送给指定设备:写入共享剪贴板;实时通知只推送到目标设备(未指定目标时广播全员,保持旧行为)
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendRequest req)
    {
        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest(new { error = "text 不能为空" });
        var deviceId = string.IsNullOrEmpty(req.DeviceId) ? "web-" + Guid.NewGuid().ToString("N")[..8] : req.DeviceId;
        var deviceName = string.IsNullOrEmpty(req.DeviceName) ? deviceId : req.DeviceName;
        var targets = req.DeviceIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? [];
        var (entry, _) = await svc.UploadTextAsync(text, deviceId, deviceName, "Web", null,
            IpUtil.Normalize(HttpContext.Connection.RemoteIpAddress?.ToString()), broadcast: targets.Count == 0, isManual: true);
        if (entry is not null && targets.Count > 0)
            await svc.BroadcastAsync(entry, targets);
        return Ok(entry);
    }
}
