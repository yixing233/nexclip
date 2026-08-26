using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexClip.Installer.Services;

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
    public static async Task TerminateRunningInstancesAsync()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
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

            var psi = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = installDir,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
