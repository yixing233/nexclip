using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace NexClipServer;

public class AppOptions
{
    public string AuthToken { get; set; } = "change-me";
    public int MaxHistoryCount { get; set; } = 1;
    public string DatabasePath { get; set; } = "data/nexclip.db";
    public string ImageStoragePath { get; set; } = "data/images";
    public long MaxImageSizeBytes { get; set; } = 10 * 1024 * 1024;
    public int ImageCacheTtlMinutes { get; set; } = 15;
    public int OnlineThresholdSeconds { get; set; } = 120;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
}

/// 请求耗时采样(统计卡"平均延迟"用)
public class LatencyTracker
{
    private readonly long[] _samples = new long[64];
    private int _count;

    public void Add(long microseconds)
    {
        var i = Interlocked.Increment(ref _count) - 1;
        _samples[i % _samples.Length] = microseconds;
    }

    public double AvgMs
    {
        get
        {
            var n = Math.Min(_count, _samples.Length);
            if (n == 0) return 0;
            long sum = 0;
            for (var i = 0; i < n; i++) sum += _samples[i];
            return Math.Round(sum / (double)n / 1000.0, 1);
        }
    }

    public double[] Last12
    {
        get
        {
            var n = Math.Min(_count, _samples.Length);
            var res = new double[12];
            for (var i = 0; i < 12; i++)
            {
                var idx = _count - 12 + i;
                res[i] = idx >= 0 && idx < _count ? Math.Round(_samples[idx % _samples.Length] / 1000.0, 1) : 0;
            }
            return res;
        }
    }
}

/// <summary>IP 规范化:去 IPv4-mapped 前缀(::ffff:a.b.c.d → a.b.c.d),本机回环统一 127.0.0.1。</summary>
public static class IpUtil
{
    public static string? Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(ip, @"^::ffff:(d+.d+.d+.d+)$");
        if (m.Success) return m.Groups[1].Value;
        return ip == "::1" ? "127.0.0.1" : ip;
    }
}

