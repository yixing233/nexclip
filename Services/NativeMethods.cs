namespace SyncClipboard.Desktop.Services;

using System.Runtime.InteropServices;

/// <summary>极小 Win32 调用。</summary>
internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolwindow = 0x00000080;
    internal const long WsExAppwindow = 0x00040000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>绕过 Windows 前台锁定:模拟一次 ALT 键(经典技巧),再请求前台。</summary>
    internal static void ForceForeground(IntPtr hwnd)
    {
        keybd_event(0x12 /* VK_MENU */, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 0x0002 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern long GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);

    // ---- 托盘右键菜单(TrackPopupMenu) ----
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint flags, nuint idNewItem, string? newItem);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    // ---- 托盘(Shell_NotifyIcon) ----
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
