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
}
