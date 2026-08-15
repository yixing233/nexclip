namespace SyncClipboard.Desktop.Models;

/// <summary>设备列表项(GET /api/devices)。</summary>
public sealed class DeviceInfo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Platform { get; set; }
    public string? Ip { get; set; }
    public string? Version { get; set; }
    public bool Online { get; set; }
    public DateTime LastSeenAt { get; set; }   // UTC

    /// <summary>副标题:平台 · 版本 · IP(非空拼接)。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new[] { Platform, Version, Ip }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            return string.Join(" · ", parts);
        }
    }

    /// <summary>最后在线文案。</summary>
    public string LastSeenText
    {
        get
        {
            if (Online) return "在线";
            var diff = DateTime.UtcNow - LastSeenAt;
            if (diff < TimeSpan.FromMinutes(1)) return "刚刚离线";
            if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} 分钟前离线";
            if (diff < TimeSpan.FromHours(24)) return $"{(int)diff.TotalHours} 小时前离线";
            return $"{(int)diff.TotalDays} 天前离线";
        }
    }
}
