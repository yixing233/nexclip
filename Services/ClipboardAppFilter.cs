namespace NexClip.Desktop.Services;

/// <summary>自动剪贴板采集的来源应用过滤规则。</summary>
public static class ClipboardAppFilter
{
    /// <summary>正在运行的应用选项;IconPath 是 AppIconCache 落盘的 PNG 路径,取不到为 null。</summary>
    public sealed record RunningProcessOption(string ProcessName, string DisplayName, string? ExecutablePath, string? IconPath = null)
    {
        public string Label => string.IsNullOrWhiteSpace(ExecutablePath)
            ? $"{DisplayName} ({ProcessName})"
            : $"{DisplayName} ({ProcessName})";
    }

    public static IReadOnlyList<string> BuiltInRemoteControlApps { get; } = new[]
    {
        "AnyDesk", "TeamViewer", "RustDesk", "ToDesk", "向日葵", "Sunlogin",
        "Chrome Remote Desktop", "mstsc", "远程桌面", "Parsec", "Splashtop",
        "UltraViewer", "RealVNC", "VNC Viewer", "Remote Utilities", "HopToDesk",
        "Supremo", "AeroAdmin", "Radmin", "DWService",
    };

    private static readonly string[] ProcessTokens =
    {
        "anydesk", "teamviewer", "rustdesk", "todesk", "sunlogin", "sunloginclient",
        "chrome-remote-desktop-host", "mstsc", "remotedesktop", "parsec", "splashtop",
        "ultraviewer", "vncviewer", "winvnc", "tvnserver", "remoteutilities", "hoptodesk",
        "supremo", "aeroadmin", "radmin", "dwagent", "dwservice", "remotepc", "ammyy",
    };

    /// <summary>返回是否应阻止该来源应用的自动采集。无法检测来源时默认允许。</summary>
    public static bool ShouldFilter(SourceAppInfo? sourceApp, bool enabled, IEnumerable<string>? customProcesses = null)
    {
        if (!enabled || sourceApp is null) return false;
        foreach (var value in new[] { sourceApp.ProcessName, sourceApp.Name, sourceApp.ExecutablePath })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var normalized = value.Trim();
            var fileName = System.IO.Path.GetFileNameWithoutExtension(normalized);
            if (Matches(normalized) || Matches(fileName)) return true;
            if (customProcesses?.Any(rule => RuleMatches(rule, normalized, fileName)) == true) return true;
        }
        return false;
    }

    public static IReadOnlyList<RunningProcessOption> GetRunningProcesses()
    {
        var currentPid = Environment.ProcessId;
        var result = new Dictionary<string, RunningProcessOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentPid) continue;
                var processName = process.ProcessName?.Trim();
                if (string.IsNullOrWhiteSpace(processName) || processName is "Idle" or "System") continue;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                var displayName = processName;
                if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                {
                    try
                    {
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                        displayName = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                            : (!string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim() : processName);
                    }
                    catch { }
                }
                var key = string.IsNullOrWhiteSpace(path) ? processName : path;
                if (result.ContainsKey(key)) continue;
                // 图标提取有磁盘 IO/GDI 开销,所以只对最终留下的那一份做;AppIconCache 内部有内存+磁盘双缓存
                var iconPath = AppIconCache.GetOrCreateIconPath(path, processName);
                result[key] = new RunningProcessOption(processName, displayName, path, iconPath);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return result.Values.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Matches(string value)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return ProcessTokens.Any(token => normalized.Contains(token.Replace("-", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
            || BuiltInRemoteControlApps.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RuleMatches(string rule, string value, string fileName)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        var normalizedRule = System.IO.Path.GetFileNameWithoutExtension(rule.Trim());
        return value.Equals(rule.Trim(), StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(normalizedRule, StringComparison.OrdinalIgnoreCase)
            || value.Contains(rule.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
