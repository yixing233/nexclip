namespace NexClip.Desktop.Services;

using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

/// <summary>
/// 应用程序图标提取与磁盘/内存双层缓存服务
/// 缓存目录：%LOCALAPPDATA%/NexClip/app_icons/{processName}_{hash}.png
/// </summary>
public static class AppIconCache
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string IconDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexClip", "app_icons");

    static AppIconCache()
    {
        try
        {
            Directory.CreateDirectory(IconDir);
        }
        catch (Exception ex)
        {
            Log.Debug($"创建应用图标缓存目录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取或提取应用程序图标并返回本地 PNG 文件路径
    /// </summary>
    public static string? GetOrCreateIconPath(string? executablePath, string? processName)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        var key = $"{processName ?? Path.GetFileNameWithoutExtension(executablePath)}_{executablePath.GetHashCode():X8}";
        if (Cache.TryGetValue(key, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var targetFile = Path.Combine(IconDir, $"{key}.png");
        if (File.Exists(targetFile))
        {
            Cache[key] = targetFile;
            return targetFile;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null) return null;

            using var bmp = icon.ToBitmap();
            bmp.Save(targetFile, ImageFormat.Png);

            Cache[key] = targetFile;
            return targetFile;
        }
        catch (Exception ex)
        {
            Log.Debug($"提取程序图标失败 ({executablePath}): {ex.Message}");
            return null;
        }
    }
}
