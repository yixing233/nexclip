using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace SyncClipboardServer;

public class AppOptions
{
    public string AuthToken { get; set; } = "change-me";
    public int MaxHistoryCount { get; set; } = 1000;
    public string DatabasePath { get; set; } = "data/syncclipboard.db";
    public string ImageStoragePath { get; set; } = "data/images";
    public long MaxImageSizeBytes { get; set; } = 10 * 1024 * 1024;
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

public class ClipboardService(
    AppDbContext db,
    IHubContext<ClipboardHub> hub,
    AppOptions options,
    ILogger<ClipboardService> log)
{
    private readonly AppOptions _opt = options;
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public AppOptions Options => _opt;

    /// 文本上传:内容 hash 去重,返回 (entry, unchanged)
    public async Task<(ClipboardEntry? entry, bool unchanged)> UploadTextAsync(string text, string deviceId, string deviceName, string? platform, string? version, string? ip)
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
            CreatedAt = DateTime.UtcNow,
        };
        db.Entries.Add(entry);
        await TouchDeviceAsync(deviceId, deviceName, platform, version, ip);
        db.Activities.Add(new ActivityLog { Action = "push", DeviceName = deviceName, Content = Truncate(text, 120), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await TrimHistoryAsync();
        await BroadcastAsync(entry);
        return (entry, false);
    }

    /// 图片上传:文件落盘 + 元数据入库
    public async Task<ClipboardEntry> UploadImageAsync(Stream file, string fileName, string deviceId, string deviceName, string? ip)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var dir = Path.Combine(_opt.ImageStoragePath, DateTime.UtcNow.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var rel = $"{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var full = Path.Combine(_opt.ImageStoragePath, rel);
        await using (var fsOut = File.Create(full))
            await file.CopyToAsync(fsOut);

        var entry = new ClipboardEntry
        {
            Type = "Image",
            Text = fileName,
            ImageRef = rel,
            ContentHash = Hash(rel),
            DeviceId = deviceId,
            DeviceName = deviceName,
            CreatedAt = DateTime.UtcNow,
        };
        db.Entries.Add(entry);
        await TouchDeviceAsync(deviceId, deviceName, null, null, ip);
        db.Activities.Add(new ActivityLog { Action = "push", DeviceName = deviceName, Content = Truncate(fileName, 120), CreatedAt = DateTime.UtcNow });
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

    private void TryDeleteImage(string rel)
    {
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

    public async Task BroadcastAsync(ClipboardEntry entry) =>
        await hub.Clients.All.SendAsync("ClipboardUpdated", entry);

    public async Task BroadcastClearedAsync() =>
        await hub.Clients.All.SendAsync("ClipboardCleared");
}
