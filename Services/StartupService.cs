using Microsoft.Win32;

namespace NexClip.Desktop.Services;

/// <summary>
/// 开机自启动(unpackaged 应用):写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 值格式: "exe路径" --autostart —— --autostart 供启动时识别"本次来自开机自启",配合静默启动。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexClip";
    private static readonly string[] LegacyValueNames = ["SyncClipboard.Desktop", "NexClip.Desktop"];
    private const string AutoStartArg = " --autostart";

    /// <summary>当前是否已注册开机自启动。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key is null) return false;
            if (key.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s)) return true;
            foreach (var leg in LegacyValueNames)
            {
                if (key.GetValue(leg) is string ls && !string.IsNullOrWhiteSpace(ls)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置(true)/取消(false)开机自启动。返回是否成功。</summary>
    public static bool SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;
            foreach (var leg in LegacyValueNames)
            {
                try { key.DeleteValue(leg, false); } catch { /* ignore */ }
            }
            if (enable)
            {
                var exe = Environment.ProcessPath
                          ?? Path.Combine(AppContext.BaseDirectory, "NexClip.exe");
                key.SetValue(ValueName, $"\"{exe}\"{AutoStartArg}");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启动失败", ex);
            return false;
        }
    }
}
