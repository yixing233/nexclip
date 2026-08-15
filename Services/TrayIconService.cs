using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Media;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 托盘图标:状态图标联动 + 左键/双击切换剪贴板窗口 + 右键原生菜单(打开剪贴板/设置/退出)。
/// 注:H.NotifyIcon.WinUI 的右键菜单 API 不可用(internal/类型不匹配),菜单用 Win32 TrackPopupMenu 实现。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const nuint IdOpen = 1;
    private const nuint IdSettings = 2;
    private const nuint IdExit = 3;

    private readonly TaskbarIcon _tray = new();
    private readonly Dictionary<TrayState, ImageSource> _icons = new();
    private readonly Action _onActivate;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly IntPtr _ownerHwnd;
    private TrayState _current = TrayState.Disconnected;
    private bool _disposed;

    public TrayIconService(Action onActivate, Action onSettings, Action onExit, IntPtr ownerHwnd)
    {
        _onActivate = onActivate;
        _onSettings = onSettings;
        _onExit = onExit;
        _ownerHwnd = ownerHwnd;
        _icons[TrayState.Disconnected] = Make("—", "#6B7280");
        _icons[TrayState.Connected] = Make("✓", "#2563EB");
        _icons[TrayState.Uploading] = Make("↑", "#2563EB");
        _icons[TrayState.Downloading] = Make("↓", "#2563EB");
        _icons[TrayState.Error] = Make("!", "#EF4444");
    }

    public void Initialize()
    {
        _tray.ToolTipText = "SyncClipboard 桌面端 (Ctrl+Alt+S)";
        _tray.LeftClickCommand = new RelayCommand(_onActivate);
        _tray.DoubleClickCommand = new RelayCommand(_onActivate);
        _tray.RightClickCommand = new RelayCommand(ShowContextMenu);
        SetState(TrayState.Disconnected);
    }

    public void SetState(TrayState state)
    {
        if (_disposed || state == _current) return;
        _current = state;
        try
        {
            _tray.IconSource = _icons[state];
        }
        catch (ObjectDisposedException)
        {
            _disposed = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘图标切换失败:{ex.Message}");
        }
    }

    /// <summary>右键:在光标位置弹出原生菜单。</summary>
    private void ShowContextMenu()
    {
        if (_disposed) return;
        NativeMethods.GetCursorPos(out var pt);
        var hMenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenu(hMenu, MfString, IdOpen, "打开剪贴板");
        NativeMethods.AppendMenu(hMenu, MfSeparator, 0, null);
        NativeMethods.AppendMenu(hMenu, MfString, IdSettings, "设置");
        NativeMethods.AppendMenu(hMenu, MfString, IdExit, "退出");
        try
        {
            var cmd = (nuint)NativeMethods.TrackPopupMenu(
                hMenu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, 0, _ownerHwnd, IntPtr.Zero);
            switch (cmd)
            {
                case IdOpen: _onActivate(); break;
                case IdSettings: _onSettings(); break;
                case IdExit: _onExit(); break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    public void Notify(string title, string text) =>
        _tray.ShowNotification(title, text, NotificationIcon.Info);

    private static ImageSource Make(string glyph, string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return new GeneratedIconSource
        {
            Text = glyph,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
            FontSize = 22,
            Size = 32,
        };
    }

    private static (byte R, byte G, byte B) ParseHex(string hex) => (
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    public void Dispose()
    {
        _disposed = true;
        _tray.Dispose();
    }
}
