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
/// 提供键盘流（Enter 默认执行、Esc 忽略）与鼠标直达操作。5 秒自动关闭，无边框轻量浮窗。
/// </summary>
public sealed partial class LinkToastWindow : Window
{
    private const double CardWidthDips = 450;
    private const double CardMinHeightDips = 160;
    private readonly SmartAction _action;
    private DispatcherQueueTimer? _autoClose;
    private int _remainingSeconds = 5;

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
        var hotkey = App.Services.Settings.HotkeyOpenUrl;
        OpenHotkeyText.Text = string.IsNullOrWhiteSpace(hotkey) ? "Enter 直达" : $"{hotkey} 直达";
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

        // 3. 主要操作按钮 (Enter 默认执行，亮蓝实底采用高对比度白色图标)
        PrimaryButtonLabel.Text = $"{action.PrimaryButtonText} (Enter)";
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
        PositionBottomRight();

        // 显示浮窗 (不抢占焦点)
        AppWindow.Show();

        // 首轮布局完成后按内容实际高度调整窗口尺寸并重新精准锚定
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ResizeToContent);

        // 5 秒自动关闭 (带秒级动态倒计时与悬停暂停)
        _autoClose = DispatcherQueue.CreateTimer();
        _autoClose.Interval = TimeSpan.FromSeconds(1);
        _autoClose.IsRepeating = true;
        _autoClose.Tick += (_, _) =>
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                _autoClose.Stop();
                Close();
                return;
            }
            CountdownBadgeText.Text = $"{_remainingSeconds}s";
        };
        _autoClose.Start();
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

    /// <summary>定位到屏幕右下角 (避开任务栏)。</summary>
    private void PositionBottomRight()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var w = (int)Math.Ceiling(CardWidthDips * scale);
        var h = (int)Math.Ceiling(CardMinHeightDips * scale);
        AppWindow.Resize(new SizeInt32(w, h));

        var work = GetWorkArea();
        var margin = (int)Math.Round(16 * scale);
        AppWindow.Move(new PointInt32(work.Right - w - margin, work.Bottom - h - margin));
    }

    /// <summary>按固定宽度测量卡片内容的真实所需高度并自适应窗口尺寸。</summary>
    private void ResizeToContent()
    {
        RootGrid.Measure(new Windows.Foundation.Size(CardWidthDips, double.PositiveInfinity));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var desiredDips = Math.Max(RootGrid.DesiredSize.Height + 12, CardMinHeightDips);
        var w = (int)Math.Ceiling(CardWidthDips * scale);
        var h = (int)Math.Ceiling(desiredDips * scale);
        AppWindow.Resize(new SizeInt32(w, h));

        var work = GetWorkArea();
        var margin = (int)Math.Round(16 * scale);
        AppWindow.Move(new PointInt32(work.Right - w - margin, work.Bottom - h - margin));
    }

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
        try
        {
            _action.PrimaryAction();
        }
        catch (Exception ex)
        {
            Log.Error("执行主直达动作失败", ex);
        }
        Close();
    }

    public void ExecuteSecondaryAction()
    {
        try
        {
            _action.SecondaryAction?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("执行次要直达动作失败", ex);
        }
        Close();
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e) => ExecutePrimaryAction();

    private void SecondaryAction_Click(object sender, RoutedEventArgs e) => ExecuteSecondaryAction();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _autoClose?.Stop();
        CountdownBadgeText.Text = "已暂停";
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
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
