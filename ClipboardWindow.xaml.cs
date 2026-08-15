using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;
using Windows.Foundation;
using Windows.Graphics;

namespace SyncClipboard.Desktop;

/// <summary>
/// 剪贴板主窗口:轻量工具窗——无标题栏/无窗口按钮/不占任务栏(WS_EX_TOOLWINDOW);
/// 热键或托盘呼出;**点击外部(失焦)自动隐藏**;关闭 = 隐藏(除非应用退出)。
/// </summary>
public sealed partial class ClipboardWindow : Window
{
    private const double MinWidthDips = 440;
    private const double MinHeightDips = 400;

    private DispatcherQueueTimer? _hideTimer;

    /// <summary>显示保护期:热键/托盘呼出后短暂忽略"假 Deactivated",避免窗口刚显示就被自动隐藏吞掉。</summary>
    private DateTime _showGuardUntil = DateTime.MinValue;

    public ClipboardWindow()
    {
        InitializeComponent();
        Title = "SyncClipboard 剪贴板";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // 轻量工具窗:去掉系统标题栏与窗口按钮
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }

        AppWindow.Resize(new SizeInt32(DipsToPx(540), DipsToPx(600)));
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
                EnsureHideTimer();
                _hideTimer!.Start();
            }
        };

        // 关闭按钮 = 隐藏到托盘(退出流程除外)
        AppWindow.Closing += (_, e) =>
        {
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
    }

    /// <summary>不占任务栏 / 不出现在 Alt-Tab(工具窗样式)。在激活时重申,避免被框架覆盖。</summary>
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

    private void EnsureHideTimer()
    {
        if (_hideTimer is not null) return;
        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(200);
        _hideTimer.Tick += (_, _) =>
        {
            if (!App.IsExiting && AppWindow.IsVisible)
            {
                AppWindow.Hide();
            }
        };
    }

    /// <summary>切换显示/隐藏(托盘左键/双击/热键):取消挂起的失焦隐藏,再按当前状态切换。</summary>
    public void ToggleVisibility()
    {
        _hideTimer?.Stop();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                AppWindow.Hide();
            }
            else
            {
                // 显示保护期:窗口从隐藏恢复时会有焦点竞争,忽略随后短时间内的假 Deactivated
                _showGuardUntil = DateTime.UtcNow.AddMilliseconds(600);
                AppWindow.Show();
                Activate();
                // 热键/托盘回调上下文里 Activate 可能被"前台锁定"拒绝;
                // 窗口未获得焦点则失焦自动隐藏不会触发(窗口会一直挂着,再按热键变成隐藏,用户感觉"失效")。
                // 此处强制置顶前台,确保 Deactivated -> 自动隐藏链路工作。
                if (NativeMethods.GetForegroundWindow() != hwnd)
                {
                    NativeMethods.ShowWindow(hwnd, 5 /* SW_SHOW */);
                    NativeMethods.ForceForeground(hwnd);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换剪贴板窗口失败", ex);
        }
    }

    /// <summary>顶栏作为拖拽区,按钮区域标记为穿透(可点击)。</summary>
    private void SetupDragRegions()
    {
        if (Content is not FrameworkElement root) return;
        var topBar = root.FindName("TopBar") as FrameworkElement;
        var topButtons = root.FindName("TopButtons") as FrameworkElement;
        if (topBar is null || topButtons is null) return;

        SetTitleBar(topBar);
        UpdatePassthrough(topButtons, root);
        topBar.SizeChanged += (_, _) => UpdatePassthrough(topButtons, root);
    }

    private void UpdatePassthrough(FrameworkElement buttons, FrameworkElement root)
    {
        var transform = buttons.TransformToVisual(root);
        var point = transform.TransformPoint(new Point(0, 0));
        var scale = root.XamlRoot.RasterizationScale;
        var rect = new RectInt32(
            (int)(point.X * scale),
            (int)(point.Y * scale),
            (int)(buttons.ActualWidth * scale),
            (int)(buttons.ActualHeight * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
    }

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
