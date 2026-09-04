using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using NexClip.Installer.Native.Services;
using NexClip.Installer.Native.UI;

namespace NexClip.Installer.Native;

public static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitCancelled = 2;
    private const int ExitRestartRequired = 3010;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = SetupArguments.Parse(args, Environment.ProcessPath);

        if (!SetupPolicy.IsSupportedPlatform(out var platformFailure))
        {
            return Fail(options, platformFailure, "NexClip 无法安装");
        }

        if (!TryLoadDependencies(options, out var dependencies))
        {
            return ExitFailure;
        }

        if (options.Diagnose)
        {
            return RunDiagnostics(options, dependencies);
        }

        using var instanceLock = SetupInstanceLock.TryAcquire();
        if (instanceLock is null)
        {
            return Fail(
                options,
                "NexClip 安装程序已在运行，请先完成或关闭正在进行的安装。",
                "NexClip 安装程序");
        }

        if (options.Silent)
        {
            return options.Uninstall ? RunSilentUninstall() : RunSilentInstall(options);
        }

        try
        {
            var window = new FluentInstallerWindow(options.Uninstall);
            window.Run();
            return ExitSuccess;
        }
        catch (Exception exception)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "nexclip_installer_error.log");
            try { File.WriteAllText(logPath, exception.ToString()); } catch { }
            ShowMessage(
                $"安装器启动失败:\n{exception.Message}\n\n详细信息已记录至:\n{logPath}",
                "NexClip 安装错误");
            return ExitFailure;
        }
    }

    /// <summary>
    /// 无人值守安装：补齐运行环境依赖后释放主程序，全过程仅写日志不弹窗，
    /// 便于企业批量部署与 CI 验证。
    /// </summary>
    private static int RunSilentInstall(SetupArguments options)
    {
        try
        {
            SetupLog.Write($"开始静默安装，目标目录：{options.InstallDirectory}");
            PayloadService.ValidateEmbeddedPayload();
            EnsureSufficientDiskSpace(options.InstallDirectory);

            var dependencyResult = DependencyService
                .EnsureDependenciesAsync(
                    (progress, detail) => SetupLog.Write($"[{progress:P0}] {detail}"))
                .GetAwaiter()
                .GetResult();

            ProcessHelper.TerminateRunningInstancesAsync().GetAwaiter().GetResult();
            PayloadService
                .InstallPayloadWithRollbackAsync(
                    options.InstallDirectory,
                    (_, fileName) => SetupLog.Write($"释放 {fileName}"))
                .GetAwaiter()
                .GetResult();

            if (!PayloadService.DeployUninstaller(options.InstallDirectory))
            {
                throw new IOException("无法部署卸载程序。");
            }

            ShortcutHelper.CreateStartMenuShortcut(options.InstallDirectory);
            if (options.CreateDesktopShortcut)
            {
                ShortcutHelper.CreateDesktopShortcut(options.InstallDirectory);
            }
            if (options.AutoStartup)
            {
                ShortcutHelper.SetStartupShortcut(options.InstallDirectory, true);
            }
            RegistryHelper.RegisterUninstall(options.InstallDirectory);

            var needsRestart = dependencyResult.RestartRequired || !dependencyResult.DetectionConfirmed;
            SetupLog.Write(needsRestart
                ? "静默安装完成，需要重启系统后运行时组件才会生效。"
                : "静默安装完成。");
            return needsRestart ? ExitRestartRequired : ExitSuccess;
        }
        catch (OperationCanceledException)
        {
            SetupLog.Write("静默安装被取消。");
            return ExitCancelled;
        }
        catch (Exception exception)
        {
            SetupLog.Write($"静默安装失败：{exception}");
            return ExitFailure;
        }
    }

    /// <summary>
    /// 静默安装同样需要空间预检：目标盘容纳解包后的主程序，临时盘容纳待下载的依赖安装包，
    /// 否则会在释放文件或安装运行时中途失败，留下半成品目录。
    /// </summary>
    private static void EnsureSufficientDiskSpace(string installDirectory)
    {
        var installRequired = SetupPolicy.CalculateRequiredSpaceBytes(
            PayloadService.GetExpandedPayloadSizeBytes(),
            0L);
        if (SetupPolicy.TryGetAvailableDiskSpace(installDirectory, out var installAvailable) &&
            !SetupPolicy.HasSufficientSpace(installAvailable, installRequired))
        {
            throw new IOException(
                $"目标磁盘空间不足，至少需要 {installRequired / (1024 * 1024)} MB 可用空间。");
        }

        var dependencyBytes = DependencyService.Dependencies
            .Where(dependency => !DependencyService.IsInstalled(dependency))
            .Sum(dependency => dependency.ExpectedDownloadBytes + dependency.ExpectedDownloadBytes / 4);
        if (dependencyBytes <= 0)
        {
            return;
        }

        var temporaryRequired = SetupPolicy.CalculateRequiredSpaceBytes(0L, dependencyBytes);
        if (SetupPolicy.TryGetAvailableDiskSpace(DependencyService.DownloadCacheDirectory, out var temporaryAvailable) &&
            !SetupPolicy.HasSufficientSpace(temporaryAvailable, temporaryRequired))
        {
            throw new IOException(
                $"临时文件磁盘空间不足，运行环境配置至少需要 {temporaryRequired / (1024 * 1024)} MB 可用空间。");
        }
    }

    private static int RunSilentUninstall()
    {
        try
        {
            var installDir = InstallerPathHelper.TryGetRegisteredInstallDirectory();
            if (string.IsNullOrWhiteSpace(installDir))
            {
                installDir = Path.GetDirectoryName(Environment.ProcessPath);
            }
            ProcessHelper.TerminateRunningInstancesAsync().GetAwaiter().GetResult();
            ShortcutHelper.RemoveAllShortcuts();
            RegistryHelper.UnregisterUninstall();
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                ProcessHelper.ScheduleDirectoryDeletion(installDir);
            }
            return ExitSuccess;
        }
        catch (Exception exception)
        {
            SetupLog.Write($"静默卸载失败：{exception}");
            return ExitFailure;
        }
    }

    /// <summary>
    /// 输出运行环境体检报告：依赖检测结果、清单固定版本与磁盘空间，
    /// 用于用户反馈“装不上/装完启动不了”时快速定位。
    /// </summary>
    /// <summary>
    /// 提前加载并校验内嵌依赖清单：清单损坏时给出明确提示，
    /// 而不是等到窗口字段初始化时抛出 TypeInitializationException。
    /// </summary>
    private static bool TryLoadDependencies(
        SetupArguments options,
        out IReadOnlyList<DependencyDefinition> dependencies)
    {
        try
        {
            dependencies = DependencyService.Dependencies;
            return dependencies.Count > 0;
        }
        catch (Exception exception)
        {
            dependencies = [];
            Fail(
                options,
                $"安装器内置的运行环境依赖清单无效：\n{(exception.InnerException ?? exception).Message}",
                "NexClip 安装错误");
            return false;
        }
    }

    private static int RunDiagnostics(
        SetupArguments options,
        IReadOnlyList<DependencyDefinition> dependencies)
    {
        var report = new StringBuilder();
        report.AppendLine("NexClip 安装器运行环境诊断报告");
        report.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Windows 内部版本: {Environment.OSVersion.Version}");
        report.AppendLine($"64 位系统: {Environment.Is64BitOperatingSystem}");
        report.AppendLine($"管理员权限: {IsElevated()}");
        report.AppendLine($"安装目录: {options.InstallDirectory}");
        report.AppendLine($"内嵌 Payload: {PayloadService.HasEmbeddedPayload()}");

        if (SetupPolicy.TryGetAvailableDiskSpace(options.InstallDirectory, out var installFree))
        {
            report.AppendLine($"目标磁盘可用空间: {installFree / (1024 * 1024)} MB");
        }
        if (SetupPolicy.TryGetAvailableDiskSpace(Path.GetTempPath(), out var tempFree))
        {
            report.AppendLine($"临时目录可用空间: {tempFree / (1024 * 1024)} MB");
        }
        report.AppendLine($"依赖下载缓存: {DependencyService.DownloadCacheDirectory}");
        report.AppendLine($"诊断日志: {SetupLog.FilePath}");
        AppendCachedDownloads(report);

        foreach (var dependency in dependencies)
        {
            report.AppendLine();
            report.AppendLine($"[{dependency.Kind}] {dependency.DisplayName}");
            report.AppendLine($"  已安装: {SafeIsInstalled(dependency)}");
            report.AppendLine($"  最低版本: {dependency.MinimumVersion}");
            report.AppendLine($"  下载大小: {dependency.ExpectedDownloadBytes / (1024 * 1024)} MB");
            foreach (var source in dependency.Sources)
            {
                report.AppendLine($"  源: {source.Uri}");
                report.AppendLine($"    SHA-256: {(source.HasPinnedHash ? source.Sha256 : "(evergreen，仅校验签名)")}");
            }
        }

        var path = string.IsNullOrWhiteSpace(options.DiagnosticsPath)
            ? Path.Combine(SetupLog.Directory, "diagnostics.txt")
            : options.DiagnosticsPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            SetupLog.Write($"写入诊断报告失败：{exception}");
            return ExitFailure;
        }

        if (!options.Silent)
        {
            ShowMessage($"运行环境诊断报告已生成:\n{path}", "NexClip 安装器诊断", 0x40);
        }
        return ExitSuccess;
    }

    /// <summary>列出缓存中已存在的安装包，便于判断“重试是否需要重新下载”。</summary>
    private static void AppendCachedDownloads(StringBuilder report)
    {
        try
        {
            var files = Directory.Exists(DependencyService.DownloadCacheDirectory)
                ? Directory.GetFiles(DependencyService.DownloadCacheDirectory)
                : [];
            if (files.Length == 0)
            {
                report.AppendLine("缓存内容: (空)");
                return;
            }

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                report.AppendLine(
                    $"缓存内容: {info.Name} · {info.Length / (1024 * 1024)} MB · {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.AppendLine($"缓存内容: 读取失败（{exception.Message}）");
        }
    }

    private static bool SafeIsInstalled(DependencyDefinition dependency)
    {
        try
        {
            return DependencyService.IsInstalled(dependency);
        }
        catch (Exception exception)
        {
            SetupLog.Write($"{dependency.DisplayName} 诊断检测异常：{exception}");
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    private static int Fail(SetupArguments options, string message, string caption)
    {
        SetupLog.Write(message);
        if (!options.Silent)
        {
            ShowMessage(message, caption);
        }
        return ExitFailure;
    }

    private static void ShowMessage(string message, string caption, uint icon = 0x10) =>
        MessageBoxW(IntPtr.Zero, message, caption, icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}