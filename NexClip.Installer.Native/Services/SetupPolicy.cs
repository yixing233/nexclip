using System.Diagnostics;
using System.Net;

namespace NexClip.Installer.Native.Services;

internal static class SetupPolicy
{
    internal const int DownloadMaxAttempts = 3;
    internal const int InstallerTimeoutMilliseconds = 20 * 60 * 1000;
    internal const long DependencyDownloadAllowanceBytes = 512L * 1024 * 1024;
    internal const long VisualCppDownloadLimitBytes = 80L * 1024 * 1024;
    internal const long DotNetDownloadLimitBytes = 220L * 1024 * 1024;
    internal const long WindowsAppRuntimeDownloadLimitBytes = 320L * 1024 * 1024;
    internal const long DiskSafetyMarginBytes = 128L * 1024 * 1024;

    /// <summary>连接建立超时：超过该时间视为镜像不可达并切换下一个镜像。</summary>
    internal static TimeSpan ConnectTimeout { get; } = TimeSpan.FromSeconds(20);

    /// <summary>下载停滞超时：连续该时长未收到任何字节即判定连接假死并触发断点续传重试。</summary>
    internal static TimeSpan DownloadIdleTimeout { get; } = TimeSpan.FromSeconds(45);

    /// <summary>依赖安装完成后等待系统完成注册/生效的检测窗口。</summary>
    internal static TimeSpan DependencyDetectionTimeout { get; } = TimeSpan.FromSeconds(45);

    /// <summary>Windows App SDK 运行时要求的最低系统内部版本（Windows 10 2004）。</summary>
    internal const int MinimumWindowsBuild = 19041;

    /// <summary>“另一个安装正在进行”时的最大重试次数。</summary>
    internal const int InstallerBusyMaxAttempts = 4;

    internal static bool IsSuccessfulInstallerExitCode(int exitCode) =>
        exitCode is 0 || IsAlreadyInstalledExitCode(exitCode) || RequiresRestart(exitCode);

    /// <summary>
    /// 组件已存在（同版本或更高版本）的退出码，应视为成功而非失败。
    /// 1638 = ERROR_PRODUCT_VERSION；0x80070666 = 同一 MSI 已安装；0x80073D06 = MSIX 包已安装更高版本。
    /// </summary>
    internal static bool IsAlreadyInstalledExitCode(int exitCode) =>
        exitCode is 1638 ||
        exitCode == unchecked((int)0x80070666) ||
        exitCode == unchecked((int)0x80073D06);

    /// <summary>1641/3010 及其 HRESULT 形式均表示安装成功但需要重启。</summary>
    internal static bool RequiresRestart(int exitCode) =>
        exitCode is 1641 or 3010 ||
        exitCode == unchecked((int)0x80070669) ||
        exitCode == unchecked((int)0x80070BC2);

    /// <summary>
    /// Windows Installer / MSIX 部署互斥时的退出码；等待后重试即可恢复，
    /// 不应直接判定依赖安装失败。
    /// </summary>
    internal static bool IsInstallerBusyExitCode(int exitCode) =>
        exitCode is 1618 ||
        exitCode == unchecked((int)0x80070652) ||
        exitCode == unchecked((int)0x80073D00);

    /// <summary>用户在 UAC 或安装向导中主动取消。</summary>
    internal static bool IsUserCancelledExitCode(int exitCode) =>
        exitCode is 1223 or 1602 ||
        exitCode == unchecked((int)0x800704C7) ||
        exitCode == unchecked((int)0x80070642);

    internal static bool IsSupportedPlatform(out string failureReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            failureReason = "NexClip 仅支持 Windows x64。";
            return false;
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            failureReason = "NexClip 需要 64 位 Windows，当前系统为 32 位。";
            return false;
        }

        if (Environment.OSVersion.Version.Build < MinimumWindowsBuild)
        {
            failureReason =
                $"NexClip 需要 Windows 10 版本 2004（内部版本 {MinimumWindowsBuild}）或更高版本，" +
                $"当前为内部版本 {Environment.OSVersion.Version.Build}。";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    internal static bool IsTransientDownloadStatus(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    internal static TimeSpan GetRetryDelay(int failedAttempt)
    {
        if (failedAttempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttempt));
        }

        return TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, failedAttempt - 1)));
    }

    internal static long CalculateRequiredSpaceBytes(long payloadBytes, int missingDependencyCount)
        => CalculateRequiredSpaceBytes(payloadBytes, missingDependencyCount * DependencyDownloadAllowanceBytes);

    internal static long CalculateRequiredSpaceBytes(long payloadBytes, long dependencyDownloadBytes)
    {
        payloadBytes = Math.Max(0, payloadBytes);
        dependencyDownloadBytes = Math.Max(0, dependencyDownloadBytes);
        try
        {
            return checked(payloadBytes +
                dependencyDownloadBytes +
                DiskSafetyMarginBytes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    internal static bool HasSufficientSpace(long availableBytes, long requiredBytes) =>
        availableBytes >= 0 && requiredBytes >= 0 && availableBytes >= requiredBytes;

    internal static bool TryGetAvailableDiskSpace(string path, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            return availableBytes >= 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryCreateHttpsUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    internal static double AnimateTowards(double current, double target, double elapsedSeconds, double response = 14.0)
    {
        target = Math.Max(current, target);
        if (target - current <= 0.0001)
        {
            return target;
        }

        var factor = 1.0 - Math.Exp(-Math.Max(1.0, response) * Math.Clamp(elapsedSeconds, 0.0, 0.1));
        var next = current + (target - current) * factor;
        return target - next <= 0.0001 ? target : Math.Min(target, next);
    }

    internal static string FormatRemainingTime(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 1024)
        {
            return string.Empty;
        }

        var seconds = (long)Math.Ceiling(remainingBytes / bytesPerSecond);
        if (seconds >= 3600)
        {
            return $"{seconds / 3600}h{seconds % 3600 / 60}m";
        }

        return seconds >= 60 ? $"{seconds / 60}m{seconds % 60:D2}s" : $"{seconds}s";
    }

    internal static async Task<int> WaitForInstallerAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallerTimeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2_000);
                }
            }
            catch
            {
            }

            throw new TimeoutException($"依赖安装程序运行超过 {InstallerTimeoutMilliseconds / 60000} 分钟，已终止。");
        }
    }
}
