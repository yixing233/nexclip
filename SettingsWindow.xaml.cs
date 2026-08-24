using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NexClip.Desktop.Services;
using Windows.Graphics;

namespace NexClip.Desktop;

/// <summary>设置窗口:从托盘菜单打开;关闭即销毁。</summary>
public sealed partial class SettingsWindow : Window
{
    private const double MinWidthDips = 560;
    private const double MinHeightDips = 520;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "NexClip 设置";
        SystemBackdrop = App.CreateBackdrop();

        // 自定义标题栏:去掉系统标题栏与系统窗口按钮,改为自绘拖拽区 + 最小化/关闭
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.TitleBar is { } titleBar)
        {
            titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }
        SetTitleBar(TitleDragRegion);

        AppWindow.Resize(new SizeInt32(DipsToPx(720), DipsToPx(680)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = DipsToPx(MinWidthDips);
            presenter.PreferredMinimumHeight = DipsToPx(MinHeightDips);
        }

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                SettingsHost.ResetContentScroll();
            }
        };
    }

    /// <summary>
    /// 显示前准备窗口:首次打开时居中到鼠标所在显示器,最小化状态先恢复。
    /// 坐标在 Show 前设置,避免用户看到窗口从默认位置闪现到目标位置。
    /// </summary>
    public void PrepareForShow(bool centerOnCurrentMonitor)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (NativeMethods.IsIconic(hwnd))
        {
            (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        }

        if (centerOnCurrentMonitor)
        {
            CenterOnCursorMonitor();
        }
    }

    private void CenterOnCursorMonitor()
    {
        try
        {
            var work = GetCursorWorkArea();
            if (work is null) return;

            var size = AppWindow.Size;
            var width = work.Value.Right - work.Value.Left;
            var height = work.Value.Bottom - work.Value.Top;
            var x = work.Value.Left + Math.Max(0, (width - size.Width) / 2);
            var y = work.Value.Top + Math.Max(0, (height - size.Height) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex)
        {
            Log.Error("定位设置窗口失败", ex);
        }
    }

    /// <summary>获取鼠标所在显示器的工作区,排除任务栏。</summary>
    private NativeMethods.RECT? GetCursorWorkArea()
    {
        try
        {
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero)
                {
                    var info = new NativeMethods.MONITORINFO
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                    };
                    if (NativeMethods.GetMonitorInfo(monitor, ref info)) return info.rcWork;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"获取设置窗口显示器失败:{ex.Message}");
        }

        var width = NativeMethods.GetSystemMetrics(0 /* SM_CXSCREEN */);
        var height = NativeMethods.GetSystemMetrics(1 /* SM_CYSCREEN */);
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        (AppWindow.Presenter as OverlappedPresenter)?.Minimize();

    /// <summary>标题栏汉堡:切换侧边栏开合。</summary>
    private void TogglePane_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsHost.SettingsNav is { } nav)
        {
            nav.IsPaneOpen = !nav.IsPaneOpen;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
