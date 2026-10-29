namespace NexClip.Desktop.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NexClip.Tray;

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
    /// 取剪贴板**数据所有者**的进程名(即"内容是谁放进剪贴板的")。取不到返回 null。
    ///
    /// 语义边界(重要):GetClipboardOwner 返回的是**内容所有权**,不是**持锁状态**。
    /// 内容所有者可以长期存在而此刻完全没人锁着剪贴板 —— 这两种情况必须区别对待,
    /// 否则诊断会指向错误的程序。要判断"谁正在挡住我们的 OpenClipboard",
    /// 请用 <see cref="TryGetClipboardLockHolderProcessName"/>。
    ///
    /// 本方法的正确用途是"来源过滤":发起跨进程读取之前,若数据所有者是无响应的
    /// 远控程序,读取时会因它的延迟渲染(WM_RENDERFORMAT)挂住且无法中断,
    /// 这类来源必须提前跳过 —— 判断依据正是数据所有者,所以这里用 GetClipboardOwner 是对的。
    ///
    /// 刻意不使用 <see cref="CaptureOwnerHandle"/> 的前台窗口兜底:那个兜底对"识别来源应用"
    /// 是合理的,但用于"是否跳过读取"会拿前台窗口冒充剪贴板所有者,既可能误过滤正常复制,
    /// 也可能漏掉真正的风险来源。
    /// </summary>
    public static string? TryGetClipboardOwnerProcessName() => ProcessNameOfWindow(GetClipboardOwner());

    /// <summary>
    /// 取剪贴板**持锁者**的进程名,即"此刻 OpenClipboard 尚未 Close 的进程"。无人持锁时返回 null。
    ///
    /// 与 <see cref="TryGetClipboardOwnerProcessName"/> 的区别是排查问题的关键:
    /// 剪贴板是"谁持锁谁说了算"的全局资源,我们的 OpenClipboard/SetContent 收到
    /// CLIPBRD_E_CANT_OPEN 时,原因是**持锁者**,而不是内容所有者。
    /// 实测本机:GetClipboardOwner 常驻指向 GameViewer(UU远程,它放了内容),
    /// 而 GetOpenClipboardWindow 在无人操作时是 0 —— 若拿前者当"占用者"上报,
    /// 排查时会把每一次偶发争用都错误地归因到它身上。
    ///
    /// <para><b>已知盲区(实测确认,必须知道)</b>:<c>OpenClipboard(NULL)</c> 持锁时,
    /// 系统会把这个锁记为"无主窗口",<c>GetOpenClipboardWindow</c> 返回 0,
    /// 于是这里查不到持有者。只有传入了真实窗口句柄的持锁者才可见 ——
    /// 用跨进程实测验证过:传 HWND 时外部能读到 pid 且外部 OpenClipboard 同步失败;
    /// 传 NULL 时外部读到 0,锁依然存在但无法归因。
    /// 本应用自己的探针用的就是 NULL,所以**绝不能**用它来判断"是不是我们自己"。
    /// 因此调用方必须把 null 当作"未知"处理,不能理解成"没人占用"。</para>
    ///
    /// 使用时机:探针失败、写入收到 CLIPBRD_E_CANT_OPEN、要在 UI 上提示"是谁占着剪贴板"。
    /// </summary>
    public static string? TryGetClipboardLockHolderProcessName() => ProcessNameOfWindow(GetOpenClipboardWindow());

    /// <summary>
    /// 生成"剪贴板被谁占着"的用户可读说明。
    ///
    /// 措辞刻意区分两种情况,避免把排查方向带偏:
    /// - 有持锁者(GetOpenClipboardWindow 非 0):明确说"X 正在占用系统剪贴板",这是可执行的结论;
    /// - 只有数据所有者:不说"占用"(那会误导用户以为内容所有者就是元凶),
    ///   而是描述为"内容来自 X"并提示短暂争用。
    /// </summary>
    public static string DescribeClipboardBlocker() =>
        DescribeClipboardBlocker(null);

    /// <summary>同上,但可传入调用方已经测到的持锁者(写入器重试期间观察到的那个)。</summary>
    public static string DescribeClipboardBlocker(string? measuredLockHolder) =>
        NexClip.Tray.ClipboardWritePolicy.DescribeBlocker(
            string.IsNullOrEmpty(measuredLockHolder) ? TryGetClipboardLockHolderProcessName() : measuredLockHolder,
            TryGetClipboardOwnerProcessName());

    /// <summary>
    /// 窗口句柄 → 进程名。只做 Win32 查询 + 一次进程路径读取:无 PE 版本信息解析(磁盘 IO)、
    /// 无图标提取(GDI),可在 UI 线程即时执行。
    /// 句柄无效、属于本进程或取不到进程时一律返回 null,由调用方按"未知"处理。
    /// </summary>
    private static string? ProcessNameOfWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return null;

            var path = QueryProcessImagePath(pid);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按 PID 直接取映像路径:单次 OpenProcess + QueryFullProcessImageName,不构造 Process 对象。
    /// 权限不足(提升进程)或进程已退出时返回 null,由调用方按"未知"放行。
    /// </summary>
    private static string? QueryProcessImagePath(uint pid)
    {
        var hProcess = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProcess == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hProcess, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

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
    /// 剪贴板来源窗口的轻量快照(窗口句柄 + 进程 Id)。
    /// 仅由 Win32 调用取得,不含任何磁盘 IO 或进程查询,可在 UI 线程即时执行。
    /// </summary>
    public readonly record struct ClipboardOwnerHandle(IntPtr Hwnd, uint Pid);

    /// <summary>
    /// 快速捕获剪贴板当前所有者窗口。返回 null 表示无法确定来源或来源即本进程。
    /// 只做 GetClipboardOwner / GetOpenClipboardWindow / GetForegroundWindow 三次廉价调用,
    /// 目的是在 UI 线程上先"钉住"来源,再交给后台线程做重活,避免来源在解析期间漂移。
    /// </summary>
    public static ClipboardOwnerHandle? CaptureOwnerHandle()
    {
        try
        {
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

            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == currentPid) return null;

            return new ClipboardOwnerHandle(hwnd, pid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析来源应用详情:进程名、可执行文件路径、PE 版本信息(磁盘读取)、窗口标题与图标提取(GDI+)。
    /// 这些操作单次可达数毫秒,必须在后台线程执行,不能放在剪贴板捕获的 UI 线程路径上。
    /// </summary>
    public static SourceAppInfo? ResolveSourceApp(ClipboardOwnerHandle owner)
    {
        try
        {
            var currentPid = (uint)Environment.ProcessId;
            var hwnd = owner.Hwnd;
            var pid = owner.Pid;

            // 处理 UWP 宿主进程 (ApplicationFrameHost.exe)
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

            // 处理 WebView2 宿主 (msedgewebview2.exe)：
            // 很多现代桌面应用(DSH-X / GameViewer / Widgets / Antigravity 等)用 WebView2 承载界面，
            // 剪贴板写入实际发生在 msedgewebview2.exe 的子进程里，于是来源被误识别成
            // "Microsoft Edge WebView2"。必须回溯到真正的宿主应用。
            var (hostHwnd, hostPid) = ResolveRealWebView2Host(hwnd, pid);
            if (hostPid != 0 && hostPid != pid)
            {
                if (hostPid == currentPid)
                {
                    return null;
                }
                hwnd = hostHwnd;
                pid = hostPid;
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
            var friendlyName = ResolveFriendlyNameCached(processName, exePath);

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
            Log.Debug($"解析剪贴板来源程序失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 嗅探当前向剪贴板写入内容或正在前台交互的应用程序来源(同步版本,UI 线程调用会阻塞)。
    /// 剪贴板捕获路径请改用 <see cref="CaptureOwnerHandle"/> + <see cref="ResolveSourceApp"/>。
    /// </summary>
    public static SourceAppInfo? DetectSourceApp()
    {
        var owner = CaptureOwnerHandle();
        return owner is null ? null : ResolveSourceApp(owner.Value);
    }

    /// <summary>
    /// 若来源进程是 WebView2（msedgewebview2.exe），回溯到真正承载它的宿主应用。
    ///
    /// <para>为什么需要：WebView2 把「浏览器内核」与「宿主应用」拆成两个进程。
    /// 用户在 DSH-X / GameViewer 里复制文本时，实际调用剪贴板 API 的是
    /// msedgewebview2.exe 的子进程，所以直接取进程名会得到
    /// "Microsoft Edge WebView2" —— 对用户毫无意义，因为界面上根本没有这个程序。</para>
    ///
    /// <para>识别依据是**父进程链**：WebView2 的浏览器进程由宿主应用直接创建，
    /// 中间没有额外转发进程，因此"父进程 = 宿主"这条规则稳定可靠。
    /// 实测确认：DSH-X 的 msedgewebview2(18528) 父进程正是 DSH.exe(28300)，
    /// GameViewer / Widgets / Antigravity 同样成立。</para>
    ///
    /// <para>不用命令行 <c>--webview-exe-name</c> 的原因：读取命令行需要 WMI
    /// (System.Management) 或解析远程 PEB，前者要多一个包引用、后者过于复杂，
    /// 而父进程链用 CreateToolhelp32Snapshot 即可，零额外依赖且更快。</para>
    ///
    /// <para>回溯失败时保持原值，退回显示 WebView2 —— 至少不丢信息。</para>
    /// </summary>
    private static (IntPtr Hwnd, uint Pid) ResolveRealWebView2Host(IntPtr hwnd, uint pid)
    {
        try
        {
            if (!IsWebView2Process(pid))
            {
                return (hwnd, pid);
            }

            // 沿父进程链上溯，跳过所有 msedgewebview2 进程，拿到真正的宿主
            var ancestor = TryGetUltimateHostPid(pid);
            if (ancestor != 0 && ancestor != pid)
            {
                return (TryGetMainWindowOfProcess(ancestor, hwnd), ancestor);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"解析 WebView2 宿主失败: {ex.Message}");
        }

        return (hwnd, pid);
    }

    /// <summary>判断指定进程是否为 WebView2 宿主进程（msedgewebview2.exe）。</summary>
    private static bool IsWebView2Process(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return SourceAppClassifier.IsWebView2Process(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按进程名查找第一个同名进程。
    /// 作为 <c>--webview-exe-name</c> 的匹配入口保留，但**不依赖命令行读取**：
    /// 主路径是父进程链（见 <see cref="ResolveRealWebView2Host"/>）。
    /// </summary>
    private static uint TryFindProcessByName(string processName)
    {
        try
        {
            var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            foreach (var process in Process.GetProcessesByName(normalized))
            {
                using (process)
                {
                    if (string.Equals(process.ProcessName, "msedgewebview2", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return (uint)process.Id;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return 0;
    }

    /// <summary>
    /// 沿父进程链上溯，跳过连续的 msedgewebview2 进程，返回第一个非 WebView2 祖先。
    ///
    /// <para>这是识别 WebView2 宿主最可靠也最廉价的方式，实测数据：
    /// DSH-X 的 <c>msedgewebview2.exe</c>(PID 18528) 父进程正是宿主
    /// <c>DSH.exe</c>(PID 28300)；GameViewer、Widgets、Antigravity 同样如此。
    /// WebView2 的浏览器进程由宿主直接创建，中间没有额外转发进程，
    /// 因此"父进程 = 宿主"这条规则足够稳定，无需解析命令行。</para>
    /// </summary>
    private static uint TryGetUltimateHostPid(uint pid)
    {
        var current = pid;
        for (var depth = 0; depth < 8; depth++)
        {
            var parent = TryGetParentProcessId(current);
            if (parent == 0 || parent == current)
            {
                break;
            }

            // 用共享分类器判断"能否作为宿主"：排除内核自身与 System/Idle 等伪进程
            if (!IsWebView2Process(parent) && !IsSystemPseudoProcess(parent))
            {
                return parent;
            }

            current = parent;
        }

        return 0;
    }

    /// <summary>System / Idle 是内核伪进程，回溯到它们没有任何意义。</summary>
    private static bool IsSystemPseudoProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return !SourceAppClassifier.CanBeWebView2Host(process.ProcessName);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>用 CreateToolhelp32Snapshot 取父进程 Id（纯 Win32，无 WMI/COM 依赖）。</summary>
    private static uint TryGetParentProcessId(uint pid)
    {
        IntPtr snapshot = IntPtr.Zero;
        try
        {
            snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return 0;
            }

            var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if (entry.th32ProcessID == pid)
                {
                    return entry.th32ParentProcessID;
                }
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch
        {
            // 忽略
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                CloseHandle(snapshot);
            }
        }

        return 0;
    }

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    /// <summary>尝试取得进程的顶层主窗口；取不到时回退到原句柄。</summary>
    private static IntPtr TryGetMainWindowOfProcess(uint pid, IntPtr fallback)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var main = process.MainWindowHandle;
            return main != IntPtr.Zero ? main : fallback;
        }
        catch
        {
            return fallback;
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
    /// 友好名称缓存(可执行文件路径 → 名称)。
    /// FileVersionInfo.GetVersionInfo 需要打开并解析目标 PE 文件的资源段,属磁盘 IO,
    /// 同一程序反复复制时没必要每次都读一遍。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FriendlyNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string ResolveFriendlyNameCached(string processName, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return ResolveFriendlyName(processName, exePath);
        return FriendlyNameCache.GetOrAdd(exePath, key => ResolveFriendlyName(processName, key));
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