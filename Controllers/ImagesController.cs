using Microsoft.AspNetCore.Mvc;

namespace SyncClipboardServer;

[ApiController]
[Route("api/images")]
public class ImagesController(ClipboardService svc) : ControllerBase
{
    /// 图片二进制,{path} 为相对路径(如 20260815/xxx.png),防路径穿越
    [HttpGet("{**path}")]
    public IActionResult Get(string path)
    {
        var root = Path.GetFullPath(svc.Options.ImageStoragePath);
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return NotFound();
        var ext = Path.GetExtension(full).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
        return PhysicalFile(full, contentType);
    }
}
