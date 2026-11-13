namespace NexClip.Desktop.Models;

/// <summary>
/// 文件互传条目(GET /api/files、POST /api/files 返回的元数据,2026-11-13 服务端新增)。
/// </summary>
/// <remarks>
/// 与 <see cref="ClipboardEntry"/> 的关键区别:文件不在剪贴板历史里(独立 Files 表),
/// 接收端收到 FileTransferred 推送后应展示为「文件」,绝不写入剪贴板/自动粘贴 ——
/// 这保证了传文件不会像文本推送那样在接收端触发粘贴链路。
/// </remarks>
public sealed class FileTransferInfo
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    /// <summary>服务端按内容算的 SHA-256(大写),可用于去重/校验。</summary>
    public string ContentHash { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>服务端生成的下载路径(/api/files/{id}/download),带凭证 GET 即可。</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>人类可读大小("12.3 KB / 4.5 MB")。</summary>
    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Size / 1024.0 / 1024.0:F1} MB",
        _ => $"{Size / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}
