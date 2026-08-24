using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NexClip.Desktop.Services;
using System.Windows.Automation;
using Windows.Foundation;
using Windows.Graphics;

namespace NexClip.Desktop;

/// <summary>
/// 剪贴板主窗口:轻量工具窗——无标题栏/无窗口按钮/不占任务栏(WS_EX_TOOLWINDOW);
/// 热键或托盘呼出;**点击外部(失焦)自动隐藏**;关闭 = 隐藏(除非应用退出)。
/// </summary>
public sealed partial class ClipboardWindow : Window
{
    private const double MinWidthDips = 440;
    private const double MinHeightDips = 400;

    private DispatcherQueueTimer? _hideTimer;
    // 点击窗口外部后会延迟隐藏;快捷键在这段时间内再次触发时应直接取消隐藏并呼出窗口,
    // 避免第一次快捷键只完成“待隐藏”状态切换,用户需要再按一次才能看到窗口。
    private bool _hidePending;

    /// <summary>显示保护期:热键/托盘呼出后短暂忽略"假 Deactivated",避免窗口刚显示就被自动隐藏吞掉。</summary>
    private DateTime _showGuardUntil = DateTime.MinValue;

    /// <summary>呼出前的前台窗口(粘贴目标);呼出时记录。</summary>
    private IntPtr _pasteTarget;

    /// <summary>呼出前窗口内的焦点控件(输入框);恢复焦点时精确回焦。</summary>
    private IntPtr _pasteFocus;

    /// <summary>呼出前由 UI Automation 看到的焦点元素;用于 Chromium/Electron 等自绘输入框。</summary>
    private AutomationElement? _pasteAutomationFocus;

    private readonly object _automationFocusSync = new();
    private AutomationElement? _lastExternalAutomationFocus;
    private IntPtr _lastExternalAutomationRoot;

    private bool _isPasting;

    /// <summary>窗口显示完成事件(页面订阅:聚焦列表支持键盘操作)。</summary>
    public event Action? Shown;

    public ClipboardWindow()
    {
        InitializeComponent();
        Title = "NexClip 剪贴板";
        SystemBackdrop = App.CreateBackdrop();
        DisableCaptionDoubleClick();

        // 轻量工具窗:去掉系统标题栏与窗口按钮
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }

