using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NexClip.Installer.Native.Services;

public struct DependencyStatus
{
    public bool DotNet9;
    public bool WindowsAppSdk;
    public bool VCRedist;
    public bool WebView2;

    public bool IsAllSatisfied => DotNet9 && WindowsAppSdk && VCRedist && WebView2;
}

public static class DependencyService
{
    private const string DotNet9DownloadUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";
    private const string Wasdk18DownloadUrl = "https://download.microsoft.com/download/712421b4-6f72-47fc-acb8-2ebf030b2260/WindowsAppRuntimeInstall-x64.exe";
    private const string VcRedistDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        };

        try
        {
            var envProxy = Environment.GetEnvironmentVariable("https_proxy")
                        ?? Environment.GetEnvironmentVariable("http_proxy")
                        ?? Environment.GetEnvironmentVariable("all_proxy");
            if (!string.IsNullOrEmpty(envProxy))
            {
                handler.Proxy = new WebProxy(envProxy);
            }
            else
            {
                handler.Proxy = WebRequest.GetSystemWebProxy();
            }
        }
        catch { }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return client;
    }

    public static DependencyStatus CheckAllDependencies()
    {
        return new DependencyStatus
        {
            DotNet9 = IsDotNet9Installed(),
            WindowsAppSdk = IsWindowsAppSdkInstalled(),
            VCRedist = IsVCRedistInstalled(),
            WebView2 = IsWebView2Installed()
        };
    }

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

            using var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (regKey != null)
            {
                foreach (var name in regKey.GetValueNames())
                {
                    if (name.StartsWith("9.", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static bool IsWindowsAppSdkInstalled()
    {
        try
        {
            // 1. 检查 HKLM AppModel PackageRepository (所有用户只读可访问)
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
            // 2. 检查 HKCU AppModel Repository (当前用户注册包)
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
            // 3. 检查系统依赖注册表
            using var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Installer\Dependencies");
            if (regKey != null)
            {
                foreach (var sub in regKey.GetSubKeyNames())
                {
                    if (sub.Contains("WinAppRuntime", StringComparison.OrdinalIgnoreCase) && sub.Contains("1.8", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (sub.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) && sub.Contains("1.8", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        try
        {
            // 4. 检查 WindowsApps 目录
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

    public static bool IsVCRedistInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            if (key != null)
            {
                var installed = key.GetValue("Installed");
                if (installed is int intVal && intVal == 1) return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsWebView2Installed()
    {
        try
        {
            using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (key64 != null)
            {
                var pv = key64.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
            }

            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (key32 != null)
            {
                var pv = key32.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 高性能多线程分片并发极速下载引擎 (支持 HTTP Range 8 线程并行与实时测速)
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, Action<double, string>? onProgress, string taskName)
    {
        try
        {
            using var headClient = CreateHttpClient();
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await headClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead);

            long totalBytes = headResp.Content.Headers.ContentLength ?? -1L;
            bool supportsRange = headResp.Headers.AcceptRanges.Contains("bytes") || headResp.Headers.Contains("Accept-Ranges");

            if (totalBytes > 2 * 1024 * 1024 && supportsRange)
            {
                await DownloadParallelAsync(url, destPath, totalBytes, onProgress, taskName, 8);
                return;
            }
        }
        catch { }

        await DownloadSingleStreamAsync(url, destPath, onProgress, taskName);
    }

    /// <summary>
    /// 平滑下载测速与节流上报器 (基于 EMA 指数移动平均平滑滤波，消除多线程分片抖动与跳变)
    /// </summary>
    private sealed class DownloadSpeedSmoother
    {
        private readonly object _lock = new();
        private long _lastSpeedSampleTicks = Environment.TickCount64;
        private long _lastSpeedSampleBytes = 0;
        private double _smoothedMBps = 0.0;
        private long _lastReportTicks = 0;
        private const int SampleIntervalMs = 300; // 300ms 测速采样窗口
        private const int ReportIntervalMs = 120; // 120ms UI 节流回调

        public void ReportProgress(long currentBytes, long totalBytes, Action<double, string>? onProgress)
        {
            if (onProgress == null) return;

            double speedToDisplay;
            bool shouldInvoke = false;

            lock (_lock)
            {
                long now = Environment.TickCount64;
                long elapsedSample = now - _lastSpeedSampleTicks;

                if (elapsedSample >= SampleIntervalMs || (totalBytes > 0 && currentBytes == totalBytes))
                {
                    double elapsedSec = elapsedSample / 1000.0;
                    if (elapsedSec > 0)
                    {
                        long deltaBytes = currentBytes - _lastSpeedSampleBytes;
                        double instantMBps = (deltaBytes / (1024.0 * 1024.0)) / elapsedSec;

                        if (_smoothedMBps <= 0.0001)
                        {
                            _smoothedMBps = instantMBps;
                        }
                        else
                        {
                            // EMA 指数移动平均滤波: 65% 历史平滑值 + 35% 瞬时采样值
                            _smoothedMBps = (_smoothedMBps * 0.65) + (instantMBps * 0.35);
                        }

                        _lastSpeedSampleTicks = now;
                        _lastSpeedSampleBytes = currentBytes;
                    }
                }

                speedToDisplay = _smoothedMBps;

                if (now - _lastReportTicks >= ReportIntervalMs || (totalBytes > 0 && currentBytes == totalBytes))
                {
                    _lastReportTicks = now;
                    shouldInvoke = true;
                }
            }

            if (shouldInvoke)
            {
                double p = totalBytes > 0 ? (double)currentBytes / totalBytes : 0.5;
                string speedStr = FormatSpeed(speedToDisplay);
                string info = totalBytes > 0
                    ? $"{(currentBytes / 1024.0 / 1024.0):F1}M/{(totalBytes / 1024.0 / 1024.0):F1}M · {speedStr}"
                    : $"{(currentBytes / 1024.0 / 1024.0):F1}M · {speedStr}";
                onProgress.Invoke(p, info);
            }
        }

        private static string FormatSpeed(double mbps)
        {
            if (mbps >= 1.0)
                return $"{mbps:F1} MB/s";
            if (mbps >= 0.01)
                return $"{(mbps * 1024.0):F0} KB/s";
            return "0 KB/s";
        }
    }

    private static async Task DownloadParallelAsync(string url, string destPath, long totalBytes, Action<double, string>? onProgress, string taskName, int threads = 8)
    {
        using (var initFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            initFs.SetLength(totalBytes);
        }

        long chunkSize = (totalBytes + threads - 1) / threads;
        long totalDownloaded = 0;
        var smoother = new DownloadSpeedSmoother();

        var tasks = new Task[threads];
        for (int i = 0; i < threads; i++)
        {
            int threadIdx = i;
            long start = threadIdx * chunkSize;
            long end = Math.Min(start + chunkSize - 1, totalBytes - 1);

            tasks[threadIdx] = Task.Run(async () =>
            {
                if (start > end) return;

                using var subClient = CreateHttpClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

                using var resp = await subClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var outFs = new FileStream(destPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 128 * 1024);
                outFs.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await outFs.WriteAsync(buffer, 0, read);
                    long currentTotal = Interlocked.Add(ref totalDownloaded, read);
                    smoother.ReportProgress(currentTotal, totalBytes, onProgress);
                }
            });
        }

        await Task.WhenAll(tasks);
        onProgress?.Invoke(1.0, $"{(totalBytes / 1024.0 / 1024.0):F1}M/{(totalBytes / 1024.0 / 1024.0):F1}M · 完成");
    }

    private static async Task DownloadSingleStreamAsync(string url, string destPath, Action<double, string>? onProgress, string taskName)
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);

        var smoother = new DownloadSpeedSmoother();
        var buffer = new byte[128 * 1024];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;
            smoother.ReportProgress(totalRead, totalBytes, onProgress);
        }

        if (totalBytes > 0)
        {
            onProgress?.Invoke(1.0, $"{(totalBytes / 1024.0 / 1024.0):F1}M/{(totalBytes / 1024.0 / 1024.0):F1}M · 完成");
        }
    }

    public static async Task EnsureDependenciesAsync(Action<double, string>? onProgress)
    {
        var tempDir = Path.GetTempPath();

        // 统计所有缺失的依赖项
        var missingDeps = new System.Collections.Generic.List<string>();
        if (!IsVCRedistInstalled()) missingDeps.Add("vcredist");
        if (!IsDotNet9Installed()) missingDeps.Add("dotnet9");
        if (!IsWindowsAppSdkInstalled()) missingDeps.Add("wasdk18");
        if (!IsWebView2Installed()) missingDeps.Add("webview2");

        if (missingDeps.Count == 0)
        {
            onProgress?.Invoke(1.0, "系统运行环境已就绪");
            return;
        }

        int totalCount = missingDeps.Count;
        int completedCount = 0;

        void ReportSubProgress(double subProgress, string msg)
        {
            double overall = (completedCount + Math.Clamp(subProgress, 0.0, 1.0)) / totalCount;
            onProgress?.Invoke(overall, msg);
        }

        if (missingDeps.Contains("vcredist"))
        {
            var installerPath = Path.Combine(tempDir, "vc_redist.x64.exe");
            await DownloadFileAsync(VcRedistDownloadUrl, installerPath, (p, s) => ReportSubProgress(p * 0.85, s), "正在下载 Visual C++ 运行库");

            ReportSubProgress(0.90, "正在安装 Visual C++ 2015-2022 运行库...");
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
            completedCount++;
            ReportSubProgress(0.0, "Visual C++ 运行库配置完成");
        }

        if (missingDeps.Contains("dotnet9"))
        {
            var installerPath = Path.Combine(tempDir, "dotnet9_desktop_runtime_x64.exe");
            await DownloadFileAsync(DotNet9DownloadUrl, installerPath, (p, s) => ReportSubProgress(p * 0.85, s), "正在下载 .NET 9 桌面运行时");

            ReportSubProgress(0.90, "正在安装 .NET 9 Desktop Runtime 运行时...");
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
            completedCount++;
            ReportSubProgress(0.0, ".NET 9 桌面运行时配置完成");
        }

        if (missingDeps.Contains("wasdk18"))
        {
            var installerPath = Path.Combine(tempDir, "windowsappruntimeinstall-x64.exe");
            await DownloadFileAsync(Wasdk18DownloadUrl, installerPath, (p, s) => ReportSubProgress(p * 0.85, s), "正在下载 Windows App SDK 1.8 运行时");

            ReportSubProgress(0.90, "正在安装 Windows App SDK 1.8 运行时...");
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
            completedCount++;
            ReportSubProgress(0.0, "Windows App SDK 1.8 配置完成");
        }

        if (missingDeps.Contains("webview2"))
        {
            var installerPath = Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe");
            await DownloadFileAsync(WebView2DownloadUrl, installerPath, (p, s) => ReportSubProgress(p * 0.85, s), "正在下载 WebView2 运行时");

            ReportSubProgress(0.90, "正在安装 WebView2 运行时...");
            await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install",
                    UseShellExecute = true
                });
                p?.WaitForExit();
            });
            completedCount++;
            ReportSubProgress(0.0, "WebView2 运行时配置完成");
        }

        onProgress?.Invoke(1.0, "环境依赖配置完成");
    }
}
