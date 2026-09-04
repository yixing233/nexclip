using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NexClip.Desktop.Services;
using Windows.Graphics;

namespace NexClip.Desktop;

/// <summary>
/// 复制直达智能浮窗：展示智能识别结果（色彩预览、文件路径、GitHub 仓库、网盘提取码、网址等），
/// 提供全局热键直达与鼠标直达操作。5 秒自动关闭，无边框轻量浮窗。
/// 显示时不抢占前台焦点，因此不会把正在浏览的剪贴板窗口顶掉。
/// </summary>
public sealed partial class LinkToastWindow : Window
{
    private const double CardWidthDips = 450;
    private const double CardMinHeightDips = 160;
    private readonly SmartAction _action;
    private DispatcherQueueTimer? _autoClose;
    private int _remainingSeconds = 5;
    private bool _closed;

    public SmartAction Action => _action;

    public LinkToastWindow(string url) : this(SmartActionService.Detect(url) ?? new SmartAction
    {
        Kind = SmartActionKind.Url,
        Title = "已复制链接",
        Subtitle = url,
        Icon = Lucide.ExternalLink,
        PrimaryButtonText = "打开链接",
        PrimaryButtonIcon = Lucide.ExternalLinkWhite,
        PrimaryAction = () =>
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Log.Error($"打开链接失败: {url}", ex);
            }
        }
    })
    {
    }

    public LinkToastWindow(SmartAction action)
    {
        InitializeComponent();
        SystemBackdrop = App.CreateBackdrop();
        _action = action;

        // 1. 顶栏信息与右上角倒计时/快捷键胶囊
        ClockIcon.Source = Lucide.Clock;
        TypeIcon.Source = action.Icon;
        TitleText.Text = action.Title;
        // 键盘直达提示：仅当全局热键确实注册成功时才宣传，否则只提示鼠标点击
        var hotkey = App.Services.Settings.HotkeyOpenUrl;
        var hotkeyReady = App.HotkeyOpenUrl is { IsRegistered: true } && !string.IsNullOrWhiteSpace(hotkey);
        OpenHotkeyText.Text = hotkeyReady ? $"{hotkey} 直达" : "点击直达";
        CountdownBadgeText.Text = $"{_remainingSeconds}s";

        // 2. 中部内容区
        SubtitleText.Text = action.Subtitle ?? "";

        if (action.PreviewColor.HasValue)
        {
            ColorPreviewBorder.Background = new SolidColorBrush(action.PreviewColor.Value);
            ColorPreviewBorder.Visibility = Visibility.Visible;
        }
        else
        {
            ColorPreviewBorder.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(action.ExtractionCode))
        {
            ExtractionCodeText.Text = $"提取码: {action.ExtractionCode}";
            ExtractionCodePill.Visibility = Visibility.Visible;
        }
        else
        {
            ExtractionCodePill.Visibility = Visibility.Collapsed;
        }

        // 3. 主要操作按钮 (亮蓝实底采用高对比度白色图标)
        // 键盘直达提示统一放在右上角胶囊：底栏是不换行水平布局，标签再拼热键会挤掉次要按钮
        PrimaryButtonLabel.Text = action.PrimaryButtonText;
        PrimaryButtonImage.Source = Lucide.GetWhiteVariant(action.PrimaryButtonIcon ?? action.Icon);

        // 4. 次要操作按钮
        if (!string.IsNullOrEmpty(action.SecondaryButtonText) && action.SecondaryAction != null)
        {
            SecondaryButtonLabel.Text = action.SecondaryButtonText;
            SecondaryButtonImage.Source = action.SecondaryButtonIcon ?? Lucide.Copy;
            SecondaryActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            SecondaryActionButton.Visibility = Visibility.Collapsed;
        }

        // 轻量浮窗：去标题栏、不占任务栏、不进 Alt+Tab、置顶
        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
        }
        ApplyToolWindowStyle();
        Activated += (_, _) => ApplyToolWindowStyle();
        // 显示前就按内容真实高度定位：避让分支依赖真实高度，
        // 若先用最小高度定位一次，下一帧重算会让浮窗跳位置。
        ResizeToContent();

        // 显示浮窗：以"不激活"方式呈现，避免抢走当前前台窗口的焦点。
        // AppWindow.Show() 的无参重载等价于 Show(true)，会激活窗口；
        // 那会让正在前台的剪贴板窗口收到 Deactivated 而被失焦自动隐藏，
        // 表现为"复制后直达浮窗把剪贴板窗口直接挤掉"。
        ShowWithoutActivation();

        // 首轮布局完成后按内容实际高度调整窗口尺寸并重新精准锚定
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ResizeToContent);

        // 5 秒自动关闭 (带秒级动态倒计时与悬停暂停)
        _autoClose = DispatcherQueue.CreateTimer();
        _autoClose.Interval = TimeSpan.FromSeconds(1);
        _autoClose.IsRepeating = true;
        _autoClose.Tick += OnAutoCloseTick;
        _autoClose.Start();

        // 窗口关闭时必须停止并解绑倒计时定时器：否则已关闭窗口会被定时器持续引用而无法回收，
        // 且 Tick 内访问已销毁的 XAML 元素会反复抛异常，造成内存与 CPU 持续占用。
        Closed += OnWindowClosed;
    }

    /// <summary>倒计时 Tick：窗口已关闭时立即自停，避免访问已销毁元素。</summary>
    private void OnAutoCloseTick(DispatcherQueueTimer sender, object args)
    {
        if (_closed)
        {
            sender.Stop();
            return;
        }

        try
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                sender.Stop();
                Close();
                return;
            }
            CountdownBadgeText.Text = $"{_remainingSeconds}s";
        }
        catch
        {
            // 窗口已销毁或元素不可访问时停止计时，防止异常反复抛出
            sender.Stop();
        }
    }

    /// <summary>释放浮窗持有的定时器与静态引用，确保关闭后可被 GC 回收。</summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Closed -= OnWindowClosed;

        if (_autoClose is { } timer)
        {
            timer.Tick -= OnAutoCloseTick;
            timer.Stop();
            _autoClose = null;
        }

        App.ReleaseLinkToast(this);

        // 焦点若曾被本浮窗接走且没还回剪贴板窗口，此处让它重新走一遍失焦隐藏判定，
        // 避免剪贴板窗口以"可见但在后台"的状态长期滞留（同时也会恢复隐藏后的内存回收）。
        App.ClipboardWindow?.RequestHideIfBackground();
    }

    /// <summary>不占任务栏 / 不出现在 Alt-Tab (工具窗样式)。</summary>
    private void ApplyToolWindowStyle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        if ((ex & NativeMethods.WsExToolwindow) == 0 || (ex & NativeMethods.WsExAppwindow) != 0)
        {
            ex |= NativeMethods.WsExToolwindow;
            ex &= ~NativeMethods.WsExAppwindow;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, ex);
        }
    }

    /// <summary>以"不激活"方式显示浮窗并保持置顶：当前前台窗口(如剪贴板窗口)的焦点不受影响。</summary>
    private void ShowWithoutActivation()
    {
        try
        {
            AppWindow.Show(false);
        }
        catch
        {
            // 运行时不支持带参重载时退回默认显示：宁可短暂抢焦点，也不能让浮窗不显示
            AppWindow.Show();
        }

        try
        {
            // 置顶但不激活：SWP_NOACTIVATE 保证 Z 序提升的同时前台焦点留在原窗口
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTopmost,
                0, 0, 0, 0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoMove | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }
        catch (Exception ex)
        {
            Log.Error("浮窗置顶显示失败", ex);
        }
    }

    /// <summary>按固定宽度测量卡片内容的真实所需高度并自适应窗口尺寸。</summary>
    private void ResizeToContent()
    {
        var desiredDips = CardMinHeightDips;
        try
        {
            // 构造期首次调用时布局树尚未加载，Measure 可能失败或返回 0，此时按最小高度处理
            RootGrid.Measure(new Windows.Foundation.Size(CardWidthDips, double.PositiveInfinity));
            desiredDips = Math.Max(RootGrid.DesiredSize.Height + 12, CardMinHeightDips);
        }
        catch (Exception ex)
        {
            Log.Debug($"测量浮窗内容高度失败，按最小高度显示: {ex.Message}");
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var w = (int)Math.Ceiling(CardWidthDips * scale);
        var h = (int)Math.Ceiling(desiredDips * scale);
        AppWindow.Resize(new SizeInt32(w, h));
        MoveToAnchor(w, h, scale);
    }

    /// <summary>
    /// 锚定浮窗位置：默认贴屏幕右下角；若与正在显示的剪贴板窗口重叠，
    /// 则依次尝试"移到其上方 → 移到其左侧"，避免遮挡用户正在浏览的列表。
    /// 两侧都放不下时保持右下角原位：贴右上角会压住剪贴板窗口的搜索框与顶栏按钮，比压住列表底角更糟。
    /// </summary>
    private void MoveToAnchor(int w, int h, double scale)
    {
        var work = GetWorkArea();
        var margin = (int)Math.Round(16 * scale);
        var x = work.Right - w - margin;
        var y = work.Bottom - h - margin;

        if (GetVisibleClipboardWindowRect() is { } main && Intersects(x, y, w, h, main))
        {
            var above = main.Top - h - margin;
            var left = main.Left - w - margin;
            if (above >= work.Top)
            {
                y = above;
            }
            else if (left >= work.Left)
            {
                x = left;
            }
        }

        AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>当前可见的剪贴板窗口屏幕矩形；窗口未创建或已隐藏时返回 null。</summary>
    private static NativeMethods.RECT? GetVisibleClipboardWindowRect()
    {
        try
        {
            if (App.ClipboardWindow is not { } window) return null;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd)) return null;
            return NativeMethods.GetWindowRect(hwnd, out var rect) ? rect : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Intersects(int x, int y, int w, int h, NativeMethods.RECT other)
        => x < other.Right && x + w > other.Left && y < other.Bottom && y + h > other.Top;

    private NativeMethods.RECT GetWorkArea()
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
        catch { }

        int sw = NativeMethods.GetSystemMetrics(0);
        int sh = NativeMethods.GetSystemMetrics(1);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh - DipsToPx(48) };
    }

    public void ExecutePrimaryAction()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _action.PrimaryAction();
        }
        catch (Exception ex)
        {
            Log.Error("执行主直达动作失败", ex);
        }
        finally
        {
            // 动作抛异常时也必须关闭：否则浮窗永久留在屏幕上，且热键因 _closed 变哑
            Close();
        }
    }

    public void ExecuteSecondaryAction()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _action.SecondaryAction?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("执行次要直达动作失败", ex);
        }
        finally
        {
            Close();
        }
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e) => ExecutePrimaryAction();

    private void SecondaryAction_Click(object sender, RoutedEventArgs e) => ExecuteSecondaryAction();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_closed) return;
        _autoClose?.Stop();
        CountdownBadgeText.Text = "已暂停";
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_closed) return;
        if (_remainingSeconds > 0)
        {
            CountdownBadgeText.Text = $"{_remainingSeconds}s";
            _autoClose?.Start();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            ExecutePrimaryAction();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
