using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexClip.Installer.Native.Services;

public static class ProcessHelper
{
    private static readonly string[] ProcessNames = { "NexClip", "NexClip.Tray" };

    /// <summary>
    /// 检查是否有 NexClip 相关进程正在运行
    /// </summary>
    public static bool IsNexClipRunning()
    {
        foreach (var name in ProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 自动平滑且强制结束所有正在运行的 NexClip 实例，并等待文件句柄完全释放
    /// </summary>
    public static async Task TerminateRunningInstancesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var name in ProcessNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                        // 忽略权限或已退出的异常
                    }
                }
            }
            catch
            {
            }
        }

        // 辅以 taskkill 兜底，确保子进程及托盘完全清理
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var psi = new Process();
            psi.StartInfo.FileName = "taskkill.exe";
            psi.StartInfo.Arguments = "/F /IM NexClip.exe /T";
            psi.StartInfo.CreateNoWindow = true;
            psi.StartInfo.UseShellExecute = false;
            psi.Start();
            psi.WaitForExit(1500);
        }
        catch
        {
        }

        // 留出短暂缓冲以释放所有 DLL 与资源文件占用
        await Task.Delay(500);
    }

    /// <summary>
    /// 启动已安装好的 NexClip.exe
    /// </summary>
    public static bool LaunchApp(string installDir)
    {
        try
        {
            var exePath = Path.Combine(installDir, "NexClip.exe");
            if (!File.Exists(exePath)) return false;

            var psi = new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{exePath}\"",
                WorkingDirectory = installDir,
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ScheduleDirectoryDeletion(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"NexClip-cleanup-{Guid.NewGuid():N}.cmd");
            var escaped = directory.Replace("\"", "\"\"");
            File.WriteAllText(scriptPath, $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrmdir /s /q \"{escaped}\"\r\ndel /f /q \"%~f0\"\r\n");

            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c \"\"{scriptPath}\"\"",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

