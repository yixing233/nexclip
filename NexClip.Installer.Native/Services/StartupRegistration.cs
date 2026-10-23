using Microsoft.Win32;

namespace NexClip.Installer.Native.Services;

/// <summary>
/// 开机自启动项的统一注册与清理(HKCU\...\CurrentVersion\Run)。
///
/// 为什么安装器也要管这里
/// ----------------------
/// 自启动在本项目里有**两条互不相通的实现**:
/// 1. 安装器此前写的是「启动」文件夹快捷方式(<see cref="ShortcutHelper.SetStartupShortcut"/>);
/// 2. 主程序设置页写的是注册表 Run 键(桌面端 Services/StartupService)。
///
/// 两边各写各的,导致两个真实问题:
/// - 同一台机器上会同时存在两份自启动项(快捷方式 + Run 键),开机被拉起两次;
/// - 卸载时 <see cref="ShortcutHelper.RemoveAllShortcuts"/> 只删快捷方式,
///   **Run 键永久残留** —— 用户卸载后每次开机,Windows 都会尝试启动一个已删除的程序。
///
/// 现在统一为「只写 Run 键」,与主程序设置页保持单一实现路径;安装器负责在以下时机清理:
/// 卸载、以及用户取消勾选「开机自启动」的覆盖安装。
///
/// 值格式与主程序完全一致:<c>"exe路径" --autostart</c>,
/// 其中 <c>--autostart</c> 供主程序识别「本次来自开机自启」以配合静默启动。
/// </summary>
internal static class StartupRegistration
{
    /// <summary>HKCU 下的自启动键路径(用户级,不需要管理员权限)。</summary>
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>当前使用的值名。</summary>
    internal const string ValueName = "NexClip";

    /// <summary>命令行参数,供主程序识别开机自启。</summary>
    internal const string AutoStartArgument = " --autostart";

    /// <summary>
    /// 历史遗留值名。早期版本(SyncClipboard 时代与早期 NexClip)用过这些名字,
    /// 清理时一并删除,避免升级后残留多份自启动项。
    /// 与桌面端 Services/StartupService.LegacyValueNames 保持一致。
    /// </summary>
    internal static IReadOnlyList<string> LegacyValueNames { get; } =
    [
        "SyncClipboard.Desktop",
        "NexClip.Desktop"
    ];

    /// <summary>需要清理的全部值名(主值 + 遗留值)。</summary>
    internal static IEnumerable<string> AllValueNames() =>
        new[] { ValueName }.Concat(LegacyValueNames);

    /// <summary>
    /// 生成写入注册表的命令行值。<paramref name="exePath"/> 始终加引号:
    /// 安装路径可能含空格(例如 "C:\Program Files\NexClip")。
    /// </summary>
    internal static string BuildValue(string exePath) =>
        $"\"{exePath}\"{AutoStartArgument}";

    /// <summary>
    /// 注册表根键。默认 HKCU;测试可替换为一次性子键,避免污染真实自启动项。
    /// </summary>
    internal static RegistryKey Root { get; set; } = Registry.CurrentUser;

    /// <summary>
    /// 启用开机自启动,并清除所有遗留值名。
    /// 返回 false 表示注册表写入失败(组策略受限等),调用方应提示用户但不中断安装。
    /// </summary>
    internal static bool Enable(string installDirectory)
    {
        var exePath = Path.Combine(installDirectory, "NexClip.exe");
        try
        {
            using var key = Root.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            DeleteLegacyValues(key);
            key.SetValue(ValueName, BuildValue(exePath), RegistryValueKind.String);
            SetupLog.Write($"已注册开机自启动(注册表 Run): {exePath}");
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            SetupLog.Write($"注册开机自启动失败:{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除全部自启动项。**幂等**:键不存在、值不存在都算成功,
    /// 因为「本来就干净」与「刚清理完」对调用方是同一结果。
    /// </summary>
    internal static bool Disable()
    {
        try
        {
            using var key = Root.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return true;

            var removed = DeleteLegacyValues(key);
            try
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    removed++;
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                SetupLog.Write($"删除自启动值 {ValueName} 失败:{exception.Message}");
                return false;
            }

            if (removed > 0)
            {
                SetupLog.Write($"已清理开机自启动项 {removed} 个(Run 键)");
            }
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            SetupLog.Write($"清理开机自启动失败:{exception.Message}");
            return false;
        }
    }

    /// <summary>当前是否已注册自启动(含遗留值名)。供诊断报告使用。</summary>
    internal static bool IsEnabled()
    {
        try
        {
            using var key = Root.OpenSubKey(RunKeyPath);
            if (key is null) return false;
            return AllValueNames().Any(name =>
                key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>列出当前存在的自启动值,便于诊断报告与排查残留。</summary>
    internal static IReadOnlyList<(string Name, string Value)> Enumerate()
    {
        var results = new List<(string, string)>();
        try
        {
            using var key = Root.OpenSubKey(RunKeyPath);
            if (key is null) return results;
            foreach (var name in AllValueNames())
            {
                if (key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    results.Add((name, value));
                }
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }

        return results;
    }

    /// <summary>删除遗留值名,返回实际删除的个数。</summary>
    private static int DeleteLegacyValues(RegistryKey runKey)
    {
        var removed = 0;
        foreach (var legacy in LegacyValueNames)
        {
            try
            {
                if (runKey.GetValue(legacy) is not null)
                {
                    runKey.DeleteValue(legacy, throwOnMissingValue: false);
                    removed++;
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // 遗留值清理失败不应影响主值处理
                SetupLog.Write($"删除遗留自启动值 {legacy} 失败:{exception.Message}");
            }
        }
        return removed;
    }
}
