using System.Drawing;
using System.Windows.Forms;

namespace SyncClipboard.Tray;

public sealed class TrayManager : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private readonly Action _onActivate;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private readonly Action<string>? _log;
    private bool _disposed;

    public TrayManager(Action onActivate, Action onSettings, Action onExit, string baseDirectory, Action<string>? log = null)
    {
        _onActivate = onActivate;
        _onSettings = onSettings;
        _onExit = onExit;
        _log = log;

        _icons[TrayState.Disconnected] = MakeIcon(baseDirectory, "—", "#6B7280");
        _icons[TrayState.Connected] = MakeIcon(baseDirectory, "✓", "#2563EB");
        _icons[TrayState.Uploading] = MakeIcon(baseDirectory, "↑", "#2563EB");
        _icons[TrayState.Downloading] = MakeIcon(baseDirectory, "↓", "#2563EB");
        _icons[TrayState.Error] = MakeIcon(baseDirectory, "!", "#EF4444");

        var menu = new ContextMenuStrip();
        var itemOpen = new ToolStripMenuItem("打开剪贴板", null, (_, _) => _onActivate())
        {
            Font = new Font(Control.DefaultFont, FontStyle.Bold)
        };
        var itemSettings = new ToolStripMenuItem("设置", null, (_, _) => _onSettings());
        var itemExit = new ToolStripMenuItem("退出", null, (_, _) => _onExit());

        menu.Items.Add(itemOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(itemSettings);
        menu.Items.Add(itemExit);

        _notifyIcon = new NotifyIcon
        {
            Text = "NexClip 桌面端 (Ctrl+Alt+S)",
            Icon = _icons[TrayState.Disconnected],
            ContextMenuStrip = menu,
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

        _notifyIcon.DoubleClick += (_, _) => _onActivate();
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
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            if (File.Exists(iconPng))
            {
                using var baseImg = Image.FromFile(iconPng);
                g.DrawImage(baseImg, new Rectangle(0, 0, 32, 32));
            }
            else
            {
                using var brush = new SolidBrush(ParseHex(hex));
                using var font = new Font("Segoe UI Symbol", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, 32, 32), format);
            }

            if (glyph != "✓")
            {
                using var badgeBg = new SolidBrush(ParseHex(hex));
                g.FillEllipse(badgeBg, 16, 16, 16, 16);
                using var textBrush = new SolidBrush(Color.White);
                using var badgeFont = new Font("Segoe UI Symbol", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var badgeFmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(glyph, badgeFont, textBrush, new RectangleF(16, 16, 16, 16), badgeFmt);
            }

            var hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }
        catch
        {
            return SystemIcons.Application;
        }
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