        // 尺寸记忆:有记忆用记忆(下限=最小尺寸);首次显示默认宽度 = 最小宽度
        var winSize = App.Services.Settings;
        if (winSize.WindowWidth > 0 && winSize.WindowHeight > 0)
        {
            var w = Math.Max(winSize.WindowWidth, MinWidthDips);
            var h = Math.Max(winSize.WindowHeight, MinHeightDips);
            AppWindow.Resize(new SizeInt32(DipsToPx(w), DipsToPx(h)));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(DipsToPx(MinWidthDips), DipsToPx(600)));
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = DipsToPx(MinWidthDips);
            presenter.PreferredMinimumHeight = DipsToPx(MinHeightDips);
        }

        // 失焦(点击外部)→ 延迟隐藏;200ms 窗口内留给托盘/热键的"切换"语义。
        // 注意:窗口从隐藏恢复显示瞬间会因焦点竞争触发一次"假 Deactivated",
        // 若立即启动隐藏 timer,窗口刚显示就被吞掉(表现为"快捷键第二次失效")。
        // 显示保护期内忽略 Deactivated,保护期过后用户点击外部仍正常自动隐藏。
        Activated += (_, e) =>
        {
            ApplyToolWindowStyle();
            if (e.WindowActivationState == WindowActivationState.Deactivated && !App.IsExiting)
            {
                if (DateTime.UtcNow < _showGuardUntil) return;
                // 置顶时点击外部仅失去焦点,不自动隐藏;用户可继续浏览/操作条目。
                if (IsTopmost) return;
                _hidePending = true;
                EnsureHideTimer();
                _hideTimer!.Start();
            }
        };

        // 关闭按钮 = 隐藏到托盘(退出流程除外);关闭/退出前记忆当前窗口尺寸
        AppWindow.Closing += (_, e) =>
        {
            PersistWindowSize();
            if (!App.IsExiting && App.Services.Settings.CloseToTray)
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => SetupDragRegions();
        }

        Automation.AddAutomationFocusChangedEventHandler(TrackExternalAutomationFocus);
        TryTrackCurrentAutomationFocus();
    }

    // ---- 禁用标题栏双击最大化(ExtendsContentIntoTitleBar 的系统默认行为) ----
    // 子类化窗口过程吞掉 WM_NCLBUTTONDBLCLK;静态持有委托避免被 GC 回收。
    private static IntPtr _origWndProc = IntPtr.Zero;
    private static readonly NativeMethods.WndProc NoDblClickWndProc = (hwnd, msg, wParam, lParam) =>
    {
        if (msg == NativeMethods.WmNcLButtonDblClk) return IntPtr.Zero;
        return NativeMethods.CallWindowProc(_origWndProc, hwnd, msg, wParam, lParam);
    };

    private void DisableCaptionDoubleClick()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var procPtr = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(NoDblClickWndProc);
            var old = (IntPtr)NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlWndproc, procPtr.ToInt64());
            if (old != IntPtr.Zero) _origWndProc = old;
        }
        catch (Exception ex)
        {
            Log.Error("禁用标题栏双击最大化失败", ex);
        }
    }

    /// <summary>不占任务栏 / 不出现在 Alt-Tab(工具窗样式)。在激活时重申,避免被框架覆盖。</summary>
    private void ApplyToolWindowStyle()    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        if ((ex & NativeMethods.WsExToolwindow) == 0 || (ex & NativeMethods.WsExAppwindow) != 0)
        {
            ex |= NativeMethods.WsExToolwindow;
            ex &= ~NativeMethods.WsExAppwindow;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, ex);
        }
    }

    private void EnsureHideTimer()
    {
        if (_hideTimer is not null) return;
        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(50);
        _hideTimer.Tick += (_, _) =>
        {
            _hidePending = false;
            _hideTimer?.Stop();
            if (!App.IsExiting)
            {
                PersistWindowSize();
                AppWindow.Hide();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                NativeMethods.ShowWindow(hwnd, 0 /* SW_HIDE */);
            }
        };
    }

    /// <summary>当前是否置顶(始终在最前)。</summary>
    public bool IsTopmost { get; private set; }

    /// <summary>置顶状态变化(on 置顶 / off 取消)。</summary>
    public event Action<bool>? TopmostChanged;

    /// <summary>切换窗口置顶(始终在最前)。</summary>
    public void ToggleTopmost()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var topmost = !IsTopmost;
            // 置顶需要改变 Z 序:不能传 SWP_NOZORDER(0x0004)
            NativeMethods.SetWindowPos(
                hwnd,
                topmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNotTopmost,
                0, 0, 0, 0,
                0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */);
            IsTopmost = topmost;
            TopmostChanged?.Invoke(topmost);
        }
        catch (Exception ex)
        {
            Log.Error("切换置顶失败", ex);
        }
    }

    /// <summary>切换显示/隐藏(托盘左键/双击/热键):取消挂起的失焦隐藏,再按当前状态切换。</summary>
    public void ToggleVisibility()
    {
        var hidePending = _hidePending;
        _hideTimer?.Stop();
        _hidePending = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var isVisible = NativeMethods.IsWindowVisible(hwnd);
            var isForeground = NativeMethods.GetForegroundWindow() == hwnd;

            // 只有当窗口已可见且处于当前前台获得焦点时，按热键才是“收起/隐藏”；
            // 若窗口已隐藏、或处于失焦/待隐藏/后台状态，按热键一律直接“呼出并激活”。
            var shouldShow = !isVisible || !isForeground || hidePending;
            if (!shouldShow)
            {
                PersistWindowSize();
                AppWindow.Hide();
                NativeMethods.ShowWindow(hwnd, 0 /* SW_HIDE */);
                // 热键/托盘切换隐藏:把焦点还给呼出前的窗口与输入框,避免聚焦状态丢失
                RestorePreviousFocus();
            }
            else
            {
                // 显示保护期:窗口从隐藏恢复时会有瞬时焦点竞争,忽略随后极短时间内的假 Deactivated (150ms 足够)
                _showGuardUntil = DateTime.UtcNow.AddMilliseconds(150);
                CapturePasteTarget();
                PositionAtShow();
                AppWindow.Show();
                Activate();
                // 热键/托盘回调上下文里 Activate 可能被"前台锁定"拒绝;
                // 此处强制置顶前台(无按键注入,避免 Alt 模拟破坏 Chromium 页面焦点)。
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    NativeMethods.ShowWindow(hwnd, 5 /* SW_SHOW */);
                    NativeMethods.ActivateWindow(hwnd);
                }
                Shown?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换剪贴板窗口失败", ex);
        }
    }

    /// <summary>显式显示并激活剪贴板窗口(如应用启动/二次启动/唤醒时)。</summary>
    public void ShowWindow()
    {
        _hideTimer?.Stop();
        _hidePending = false;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _showGuardUntil = DateTime.UtcNow.AddMilliseconds(150);
            CapturePasteTarget();
            PositionAtShow();
            AppWindow.Show();
            Activate();
            NativeMethods.ShowWindow(hwnd, 5 /* SW_SHOW */);
            NativeMethods.SetForegroundWindow(hwnd);
            Shown?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("显示剪贴板窗口失败", ex);
        }
    }

    /// <summary>在本窗口抢走前台之前保存粘贴目标及其精确焦点。</summary>
    public void CapturePasteTarget()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var target = NativeMethods.GetRootWindow(NativeMethods.GetForegroundWindow());
        if (target == IntPtr.Zero || target == hwnd || !NativeMethods.IsWindow(target)) return;

        _pasteTarget = target;
        _pasteFocus = NativeMethods.GetFocusedControl();
        _pasteAutomationFocus = null;
        try
        {
            var capturedFocus = CaptureAutomationFocus(target);
            if (IsTransientBrowserHotkeyFocus(capturedFocus) &&
                TryGetTrackedAutomationFocus(target, out var trackedFocus))
            {
                capturedFocus = trackedFocus;
                Log.Debug($"粘贴目标:使用热键前缓存焦点 {DescribeAutomationElement(capturedFocus)}");
            }
            _pasteAutomationFocus = capturedFocus;
        }
        catch (Exception ex)
        {
            Log.Debug($"捕获 UIA 焦点失败:{ex.Message}");
        }

        Log.Debug($"粘贴目标已捕获:target={_pasteTarget}, focus={_pasteFocus}, uia={DescribeAutomationElement(_pasteAutomationFocus)}");
    }

    private static AutomationElement? CaptureAutomationFocus(IntPtr target)
    {
        var root = AutomationElement.FromHandle(target);
        var focusedDescendant = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true),
                new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)));
        if (focusedDescendant is not null) return focusedDescendant;

        var focusedElement = AutomationElement.FocusedElement;
        return focusedElement is not null && focusedElement.Current.IsKeyboardFocusable
            ? focusedElement
            : null;
    }

    private void TryTrackCurrentAutomationFocus()
    {
        try
        {
            if (AutomationElement.FocusedElement is { } focused)
            {
                TrackExternalAutomationFocus(focused, new AutomationFocusChangedEventArgs(0, 0));
            }
        }
        catch
        {
            // 初始焦点可能在受保护或正在退出的进程中。
        }
    }

    private void TrackExternalAutomationFocus(object sender, AutomationFocusChangedEventArgs _)
    {
        if (sender is not AutomationElement element) return;
        try
        {
            if (element.Current.ProcessId == Environment.ProcessId ||
                !element.Current.IsKeyboardFocusable ||
                IsTransientBrowserHotkeyFocus(element))
            {
                return;
            }

            var root = GetAutomationRootWindow(element);
            if (root == IntPtr.Zero) return;
            lock (_automationFocusSync)
            {
                _lastExternalAutomationFocus = element;
                _lastExternalAutomationRoot = root;
            }
        }
        catch
        {
            // UIA 元素会随目标应用导航/退出失效,下次焦点事件会刷新缓存。
        }
    }

    private bool TryGetTrackedAutomationFocus(IntPtr target, out AutomationElement? element)
    {
        lock (_automationFocusSync)
        {
            if (_lastExternalAutomationFocus is not null &&
                NativeMethods.IsSameRootWindow(_lastExternalAutomationRoot, target))
            {
                element = _lastExternalAutomationFocus;
                return true;
            }
        }
        element = null;
        return false;
    }

    private static bool IsTransientBrowserHotkeyFocus(AutomationElement? element)
    {
        if (element is null) return false;
        try
        {
            return element.Current.ClassName == "BrowserAppMenuButton";
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr GetAutomationRootWindow(AutomationElement element)
    {
        var current = element;
        var root = IntPtr.Zero;
        for (var depth = 0; current is not null && depth < 64; depth++)
        {
            var nativeHandle = current.Current.NativeWindowHandle;
            if (nativeHandle != 0) root = NativeMethods.GetRootWindow(new IntPtr(nativeHandle));
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return root;
    }

    private static string DescribeAutomationElement(AutomationElement? element)
    {
        if (element is null) return "none";
        try
        {
            return $"{element.Current.ControlType.ProgrammaticName}/{element.Current.ClassName}" +
                   $"/focusable={element.Current.IsKeyboardFocusable}/focused={element.Current.HasKeyboardFocus}";
        }
        catch
        {
            return "unavailable";
        }
    }

    /// <summary>把前台与焦点还给呼出前的窗口/输入框(隐藏路径用,粘贴路径自带)。</summary>
    private void RestorePreviousFocus()
    {
        try
        {
            var target = _pasteTarget;
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;
            if (!NativeMethods.IsSameRootWindow(NativeMethods.GetForegroundWindow(), target))
            {
                NativeMethods.ActivateWindow(target);
            }
            TryRestorePasteFocus(target, _pasteFocus, _pasteAutomationFocus);
        }
        catch (Exception ex)
        {
            Log.Error("恢复呼出前焦点失败", ex);
        }
    }

    /// <summary>
    /// 粘贴条目到呼出前的窗口。主路径对齐 PastePaw:写剪贴板 → 隐藏选择窗
    /// → 等 Windows 自然恢复原窗口及输入焦点 → SendInput。自然恢复失败时使用 Ditto 的
    /// 激活目标 + 精确回焦方案兜底,且仅在目标确实成为前台后注入按键。
    /// 双击条目 / 列表选中后按回车 触发。
    /// </summary>
    public async Task PasteItemAsync(ViewModels.HistoryItemViewModel vm)
    {
        if (_isPasting) return;
        _isPasting = true;
        try
        {
            var engine = App.Services.Engine;
            if (engine is null) return;
            _hideTimer?.Stop();
            var target = _pasteTarget;
            var focus = _pasteFocus;
            var automationFocus = _pasteAutomationFocus;
            Log.Debug($"粘贴开始:id={vm.Item.Id}, target={target}, focus={focus}, uia={automationFocus is not null}");
            await engine.CopyHistoryItemAsync(vm.Item);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.AllKeysUp();
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
            {
                Log.Warn($"粘贴:呼出前窗口已失效 target={target}");
                return;
            }

            // PastePaw 的可靠路径:先隐藏选择窗,让 Windows 按原激活顺序自然恢复目标窗口及其内部焦点。
            // 对 Chromium/Electron 尤其重要:仅 SetForegroundWindow 能恢复顶层窗口,不能保证 DOM 输入框回焦。
            AppWindow.Hide();
            NativeMethods.ShowWindow(hwnd, 0 /* SW_HIDE */);
            _hidePending = false;
            var restoredNaturally = await WaitForForegroundAsync(target, 300);

            // 自然恢复失败时再使用 Ditto 的显式激活方案。
            var activated = false;
            if (!restoredNaturally)
            {
                activated = NativeMethods.ActivateWindow(target);
                await WaitForForegroundAsync(target, 700);
            }

            // 顶层窗口恢复不代表 Chromium/Electron 内部的 DOM 编辑框已恢复。
            // 无论走自然恢复还是显式激活,都恢复呼出时捕获的真实 UIA/Win32 焦点。
            var focusRestored = TryRestorePasteFocus(target, focus, automationFocus);
            await WaitForForegroundAsync(target, 300);
            await Task.Delay(100);
            var foregroundAtInject = NativeMethods.GetForegroundWindow();
            if (!NativeMethods.IsSameRootWindow(foregroundAtInject, target))
            {
                Log.Warn($"粘贴:目标未成为前台,取消按键注入 target={target}, fg={foregroundAtInject}");
                return;
            }

            // 成熟剪贴板管理器会按目标应用选择粘贴键。Chromium/Electron 的
            // contenteditable/ProseMirror 对 Ctrl+V 更可靠,不能套用全局 Shift+Insert。
            var forceCtrlV = NativeMethods.IsChromiumWindow(target);
            var useCtrlV = forceCtrlV || App.Services.Settings.PasteKey == "CtrlV";
            var keyName = useCtrlV ? "Ctrl+V" : "Shift+Insert";
            var strategy = forceCtrlV ? "chromium" : "configured";
            var injected = useCtrlV
                ? NativeMethods.SendInputCtrlV()
                : NativeMethods.SendInputShiftInsert();
            Log.Debug($"粘贴:SendInput {keyName} (strategy={strategy}, natural={restoredNaturally}, activated={activated}, focusRestored={focusRestored}, injected={injected})");
        }
        catch (Exception ex)
        {
            Log.Error("粘贴条目失败", ex);
        }
        finally
        {
            _isPasting = false;
        }
    }

    private static async Task<bool> WaitForForegroundAsync(IntPtr target, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        DateTime? stableSince = null;
        while (DateTime.UtcNow < deadline)
        {
            if (NativeMethods.IsSameRootWindow(NativeMethods.GetForegroundWindow(), target))
            {
                stableSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stableSince.Value >= TimeSpan.FromMilliseconds(150)) return true;
            }
            else
            {
                stableSince = null;
            }
            await Task.Delay(15);
        }
        return false;
    }

    private static bool TryRestorePasteFocus(
        IntPtr target,
        IntPtr focus,
        AutomationElement? automationFocus)
    {
        if (automationFocus is not null)
        {
            try
            {
                automationFocus.SetFocus();
                return NativeMethods.IsSameRootWindow(NativeMethods.GetForegroundWindow(), target);
            }
            catch (Exception ex)
            {
                Log.Debug($"恢复 UIA 焦点失败:{ex.Message}");
            }
        }

        var focusTarget = focus;
        if (focusTarget == IntPtr.Zero || focusTarget == target)
        {
            var renderWidget = NativeMethods.FindRenderWidgetChild(target);
            if (renderWidget != IntPtr.Zero) focusTarget = renderWidget;
        }
        return NativeMethods.SetFocusTo(focusTarget);
    }

    /// <summary>
    /// 按设置定位窗口(呼出时):center=屏幕中心(光标所在屏幕),cursor=跟随鼠标(光标右下方)。
    /// 定位在 Show 之前执行,避免旧位置闪现。
    /// </summary>
    private void PositionAtShow()
    {
        try
        {
            var work = GetCursorWorkArea();
            if (work is null) return;

            var wDips = Math.Max(App.Services.Settings.WindowWidth, MinWidthDips);
            var hDips = Math.Max(App.Services.Settings.WindowHeight, MinHeightDips);
            var widthPx = Math.Max(DipsToPx(wDips), DipsToPx(MinWidthDips));
            var heightPx = Math.Max(DipsToPx(hDips), DipsToPx(MinHeightDips));

            AppWindow.Resize(new SizeInt32(widthPx, heightPx));

            int x, y;
            if (App.Services.Settings.WindowPositionMode == "center")
            {
                x = work.Value.Left + (work.Value.Right - work.Value.Left - widthPx) / 2;
                y = work.Value.Top + (work.Value.Bottom - work.Value.Top - heightPx) / 2;
            }
            else
            {
                const int offset = 16;
                if (!NativeMethods.GetCursorPos(out var cursor))
                {
                    cursor = new NativeMethods.POINT { X = work.Value.Left + 100, Y = work.Value.Top + 100 };
                }
                x = cursor.X + offset;
                y = cursor.Y + offset;
                // 防止超出屏幕右下边缘(光标靠近角落时窗口整体回移)
                if (x + widthPx > work.Value.Right) x = work.Value.Right - widthPx;
                if (y + heightPx > work.Value.Bottom) y = work.Value.Bottom - heightPx;
                if (x < work.Value.Left) x = work.Value.Left;
                if (y < work.Value.Top) y = work.Value.Top;
            }
            AppWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            Log.Error("定位剪贴板窗口失败", ex);
        }
    }

    /// <summary>光标所在监视器的工作区(排除任务栏);失败回退主屏工作区。</summary>
    private NativeMethods.RECT? GetCursorWorkArea()
    {
        try
        {
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                var hMon = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
                if (hMon != IntPtr.Zero)
                {
                    var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                    if (NativeMethods.GetMonitorInfo(hMon, ref info)) return info.rcWork;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("获取光标所在屏幕失败", ex);
        }
        // 回退:主屏(GetSystemMetrics 不含工作区概念,直接用屏幕尺寸)
        int sw = NativeMethods.GetSystemMetrics(0 /* SM_CXSCREEN */);
        int sh = NativeMethods.GetSystemMetrics(1 /* SM_CYSCREEN */);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh };
    }

    /// <summary>顶栏作为拖拽区,搜索框与按钮区域标记为穿透(可点击/输入)。</summary>
    private void SetupDragRegions()
    {
        if (Content is not FrameworkElement root) return;
        var topBar = root.FindName("TopBar") as FrameworkElement;
        if (topBar is null) return;

        SetTitleBar(topBar);
        UpdatePassthrough(root);
        topBar.SizeChanged += (_, _) => UpdatePassthrough(root);
    }

    /// <summary>把顶栏内的交互控件(搜索框 + 按钮)区域设为点击穿透,拖拽区其余部分仍可拖动窗口。</summary>
    private void UpdatePassthrough(FrameworkElement root)
    {
        var scale = root.XamlRoot.RasterizationScale;
        var regions = new List<RectInt32>();
        foreach (var name in new[] { "SearchBox", "TopButtons" })
        {
            if (root.FindName(name) is FrameworkElement el &&
                el.ActualWidth > 0 && el.ActualHeight > 0)
            {
                var transform = el.TransformToVisual(root);
                var point = transform.TransformPoint(new Point(0, 0));
                regions.Add(new RectInt32(
                    (int)(point.X * scale),
                    (int)(point.Y * scale),
                    (int)(el.ActualWidth * scale),
                    (int)(el.ActualHeight * scale)));
            }
        }
        if (regions.Count > 0)
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, regions.ToArray());
        }
    }

    /// <summary>记忆当前窗口尺寸(隐藏/关闭前调用,含 DPI 换算回 dips 存储)。</summary>
    private void PersistWindowSize()
    {
        try
        {
            if (!AppWindow.IsVisible) return;
            var wDips = PxToDips(AppWindow.Size.Width);
            var hDips = PxToDips(AppWindow.Size.Height);
            if (wDips < MinWidthDips || hDips < MinHeightDips) return;

            var s = App.Services.Settings;
            s.WindowWidth = wDips;
            s.WindowHeight = hDips;
            s.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("记忆窗口尺寸失败:" + ex.Message);
        }
    }

    private double PxToDips(int px)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? px * 96.0 / dpi : px;
    }

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
