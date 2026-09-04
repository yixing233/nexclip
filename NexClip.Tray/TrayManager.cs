using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Svg;

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
    private readonly FluentContextMenuStrip _menu;

    private readonly FluentMenuItem _itemOpen;
    private readonly FluentMenuItem _itemUpdate;
    private readonly FluentMenuItem _itemSettings;
    private readonly FluentMenuItem _itemRestart;
    private readonly FluentMenuItem _itemExit;
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

        var menuFont = CreateMenuFont(9.25f, FontStyle.Regular);

        _menu = new FluentContextMenuStrip
        {
            ShowImageMargin = true,
            ShowCheckMargin = false,
            ImageScalingSize = new Size(16, 16),
            AutoSize = true,
            DropShadowEnabled = false, // 禁用系统旧版方角灰边阴影，由 DWM 沉浸式圆角/边框接管
            Font = menuFont
        };

        _menu.Opened += (_, _) =>
        {
            ApplyDwmMenuEffects(_menu.Handle, _isDarkTheme);
        };

        _itemOpen = new FluentMenuItem("打开剪贴板", null, (_, _) => _onShow())
        {
            Font = menuFont
        };

        _itemUpdate = new FluentMenuItem("检查更新...", null, (_, _) => _onCheckUpdate())
        {
            Font = menuFont
        };

        _itemSettings = new FluentMenuItem("设置", null, (_, _) => _onSettings())
        {
            Font = menuFont
        };

        _separator = new ToolStripSeparator
        {
            Margin = new Padding(0, 4, 0, 4)
        };

        _itemRestart = new FluentMenuItem("重启 NexClip", null, (_, _) => _onRestart())
        {
            Font = menuFont
        };

        _itemExit = new FluentMenuItem("退出", null, (_, _) => _onExit(), isDanger: true)
        {
            Font = menuFont
        };

        _menu.Items.Add(_itemOpen);
        _menu.Items.Add(_itemUpdate);
        _menu.Items.Add(_itemSettings);
        _menu.Items.Add(_separator);
        _menu.Items.Add(_itemRestart);
        _menu.Items.Add(_itemExit);

        SetTheme(false); // 默认浅色，后续由 TrayIconService 同步实际系统主题

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

    private static Font CreateMenuFont(float size, FontStyle style)
    {
        string[] preferredFonts = ["Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI"];
        foreach (var fontName in preferredFonts)
        {
            try
            {
                using var testFont = new Font(fontName, size, style, GraphicsUnit.Point);
                if (testFont.Name.Equals(fontName, StringComparison.OrdinalIgnoreCase))
                {
                    return new Font(fontName, size, style, GraphicsUnit.Point);
                }
            }
            catch
            {
            }
        }
        return new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
    }

    /// <summary>动态更新托盘菜单显示的快捷键字符串。</summary>
    public void UpdateHotkeys(string? clipboardHotkey, string? settingsHotkey)
    {
        try
        {
            _itemOpen.ShortcutKeyDisplayString = string.IsNullOrWhiteSpace(clipboardHotkey) ? "" : clipboardHotkey.Trim();
            _itemSettings.ShortcutKeyDisplayString = string.IsNullOrWhiteSpace(settingsHotkey) ? "" : settingsHotkey.Trim();
            _menu.Invalidate();
        }
        catch
        {
        }
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
            var foreColor = isDark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27);
            var exitColor = isDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(220, 38, 38);
            var iconColor = isDark ? Color.FromArgb(212, 212, 216) : Color.FromArgb(63, 63, 70);

            _itemOpen.ForeColor = foreColor;
            _itemUpdate.ForeColor = foreColor;
            _itemSettings.ForeColor = foreColor;
            _itemRestart.ForeColor = foreColor;
            _itemExit.ForeColor = exitColor;

            // 动态生成饱满高清晰度 16x16 官方正版 Lucide 矢量图标
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

            _menu.BackColor = isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
            _menu.Invalidate();
        }
        catch (Exception ex)
        {
            _log?.Invoke("SetTheme error: " + ex.Message);
        }
    }

    private enum TrayIconType { Clipboard, Refresh, Settings, Restart, Exit }

    /// <summary>
    /// 从项目内置的 Lucide 官方 SVG 资源渲染菜单图标。
    /// </summary>
    private static Bitmap DrawLucideIcon(TrayIconType type, Color color, int size = 16)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        string assetName = type switch
        {
            TrayIconType.Clipboard => "clipboard.svg",
            TrayIconType.Refresh => "refresh-cw.svg",
            TrayIconType.Settings => "settings.svg",
            TrayIconType.Restart => "rotate-cw.svg",
            TrayIconType.Exit => "x.svg",
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        string resourceName = $"NexClip.Tray.Assets.lucide.{assetName}";
        using var stream = typeof(TrayManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded Lucide asset: {assetName}");
        using var reader = new StreamReader(stream);
        var document = SvgDocument.FromSvg<SvgDocument>(reader.ReadToEnd());
        document.Color = new SvgColourServer(color);
        return document.Draw(size, size);
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

/// <summary>去除系统默认方角背景与残留方角阴影的 Fluent 现代上下文菜单容器。</summary>
internal sealed class FluentContextMenuStrip : ContextMenuStrip
{
    private const int CS_DROPSHADOW = 0x00020000;
    private bool _stretchingItems;

    protected override Padding DefaultPadding => FluentMenuLayout.OuterPadding;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // 剥离系统原生方角阴影 class style，杜绝底层方角黑影/灰角
            cp.ClassStyle &= ~CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        StretchItemsToClientWidth();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        StretchItemsToClientWidth();
        Invalidate(true);
    }

    private void StretchItemsToClientWidth()
    {
        if (_stretchingItems || ClientSize.Width <= 0)
        {
            return;
        }

        _stretchingItems = true;
        try
        {
            foreach (ToolStripItem item in Items)
            {
                int width = Math.Max(1, ClientSize.Width - item.Bounds.Left - item.Margin.Right);
                item.AutoSize = false;
                item.Width = width;
            }
        }
        finally
        {
            _stretchingItems = false;
        }
    }
}

internal static class FluentMenuLayout
{
    internal const int ItemHeight = 34;
    internal const int MinimumItemWidth = 220;
    internal const int IconSize = 16;
    internal const int TextLeft = 40;
    internal const int ContentRight = 16;
    internal const int ShortcutGap = 16;

    internal static Padding OuterPadding => new(4, 6, 4, 6);

    internal static Rectangle GetItemBackgroundBounds(Size itemSize) =>
        Rectangle.FromLTRB(4, 2, Math.Max(4, itemSize.Width - 4), Math.Max(2, itemSize.Height - 2));

    internal static Rectangle GetIconBounds(Size itemSize) =>
        new(12, Math.Max(0, (itemSize.Height - IconSize) / 2), IconSize, IconSize);

    internal static Rectangle GetShortcutBounds(Size itemSize, int shortcutWidth)
    {
        int right = Math.Max(0, itemSize.Width - ContentRight);
        int left = Math.Max(TextLeft, right - Math.Max(0, shortcutWidth));
        return Rectangle.FromLTRB(left, 0, right, Math.Max(0, itemSize.Height));
    }

    internal static Rectangle GetTextBounds(Size itemSize, int shortcutWidth)
    {
        int right = shortcutWidth > 0
            ? GetShortcutBounds(itemSize, shortcutWidth).Left - ShortcutGap
            : itemSize.Width - ContentRight;
        return Rectangle.FromLTRB(TextLeft, 0, Math.Max(TextLeft, right), Math.Max(0, itemSize.Height));
    }
}

/// <summary>支持精确测量宽度、消除截断并承载 Windows 11 Fluent 规范的菜单项控件。</summary>
internal sealed class FluentMenuItem : ToolStripMenuItem
{
    public bool IsDanger { get; }

    public FluentMenuItem(string text, Image? image, EventHandler? onClick, bool isDanger = false)
        : base(text, image, onClick)
    {
        IsDanger = isDanger;
        AutoSize = true;
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var font = Font ?? Control.DefaultFont;
        const TextFormatFlags measureFlags = TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPrefix |
                                             TextFormatFlags.NoPadding;
        var textSize = TextRenderer.MeasureText(Text, font, Size.Empty, measureFlags);
        var shortcutSize = string.IsNullOrWhiteSpace(ShortcutKeyDisplayString)
            ? Size.Empty
            : TextRenderer.MeasureText(ShortcutKeyDisplayString, font, Size.Empty, measureFlags);

        int width = FluentMenuLayout.TextLeft + textSize.Width + FluentMenuLayout.ContentRight;
        if (!shortcutSize.IsEmpty)
        {
            width += FluentMenuLayout.ShortcutGap + shortcutSize.Width;
        }

        return new Size(Math.Max(width, FluentMenuLayout.MinimumItemWidth), FluentMenuLayout.ItemHeight);
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
        var bounds = FluentMenuLayout.GetItemBackgroundBounds(e.Item.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(bounds, 5);

        var isDanger = e.Item is FluentMenuItem { IsDanger: true } || e.Item.Text == "退出";
        Color backColor;
        if (isDanger)
        {
            backColor = _isDark ? Color.FromArgb(58, 28, 28) : Color.FromArgb(254, 242, 242);
        }
        else
        {
            backColor = _isDark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(238, 240, 243);
        }

        using var brush = new SolidBrush(backColor);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null || e.Item.Width <= 0 || e.Item.Height <= 0) return;
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        g.DrawImage(e.Image, FluentMenuLayout.GetIconBounds(e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem mi)
        {
            base.OnRenderItemText(e);
            return;
        }

        var font = mi.Font ?? e.TextFont ?? SystemFonts.MessageBoxFont ?? Control.DefaultFont;

        var isDanger = mi is FluentMenuItem { IsDanger: true } || mi.Text == "退出";
        var textColor = isDanger
            ? (_isDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(220, 38, 38))
            : (_isDark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27));
        var shortcutColor = _isDark ? Color.FromArgb(161, 161, 170) : Color.FromArgb(113, 113, 122);

        var shortcutText = mi.ShortcutKeyDisplayString;
        int shortcutWidth = 0;
        if (!string.IsNullOrWhiteSpace(shortcutText))
        {
            shortcutWidth = TextRenderer.MeasureText(
                shortcutText,
                font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
        }

        var textRect = FluentMenuLayout.GetTextBounds(mi.Size, shortcutWidth);
        if (textRect.Width > 0 && textRect.Height > 0 && !string.IsNullOrEmpty(mi.Text))
        {
            const TextFormatFlags textFlags = TextFormatFlags.Left |
                                             TextFormatFlags.VerticalCenter |
                                             TextFormatFlags.EndEllipsis |
                                             TextFormatFlags.SingleLine |
                                             TextFormatFlags.NoPrefix |
                                             TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, mi.Text, font, textRect, textColor, textFlags);
        }

        if (!string.IsNullOrWhiteSpace(shortcutText) && mi.Width > 0 && mi.Height > 0)
        {
            var shortcutRect = FluentMenuLayout.GetShortcutBounds(mi.Size, shortcutWidth);
            const TextFormatFlags shortcutFlags = TextFormatFlags.Right |
                                                 TextFormatFlags.VerticalCenter |
                                                 TextFormatFlags.SingleLine |
                                                 TextFormatFlags.NoPrefix |
                                                 TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, shortcutText, font, shortcutRect, shortcutColor, shortcutFlags);
        }
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // 消除原生左侧凸起槽和阴影竖线，保持与菜单背景一致
        var g = e.Graphics;
        var bg = _isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
        using var brush = new SolidBrush(bg);
        g.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip.Width <= 1 || e.ToolStrip.Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_isDark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(228, 228, 231));
        g.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Item.Width <= 24 || e.Item.Height <= 0) return;
        var g = e.Graphics;
        var y = e.Item.Height / 2;
        using var pen = new Pen(_isDark ? Color.FromArgb(46, 46, 51) : Color.FromArgb(232, 234, 237));
        g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0) return path;

        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

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

    public override Color ToolStripDropDownBackground => _isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
    public override Color MenuBorder => _isDark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(228, 228, 231);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => _isDark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(241, 243, 246);
    public override Color MenuItemSelectedGradientBegin => _isDark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(241, 243, 246);
    public override Color MenuItemSelectedGradientEnd => _isDark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(241, 243, 246);
    public override Color MenuItemPressedGradientBegin => _isDark ? Color.FromArgb(60, 60, 68) : Color.FromArgb(230, 232, 236);
    public override Color MenuItemPressedGradientEnd => _isDark ? Color.FromArgb(60, 60, 68) : Color.FromArgb(230, 232, 236);
    public override Color ImageMarginGradientBegin => _isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
    public override Color ImageMarginGradientMiddle => _isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
    public override Color ImageMarginGradientEnd => _isDark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(252, 252, 253);
    public override Color SeparatorDark => _isDark ? Color.FromArgb(46, 46, 51) : Color.FromArgb(228, 228, 231);
    public override Color SeparatorLight => Color.Transparent;
}
