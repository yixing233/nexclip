namespace SyncClipboard.Desktop.Models;

/// <summary>与服务端条目 JSON 一一对应(GET/PUT /api/clipboard)。</summary>
public sealed class ClipboardEntry
{
    public long Id { get; set; }
    public string Type { get; set; } = "Text";       // Text | Image
    public string? Text { get; set; }
    public string? ImageRef { get; set; }
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public DateTime CreatedAt { get; set; }          // UTC

    /// <summary>相对时间展示("刚刚 / 3 分钟前 / 昨天 / 2026/08/15")。</summary>
    public string RelativeTimeText
    {
        get
        {
            var now = DateTime.UtcNow;
            var diff = now - CreatedAt;
            if (diff < TimeSpan.FromSeconds(30)) return "刚刚";
            if (diff < TimeSpan.FromMinutes(60)) return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff < TimeSpan.FromHours(24)) return $"{(int)diff.TotalHours} 小时前";
            if (diff < TimeSpan.FromDays(7)) return $"{(int)diff.TotalDays} 天前";
            return CreatedAt.ToLocalTime().ToString("yyyy/MM/dd");
        }
    }

    /// <summary>首页摘要(单行省略用)。</summary>
    public string SummaryText => Type == "Image"
        ? "[图片]"
        : (Text?.ReplaceLineEndings(" ").Trim() ?? "");
}
