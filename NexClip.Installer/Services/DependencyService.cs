using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NexClip.Installer.Services;

public static class DependencyService
{
    private const string DotNet9DownloadUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";
    private const string Wasdk18DownloadUrl = "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";

    /// <summary>
    /// 检查系统中是否已安装 .NET 9 Desktop Runtime
    /// </summary>
    public static bool IsDotNet9Installed()
    {
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dotnetDir = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(dotnetDir))
            {
                var dirs = Directory.GetDirectories(dotnetDir, "9.*");
                if (dirs.Length > 0) return true;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localDotnetDir = Path.Combine(localAppData, "Microsoft", "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (Directory.Exists(localDotnetDir))
            {
                var dirs = Directory.GetDirectories(localDotnetDir, "9.*");
                if (dirs.Length > 0) return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>
    /// 检查系统中是否已安装 Windows App SDK 1.8 运行时
    /// </summary>
    public static bool IsWindowsAppSdkInstalled()
    {
        try
        {
            using var regHklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages");
            if (regHklm != null)
            {
                foreach (var sub in regHklm.GetSubKeyNames())
                {
                    if (sub.StartsWith("Microsoft.WindowsAppRuntime.1.8", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        try
        {
            using var regHkcu = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (regHkcu != null)
            {
                foreach (var sub in regHkcu.GetSubKeyNames())
                {
                    if (sub.StartsWith("Microsoft.WindowsAppRuntime.1.8", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var winApps = Path.Combine(pf, "WindowsApps");
            if (Directory.Exists(winApps))
            {
                var dirs = Directory.GetDirectories(winApps, "*WindowsAppRuntime.1.8*");
                if (dirs.Length > 0) return true;
                var dirs2 = Directory.GetDirectories(winApps, "*WinAppRuntime*1.8*");
                if (dirs2.Length > 0) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 异步下载文件并报告进度
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, Action<double, string>? onProgress, string taskName)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                double progress = (double)totalRead / totalBytes;
                onProgress?.Invoke(progress, $"{taskName} ({totalRead / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB)");
            }
            else
            {
                onProgress?.Invoke(0.5, $"{taskName} ({totalRead / 1024 / 1024}MB)");
            }
        }
    }

    /// <summary>
    /// 确保所有必要的前置依赖项已安装。若缺失，则自动在线下载并安装。
    /// </summary>
    public static async Task EnsureDependenciesAsync(Action<double, string>? onProgress)
    {
        var tempDir = Path.GetTempPath();

        // 1. 检查并安装 .NET 9 Desktop Runtime
        if (!IsDotNet9Installed())
        {
            var installerPath = Path.Combine(tempDir, "dotnet9_desktop_runtime_x64.exe");
            await DownloadFileAsync(DotNet9DownloadUrl, installerPath, onProgress, "正在下载 .NET 9 运行时");

            onProgress?.Invoke(0.9, "正在安装 .NET 9 Desktop Runtime 运行时...");
            await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /norestart",
                    UseShellExecute = true
                });
                p?.WaitForExit();
            });
        }

        // 2. 检查并安装 Windows App SDK 1.8 运行时
        if (!IsWindowsAppSdkInstalled())
        {
            var installerPath = Path.Combine(tempDir, "windowsappruntimeinstall-x64.exe");
            await DownloadFileAsync(Wasdk18DownloadUrl, installerPath, onProgress, "正在下载 Windows App SDK 运行时");

            onProgress?.Invoke(0.9, "正在安装 Windows App SDK 运行时...");
            await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "--quiet --force",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit();
            });
        }
    }
}
