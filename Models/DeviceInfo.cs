using CommunityToolkit.Mvvm.ComponentModel;

namespace NexClip.Desktop.Models;

/// <summary>设备列表项(GET /api/devices)。</summary>
public sealed class DeviceInfo : ObservableObject
{
    private string _id = "";
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    private string? _name;
    public string? Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string? _platform;
    public string? Platform
    {
        get => _platform;
        set
        {
            if (SetProperty(ref _platform, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    private string? _ip;
    public string? Ip
    {
        get => _ip;
        set
        {
            if (SetProperty(ref _ip, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    private string? _version;
    public string? Version
    {
        get => _version;
        set
        {
            if (SetProperty(ref _version, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    private bool _online;
    public bool Online
    {
        get => _online;
        set
        {
            if (SetProperty(ref _online, value)) OnPropertyChanged(nameof(LastSeenText));
        }
    }

    private DateTime _lastSeenAt;
    public DateTime LastSeenAt   // UTC
    {
        get => _lastSeenAt;
        set
        {
            if (SetProperty(ref _lastSeenAt, value)) OnPropertyChanged(nameof(LastSeenText));
        }
    }

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

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