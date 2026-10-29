namespace NexClip.Desktop.Services;

using System.Runtime.InteropServices;

/// <summary>极小 Win32 调用。</summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolwindow = 0x00000080;
    /// <summary>窗口永不获得激活(焦点)。粘贴期间临时加上,避免剪贴板窗口抢走目标的输入焦点。</summary>
    internal const long WsExNoActivate = 0x08000000;
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
    /// 残留状态会干扰后续注入的 Ctrl+V(如 Alt 残留会激活 Chromium 菜单栏)。
    ///
    /// 注意:这只是**往输入流里补发 KEYUP 事件**,并不能改变用户手指仍按着物理键的事实。
    /// 若用户还按着 Ctrl,注入的 Ctrl+V 会与物理 Ctrl 叠加成 Ctrl+V+V。
    /// 因此注入前必须用 <see cref="WaitForModifiersReleased"/> 等待真实松开。</summary>
    internal static void AllKeysUp()
    {
        foreach (var vk in new byte[] { 0x10 /* Shift */, 0x11 /* Ctrl */, 0x12 /* Alt */, 0x5B /* LWin */, 0x5C /* RWin */ })
        {
            keybd_event(vk, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>修饰键是否被物理按下(GetKeyState 最高位为 1)。</summary>
    private static bool IsModifierDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 当前是否有任何修饰键处于真实按下状态。
    /// 覆盖 Ctrl/Shift/Alt/Win 的左右两侧,与 CopyQ 的 isModifierPressed 等价。
    /// </summary>
    internal static bool IsAnyModifierDown() =>
        IsModifierDown(0x11 /* VK_CONTROL */) || IsModifierDown(0xA2 /* VK_LCONTROL */) ||
        IsModifierDown(0xA3 /* VK_RCONTROL */) ||
        IsModifierDown(0x10 /* VK_SHIFT */) || IsModifierDown(0xA0 /* VK_LSHIFT */) ||
        IsModifierDown(0xA1 /* VK_RSHIFT */) ||
        IsModifierDown(0x12 /* VK_MENU */) || IsModifierDown(0xA4 /* VK_LMENU */) ||
        IsModifierDown(0xA5 /* VK_RMENU */) ||
        IsModifierDown(0x5B /* VK_LWIN */) || IsModifierDown(0x5C /* VK_RWIN */);

    /// <summary>
    /// 等待用户松开所有修饰键,最多等 <paramref name="timeoutMilliseconds"/>。
    /// 返回 true 表示可以安全注入粘贴键。
    ///
    /// <para>为什么粘贴前必须等:热键(如 Alt+V)呼出选择窗后,用户手指常常还停在 Alt 上。
    /// 此时注入 Ctrl+V,系统收到的是"Ctrl+Alt+V"或与物理键叠加的双击,目标应用可能
    /// 执行到其它命令、或粘贴两次。CopyQ 对同一问题也做了等待
    /// (winplatformwindow.cpp 的 waitForModifiersReleased)。</para>
    ///
    /// <para>轮询间隔 10ms、默认上限 300ms:既不让用户感到迟滞,又覆盖了绝大多数人
    /// 松开按键所需的时间。超时后仍然继续粘贴 —— 宁可冒叠加风险,也不要让粘贴失败。</para>
    /// </summary>
    internal static async Task<bool> WaitForModifiersReleasedAsync(
        int timeoutMilliseconds = 300, CancellationToken cancellationToken = default)
    {
        if (!IsAnyModifierDown())
        {
            return true;
        }

        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (!IsAnyModifierDown())
            {
                return true;
            }
        }

        return false;
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

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    /// <summary>
    /// 探测剪贴板当前能否被打开。返回 false 表示剪贴板正被某个窗口独占(未调用 CloseClipboard)。
    ///
    /// 该调用是"剪贴板是否已经卡死"的低成本探针:被独占时 OpenClipboard 会立即返回
    /// ERROR_ACCESS_DENIED,绝不阻塞;而一旦读取剪贴板的跨进程 OLE 调用被挂起,
    /// 客户端侧没有任何超时手段可以打断,只能在发起之前先确认剪贴板可打开。
    /// 仅探测不写入:不调用 EmptyClipboard,不影响剪贴板内容与序列号。
    /// </summary>
    internal static bool TryProbeClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        // 必须无条件关闭:一旦这两步之间抛出异常而没关,本进程就会永久持锁,
        // 表现为全机复制粘贴失效,直到本进程退出。
        try
        {
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ---- 专用 STA 线程的消息泵(剪贴板写入线程用) ----
    // WinRT 的剪贴板调用要求 STA;而 SETCONTENT/延迟渲染的数据是经窗口消息回取到本线程的,
    // 所以这条线程必须同时是一个消息泵,否则别的程序来粘贴时会一直等我们。

    internal const uint WmQuit = 0x0012;
    /// <summary>MsgWaitForMultipleObjectsEx 的失败返回值。</summary>
    internal const uint WaitFailed = 0xFFFFFFFF;
    private const uint PM_REMOVE = 0x0001;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x00000040;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles,
        uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    // RoInitialize 在 Windows 8+ 由 combase.dll 导出(ole32.dll 没有这个入口点,写错会抛
    // EntryPointNotFoundException —— 而它在 STA 初始化路径上,一旦抛出写入线程会直接起不来)。
    [System.Runtime.InteropServices.DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    /// <summary>在当前线程初始化 Windows Runtime(STA)。已初始化时返回的 S_FALSE/RPC_E_CHANGED_MODE 均可忽略。</summary>
    internal static void EnsureWinRtInitialized()
    {
        try
        {
            const int RO_INIT_SINGLETHREADED = 0;
            RoInitialize(RO_INIT_SINGLETHREADED);
        }
        catch
        {
            // 已初始化或不可用时继续:后续 WinRT 调用会自行报错,不影响 UI 线程
        }
    }

    /// <summary>确保当前线程有一个消息队列(仅创建、不取消息)。</summary>
    internal static void EnsureMessageQueue()
    {
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */);
    }

    /// <summary>把当前线程待处理的消息全部处理掉(不阻塞)。</summary>
    internal static void PumpMessages()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == WmQuit) return;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>
    /// 等待"有新工作(事件句柄被置位)"或"有窗口消息到达",最多 timeoutMs 毫秒。
    /// 返回 true 表示可能还需要继续泵消息。
    /// </summary>
    internal static bool WaitForWorkOrMessage(IntPtr handle, uint timeoutMs)
    {
        if (handle == IntPtr.Zero) return false;
        var result = MsgWaitForMultipleObjectsEx(1, new[] { handle }, timeoutMs, QsAllInput, 0);
        if (result == WaitFailed) return false;
        // 0 = 事件被置位, 1 = 有消息到达, 258 = 超时
        return result == 0 || result == 1;
    }

    /// <summary>请求当前线程的消息泵退出。</summary>
    internal static void PostQuitToCurrentThread() =>
        PostThreadMessageW(GetCurrentThreadId(), WmQuit, IntPtr.Zero, IntPtr.Zero);

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
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    private const uint SpiGetForegroundLockTimeout = 0x2000;
    private const uint SpiSetForegroundLockTimeout = 0x2001;

    /// <summary>
    /// 暂时关闭 Windows 的"前台锁定超时"(SPI_SETFOREGROUNDLOCKTIMEOUT = 0)。
    ///
    /// 系统默认会阻止非前台进程抢前台(用户刚操作过别的窗口时尤其明显),
    /// 这会让粘贴前的 SetForegroundWindow 静默失败 —— 表现为"按键发出去了但没粘上"。
    /// Ditto 的 ActivateTarget 就是先把它设为 0、激活目标、再恢复原值。
    ///
    /// 返回原值,调用方用 <see cref="RestoreForegroundLockTimeout"/> 还原。
    /// </summary>
    internal static uint DisableForegroundLockTimeout()
    {
        try
        {
            uint previous = 0;
            if (!SystemParametersInfo(SpiGetForegroundLockTimeout, 0, ref previous, 0)) return 0;
            uint zero = 0;
            SystemParametersInfo(SpiSetForegroundLockTimeout, 0, ref zero, 0);
            return previous;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>恢复前台锁定超时(传 <see cref="DisableForegroundLockTimeout"/> 的返回值)。</summary>
    internal static void RestoreForegroundLockTimeout(uint previous)
    {
        if (previous == 0) return;
        try
        {
            SystemParametersInfo(SpiSetForegroundLockTimeout, 0, ref previous, 0);
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    internal static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;
        var currentThread = GetCurrentThreadId();

        // AttachThreadInput 会把本线程的输入队列挂到目标线程上:目标一旦不泵消息,
        // 本线程也跟着不再泵消息 —— 而消息泵恰恰是本进程响应别人粘贴请求的唯一通道。
        // 所以附加之前必须先确认目标线程还活着;无响应就放弃附加,只做裸 SetForegroundWindow
        // (成功率低一些,但绝不会把调用方拖死)。
        var canAttach = foregroundThread != 0 &&
                        foregroundThread != currentThread &&
                        IsThreadResponsive(foregroundThread);

        var attached = canAttach && AttachThreadInput(currentThread, foregroundThread, true);
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

    /// <summary>
    /// 判断指定线程是否仍在处理消息。用带 150ms 超时 + SMTO_ABORTIFHUNG 的空消息探测:
    /// 线程无响应时立即返回 false(而不是无限等待),这是安全 AttachThreadInput 的前置条件。
    /// </summary>
    internal static bool IsThreadResponsive(uint threadId)
    {
        if (threadId == 0) return false;
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return false;
        // 空闲线程(GUI 结构里没有任何活动窗口/焦点)通常是已退出或不可交互,不应附加
        var probe = info.hwndActive != IntPtr.Zero ? info.hwndActive
                  : info.hwndFocus != IntPtr.Zero ? info.hwndFocus
                  : info.hwndCaret;
        if (probe == IntPtr.Zero) return false;

        var ok = SendMessageTimeout(probe, 0 /* WM_NULL */, UIntPtr.Zero, IntPtr.Zero,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 150, out _);
        return ok != IntPtr.Zero;
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
    internal const uint SMTO_BLOCK = 0x0001;

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
    /// 判断某控件是否值得尝试 WM_PASTE。
    ///
    /// 只在"标准 Win32 编辑控件"上尝试:Ctrl+V 在这些控件里同样有效,
    /// 但 WM_PASTE 少一次全局按键注入,不受修饰键与前台状态影响。
    /// 自绘输入框(Chrome_WidgetWin_*/RichEdit 的复杂变体)一律返回 false,
    /// 让调用方直接走按键注入,避免白白浪费一次跨进程 SendMessage。
    /// </summary>
    internal static bool IsPasteMessageTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        // 判定规则交给共享策略(与单元测试覆盖的规则完全一致)
        return NexClip.Tray.PasteTargetPolicy.ShouldTryPasteMessage(GetWindowClassName(hwnd));
    }

    /// <summary>
    /// 向目标控件直接发送 WM_PASTE(编辑框粘贴)。
    ///
    /// <para><b>注意返回值不可信</b>:实测 <c>RichEditD2DPT</c>(Win11 记事本)收到 WM_PASTE
    /// 后**确实粘贴成功了**,但返回 0。旧实现用"返回值非 0"判断成功,
    /// 于是误判为失败并回退去注入 Ctrl+V —— 目标应用因此粘贴了两次,
    /// 用户看到内容重复(实测复现:内容变为 "XXX XXX")。</para>
    ///
    /// <para>因此判定标准改为**消息是否送达**而不是返回值:
    /// SendMessageTimeout 返回非 0 表示消息已被控件处理(即使它返回 0);
    /// 返回 0 表示超时或窗口无响应,此时才回退按键注入。</para>
    ///
    /// <para>调用方必须先通过 <see cref="IsPasteMessageTarget"/> 确认控件类型,
    /// 只有确定支持 WM_PASTE 的标准编辑控件才走到这里 ——
    /// 对不支持的控件发消息不会粘贴,而我们又没有可靠的"没粘贴"信号,
    /// 那种情况下宁可不用这条路。</para>
    /// </summary>
    internal static bool TryPasteViaMessage(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        var delivered = SendMessageTimeout(
            hwnd, WM_PASTE, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out _);

        // 只有"超时/无响应"才算失败;返回值是否为 0 不作为判断依据(见方法注释)
        return delivered != IntPtr.Zero;
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
    /// 指定窗口所在线程的焦点控件句柄(输入框等);取不到返回 0。
    /// GetGUIThreadInfo 本身支持跨线程/跨进程查询,**不需要** AttachThreadInput
    /// (需要共享输入队列的是 GetFocus/GetCaretPos,不是本函数)。
    /// 这里必须避开 AttachThreadInput:合并输入队列会连带搅乱目标线程的输入法上下文,
    /// detach 之后输入法查不到插入符位置,中文候选栏会跳到屏幕左上角。
    /// </summary>
    internal static IntPtr GetFocusedControlOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return IntPtr.Zero;
        var tid = GetWindowThreadProcessId(window, out _);
        if (tid == 0) return IntPtr.Zero;
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    /// <summary>当前前台窗口内的焦点控件句柄(输入框等);无前台/取不到返回 0。</summary>
    internal static IntPtr GetFocusedControl() => GetFocusedControlOf(GetForegroundWindow());

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

    // ---- 粘贴目标窗口合法性判定 ----

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaCloaked = 14;  // DWMWA_CLOAKED

    /// <summary>读取窗口类名(仅用于日志,失败返回空串)。</summary>
    internal static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new System.Text.StringBuilder(128);
        return GetClassName(hwnd, sb, sb.Capacity) != 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// 该窗口是否是**可见但被 DWM 隐藏**(cloaked)。
    ///
    /// Win10+ 的 UWP、部分商店应用与虚拟桌面里"已切走"的窗口会处于此状态:
    /// IsWindowVisible 返回 true,用户却完全看不见。往这类窗口注入粘贴键没有任何意义。
    /// </summary>
    internal static bool IsWindowCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            // DWMWA_CLOAKED 非 0 表示被遮蔽;旧系统不支持该属性时返回非 0 HRESULT,按"未遮蔽"处理
            var hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch (System.DllNotFoundException)
        {
            return false;
        }
        catch (System.EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>是否带 WS_EX_TOOLWINDOW(输入法候选栏、托盘气泡、屏幕键盘等辅助窗口)。</summary>
    internal static bool IsToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var exStyle = (int)GetWindowLongPtr(hwnd, GwlExStyle);
        return (exStyle & WsExToolWindow) != 0;
    }

    /// <summary>
    /// 判断窗口能否作为粘贴目标。参考 CopyQ 的 isAltTabWindow。
    ///
    /// 规则(任一不满足即拒绝):
    /// 1. 必须是有效窗口且可见;
    /// 2. 不能是 DWM cloaked(用户看不见的窗口);
    /// 3. 不能是工具窗口(辅助 UI,不是用户正在编辑的应用窗口);
    /// 4. 排除几个已知系统壳类(与 CopyQ 一致)。
    ///
    /// 刻意**不**检查"是否有标题":部分正常应用的主窗口标题为空
    /// (例如某些 Electron 应用的初始帧),硬性要求标题会误杀。
    /// </summary>
    internal static bool IsValidPasteTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd)) return false;
        if (IsWindowCloaked(hwnd)) return false;
        if (IsToolWindow(hwnd)) return false;

        // 系统壳类判定交给共享策略(与单元测试覆盖的规则完全一致,避免两处分叉)
        return !NexClip.Tray.PasteTargetPolicy.IsShellWindowClass(GetWindowClassName(hwnd));
    }

    /// <summary>
    /// 验证目标窗口(或其子窗口)是否持有当前输入焦点。
    /// keybd_event/SendInput 注入的按键发给"输入焦点窗口"而非"前台窗口",
    /// 剪贴板窗口隐藏后若不显式释放焦点,注入会发回本窗口导致粘贴失败。
    /// </summary>
    internal static bool IsFocusedInWindow(IntPtr hwnd)
    {
        var focus = GetFocusedControl();
        if (focus == IntPtr.Zero) return false;
        return focus == hwnd || IsChild(hwnd, focus) || IsChild(focus, hwnd);
    }

    /// <summary>
    /// 把焦点精确给到指定控件(跨线程需临时附加输入队列);首次失败小延迟重试一次。
    /// **有副作用**:跨进程 SetFocus 只改 Win32 焦点,目标应用不会重走自己的获焦握手
    /// (TSF 焦点文档不同步),之后输入法取不到插入符矩形,候选栏会退化到屏幕左上角。
    /// 所以只在焦点确实没能自然恢复时兜底调用,能不调就不调。
    /// </summary>
    internal static bool SetFocusTo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var targetTid = GetWindowThreadProcessId(hwnd, out _);
            var curTid = GetCurrentThreadId();
            // 与 ActivateWindow 同理:目标线程无响应时不附加输入队列,否则本线程会一起停摆。
            var canAttach = targetTid != 0 && targetTid != curTid && IsThreadResponsive(targetTid);
            var attached = canAttach && AttachThreadInput(curTid, targetTid, true);
            try
            {
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(curTid, targetTid, false);
            }

            // SetFocus 的返回值是**调用前的焦点窗口**,不是成功标志 ——
            // 原来用 "!= IntPtr.Zero" 判断成功是错的:跨进程窗口(尤其 Chromium 的
            // Chrome_WidgetWin_1)常常返回非零但焦点并没真正过去。
            // 改为用 GetGUIThreadInfo 回读目标线程的真实焦点来验证(与 Ditto 的做法一致)。
            if (IsFocusOn(hwnd, targetTid)) return true;

            // 目标不响应就别再重试:第二次注定同样失败,只会白白多花 30ms。
            if (!canAttach) return false;
            System.Threading.Thread.Sleep(30);
        }
        return false;
    }

    /// <summary>回读目标线程的真实焦点窗口,判断焦点是否真的落在 <paramref name="expected"/> 上。</summary>
    private static bool IsFocusOn(IntPtr expected, uint threadId)
    {
        if (threadId == 0) return false;
        var info = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return false;
        return info.hwndFocus == expected;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    /// <summary>
    /// 临时把窗口设为"永不激活"(WS_EX_NOACTIVATE)。
    ///
    /// 用途:置顶粘贴期间,剪贴板窗口虽然可见,但不应抢走目标窗口的输入焦点。
    /// 这正是 EcoPaste 的核心设计 —— 它的剪贴板窗口常驻 focusable=false,
    /// 于是 show 时不会把前台应用推走,前台焦点始终留在用户正在编辑的窗口,
    /// 注入 Ctrl+V 时按键直接落到目标输入框,无需隐藏窗口、无需强制回焦。
    ///
    /// 只在粘贴期间临时设置,粘贴后**必须**清除(见 RestoreActivatable),
    /// 否则窗口永远无法获得焦点,用户点输入框打不了字。
    /// </summary>
    internal static bool SetNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((ex & WsExNoActivate) != 0) return false;   // 已经是该状态,无需记录"需要恢复"
        SetWindowLongPtr(hwnd, GwlExStyle, ex | WsExNoActivate);
        return true;
    }

    /// <summary>清除 WS_EX_NOACTIVATE(仅在 <see cref="SetNoActivate"/> 返回 true 时调用)。</summary>
    internal static void ClearNoActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        var ex = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((ex & WsExNoActivate) == 0) return;
        SetWindowLongPtr(hwnd, GwlExStyle, ex & ~WsExNoActivate);
    }

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
