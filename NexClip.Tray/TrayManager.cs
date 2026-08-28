using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NexClip.Tray;

public sealed class TrayManager : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private readonly Action _onActivate;
    private readonly Action _onShow;
    private readonly Action _onCheckUpdate;
    private readonly Action _onSettings;
    private readonly Action _onRestart;
    private readonly Action _onExit;
    private readonly Action<string>? _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private readonly ContextMenuStrip _menu;

    private readonly ToolStripMenuItem _itemOpen;
    private readonly ToolStripMenuItem _itemUpdate;
    private readonly ToolStripMenuItem _itemSettings;
    private readonly ToolStripMenuItem _itemRestart;
    private readonly ToolStripMenuItem _itemExit;
    private readonly ToolStripSeparator _separator;

    private bool _isDarkTheme;
    private bool _disposed;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayManager(
        Action onActivate,
        Action onShow,
        Action onCheckUpdate,
        Action onSettings,
        Action onRestart,
        Action onExit,
        string baseDirectory,
        Action<string>? log = null)
    {
        _onActivate = onActivate;
        _onShow = onShow;
        _onCheckUpdate = onCheckUpdate;
        _onSettings = onSettings;
        _onRestart = onRestart;
        _onExit = onExit;
        _log = log;

        _icons[TrayState.Disconnected] = MakeIcon(baseDirectory, "—", "#6B7280");
        _icons[TrayState.Connected] = MakeIcon(baseDirectory, "✓", "#2563EB");
        _icons[TrayState.Uploading] = MakeIcon(baseDirectory, "↑", "#2563EB");
        _icons[TrayState.Downloading] = MakeIcon(baseDirectory, "↓", "#2563EB");
        _icons[TrayState.Error] = MakeIcon(baseDirectory, "!", "#EF4444");

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
            ShowCheckMargin = false,
            ImageScalingSize = new Size(18, 18),
            AutoSize = true,
            DropShadowEnabled = true,
            Padding = new Padding(3, 4, 3, 4)
        };

        _menu.Opened += (_, _) =>
        {
            ApplyDwmMenuEffects(_menu.Handle, _isDarkTheme);
        };

        var defaultFont = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        var boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        var itemPadding = new Padding(4, 5, 14, 5);

        _itemOpen = new ToolStripMenuItem("打开剪贴板", null, (_, _) => _onShow())
        {
            Font = boldFont,
            ShortcutKeyDisplayString = "Alt+V",
            Padding = itemPadding
        };

        _itemUpdate = new ToolStripMenuItem("检查更新...", null, (_, _) => _onCheckUpdate())
        {
            Font = defaultFont,
            Padding = itemPadding
        };

        _itemSettings = new ToolStripMenuItem("设置", null, (_, _) => _onSettings())
        {
            Font = defaultFont,
            ShortcutKeyDisplayString = "Alt+X",
            Padding = itemPadding
        };

        _separator = new ToolStripSeparator
        {
            Margin = new Padding(0, 3, 0, 3)
        };

        _itemRestart = new ToolStripMenuItem("重启 NexClip", null, (_, _) => _onRestart())
        {
            Font = defaultFont,
            Padding = itemPadding
        };

        _itemExit = new ToolStripMenuItem("退出", null, (_, _) => _onExit())
        {
            Font = defaultFont,
            Padding = itemPadding
        };

        _menu.Items.Add(_itemOpen);
        _menu.Items.Add(_itemUpdate);
        _menu.Items.Add(_itemSettings);
        _menu.Items.Add(_separator);
        _menu.Items.Add(_itemRestart);
        _menu.Items.Add(_itemExit);

        SetTheme(false); // 默认初始化浅色，后续由 TrayIconService 同步实际主题

        _notifyIcon = new NotifyIcon
        {
            Text = "NexClip 桌面端",
            Icon = _icons[TrayState.Disconnected],
            ContextMenuStrip = _menu,
            Visible = true
        };
        _log?.Invoke($"NotifyIcon created (Visible=true, stateIcons={_icons.Count})");

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _onActivate();
            }
        };

        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                // 确保点击菜单外部区域时能正常失焦自动关闭
                SetForegroundWindow(_menu.Handle);
            }
        };

        _notifyIcon.DoubleClick += (_, _) => _onShow();
    }

    private static void ApplyDwmMenuEffects(IntPtr handle, bool isDark)
    {
        try
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int dark = isDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch
        {
        }
    }

    /// <summary>动态同步深色/浅色主题并刷新托盘菜单渲染。</summary>
    public void SetTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        try
        {
            _menu.Renderer = new FluentToolStripRenderer(isDark);
            var foreColor = isDark ? Color.FromArgb(243, 244, 246) : Color.FromArgb(17, 24, 39);
            var exitColor = isDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(239, 68, 68);
            var iconColor = isDark ? Color.FromArgb(203, 213, 225) : Color.FromArgb(55, 65, 81);

            _itemOpen.ForeColor = foreColor;
            _itemUpdate.ForeColor = foreColor;
            _itemSettings.ForeColor = foreColor;
            _itemRestart.ForeColor = foreColor;
            _itemExit.ForeColor = exitColor;

            // 动态生成高质量 18x18 官方正版 Lucide 矢量图标
            _itemOpen.Image?.Dispose();
            _itemOpen.Image = DrawLucideIcon(TrayIconType.Clipboard, iconColor);

            _itemUpdate.Image?.Dispose();
            _itemUpdate.Image = DrawLucideIcon(TrayIconType.Refresh, iconColor);

            _itemSettings.Image?.Dispose();
            _itemSettings.Image = DrawLucideIcon(TrayIconType.Settings, iconColor);

            _itemRestart.Image?.Dispose();
            _itemRestart.Image = DrawLucideIcon(TrayIconType.Restart, iconColor);

            _itemExit.Image?.Dispose();
            _itemExit.Image = DrawLucideIcon(TrayIconType.Exit, exitColor);

            _menu.BackColor = isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
            _menu.Invalidate();
        }
        catch (Exception ex)
        {
            _log?.Invoke("SetTheme error: " + ex.Message);
        }
    }

    private enum TrayIconType { Clipboard, Refresh, Settings, Restart, Exit }

    /// <summary>
    /// 100% 严谨按照 Lucide 官方 24x24 矢量规范转换为 GDI+ 路径渲染。
    /// </summary>
    private static Bitmap DrawLucideIcon(TrayIconType type, Color color, int size = 18)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 24.0f;
        using var pen = new Pen(color, 2.0f * s)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        float P(float v) => v * s;

        switch (type)
        {
            case TrayIconType.Clipboard:
                // Lucide clipboard:
                // <rect width="8" height="4" x="8" y="2" rx="1" ry="1" />
                // <path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2" />
                using (var clip = new GraphicsPath())
                {
                    clip.AddArc(P(8), P(2), P(2), P(2), 180, 90);
                    clip.AddArc(P(14), P(2), P(2), P(2), 270, 90);
                    clip.AddArc(P(14), P(4), P(2), P(2), 0, 90);
                    clip.AddArc(P(8), P(4), P(2), P(2), 90, 90);
                    clip.CloseFigure();
                    g.DrawPath(pen, clip);
                }
                using (var body = new GraphicsPath())
                {
                    body.AddLine(P(16), P(4), P(18), P(4));
                    body.AddArc(P(16), P(4), P(4), P(4), 270, 90);
                    body.AddLine(P(20), P(6), P(20), P(20));
                    body.AddArc(P(16), P(18), P(4), P(4), 0, 90);
                    body.AddLine(P(18), P(22), P(6), P(22));
                    body.AddArc(P(4), P(18), P(4), P(4), 90, 90);
                    body.AddLine(P(4), P(20), P(4), P(6));
                    body.AddArc(P(4), P(4), P(4), P(4), 180, 90);
                    body.AddLine(P(6), P(4), P(8), P(4));
                    g.DrawPath(pen, body);
                }
                break;

            case TrayIconType.Refresh:
                // Lucide refresh-cw:
                // <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" />
                // <path d="M21 3v5h-5" />
                // <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" />
                // <path d="M8 16H3v5" />
                using (var arc1 = new GraphicsPath())
                {
                    arc1.AddArc(P(3), P(3), P(18), P(18), 185, 120);
                    g.DrawPath(pen, arc1);
                }
                g.DrawLine(pen, P(21), P(3), P(21), P(8));
                g.DrawLine(pen, P(21), P(8), P(16), P(8));

                using (var arc2 = new GraphicsPath())
                {
                    arc2.AddArc(P(3), P(3), P(18), P(18), 5, 120);
                    g.DrawPath(pen, arc2);
                }
                g.DrawLine(pen, P(3), P(21), P(3), P(16));
                g.DrawLine(pen, P(3), P(16), P(8), P(16));
                break;

            case TrayIconType.Settings:
                // Lucide settings 官方精密 8 瓣齿轮:
                // 中心圆: cx=12, cy=12, r=3 (直径 6)
                g.DrawEllipse(pen, P(9), P(9), P(6), P(6));
                using (var gear = new GraphicsPath())
                {
                    gear.AddLine(P(10.5f), P(2.2f), P(13.5f), P(2.2f));
                    gear.AddLine(P(13.5f), P(2.2f), P(14.6f), P(5.3f));
                    gear.AddLine(P(14.6f), P(5.3f), P(17.5f), P(4.1f));
                    gear.AddLine(P(17.5f), P(4.1f), P(19.9f), P(6.5f));
                    gear.AddLine(P(19.9f), P(6.5f), P(18.7f), P(9.4f));
                    gear.AddLine(P(18.7f), P(9.4f), P(21.8f), P(10.5f));
                    gear.AddLine(P(21.8f), P(10.5f), P(21.8f), P(13.5f));
                    gear.AddLine(P(21.8f), P(13.5f), P(18.7f), P(14.6f));
                    gear.AddLine(P(18.7f), P(14.6f), P(19.9f), P(17.5f));
                    gear.AddLine(P(19.9f), P(17.5f), P(17.5f), P(19.9f));
                    gear.AddLine(P(17.5f), P(19.9f), P(14.6f), P(18.7f));
                    gear.AddLine(P(14.6f), P(18.7f), P(13.5f), P(21.8f));
                    gear.AddLine(P(13.5f), P(21.8f), P(10.5f), P(21.8f));
                    gear.AddLine(P(10.5f), P(21.8f), P(9.4f), P(18.7f));
                    gear.AddLine(P(9.4f), P(18.7f), P(6.5f), P(19.9f));
                    gear.AddLine(P(6.5f), P(19.9f), P(4.1f), P(17.5f));
                    gear.AddLine(P(4.1f), P(17.5f), P(5.3f), P(14.6f));
                    gear.AddLine(P(5.3f), P(14.6f), P(2.2f), P(13.5f));
                    gear.AddLine(P(2.2f), P(13.5f), P(2.2f), P(10.5f));
                    gear.AddLine(P(2.2f), P(10.5f), P(5.3f), P(9.4f));
                    gear.AddLine(P(5.3f), P(9.4f), P(4.1f), P(6.5f));
                    gear.AddLine(P(4.1f), P(6.5f), P(6.5f), P(4.1f));
                    gear.AddLine(P(6.5f), P(4.1f), P(9.4f), P(5.3f));
                    gear.CloseFigure();
                    g.DrawPath(pen, gear);
                }
                break;

            case TrayIconType.Restart:
                // Lucide rotate-cw:
                // <path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"/>
                // <path d="M21 3v5h-5"/>
                using (var arc = new GraphicsPath())
                {
                    arc.AddArc(P(3), P(3), P(18), P(18), 45, 270);
                    g.DrawPath(pen, arc);
                }
                g.DrawLine(pen, P(21), P(3), P(21), P(8));
                g.DrawLine(pen, P(21), P(8), P(16), P(8));
                break;

            case TrayIconType.Exit:
                // Lucide x:
                // <path d="M18 6 6 18"/><path d="m6 6 12 12"/>
                g.DrawLine(pen, P(6), P(6), P(18), P(18));
                g.DrawLine(pen, P(18), P(6), P(6), P(18));
                break;
        }

        return bmp;
    }

    public void SetState(TrayState state)
    {
        if (_disposed || !_icons.TryGetValue(state, out var icon)) return;
        try
        {
            _notifyIcon.Icon = icon;
        }
        catch
        {
        }
    }

    /// <summary>强制重新挂载托盘图标(Explorer/Shell 未就绪导致首次 NIM_ADD 失败时自愈)。</summary>
    public void EnsureVisible()
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
            _log?.Invoke("tray re-attached (NIM_ADD forced)");
        }
        catch (Exception ex)
        {
            _log?.Invoke("tray re-attach failed: " + ex.Message);
        }
    }

    public void Notify(string title, string text)
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    private static Icon MakeIcon(string baseDir, string glyph, string hex)
    {
        try
        {
            var iconPng = Path.Combine(baseDir, "Assets", "icon.png");
            const int size = 32;
            const float radius = 7.0f;
            using var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, size - 1.0f, size - 1.0f);
            using var path = CreateRoundedRectanglePath(rect, radius);

            if (File.Exists(iconPng))
            {
                using var baseImg = Image.FromFile(iconPng);
                using var scaled = new Bitmap(size, size);
                using (var sg = Graphics.FromImage(scaled))
                {
                    sg.SmoothingMode = SmoothingMode.AntiAlias;
                    sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    sg.DrawImage(baseImg, 0, 0, size, size);
                }
                using var brush = new TextureBrush(scaled);
                g.FillPath(brush, path);
            }
            else
            {
                using var brush = new SolidBrush(ParseHex(hex));
                g.FillPath(brush, path);
                using var textBrush = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI Symbol", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, font, textBrush, rect, format);
            }

            if (glyph != "✓")
            {
                var badgeRect = new RectangleF(17f, 17f, 14f, 14f);
                var badgeColor = ParseHex(hex);
                using var badgeBrush = new SolidBrush(badgeColor);
                using var ringPen = new Pen(Color.FromArgb(220, 20, 20, 20), 1.5f);
                g.FillEllipse(badgeBrush, badgeRect);
                g.DrawEllipse(ringPen, badgeRect);

                using var textBrush = new SolidBrush(Color.White);
                using var badgeFont = new Font("Segoe UI Symbol", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var badgeFmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, badgeFont, textBrush, badgeRect, badgeFmt);
            }

            var hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2f;
        var arc = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ParseHex(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _itemOpen.Image?.Dispose();
            _itemUpdate.Image?.Dispose();
            _itemSettings.Image?.Dispose();
            _itemRestart.Image?.Dispose();
            _itemExit.Image?.Dispose();
            _menu.Dispose();
            foreach (var ic in _icons.Values)
            {
                ic.Dispose();
            }
        }
        catch
        {
        }
    }
}

