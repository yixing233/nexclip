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
    public bool IsCurrent { get; set; }

    /// <summary>副标题:平台 · 版本 · IP(非空拼接,IP 规范化)。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new[] { Platform, Version, NormalizeIp(Ip) }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            return string.Join(" · ", parts);
        }
    }

    /// <summary>IP 规范化:去掉 ::ffff: 前缀;本机回环统一为 127.0.0.1。</summary>
    private static string? NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var mapped = System.Text.RegularExpressions.Regex.Match(
            ip, @"^::ffff:(\d+\.\d+\.\d+\.\d+)$");
        if (mapped.Success) return mapped.Groups[1].Value;
        return ip == "::1" ? "127.0.0.1" : ip;
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