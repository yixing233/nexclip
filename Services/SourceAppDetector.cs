namespace NexClip.Desktop.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 剪贴板条目来源应用信息
/// </summary>
public sealed record SourceAppInfo(
    string Name,
    string? ExecutablePath,
    string? ProcessName,
    string? IconPath,
    string? WindowTitle
);

/// <summary>
/// Windows 剪贴板来源应用程序嗅探器
/// </summary>
public static class SourceAppDetector
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? lclassName, string? windowTitle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 判断剪贴板当前所有者是否为本进程自身
    /// </summary>
    public static bool IsClipboardOwnedByCurrentProcess()
    {
        try
        {
            // 使用 Environment.ProcessId 取本进程 PID，避免每次调用都分配带终结器的 Process 对象
            var currentPid = (uint)Environment.ProcessId;
            IntPtr hwnd = GetClipboardOwner();
            if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == currentPid) return true;
            }
            IntPtr openHwnd = GetOpenClipboardWindow();
            if (openHwnd != IntPtr.Zero && NativeMethods.IsWindow(openHwnd))
            {
                GetWindowThreadProcessId(openHwnd, out uint pid);
                if (pid == currentPid) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 嗅探当前向剪贴板写入内容或正在前台交互的应用程序来源
    /// </summary>
    public static SourceAppInfo? DetectSourceApp()
    {
        try
        {
            // 使用 Environment.ProcessId 取本进程 PID，避免每次调用都分配带终结器的 Process 对象
            var currentPid = (uint)Environment.ProcessId;

            // 1. 优先级 1: 剪贴板当前所有者窗口
            IntPtr hwnd = GetClipboardOwner();

            // 2. 优先级 2: 打开剪贴板的窗口
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                hwnd = GetOpenClipboardWindow();
            }

            // 3. 优先级 3: 当前前台激活窗口兜底
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                hwnd = GetForegroundWindow();
            }

            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return null;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);

            // 若所有者就是本程序自身，则直接返回 null（说明是自身写回）
            if (pid == currentPid || pid == 0)
            {
                return null;
            }

            // 4. 处理 UWP 宿主进程 (ApplicationFrameHost.exe)
            var (realHwnd, realPid) = ResolveRealUwpWindow(hwnd, pid);
            if (realPid != 0 && realPid != currentPid)
            {
                hwnd = realHwnd;
                pid = realPid;
            }
            else if (realPid == currentPid)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;

            // 过滤系统空闲和自身
            if (string.Equals(processName, "Idle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "System", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var exePath = GetProcessExecutablePath(process, pid);
            var friendlyName = ResolveFriendlyName(processName, exePath);

            var sbTitle = new StringBuilder(256);
            GetWindowText(hwnd, sbTitle, 256);
            var windowTitle = sbTitle.ToString().Trim();

            var iconPath = AppIconCache.GetOrCreateIconPath(exePath, processName);

            return new SourceAppInfo(
                Name: friendlyName,
                ExecutablePath: exePath,
                ProcessName: processName,
                IconPath: iconPath,
                WindowTitle: windowTitle.Length > 0 ? windowTitle : null
            );
        }
        catch (Exception ex)
        {
            Log.Debug($"检测剪贴板来源程序失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 若顶层窗口属于 ApplicationFrameHost.exe，穿透子窗口寻找真实的 CoreWindow 进程
    /// </summary>
    private static (IntPtr Hwnd, uint Pid) ResolveRealUwpWindow(IntPtr topHwnd, uint topPid)
    {
        try
        {
            var sbClass = new StringBuilder(256);
            GetClassName(topHwnd, sbClass, 256);
            var className = sbClass.ToString();

            if (className == "ApplicationFrameWindow")
            {
                var coreHwnd = FindWindowEx(topHwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (coreHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(coreHwnd, out uint corePid);
                    if (corePid != 0 && corePid != topPid)
                    {
                        return (coreHwnd, corePid);
                    }
                }
            }
        }
        catch
        {
            // 忽略穿透失败
        }

        return (topHwnd, topPid);
    }

    /// <summary>
    /// 获取进程可执行文件的完整路径（支持高权限/受保护进程兜底）
    /// </summary>
    private static string? GetProcessExecutablePath(Process process, uint pid)
    {
        try
        {
            if (!string.IsNullOrEmpty(process.MainModule?.FileName))
            {
                return process.MainModule.FileName;
            }
        }
        catch
        {
            // 跨权限或64/32位访问拒绝时尝试 QueryFullProcessImageName
        }

        var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
                {
                    return sb.ToString();
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        return null;
    }

    /// <summary>
    /// 获取人类易读的友好软件名称
    /// </summary>
    private static string ResolveFriendlyName(string processName, string? exePath)
    {
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription.Trim();
                }
                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName.Trim();
                }
            }
            catch
            {
                // 读取版本信息异常时降级
            }
        }

        // 常见程序名友好映射兜底
        return processName.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "code" => "Visual Studio Code",
            "devenv" => "Visual Studio",
            "notepad" => "记事本",
            "wechat" => "微信",
            "qq" => "QQ",
            "dingtalk" => "钉钉",
            "feishu" => "飞书",
            "explorer" => "文件资源管理器",
            "windowsterminal" or "wt" => "Windows Terminal",
            "powershell" or "pwsh" => "PowerShell",
            "cmd" => "命令提示符",
            _ => processName
        };
    }
}