/// <summary>Fluent 风格现代化 ContextMenuStrip 渲染器(全系统协同排版，支持深浅色与圆角高亮)。</summary>
internal sealed class FluentToolStripRenderer : ToolStripProfessionalRenderer
{
    private readonly bool _isDark;

    public FluentToolStripRenderer(bool isDark) : base(new FluentThemeColorTable(isDark))
    {
        _isDark = isDark;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        using var path = CreateRoundedRectangle(bounds, 4);
        using var brush = new SolidBrush(_isDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235));
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        // 原生标准渲染，保证与系统测量完美协同，不截断不重叠
        base.OnRenderItemText(e);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // 消除原生左侧凸起槽和阴影竖线，保持与菜单背景一致
        var g = e.Graphics;
        var bg = _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
        using var brush = new SolidBrush(bg);
        g.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_isDark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(220, 220, 220));
        g.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var y = e.Item.Height / 2;
        using var pen = new Pen(_isDark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(230, 230, 230));
        g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Fluent 风格调色板。</summary>
internal sealed class FluentThemeColorTable : ProfessionalColorTable
{
    private readonly bool _isDark;

    public FluentThemeColorTable(bool isDark)
    {
        _isDark = isDark;
    }

    public override Color ToolStripDropDownBackground => _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
    public override Color MenuBorder => _isDark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(220, 220, 220);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => _isDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235);
    public override Color MenuItemSelectedGradientBegin => _isDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235);
    public override Color MenuItemSelectedGradientEnd => _isDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235);
    public override Color MenuItemPressedGradientBegin => _isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);
    public override Color MenuItemPressedGradientEnd => _isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 220, 220);
    public override Color ImageMarginGradientBegin => _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
    public override Color ImageMarginGradientMiddle => _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
    public override Color ImageMarginGradientEnd => _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
    public override Color SeparatorDark => _isDark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(230, 230, 230);
    public override Color SeparatorLight => Color.Transparent;
}


