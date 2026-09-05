using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NexClip.Installer.Native.Services;

public static class ProcessHelper
{
    private static readonly string[] ProcessNames = { "NexClip", "NexClip.Tray" };

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ForcedTimeout = TimeSpan.FromSeconds(5);

    /// <summary>安装器启动时继承到的当前目录，仅用于日志与诊断报告。</summary>
    public static string? StartupWorkingDirectory { get; private set; }

    /// <summary>
    /// 把安装器自身的当前工作目录挪到系统临时目录。
    /// 进程的当前目录会持有一个不带 FILE_SHARE_DELETE 的目录句柄，该目录因此无法被改名；
    /// 旧版桌面端用 ShellExecute 启动安装器时没有指定 WorkingDirectory，安装器就继承了它的
    /// 当前目录（快捷方式把它设成了安装目录），于是覆盖安装时 Directory.Move(安装目录, 备份)
    /// 必然失败，而且杀掉 NexClip 也救不回来——占用者正是安装器自己。
    /// 必须在 SetupArguments.Parse 之后调用：/diagnose= 的相对路径依赖原始当前目录。
    /// </summary>
    public static void ReleaseWorkingDirectory()
    {
        try
        {
            StartupWorkingDirectory ??= Directory.GetCurrentDirectory();

            var temp = Path.GetTempPath();
            if (!string.IsNullOrWhiteSpace(temp) && Directory.Exists(temp))
            {
                Directory.SetCurrentDirectory(temp);
            }
        }
        catch
        {
            // 换不掉也不该阻断安装：PayloadService 还有逐文件覆盖的回退路径
        }
    }

    /// <summary>
    /// 检查是否有 NexClip 相关进程正在运行。传入安装目录时，连安装目录下被改过名的可执行文件一起算。
    /// </summary>
    public static bool IsNexClipRunning(string? installDirectory = null)
    {
        var targets = CollectTargets(installDirectory);
        try
        {
            return targets.Count > 0;
        }
        finally
        {
            DisposeAll(targets);
        }
    }

    /// <summary>
    /// 先礼后兵地结束所有正在运行的 NexClip 实例，并轮询确认进程真的没了。
    /// </summary>
    /// <param name="installDirectory">安装目录；给了就顺带清理该目录下按进程名匹配不到的可执行文件（如 Uninstall.exe）。</param>
    /// <returns>仍然没能结束的进程描述；空集合表示已全部退出。</returns>
    public static async Task<IReadOnlyList<string>> TerminateRunningInstancesAsync(
        string? installDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var targets = CollectTargets(installDirectory);
        try
        {
            if (targets.Count == 0) return Array.Empty<string>();

            // 1) 先礼：让主窗口/托盘自己退出，设置与历史才有机会落盘
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!target.Process.HasExited) target.Process.CloseMainWindow();
                }
                catch
                {
                }
            }
            var alive = await WaitForExitAsync(targets, GracefulTimeout, cancellationToken).ConfigureAwait(false);

            // 2) 后兵：整棵进程树强杀。只杀父进程的话，它拉起的子进程照样占着安装目录里的文件
            foreach (var target in alive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!target.Process.HasExited) target.Process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }
            alive = await WaitForExitAsync(alive, ForcedTimeout, cancellationToken).ConfigureAwait(false);

            // 3) taskkill 兜底：Kill 失败多半是句柄权限问题，换条路再试一次。
            //    不看它的退出码——下面直接复查进程本身，比退出码可信。
            if (alive.Count > 0)
            {
                foreach (var target in alive)
                {
                    await RunTaskkillAsync(target.Id, cancellationToken).ConfigureAwait(false);
                }
                alive = await WaitForExitAsync(alive, ForcedTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (alive.Count == 0)
            {
                // 进程已退出，但镜像文件句柄的释放会略滞后一点
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                return Array.Empty<string>();
            }

            return alive.Select(target => target.ToString()).ToArray();
        }
        finally
        {
            DisposeAll(targets);
        }
    }

    /// <summary>进程名要在进程还活着时取，退出后再读 ProcessName 会抛异常，所以采集时就记下来。</summary>
    private sealed class Target(Process process, string name, int id)
    {
        public Process Process { get; } = process;
        public int Id { get; } = id;
        public override string ToString() => $"{name} (PID {Id})";
    }

    private static List<Target> CollectTargets(string? installDirectory)
    {
        var found = new Dictionary<int, Target>();
        var self = Environment.ProcessId;

        foreach (var name in ProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (process.Id == self || !found.TryAdd(process.Id, new Target(process, name, process.Id)))
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }
        }

        // 只按进程名找不全：安装目录里还可能跑着 Uninstall.exe 或被改过名的副本，
        // 它们同样占着待改名的目录。按可执行文件路径再扫一遍，但绝不能扫到安装器自己
        // （卸载时正在运行的就是 <安装目录>\Uninstall.exe）。
        var root = NormalizeDirectory(installDirectory);
        if (root != null)
        {
            Process[] all;
            try
            {
                all = Process.GetProcesses();
            }
            catch
            {
                all = Array.Empty<Process>();
            }

            foreach (var process in all)
            {
                var matched = false;
                try
                {
                    if (process.Id != self && !found.ContainsKey(process.Id))
                    {
                        var file = process.MainModule?.FileName;
                        if (file != null && file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = found.TryAdd(process.Id, new Target(process, process.ProcessName, process.Id));
                        }
                    }
                }
                catch
                {
                    // 受保护进程读不到 MainModule，跳过即可
                }

                if (!matched) process.Dispose();
            }
        }

        return found.Values.ToList();
    }

    private static async Task<List<Target>> WaitForExitAsync(
        List<Target> targets, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alive = targets.Where(IsAlive).ToList();
            if (alive.Count == 0 || DateTime.UtcNow >= deadline) return alive;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsAlive(Target target)
    {
        try
        {
            return !target.Process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunTaskkillAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var proc = new Process();
            proc.StartInfo.FileName = "taskkill.exe";
            proc.StartInfo.Arguments = $"/F /T /PID {pid}";
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.WorkingDirectory = Path.GetTempPath();
            proc.Start();

            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // taskkill 自己卡住了，不等它，后面复查进程状态
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            return Path.GetFullPath(directory)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
        }
    }

    private static void DisposeAll(List<Target> targets)
    {
        foreach (var target in targets)
        {
            try
            {
                target.Process.Dispose();
            }
            catch
            {
            }
        }
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
