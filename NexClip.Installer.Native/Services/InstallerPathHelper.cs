using Microsoft.Win32;
using System.Text.Json;

namespace NexClip.Installer.Native.Services;

internal static class InstallerPathHelper
{
    internal const string UninstallRootKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexClip";

    /// <summary>默认安装位置：优先 D 盘 Program Files，无 D 盘时退回用户目录。</summary>
    internal static string GetDefaultInstallDirectory() => Directory.Exists(@"D:\")
        ? @"D:\Program Files\NexClip"
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "NexClip");

    internal static string ResolveInstallDirectory(string? registeredPath, string fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(registeredPath) && Path.IsPathFullyQualified(registeredPath))
        {
            try
            {
                return Path.GetFullPath(registeredPath.Trim());
            }
            catch (ArgumentException)
            {
            }
        }

        return Path.GetFullPath(fallbackPath);
    }

    internal static string? TryGetRegisteredInstallDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRootKey);
            var value = key?.GetValue("InstallLocation") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> GetUserDataDirectories(string? configuredStorageDirectory)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexClip"));
        Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SyncClipboard"));
        Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexClip"));
        Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncClipboard"));
        Add(paths, configuredStorageDirectory);
        return paths.ToArray();
    }

    internal static string? TryGetConfiguredStorageDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexClip", "settings.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SyncClipboard", "settings.json")
        };

        foreach (var file in candidates)
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("StorageDir", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var storage = value.GetString();
                    if (!string.IsNullOrWhiteSpace(storage)) return storage;
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    private static void Add(ISet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            paths.Add(Path.GetFullPath(path.Trim()));
        }
        catch (ArgumentException)
        {
        }
    }
}
