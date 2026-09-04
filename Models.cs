using System.ComponentModel.DataAnnotations;

namespace NexClipServer;

/// 剪贴板条目(共享模型:当前剪贴板 = CreatedAt 最新一条)
public class ClipboardEntry
{
    public long Id { get; set; }
    [MaxLength(16)] public string Type { get; set; } = "Text";
    public string? Text { get; set; }
    /// 富文本片段(HTML,可选):不含 CF_HTML 头,由客户端写回剪贴板时重新生成;老客户端忽略该字段
    public string? Html { get; set; }
    public string? ImageRef { get; set; }
    /// 内容指纹(SHA256),用于去重:同内容不重复入库、不重复推送
    [MaxLength(64)] public string ContentHash { get; set; } = "";
    [MaxLength(64)] public string DeviceId { get; set; } = "";
    [MaxLength(128)] public string? DeviceName { get; set; }
    public bool IsManual { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// 已注册设备(首次接触自动登记;online = LastSeenAt 在阈值内)
public class Device
{
    [Key] [MaxLength(64)] public string Id { get; set; } = "";
    [MaxLength(128)] public string Name { get; set; } = "";
    [MaxLength(32)] public string Platform { get; set; } = "Unknown";
    [MaxLength(64)] public string? Ip { get; set; }
    [MaxLength(32)] public string? Version { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

/// 活动日志(Web 时间线 / 客户端展示)
public class ActivityLog
{
    public long Id { get; set; }
    /// push | receive | connect | delete
    [MaxLength(16)] public string Action { get; set; } = "push";
    [MaxLength(128)] public string DeviceName { get; set; } = "";
    [MaxLength(256)] public string? Content { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------- 请求 DTO ----------
public record UploadTextRequest(string? Type, string? Text, string? Html, string? DeviceId, string? DeviceName, string? Platform, string? Version, bool IsManual = false);
public record SendRequest(string? Text, string[]? DeviceIds, string? DeviceId, string? DeviceName, bool IsManual = true);
public record RenameDeviceRequest(string? Name);
