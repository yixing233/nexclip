using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace NexClip.Installer.Native.Win32;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public UIntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
public struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] rgbReserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_COLOR_F
{
    public float r;
    public float g;
    public float b;
    public float a;

    public D2D1_COLOR_F(float r, float g, float b, float a = 1.0f)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    public static D2D1_COLOR_F FromRgb(byte r, byte g, byte b, float a = 1.0f)
        => new(r / 255.0f, g / 255.0f, b / 255.0f, a);

    public static D2D1_COLOR_F FromHex(string hex, float alpha = 1.0f)
    {
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return FromRgb(r, g, b, alpha);
        }
        return new D2D1_COLOR_F(1, 1, 1, alpha);
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D_RECT_F
{
    public float left;
    public float top;
    public float right;
    public float bottom;

    public D2D_RECT_F(float left, float top, float right, float bottom)
    {
        this.left = left;
        this.top = top;
        this.right = right;
        this.bottom = bottom;
    }

    public float Width => right - left;
    public float Height => bottom - top;

    public bool Contains(float x, float y)
        => x >= left && x <= right && y >= top && y <= bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_ROUNDED_RECT
{
    public D2D_RECT_F rect;
    public float radiusX;
    public float radiusY;

    public D2D1_ROUNDED_RECT(D2D_RECT_F rect, float radiusX, float radiusY)
    {
        this.rect = rect;
        this.radiusX = radiusX;
        this.radiusY = radiusY;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D_POINT_2F
{
    public float x;
    public float y;

    public D2D_POINT_2F(float x, float y)
    {
        this.x = x;
        this.y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_RENDER_TARGET_PROPERTIES
{
    public int type;
    public int pixelFormat_format;
    public int pixelFormat_alphaMode;
    public float dpiX;
    public float dpiY;
    public int usage;
    public int minLevel;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_HWND_RENDER_TARGET_PROPERTIES
{
    public IntPtr hwnd;
    public uint pixelSize_width;
    public uint pixelSize_height;
    public int presentOptions;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_STROKE_STYLE_PROPERTIES
{
    public int startCap;
    public int endCap;
    public int dashCap;
    public int lineJoin;
    public float miterLimit;
    public int dashStyle;
    public float dashOffset;
}

public enum D2D1_CAP_STYLE
{
    FLAT = 0,
    SQUARE = 1,
    ROUND = 2,
    TRIANGLE = 3
}

public enum D2D1_LINE_JOIN
{
    MITER = 0,
    BEVEL = 1,
    ROUND = 2,
    MITER_OR_BEVEL = 3
}

public enum D2D1_FIGURE_BEGIN
{
    FILLED = 0,
    HOLLOW = 1
}

public enum D2D1_FIGURE_END
{
    OPEN = 0,
    CLOSED = 1
}

public enum DWRITE_TEXT_ALIGNMENT
{
    LEADING = 0,
    TRAILING = 1,
    CENTER = 2,
    JUSTIFIED = 3
}

public enum DWRITE_PARAGRAPH_ALIGNMENT
{
    NEAR = 0,
    FAR = 1,
    CENTER = 2
}

public static class NativeMethods
{
    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_THICKFRAME = 0x00040000;

    public const uint WS_EX_APPWINDOW = 0x00040000;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_USER = 0x0400;

    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern bool SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

    public const uint WM_DPICHANGED = 0x02E0;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
