using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;
using Windows.Graphics;

namespace SyncClipboard.Desktop;

/// <summary>
/// 右下角链接卡片:复制到链接时弹出,显示链接文本,提供"打开链接/忽略"。
/// 5 秒自动关闭;无边框轻量浮窗(不占任务栏)。
/// </summary>
public sealed partial class LinkToastWindow : Window
{
    private const double CardWidthDips = 380;
    // 初始高度:窗口显示后会按卡片实际内容重新测量并自适应高度,
    // 链接换行成两行时自动变高,底部“打开链接/忽略”按钮不会被挤没。
    private const double CardHeightDips = 148;
    private readonly string _url;
    private DispatcherQueueTimer? _autoClose;

    public LinkToastWindow(string url)
    {
        InitializeComponent();
        _url = url;
        UrlText.Text = url;
        var hotkey = App.Services.Settings.HotkeyOpenUrl;
        OpenHotkeyText.Text = string.IsNullOrWhiteSpace(hotkey) ? "" : $"{hotkey} 打开";
        // 轻量浮窗:去标题栏、不占任务栏
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }
        PositionBottomRight();
        // 显示(不抢占焦点:用 AppWindow.Show 而非 Activate)
        AppWindow.Show();
        // 首轮布局完成后按内容实际高度调整窗口尺寸
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ResizeToContent);
        // 5 秒自动关闭
        _autoClose = DispatcherQueue.CreateTimer();
        _autoClose.Interval = TimeSpan.FromSeconds(5);
        _autoClose.IsRepeating = false;
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    /// <summary>定位到屏幕右下角(避开任务栏)。</summary>
    private void PositionBottomRight()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var sw = NativeMethods.GetSystemMetrics(0 /* SM_CXSCREEN */);
        var sh = NativeMethods.GetSystemMetrics(1 /* SM_CYSCREEN */);
        var w = DipsToPx(CardWidthDips);
        var h = DipsToPx(CardHeightDips);
        AppWindow.Resize(new SizeInt32(w, h));
        // 距右 16px,距底 48px(任务栏约 40px)
        AppWindow.Move(new PointInt32(sw - w - DipsToPx(16), sh - h - DipsToPx(48)));
    }

    /// <summary>
    /// 按固定宽度测量卡片内容的真实所需高度并自适应窗口尺寸,
    /// 高度变化后保持右下角锚定:链接换行成两行时底部按钮不会被挤没。
    /// </summary>
    private void ResizeToContent()
    {
        RootBorder.Measure(new Windows.Foundation.Size(CardWidthDips, double.PositiveInfinity));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        var w = DipsToPx(CardWidthDips);
        var h = (int)Math.Ceiling(RootBorder.DesiredSize.Height * scale);
        AppWindow.Resize(new SizeInt32(w, h));
        // 高度变化后重新定位,保持距右 16px、距底 48px 不变
        var sw = NativeMethods.GetSystemMetrics(0);
        var sh = NativeMethods.GetSystemMetrics(1);
        AppWindow.Move(new PointInt32(sw - w - DipsToPx(16), sh - h - DipsToPx(48)));
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
