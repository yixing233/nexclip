namespace NexClip.Desktop.Services;

using System.Runtime.InteropServices;

/// <summary>极小 Win32 调用。</summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolwindow = 0x00000080;
    internal const long WsExAppwindow = 0x00040000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    internal static readonly IntPtr HwndTopmost = new(-1);
    internal static readonly IntPtr HwndNotTopmost = new(-2);
    internal static readonly IntPtr HwndTop = IntPtr.Zero;   // HWND_TOP:移到 Z 序顶部(不激活)

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;   // 关键:改变 Z 序但不抢焦点
    internal const uint SwpShowWindow = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>绕过 Windows 前台锁定:模拟一次 ALT 键(经典技巧),再请求前台。
    /// 注意:仅适合普通应用;对 Chromium 会注入 Alt 键导致浏览器进入菜单模式、页面失焦,勿用于粘贴目标。</summary>
    internal static void ForceForeground(IntPtr hwnd)
    {
        keybd_event(0x12 /* VK_MENU */, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    /// <summary>释放可能残留的修饰键(Ctrl/Alt/Shift/Win)。热键触发时这些键可能仍在按下状态,
    /// 残留状态会干扰后续注入的 Ctrl+V(如 Alt 残留会激活 Chromium 菜单栏)。</summary>
    internal static void AllKeysUp()
    {
        foreach (var vk in new byte[] { 0x10 /* Shift */, 0x11 /* Ctrl */, 0x12 /* Alt */, 0x5B /* LWin */, 0x5C /* RWin */ })
        {
            keybd_event(vk, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>剪贴板序列号:内容未变化时该值不变,可用于零成本短路轮询。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// 将进程工作集中的可分页内存换出:窗口隐藏到托盘后调用,
    /// 可把已释放但仍驻留物理内存的页归还系统,显著降低任务管理器显示的内存占用。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>回收当前进程驻留内存(压缩式 GC + 归还工作集)。请在后台线程调用,勿阻塞 UI 线程。</summary>
    internal static void TrimProcessWorkingSet()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            EmptyWorkingSet(current.Handle);
        }
        catch
        {
            // 回收失败不影响功能
        }
    }

    /// <summary>
    /// 激活目标窗口:附加当前前台线程后请求 SetForegroundWindow。
    /// 不修改全局 ForegroundLockTimeout,也不注入 Alt 键。
    /// </summary>
    internal static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            var activated = SetForegroundWindow(hwnd);
            if (activated) BringWindowToTop(hwnd);
            return activated;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>模拟 Ctrl+V 粘贴(双击/回车粘贴到目标窗口)。按键间加小延迟,避免目标应用吞键。</summary>
    internal static void SendCtrlV()
    {
        const byte vkControl = 0x11;
        const byte vkV = 0x56;
        keybd_event(vkControl, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkV, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkV, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkControl, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
    }

    /// <summary>模拟 Shift+Insert 粘贴(终端/控制台兼容)。</summary>
    internal static void SendShiftInsert()
    {
        const byte vkShift = 0x10;
        const byte vkInsert = 0x2D;
        keybd_event(vkShift, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkInsert, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkInsert, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        System.Threading.Thread.Sleep(15);
        keybd_event(vkShift, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
    }

    // ---- SendInput 注入(对齐 PastePaw:原子注入) ----
    // 注意:Win32 INPUT 是 union 布局,union 按 MOUSEINPUT(x64 下 32 字节)撑满,
    // 整个 INPUT 在 x64 下必须为 40 字节;cbSize 不符 SendInput 直接失败(错误 87)且静默返回 0。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
        [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;   // INPUT_KEYBOARD = 1
        public INPUTUNION u;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>SendInput 注入粘贴键;返回 false 表示注入失败(cbSize/UIPI 等,键未送达)。</summary>
    private static bool SendPasteKeys(ushort modifier, ushort key)
    {
        const uint keyboard = 1;
        const uint keyUp = 0x0002;
        var sent = SendInput(4, new[]
        {
            new INPUT { type = keyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = modifier } } },
            new INPUT { type = keyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = key } } },
            new INPUT { type = keyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = key, dwFlags = keyUp } } },
            new INPUT { type = keyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = modifier, dwFlags = keyUp } } },
        }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        if (sent != 4)
        {
            Log.Warn($"SendInput 仅注入 {sent}/4 个事件, GetLastError={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        return true;
    }

    /// <summary>SendInput 原子注入 Shift+Insert(对齐 PastePaw 的 send_paste_input)。</summary>
    internal static bool SendInputShiftInsert() => SendPasteKeys(0x10 /* VK_SHIFT */, 0x2D /* VK_INSERT */);

    /// <summary>SendInput 原子注入 Ctrl+V。</summary>
    internal static bool SendInputCtrlV() => SendPasteKeys(0x11 /* VK_CONTROL */, 0x56 /* VK_V */);

    // ---- WM_PASTE 直达消息(标准编辑控件最可靠,无需前台/按键注入) ----
    internal const uint WM_PASTE = 0x0302;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 模拟用户点击窗口标题栏(非客户区):发送 WM_NCLBUTTONDOWN/UP 消息。
    /// 不移动鼠标、不触碰页面内容;目标窗口视为用户正常点击激活(Chromium 恢复页面焦点)。
    /// </summary>
    internal static void ClickTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        const uint wmNcLButtonDown = 0x00A1;
        const uint wmNcLButtonUp = 0x00A2;
        const nuint htTop = 0x000C; // HTTOP:标题栏命中区
        SendMessage(hwnd, wmNcLButtonDown, (UIntPtr)htTop, IntPtr.Zero);
        SendMessage(hwnd, wmNcLButtonUp, (UIntPtr)htTop, IntPtr.Zero);
    }

    /// <summary>
    /// 向目标控件直接发送 WM_PASTE(编辑框粘贴)。返回 true 表示控件处理了粘贴(返回值非 0);
    /// 现代框架(浏览器等自绘输入框)不响应 WM_PASTE,返回 false 由调用方回退模拟 Ctrl+V。
    /// </summary>
    internal static bool TryPasteViaMessage(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (SendMessageTimeout(hwnd, WM_PASTE, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out var result) != IntPtr.Zero)
        {
            return result != UIntPtr.Zero;
        }
        return false;
    }

    // ---- 焦点控件跟踪(呼出时记录输入框,恢复时精确回焦) ----
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>
    /// 当前前台窗口内的焦点控件句柄(输入框等);无前台/取不到返回 0。
    /// GetGUIThreadInfo 只能读取与调用线程共享输入队列的线程,跨线程须先 AttachThreadInput。
    /// </summary>
    internal static IntPtr GetFocusedControl()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        var targetTid = GetWindowThreadProcessId(fg, out _);
        if (targetTid == 0) return IntPtr.Zero;
        var curTid = GetCurrentThreadId();
        var attached = targetTid != curTid && AttachThreadInput(curTid, targetTid, true);
        try
        {
            var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(targetTid, ref info)) return info.hwndFocus;
            return IntPtr.Zero;
        }
        finally
        {
            if (attached) AttachThreadInput(curTid, targetTid, false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    internal static IntPtr GetRootWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        return root == IntPtr.Zero ? hwnd : root;
    }

    internal static bool IsSameRootWindow(IntPtr first, IntPtr second) =>
        first != IntPtr.Zero && second != IntPtr.Zero && GetRootWindow(first) == GetRootWindow(second);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 查找 Chromium(Edge/Chrome)顶层的渲染窗口(Chrome_RenderWidgetHostHWND)。
    /// 网页键盘焦点宿主是渲染窗口而非顶层窗口:SetFocus 到渲染窗口才能让浏览器
    /// 恢复页面内输入框的焦点(顶层 SetFocus 反而可能把页面焦点重置到 chrome 区域)。
    /// </summary>
    internal static IntPtr FindRenderWidgetChild(IntPtr topLevel)
    {
        var found = IntPtr.Zero;
        EnumWindowsProc callback = (h, l) =>
        {
            var sb = new System.Text.StringBuilder(128);
            if (GetClassName(h, sb, 128) != 0 && sb.ToString() == "Chrome_RenderWidgetHostHWND")
            {
                found = h;
                return false;
            }
            return true;
        };
        EnumChildWindows(topLevel, callback, IntPtr.Zero);
        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>判断目标是否为 Chromium/Electron/WebView2 顶层窗口。</summary>
    internal static bool IsChromiumWindow(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero || !IsWindow(topLevel)) return false;

        var className = new System.Text.StringBuilder(128);
        if (GetClassName(topLevel, className, className.Capacity) != 0 &&
            className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
        {
            return true;
        }

        return FindRenderWidgetChild(topLevel) != IntPtr.Zero;
    }

    /// <summary>
    /// 验证目标窗口(或其子窗口)是否持有当前输入焦点。
    /// keybd_event/SendInput 注入的按键发给"输入焦点窗口"而非"前台窗口",
    /// 剪贴板窗口隐藏后若不显式释放焦点,注入会发回本窗口导致粘贴失败。
    /// </summary>
    internal static bool IsFocusedInWindow(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var tid = GetWindowThreadProcessId(fg, out _);
        var curTid = GetCurrentThreadId();
        var attached = tid != curTid && AttachThreadInput(curTid, tid, true);
        try
        {
            var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(tid, ref info)) return false;
            var focus = info.hwndFocus;
            if (focus == IntPtr.Zero) return false;
            return focus == hwnd || IsChild(hwnd, focus) || IsChild(focus, hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(curTid, tid, false);
        }
    }

    /// <summary>把焦点精确给到指定控件(跨线程需临时附加输入队列);首次失败小延迟重试一次。</summary>
    internal static bool SetFocusTo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targetTid = GetWindowThreadProcessId(hwnd, out _);
            var curTid = GetCurrentThreadId();
            var attached = targetTid != 0 && targetTid != curTid && AttachThreadInput(curTid, targetTid, true);
            try
            {
                if (SetFocus(hwnd) != IntPtr.Zero) return true;
            }
            finally
            {
                if (attached) AttachThreadInput(curTid, targetTid, false);
            }
            System.Threading.Thread.Sleep(30);
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    // ---- 窗口过程子类化(禁用标题栏双击最大化) ----
    internal const int GwlWndproc = -4;
    internal const uint WmNcLButtonDblClk = 0x00A3;

    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr CallWindowProc(IntPtr prevWndProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 托盘右键菜单(TrackPopupMenu) ----
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint flags, nuint idNewItem, string? newItem);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>在指定屏幕坐标模拟一次鼠标单击(自然切换失败时点击重建 Chromium 页面焦点)。</summary>
    internal static void MouseClick(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0002 /* LEFTDOWN */, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004 /* LEFTUP */, 0, 0, 0, UIntPtr.Zero);
    }

    // ---- 监视器(定位窗口:屏幕中心 / 跟随鼠标) ----
    internal const uint MonitorDefaultToNearest = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ---- 托盘(Shell_NotifyIcon) ----
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    // ---- 经典文件夹选择器(管理员/提升进程下 WinRT FolderPicker 不可用,作回退) ----
    internal const uint BifReturnOnlyFsDirs = 0x0001;
    internal const uint BifNewDialogStyle = 0x0040;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public string pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray)] IntPtr[]? apidl,
        uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>
    /// 在 Windows 资源管理器中打开并高亮选中指定文件或文件夹。
    /// 优先使用 Windows Shell 原生 API SHOpenFolderAndSelectItems (零进程开销、完美前台聚焦与精准高亮)，
    /// 若失败则多级回退至 explorer.exe 命令行及打开父目录。
    /// </summary>
    internal static void LocateInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path.Trim().Trim('"', '\''));
            if (!System.IO.File.Exists(fullPath) && !System.IO.Directory.Exists(fullPath)) return;

            // 1. 优先调用 Shell 原生 API (当 cidl=0 时，pidlFolder 指向文件自身 PIDL，系统自动打开其父目录并高亮选中该文件)
            var pidl = ILCreateFromPath(fullPath);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    if (SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0)
                    {
                        return;
                    }
                }
                finally
                {
                    ILFree(pidl);
                }
            }

            // 2. 回退方案：通过 explorer.exe /select,"path" 打开
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"在资源管理器中定位文件失败: {path}", ex);
            try
            {
                // 3. 终极回退：直接打开其所在父文件夹
                var parentDir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parentDir) && System.IO.Directory.Exists(parentDir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = parentDir,
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }
    }

    /// <summary>在 Windows 资源管理器中打开指定目录。</summary>
    internal static void OpenFolderInExplorer(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        try
        {
            var fullPath = System.IO.Path.GetFullPath(folderPath.Trim().Trim('"', '\''));
            if (!System.IO.Directory.Exists(fullPath)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error($"打开文件夹失败: {folderPath}", ex);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
