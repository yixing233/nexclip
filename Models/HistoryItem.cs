namespace NexClip.Desktop.Models;

/// <summary>本地历史条目(设计文档 §5)。</summary>
public sealed class HistoryItem
{
    public long Id { get; set; }
    public long? ServerId { get; set; }       // 服务端条目 Id,用于去重
    public string Type { get; set; } = "Text"; // Text | Image | File
    public string? Text { get; set; }
    public string? Html { get; set; }         // 富文本 HTML 片段(仅 Text 条目;纯文本条目为 null)
    public string? ImagePath { get; set; }    // 本地缓存文件
    public string? ImageRef { get; set; }     // 远端引用
    /// <summary>
    /// 文件条目的路径元数据(JSON 数组,见 <see cref="ClipboardFileInfo"/>)。
    /// 仅本地保存,永不上传;条目 image_path 必须保持为 NULL,
    /// 否则超限清理(TrimToLimitLocked / PruneOlderThan / Clear)会把用户真实文件当作图片缓存删除。
    /// </summary>
    public string? FilePathsJson { get; set; }
    public string? ContentHash { get; set; }    // 内容哈希(文本/图片字节,文件条目取路径元数据哈希),用于重复内容置顶去重
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? SourceAppName { get; set; } // 来源软件名称 (如 Visual Studio Code, Google Chrome)
    public string? SourceAppPath { get; set; } // 来源软件路径 (如 C:\...\Code.exe)
    public string? SourceAppIcon { get; set; } // 来源软件本地图标缓存路径
    public DateTime CreatedAt { get; set; }   // UTC
    public int Origin { get; set; }           // 0=本地捕获 1=远端推送 2=本端主动同步
    public bool Starred { get; set; }
    public string? Remark { get; set; }        // 用户自定义备注
    public bool HasRemark => !string.IsNullOrWhiteSpace(Remark);
    public bool HasHtml => !string.IsNullOrWhiteSpace(Html);
    public bool IsManual => Origin == 2 || SourceAppName == "即时互传" || SourceAppName == "手动推送";
    public bool IsFile => Type == "File";

    private IReadOnlyList<ClipboardFileInfo>? _files;

    /// <summary>文件条目的文件列表;非文件条目恒为空列表。首次访问时按需反序列化并缓存。</summary>
    public IReadOnlyList<ClipboardFileInfo> Files =>
        _files ??= Type == "File" ? ClipboardFileMeta.Parse(FilePathsJson) : Array.Empty<ClipboardFileInfo>();
}
