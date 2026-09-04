namespace NexClip.Installer.Native.Services;

/// <summary>
/// 安装器命令行参数。除交互式安装外还支持：
/// <c>/silent</c> 无人值守安装、<c>/uninstall</c> 卸载、
/// <c>/dir=&lt;path&gt;</c> 指定安装目录、<c>/diagnose[=&lt;file&gt;]</c> 生成运行环境诊断报告。
/// </summary>
internal sealed record SetupArguments(
    bool Uninstall,
    bool Silent,
    bool Diagnose,
    string InstallDirectory,
    string DiagnosticsPath,
    bool CreateDesktopShortcut,
    bool AutoStartup)
{
    internal static SetupArguments Parse(IReadOnlyList<string> args, string? processPath)
    {
        var uninstall = Path.GetFileNameWithoutExtension(processPath ?? string.Empty)
            .Contains("Uninstall", StringComparison.OrdinalIgnoreCase);
        var silent = false;
        var diagnose = false;
        var diagnosticsPath = string.Empty;
        var createDesktopShortcut = true;
        var autoStartup = false;
        string? installDirectory = null;

        foreach (var raw in args)
        {
            var argument = raw.Trim();
            if (argument.Length == 0)
            {
                continue;
            }

            if (Matches(argument, "uninstall"))
            {
                uninstall = true;
            }
            else if (Matches(argument, "silent") || Matches(argument, "verysilent") || Matches(argument, "quiet"))
            {
                silent = true;
            }
            else if (Matches(argument, "norestart"))
            {
                // 与 Microsoft Bootstrapper 参数保持兼容；安装器本身从不主动重启。
            }
            else if (Matches(argument, "nodesktopicon"))
            {
                createDesktopShortcut = false;
            }
            else if (Matches(argument, "autostart"))
            {
                autoStartup = true;
            }
            else if (TryReadValue(argument, "dir", out var directory))
            {
                installDirectory = directory;
            }
            else if (Matches(argument, "diagnose"))
            {
                diagnose = true;
            }
            else if (TryReadValue(argument, "diagnose", out var reportPath))
            {
                diagnose = true;
                diagnosticsPath = SafeFullPath(reportPath);
            }
        }

        return new SetupArguments(
            uninstall,
            silent,
            diagnose,
            InstallerPathHelper.ResolveInstallDirectory(
                installDirectory ?? InstallerPathHelper.TryGetRegisteredInstallDirectory(),
                InstallerPathHelper.GetDefaultInstallDirectory()),
            diagnosticsPath,
            createDesktopShortcut,
            autoStartup);
    }

    private static bool Matches(string argument, string name) =>
        (argument.StartsWith('/') || argument.StartsWith('-')) &&
        argument[1..].Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadValue(string argument, string name, out string value)
    {
        value = string.Empty;
        if (!argument.StartsWith('/') && !argument.StartsWith('-'))
        {
            return false;
        }

        var separator = argument.IndexOf('=');
        if (separator <= 1 ||
            !argument[1..separator].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = argument[(separator + 1)..].Trim().Trim('"');
        return value.Length > 0;
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}