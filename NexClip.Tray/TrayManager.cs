using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NexClip.Tray;

public sealed class TrayManager : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private readonly Action _onActivate;
    private readonly Action _onShow;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly Action<string>? _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _itemOpen;
    private readonly ToolStripMenuItem _itemSettings;
    private readonly ToolStripMenuItem _itemExit;
    private bool _isDarkTheme;
    private bool _disposed;

    public TrayManager(Action onActivate, Action onShow, Action onSettings, Action onExit, string baseDirectory, Action<string>? log = null)
    {
        _onActivate = onActivate;
        _onShow = onShow;
        _onSettings = onSettings;
        _onExit = onExit;
        _log = log;

        _icons[TrayState.Disconnected] = MakeIcon(baseDirectory, "—", "#6B7280");
        _icons[TrayState.Connected] = MakeIcon(baseDirectory, "✓", "#2563EB");
        _icons[TrayState.Uploading] = MakeIcon(baseDirectory, "↑", "#2563EB");
        _icons[TrayState.Downloading] = MakeIcon(baseDirectory, "↓", "#2563EB");
        _icons[TrayState.Error] = MakeIcon(baseDirectory, "!", "#EF4444");

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            AutoSize = true
        };

        var defaultFont = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        var boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

        _itemOpen = new ToolStripMenuItem("打开剪贴板", null, (_, _) => _onShow())
        {
            Font = boldFont,
            Padding = new Padding(12, 6, 12, 6)
        };
        _itemSettings = new ToolStripMenuItem("设置", null, (_, _) => _onSettings())
        {
            Font = defaultFont,
            Padding = new Padding(12, 6, 12, 6)
        };
        _itemExit = new ToolStripMenuItem("退出", null, (_, _) => _onExit())
        {
            Font = defaultFont,
            Padding = new Padding(12, 6, 12, 6)
        };

        _menu.Items.Add(_itemOpen);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_itemSettings);
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

        _notifyIcon.DoubleClick += (_, _) => _onShow();
    }

    /// <summary>动态同步深色/浅色主题并刷新托盘菜单渲染。</summary>
    public void SetTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        try
        {
            _menu.Renderer = new FluentToolStripRenderer(isDark);
            var foreColor = isDark ? Color.FromArgb(243, 244, 246) : Color.FromArgb(17, 24, 39);
            _itemOpen.ForeColor = foreColor;
            _itemSettings.ForeColor = foreColor;
            _itemExit.ForeColor = foreColor;
            _menu.BackColor = isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(255, 255, 255);
            _menu.Invalidate();
        }
        catch
        {
        }
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

/// <summary>Fluent 风格现代化 ContextMenuStrip 渲染器(支持深/浅色模式)。</summary>
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var brush = new SolidBrush(_isDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235));
        g.FillRectangle(brush, bounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
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
