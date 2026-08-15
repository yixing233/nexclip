using System.Runtime.InteropServices;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 全局热键(RegisterHotKey + 消息窗口,无第三方依赖)。
/// 格式:"Ctrl+Alt+V" / "Ctrl+Shift+C" 等;注册失败(被占用)返回 false。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int GwlWndProc = -4;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    private readonly Action _callback;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;   // 防止被 GC
    private int _hotKeyId = -1;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    public HotKeyService(Action callback)
    {
        _callback = callback;
    }

    /// <summary>解析并注册热键。失败(格式非法或被占用)返回 false。</summary>
    public bool Apply(string? hotkey)
    {
        Unregister();
        var parsed = Parse(hotkey);
        if (parsed is null) return false;
        EnsureMessageWindow();
        if (_hwnd == IntPtr.Zero) return false;
        _hotKeyId = 1;
        if (!RegisterHotKey(_hwnd, 1, parsed.Value.Mods, parsed.Value.Vk))
        {
            _hotKeyId = -1;
            return false;
        }
        return true;
    }

    private void EnsureMessageWindow()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProc = WndProc;
        _hwnd = CreateWindowExW(0, "STATIC", "SyncClipboardHotKey", 0,
            0, 0, 0, 0, new IntPtr(-3 /* HWND_MESSAGE */), IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowLongPtrW(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey)
        {
            try
            {
                _callback();
            }
            catch (Exception ex)
            {
                Log.Error("全局热键回调异常", ex);
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Unregister()
    {
        if (_hwnd != IntPtr.Zero && _hotKeyId >= 0)
        {
            UnregisterHotKey(_hwnd, _hotKeyId);
        }
        _hotKeyId = -1;
    }

    public void Dispose()
    {
        Unregister();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static (uint Mods, uint Vk)? Parse(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        uint mods = 0;
        foreach (var p in parts[..^1])
        {
            mods |= p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" => ModAlt,
                "shift" => ModShift,
                "win" or "windows" => ModWin,
                _ => 0,
            };
        }
        if (mods == 0) return null;
        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return (mods, (uint)key[0]);
        }
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 12)
        {
            return (mods, (uint)(0x70 + fn - 1));   // VK_F1 = 0x70
        }
        return null;
    }
}
