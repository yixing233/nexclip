using System;
using System.Runtime.InteropServices;

namespace NexClip.Installer.Native.Win32;

public static class GdiPlus
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public bool SuppressBackgroundThread;
        public bool SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public RECTF(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public bool Contains(float px, float py)
            => px >= X && px <= X + Width && py >= Y && py <= Y + Height;
    }

    public enum SmoothingMode
    {
        Invalid = -1,
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4
    }

    public enum TextRenderingHint
    {
        SystemDefault = 0,
        SingleBitPerPixelGridFit = 1,
        SingleBitPerPixel = 2,
        AntiAliasGridFit = 3,
        AntiAlias = 4,
        ClearTypeGridFit = 5
    }

    public enum LineCap
    {
        Flat = 0,
        Square = 1,
        Round = 2,
        Triangle = 3
    }

    public enum LineJoin
    {
        Miter = 0,
        Bevel = 1,
        Round = 2,
        MiterClipped = 3
    }

    public enum StringAlignment
    {
        Near = 0,
        Center = 1,
        Far = 2
    }

    public static uint Argb(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    public static uint FromHex(string hex, byte alpha = 255)
    {
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Argb(alpha, r, g, b);
        }
        return Argb(alpha, 255, 255, 255);
    }

    [DllImport("gdiplus.dll")]
    public static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll")]
    public static extern void GdiplusShutdown(IntPtr token);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetSmoothingMode(IntPtr graphics, SmoothingMode smoothingMode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetTextRenderingHint(IntPtr graphics, TextRenderingHint mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGraphicsClear(IntPtr graphics, uint color);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateSolidFill(uint color, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePen1(uint color, float width, int unit, out IntPtr pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPenLineCap197819(IntPtr pen, LineCap startCap, LineCap endCap, LineCap dashCap);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPenLineJoin(IntPtr pen, LineJoin lineJoin);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetInterpolationMode(IntPtr graphics, int interpolationMode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPixelOffsetMode(IntPtr graphics, int pixelOffsetMode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePen(IntPtr pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePath(int fillMode, out IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePath(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipResetPath(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipStartPathFigure(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipClosePathFigure(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathLine(IntPtr path, float x1, float y1, float x2, float y2);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathArc(IntPtr path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawRectangle(IntPtr graphics, IntPtr pen, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillRectangle(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteFontFamily(IntPtr fontFamily);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateFont(IntPtr fontFamily, float emSize, int style, int unit, out IntPtr font);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteFont(IntPtr font);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateStringFormat(int formatAttributes, int language, out IntPtr format);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatAlign(IntPtr format, StringAlignment align);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatLineAlign(IntPtr format, StringAlignment align);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteStringFormat(IntPtr format);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RECTF layoutRect, IntPtr stringFormat, IntPtr brush);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipMeasureString(IntPtr graphics, string text, int length, IntPtr font, ref RECTF layoutRect, IntPtr stringFormat, out RECTF boundingBox, out int codepointsFitted, out int linesFilled);

    [DllImport("shlwapi.dll", EntryPoint = "#12")]
    public static extern IntPtr SHCreateMemStream(byte[] pInit, uint cbInit);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateBitmapFromStream(IntPtr stream, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetImageWidth(IntPtr image, out uint width);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetImageHeight(IntPtr image, out uint height);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawImageRectRect(
        IntPtr graphics, IntPtr image,
        float dstx, float dsty, float dstwidth, float dstheight,
        float srcx, float srcy, float srcwidth, float srcheight,
        int srcUnit, IntPtr imageAttributes, IntPtr callback, IntPtr callbackData);

    // GDI 双缓冲加速
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
}
