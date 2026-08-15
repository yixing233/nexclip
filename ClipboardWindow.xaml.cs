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

        // 失焦(点击外部)→ 延迟隐藏;200ms 窗口内留给托盘/热键的"切换"语义
        Activated += (_, e) =>
        {
            ApplyToolWindowStyle();
            if (e.WindowActivationState == WindowActivationState.Deactivated && !App.IsExiting)
            {
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
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else
        {
            AppWindow.Show();
            Activate();
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
