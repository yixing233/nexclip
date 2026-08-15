using System.Runtime.InteropServices;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 托盘图标(自研,Shell_NotifyIcon):状态图标联动 + 左键/双击切换剪贴板窗口 +
/// 右键原生菜单(打开剪贴板/设置/退出)+ 气泡通知。
/// 不用 H.NotifyIcon(其 WinUI 集成存在图标/菜单不可见问题)。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private const uint NimAdd = 0x0, NimModify = 0x1, NimDelete = 0x2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const uint NiiInfo = 0x1;
    private const uint WmApp = 0x8000, WmLButtonUp = 0x202, WmLButtonDblClk = 0x203, WmRButtonUp = 0x205, WmContextMenu = 0x7B;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;
    private const nuint IdOpen = 1, IdSettings = 2, IdExit = 3;

    private readonly Action _onActivate;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<TrayState, IntPtr> _icons = new();
    private WndProcDelegate? _wndProc;
    private IntPtr _currentIcon;
    private bool _added;
    private bool _disposed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIconService(Action onActivate, Action onSettings, Action onExit, IntPtr ownerHwnd)
    {
        _onActivate = onActivate;
        _onSettings = onSettings;
        _onExit = onExit;

        _wndProc = WndProc;
        _hwnd = CreateMessageWindow(_wndProc);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("托盘消息窗口创建失败");
            return;
        }

        _icons[TrayState.Disconnected] = MakeIcon("—", "#6B7280");
        _icons[TrayState.Connected] = MakeIcon("✓", "#2563EB");
        _icons[TrayState.Uploading] = MakeIcon("↑", "#2563EB");
        _icons[TrayState.Downloading] = MakeIcon("↓", "#2563EB");
        _icons[TrayState.Error] = MakeIcon("!", "#EF4444");

        AddToTray();
    }

    public void Initialize()
    {
        SetState(TrayState.Disconnected);
    }

    public void SetState(TrayState state)
    {
        if (_disposed || !_icons.TryGetValue(state, out var icon)) return;
        if (_currentIcon == icon) return;
        _currentIcon = icon;
        ModifyIcon(icon);
    }

    private void ModifyIcon(IntPtr icon)
    {
        if (!_added || _disposed) return;
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = WmApp,
            hIcon = icon,
            szTip = "SyncClipboard 桌面端 (Ctrl+Alt+S)",
        };
        NativeMethods.Shell_NotifyIcon(NimModify, ref data);
    }

    private void AddToTray()
    {
        if (_hwnd == IntPtr.Zero || _added) return;
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifIcon | NifTip | NifMessage,
            uCallbackMessage = WmApp,
            hIcon = _icons[TrayState.Disconnected],
            szTip = "SyncClipboard 桌面端 (Ctrl+Alt+S)",
        };
        _added = NativeMethods.Shell_NotifyIcon(NimAdd, ref data);
        Log.Debug($"TrayIconService: NIM_ADD result={_added}");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmApp)
        {
            var l = (uint)lParam;
            if (l == WmRButtonUp || l == WmContextMenu)
            {
                ShowContextMenu();
            }
            else if (l == WmLButtonUp || l == WmLButtonDblClk)
            {
                _onActivate();
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

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
                hMenu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
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

    /// <summary>气泡通知(收到远端新剪贴板)。</summary>
    public void Notify(string title, string text)
    {
        if (!_added || _disposed) return;
        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifInfo | NifMessage,
            uCallbackMessage = WmApp,
            szInfo = text,
            szInfoTitle = title,
            dwInfoFlags = NiiInfo,
        };
        NativeMethods.Shell_NotifyIcon(NimModify, ref data);
    }

    // ---- Win32 辅助 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    private static IntPtr CreateMessageWindow(WndProcDelegate proc)
    {
        var hwnd = CreateWindowExW(0, "STATIC", "SyncClipboardTray", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            SetWindowLongPtrW(hwnd, -4, Marshal.GetFunctionPointerForDelegate(proc));
        }
        return hwnd;
    }

    private static IntPtr MakeIcon(string glyph, string hex)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(ParseHex(hex));
            using var font = new System.Drawing.Font("Segoe UI Symbol", 18f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString(glyph, font, brush, new System.Drawing.RectangleF(0, 0, 32, 32), format);
            return bmp.GetHicon();
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘图标生成失败:{ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static System.Drawing.Color ParseHex(string hex) => System.Drawing.Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    public void Dispose()
    {
        _disposed = true;
        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };
            NativeMethods.Shell_NotifyIcon(NimDelete, ref data);
        }
        foreach (var icon in _icons.Values)
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