public class ClipboardService(
    AppDbContext db,
    IHubContext<ClipboardHub> hub,
    IMemoryCache cache,
    AppOptions options,
    ILogger<ClipboardService> log)
{
    private readonly AppOptions _opt = options;
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public AppOptions Options => _opt;

    /// 文本上传:内容 hash 去重,返回 (entry, unchanged);broadcast=false 时不广播,由调用方决定通知范围
    public async Task<(ClipboardEntry? entry, bool unchanged)> UploadTextAsync(string text, string deviceId, string deviceName, string? platform, string? version, string? ip, bool broadcast = true, bool isManual = false)
    {
        var hash = Hash(text);
        var current = await GetCurrentAsync();
        if (current is not null && current.ContentHash == hash && current.Type == "Text")
            return (current, true);

        var entry = new ClipboardEntry
        {
            Type = "Text",
            Text = text,
            ContentHash = hash,
            DeviceId = deviceId,
            DeviceName = deviceName,
            IsManual = isManual,
            CreatedAt = DateTime.UtcNow,
        };
        db.Entries.Add(entry);
        await TouchDeviceAsync(deviceId, deviceName, platform, version, ip);
        db.Activities.Add(new ActivityLog { Action = isManual ? "transfer" : "push", DeviceName = deviceName, Content = Truncate(text, 120), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await TrimHistoryAsync();
        if (broadcast) await BroadcastAsync(entry);
        return (entry, false);
    }

    /// 图片上传:纯内存短时中转(零磁盘写入) + 元数据入库
    public async Task<ClipboardEntry> UploadImageAsync(Stream file, string fileName, string deviceId, string deviceName, string? ip, bool isManual = false)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var rel = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();

        // 写入短时内存缓存,到期后自动由 GC 释放(零磁盘写入)
        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_opt.ImageCacheTtlMinutes > 0 ? _opt.ImageCacheTtlMinutes : 15),
            Size = bytes.Length
        };
        cache.Set($"img:{rel}", bytes, cacheOptions);

        var entry = new ClipboardEntry
        {
            Type = "Image",
            Text = fileName,
            ImageRef = rel,
            ContentHash = Hash(rel),
            DeviceId = deviceId,
            DeviceName = deviceName,
            IsManual = isManual,
            CreatedAt = DateTime.UtcNow,
        };
        db.Entries.Add(entry);
        await TouchDeviceAsync(deviceId, deviceName, null, null, ip);
        db.Activities.Add(new ActivityLog { Action = isManual ? "transfer" : "push", DeviceName = deviceName, Content = Truncate(fileName, 120), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await TrimHistoryAsync();
        await BroadcastAsync(entry);
        return entry;
    }

    public Task<ClipboardEntry?> GetCurrentAsync() =>
        db.Entries.OrderByDescending(e => e.Id).FirstOrDefaultAsync();

    public async Task<(List<ClipboardEntry> items, int total)> GetHistoryAsync(int offset, int limit)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var query = db.Entries.OrderByDescending(e => e.Id);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit).ToListAsync();
        return (items, total);
    }

    public Task<ClipboardEntry?> GetByIdAsync(long id) => db.Entries.FindAsync(id).AsTask();

    public async Task DeleteAsync(long id)
    {
        var e = await db.Entries.FindAsync(id);
        if (e is null) return;
        if (e.ImageRef is not null) TryDeleteImage(e.ImageRef);
        db.Entries.Remove(e);
        db.Activities.Add(new ActivityLog { Action = "delete", DeviceName = e.DeviceName ?? "?", Content = "删除了 1 条剪贴板记录", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await TrimHistoryAsync();
    }

    public async Task ClearHistoryAsync()
    {
        var all = await db.Entries.ToListAsync();
        foreach (var e in all) if (e.ImageRef is not null) TryDeleteImage(e.ImageRef);
        db.Entries.RemoveRange(all);
        await db.SaveChangesAsync();
    }

    /// 超上限:删除最旧条目(含图片文件)
    private async Task TrimHistoryAsync()
    {
        var total = await db.Entries.CountAsync();
        if (total <= _opt.MaxHistoryCount) return;
        var overflow = await db.Entries.OrderBy(e => e.Id).Take(total - _opt.MaxHistoryCount).ToListAsync();
        foreach (var e in overflow) if (e.ImageRef is not null) TryDeleteImage(e.ImageRef);
        db.Entries.RemoveRange(overflow);
        await db.SaveChangesAsync();
    }

    /// 从内存读取图片(纯内存中转,零磁盘 I/O,兼容历史本地文件兜底)
    public (byte[] bytes, string contentType)? GetImage(string rel)
    {
        if (cache.TryGetValue($"img:{rel}", out byte[]? cached) && cached is not null)
        {
            return (cached, GetContentType(rel));
        }

        // 旧磁盘文件向后兼容兜底
        var root = Path.GetFullPath(_opt.ImageStoragePath);
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
        {
            try
            {
                var bytes = File.ReadAllBytes(full);
                return (bytes, GetContentType(full));
            }
            catch { }
        }
        return null;
    }

    public static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }

    private void TryDeleteImage(string rel)
    {
        cache.Remove($"img:{rel}");
        try
        {
            var full = Path.GetFullPath(Path.Combine(_opt.ImageStoragePath, rel));
            if (full.StartsWith(Path.GetFullPath(_opt.ImageStoragePath), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                File.Delete(full);
        }
        catch (Exception ex) { log.LogWarning(ex, "删除图片失败: {Rel}", rel); }
    }

    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// 设备登记/心跳
    public async Task TouchDeviceAsync(string id, string name, string? platform, string? version, string? ip)
    {
        if (string.IsNullOrEmpty(id)) return;
        var d = await db.Devices.FindAsync(id);
        if (d is null)
        {
            d = new Device { Id = id, Name = string.IsNullOrEmpty(name) ? "未知设备" : name };
            db.Devices.Add(d);
        }
        d.Name = string.IsNullOrEmpty(name) ? d.Name : name;
        d.Platform = string.IsNullOrEmpty(platform) ? d.Platform : platform;
        d.Version = string.IsNullOrEmpty(version) ? d.Version : version;
        d.Ip = string.IsNullOrEmpty(ip) ? d.Ip : ip;
        d.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// 广播剪贴板更新;targetDeviceIds 非空时只通知指定设备(定向推送,按 hub 连接登记的 deviceId 定位)
    public async Task BroadcastAsync(ClipboardEntry entry, IReadOnlyCollection<string>? targetDeviceIds = null)
    {
        if (targetDeviceIds is null || targetDeviceIds.Count == 0)
        {
            await hub.Clients.All.SendAsync("ClipboardUpdated", entry);
            return;
        }
        var targets = targetDeviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectionIds = ClipboardHub.ActiveDevices
            .Where(kv => targets.Contains(kv.Value))
            .Select(kv => kv.Key)
            .ToList();
        if (connectionIds.Count > 0)
            await hub.Clients.Clients(connectionIds).SendAsync("ClipboardUpdated", entry);
    }

    public async Task BroadcastClearedAsync() =>
        await hub.Clients.All.SendAsync("ClipboardCleared");
}
