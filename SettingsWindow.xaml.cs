using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;
using Windows.Graphics;

namespace SyncClipboard.Desktop;

/// <summary>设置窗口:从托盘菜单打开;关闭即销毁。</summary>
public sealed partial class SettingsWindow : Window
{
    private const double MinWidthDips = 560;
    private const double MinHeightDips = 520;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "SyncClipboard 设置";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        AppWindow.Resize(new SizeInt32(DipsToPx(720), DipsToPx(680)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = DipsToPx(MinWidthDips);
            presenter.PreferredMinimumHeight = DipsToPx(MinHeightDips);
        }
    }

    private int DipsToPx(double dips)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return (int)Math.Round(dips * dpi / 96.0);
    }
}
