using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NexClip.Desktop.Services;
using Windows.Graphics;

namespace NexClip.Desktop;

/// <summary>
/// 右下角链接卡片:复制到链接时弹出,显示链接文本,提供"打开链接/忽略"。
/// 5 秒自动关闭;无边框轻量浮窗(不占任务栏)。
/// </summary>
public sealed partial class LinkToastWindow : Window
{
    private const double CardWidthDips = 380;
    // 初始与最小保底高度:自适应测量卡片内容,预留充足缓冲区,确保按钮完全展示绝不被裁切。
    private const double CardMinHeightDips = 142;
    private readonly string _url;
    private DispatcherQueueTimer? _autoClose;

    public LinkToastWindow(string url)
    {
        InitializeComponent();
        _url = url;
        UrlText.Text = url;
        var hotkey = App.Services.Settings.HotkeyOpenUrl;
        OpenHotkeyText.Text = string.IsNullOrWhiteSpace(hotkey) ? "" : $"{hotkey} 打开";
        // 轻量浮窗:去标题栏、不占任务栏、不进 Alt+Tab、置顶
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
        // 显示(不抢占焦点:用 AppWindow.Show 而非 Activate)
        AppWindow.Show();
        // 首轮布局完成后按内容实际高度调整窗口尺寸并重新精准锚定
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ResizeToContent);
        // 5 秒自动关闭
        _autoClose = DispatcherQueue.CreateTimer();
        _autoClose.Interval = TimeSpan.FromSeconds(5);
        _autoClose.IsRepeating = false;
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
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

    /// <summary>定位到屏幕右下角(避开任务栏)。</summary>
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

    /// <summary>
    /// 按固定宽度测量卡片内容的真实所需高度并自适应窗口尺寸,
    /// 预留充足边界缓冲,高度变化后保持右下角锚定:链接换行成两行时底部按钮不会被挤没。
    /// </summary>
    private void ResizeToContent()
    {
        RootBorder.Measure(new Windows.Foundation.Size(CardWidthDips, double.PositiveInfinity));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var desiredDips = Math.Max(RootBorder.DesiredSize.Height + 12, CardMinHeightDips);
        var w = (int)Math.Ceiling(CardWidthDips * scale);
        var h = (int)Math.Ceiling(desiredDips * scale);
        AppWindow.Resize(new SizeInt32(w, h));

        var work = GetWorkArea();
        var margin = (int)Math.Round(16 * scale);
        AppWindow.Move(new PointInt32(work.Right - w - margin, work.Bottom - h - margin));
    }

    /// <summary>获取当前光标所在屏幕工作区(准确排除任务栏)。</summary>
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
        catch
        {
        }
        int sw = NativeMethods.GetSystemMetrics(0);
        int sh = NativeMethods.GetSystemMetrics(1);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh - DipsToPx(48) };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Uri.TryCreate(_url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开链接失败", ex);
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
