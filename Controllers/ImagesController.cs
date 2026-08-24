using Microsoft.AspNetCore.Mvc;

namespace NexClipServer;

[ApiController]
[Route("api/images")]
public class ImagesController(ClipboardService svc) : ControllerBase
{
    /// 图片二进制,{path} 为相对路径(如 20260815/xxx.png),纯内存中转优先
    [HttpGet("{**path}")]
    public IActionResult Get(string path)
    {
        var img = svc.GetImage(path);
        if (img is null) return NotFound();
        return File(img.Value.bytes, img.Value.contentType);
    }
}
