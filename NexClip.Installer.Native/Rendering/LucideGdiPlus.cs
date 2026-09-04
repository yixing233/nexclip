using System;
using NexClip.Installer.Native.Win32;

namespace NexClip.Installer.Native.Rendering;

public static class LucideGdiPlus
{
    public enum IconType
    {
        Download,
        Folder,
        Check,
        X,
        Minus,
        Settings,
        Trash2,
        Play,
        Clipboard,
        ChevronDown,
        ChevronUp,
        Shield,
        HardDrive,
        RefreshCw,
        AlertCircle,
        CheckCircle
    }

    public static void DrawIcon(
        IntPtr graphics,
        IconType icon,
        float x,
        float y,
        float size,
        uint color,
        float strokeWidth = 2.0f)
    {
        GdiPlus.GdipCreatePen1(color, strokeWidth * (size / 24.0f), 0, out var pen);
        GdiPlus.GdipSetPenLineCap197819(pen, GdiPlus.LineCap.Round, GdiPlus.LineCap.Round, GdiPlus.LineCap.Round);
        GdiPlus.GdipSetPenLineJoin(pen, GdiPlus.LineJoin.Round);

        float s = size / 24.0f;
        float P(float v) => v * s;

        GdiPlus.GdipCreatePath(0, out var path);

        switch (icon)
        {
            case IconType.Download:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(21), y + P(15), x + P(21), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(21), y + P(19), x + P(19), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(21), x + P(5), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(21), x + P(3), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(3), y + P(19), x + P(3), y + P(15));

                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(10), x + P(12), y + P(15));
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(15), x + P(17), y + P(10));

                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(15), x + P(12), y + P(3));
                break;

            case IconType.Folder:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(3), x + P(8), y + P(3));
                GdiPlus.GdipAddPathLine(path, x + P(8), y + P(3), x + P(10), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(10), y + P(5), x + P(20), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(5), x + P(22), y + P(7));
                GdiPlus.GdipAddPathLine(path, x + P(22), y + P(7), x + P(22), y + P(18));
                GdiPlus.GdipAddPathLine(path, x + P(22), y + P(18), x + P(20), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(20), x + P(4), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(20), x + P(2), y + P(18));
                GdiPlus.GdipAddPathLine(path, x + P(2), y + P(18), x + P(2), y + P(5));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.Check:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(12), x + P(9), y + P(17));
                GdiPlus.GdipAddPathLine(path, x + P(9), y + P(17), x + P(20), y + P(6));
                break;

            case IconType.X:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(18), y + P(6), x + P(6), y + P(18));
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(6), y + P(6), x + P(18), y + P(18));
                break;

            case IconType.Minus:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(12), x + P(19), y + P(12));
                break;

            case IconType.Settings:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(2), x + P(14), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(14), y + P(5), x + P(17), y + P(4));
                GdiPlus.GdipAddPathLine(path, x + P(17), y + P(4), x + P(19), y + P(7));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(7), x + P(17), y + P(9));
                GdiPlus.GdipAddPathLine(path, x + P(17), y + P(9), x + P(19), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(12), x + P(17), y + P(15));
                GdiPlus.GdipAddPathLine(path, x + P(17), y + P(15), x + P(19), y + P(17));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(17), x + P(17), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(17), y + P(20), x + P(14), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(14), y + P(19), x + P(12), y + P(22));
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(22), x + P(10), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(10), y + P(19), x + P(7), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(20), x + P(5), y + P(17));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(17), x + P(7), y + P(15));
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(15), x + P(5), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(12), x + P(7), y + P(9));
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(9), x + P(5), y + P(7));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(7), x + P(7), y + P(4));
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(4), x + P(10), y + P(5));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.Trash2:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(3), y + P(6), x + P(21), y + P(6));

                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(6), x + P(5), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(19), x + P(7), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(7), y + P(21), x + P(17), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(17), y + P(21), x + P(19), y + P(19));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(19), x + P(19), y + P(6));

                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(8), y + P(6), x + P(8), y + P(3));
                GdiPlus.GdipAddPathLine(path, x + P(8), y + P(3), x + P(16), y + P(3));
                GdiPlus.GdipAddPathLine(path, x + P(16), y + P(3), x + P(16), y + P(6));
                break;

            case IconType.Play:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(6), y + P(4), x + P(19), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(12), x + P(6), y + P(20));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.Clipboard:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(8), y + P(4), x + P(5), y + P(4));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(4), x + P(4), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(5), x + P(4), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(20), x + P(5), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(5), y + P(21), x + P(19), y + P(21));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(21), x + P(20), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(20), x + P(20), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(5), x + P(19), y + P(4));
                GdiPlus.GdipAddPathLine(path, x + P(19), y + P(4), x + P(16), y + P(4));

                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(9), y + P(2), x + P(15), y + P(2));
                GdiPlus.GdipAddPathLine(path, x + P(15), y + P(2), x + P(15), y + P(6));
                GdiPlus.GdipAddPathLine(path, x + P(15), y + P(6), x + P(9), y + P(6));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.ChevronDown:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(6), y + P(9), x + P(12), y + P(15));
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(15), x + P(18), y + P(9));
                break;

            case IconType.ChevronUp:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(18), y + P(15), x + P(12), y + P(9));
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(9), x + P(6), y + P(15));
                break;

            case IconType.Shield:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(2), x + P(20), y + P(5));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(5), x + P(20), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(12), x + P(12), y + P(22));
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(22), x + P(4), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(12), x + P(4), y + P(5));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.HardDrive:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(2), y + P(12), x + P(22), y + P(12));
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(6), x + P(20), y + P(6));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(6), x + P(22), y + P(12));
                GdiPlus.GdipAddPathLine(path, x + P(22), y + P(12), x + P(22), y + P(18));
                GdiPlus.GdipAddPathLine(path, x + P(22), y + P(18), x + P(20), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(20), y + P(20), x + P(4), y + P(20));
                GdiPlus.GdipAddPathLine(path, x + P(4), y + P(20), x + P(2), y + P(18));
                GdiPlus.GdipAddPathLine(path, x + P(2), y + P(18), x + P(2), y + P(12));
                GdiPlus.GdipClosePathFigure(path);
                break;

            case IconType.RefreshCw:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathArc(path, x + P(3), y + P(3), P(18), P(18), 120, 200);
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(21), y + P(3), x + P(21), y + P(8));
                GdiPlus.GdipAddPathLine(path, x + P(21), y + P(8), x + P(16), y + P(8));
                break;

            case IconType.AlertCircle:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathArc(path, x + P(2), y + P(2), P(20), P(20), 0, 360);
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(8), x + P(12), y + P(12));
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(12), y + P(16), x + P(12), y + P(16.5f));
                break;

            case IconType.CheckCircle:
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathArc(path, x + P(2), y + P(2), P(20), P(20), 0, 360);
                GdiPlus.GdipStartPathFigure(path);
                GdiPlus.GdipAddPathLine(path, x + P(8), y + P(12), x + P(11), y + P(15));
                GdiPlus.GdipAddPathLine(path, x + P(11), y + P(15), x + P(16), y + P(9));
                break;
        }

        GdiPlus.GdipDrawPath(graphics, pen, path);
        GdiPlus.GdipDeletePath(path);
        GdiPlus.GdipDeletePen(pen);
    }

    public static void DrawLoaderCircle(
        IntPtr graphics,
        float x,
        float y,
        float size,
        uint color,
        float rotationDegrees,
        float strokeWidth = 2.0f)
    {
        var centerX = x + size / 2.0f;
        var centerY = y + size / 2.0f;
        var innerRadius = size * 0.28f;
        var outerRadius = size * 0.43f;

        for (var index = 0; index < 8; index++)
        {
            var angle = (rotationDegrees + index * 45.0f - 90.0f) * MathF.PI / 180.0f;
            var alpha = (uint)(48 + index * 25);
            var segmentColor = (Math.Min(223u, alpha) << 24) | (color & 0x00FFFFFF);
            GdiPlus.GdipCreatePen1(segmentColor, strokeWidth * (size / 24.0f), 0, out var pen);
            GdiPlus.GdipSetPenLineCap197819(
                pen,
                GdiPlus.LineCap.Round,
                GdiPlus.LineCap.Round,
                GdiPlus.LineCap.Round);
            GdiPlus.GdipDrawLine(
                graphics,
                pen,
                centerX + MathF.Cos(angle) * innerRadius,
                centerY + MathF.Sin(angle) * innerRadius,
                centerX + MathF.Cos(angle) * outerRadius,
                centerY + MathF.Sin(angle) * outerRadius);
            GdiPlus.GdipDeletePen(pen);
        }
    }

    public static void FillRoundedRect(IntPtr graphics, IntPtr brush, float x, float y, float width, float height, float radius)
    {
        GdiPlus.GdipCreatePath(0, out var path);
        float d = radius * 2;
        GdiPlus.GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdiPlus.GdipAddPathArc(path, x + width - d, y, d, d, 270, 90);
        GdiPlus.GdipAddPathArc(path, x + width - d, y + height - d, d, d, 0, 90);
        GdiPlus.GdipAddPathArc(path, x, y + height - d, d, d, 90, 90);
        GdiPlus.GdipClosePathFigure(path);

        GdiPlus.GdipFillPath(graphics, brush, path);
        GdiPlus.GdipDeletePath(path);
    }

    public static void DrawRoundedRect(IntPtr graphics, IntPtr pen, float x, float y, float width, float height, float radius)
    {
        GdiPlus.GdipCreatePath(0, out var path);
        float d = radius * 2;
        GdiPlus.GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdiPlus.GdipAddPathArc(path, x + width - d, y, d, d, 270, 90);
        GdiPlus.GdipAddPathArc(path, x + width - d, y + height - d, d, d, 0, 90);
        GdiPlus.GdipAddPathArc(path, x, y + height - d, d, d, 90, 90);
        GdiPlus.GdipClosePathFigure(path);

        GdiPlus.GdipDrawPath(graphics, pen, path);
        GdiPlus.GdipDeletePath(path);
    }

    public static void FillTopRightRoundedRect(IntPtr graphics, IntPtr brush, float x, float y, float width, float height, float radius)
    {
        GdiPlus.GdipCreatePath(0, out var path);
        float d = radius * 2;
        GdiPlus.GdipStartPathFigure(path);
        GdiPlus.GdipAddPathLine(path, x, y, x + width - radius, y);
        GdiPlus.GdipAddPathArc(path, x + width - d, y, d, d, 270, 90);
        GdiPlus.GdipAddPathLine(path, x + width, y + radius, x + width, y + height);
        GdiPlus.GdipAddPathLine(path, x + width, y + height, x, y + height);
        GdiPlus.GdipClosePathFigure(path);

        GdiPlus.GdipFillPath(graphics, brush, path);
        GdiPlus.GdipDeletePath(path);
    }
}
