namespace SyncClipboard.Desktop.Models;

/// <summary>本地历史条目(设计文档 §5)。</summary>
public sealed class HistoryItem
{
    public long Id { get; set; }
    public long? ServerId { get; set; }       // 服务端条目 Id,用于去重
    public string Type { get; set; } = "Text"; // Text | Image
    public string? Text { get; set; }
    public string? ImagePath { get; set; }    // 本地缓存文件
    public string? ImageRef { get; set; }     // 远端引用
    public string? ContentHash { get; set; }    // 内容哈希(文本/图片字节),用于重复内容置顶去重
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? SourceAppName { get; set; } // 来源软件名称 (如 Visual Studio Code, Google Chrome)
    public string? SourceAppPath { get; set; } // 来源软件路径 (如 C:\...\Code.exe)
    public string? SourceAppIcon { get; set; } // 来源软件本地图标缓存路径
    public DateTime CreatedAt { get; set; }   // UTC
    public int Origin { get; set; }           // 0=本地捕获 1=远端推送 2=本端主动同步
    public bool Starred { get; set; }
}
