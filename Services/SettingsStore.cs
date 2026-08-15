using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 设置存储:%APPDATA%/SyncClipboard/settings.json。
/// 访问令牌用 DPAPI(CurrentUser)加密落盘,其余字段明文。
/// </summary>
public sealed class SettingsStore
{
    // ---- 设置项(默认值与设计文档 §7 一致) ----
    public string ServerUrl { get; set; } = "http://127.0.0.1:5033";
    public string AuthToken { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Hotkey { get; set; } = "Alt+V";            // 剪贴板窗口呼出
    public string HotkeySettings { get; set; } = "Alt+X";       // 设置窗口打开
    public string ThemeMode { get; set; } = "system";   // light | dark | system
    public bool BootStartEnabled { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;
    public bool AutoPaste { get; set; } = true;
    public bool NotifyEnabled { get; set; } = true;
    public int MaxHistory { get; set; } = 200;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SyncClipboard");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>补齐默认值(首次运行生成设备标识)。</summary>
    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            DeviceId = Guid.NewGuid().ToString("N");
        }
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            DeviceName = Environment.MachineName;
        }
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ServerUrl = "http://127.0.0.1:5033";
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(FilePath));
            if (dto is null) return;

            ServerUrl = dto.ServerUrl ?? ServerUrl;
            AuthToken = dto.AuthTokenEncrypted is { Length: > 0 }
                ? DpapiDecrypt(dto.AuthTokenEncrypted)
                : "";
            DeviceId = dto.DeviceId ?? DeviceId;
            DeviceName = dto.DeviceName ?? DeviceName;
            Hotkey = dto.Hotkey ?? Hotkey;
            HotkeySettings = dto.HotkeySettings ?? HotkeySettings;
            ThemeMode = dto.ThemeMode ?? ThemeMode;
            BootStartEnabled = dto.BootStartEnabled ?? BootStartEnabled;
            StartMinimized = dto.StartMinimized ?? StartMinimized;
            CloseToTray = dto.CloseToTray ?? CloseToTray;
            MonitorEnabled = dto.MonitorEnabled ?? MonitorEnabled;
            AutoPaste = dto.AutoPaste ?? AutoPaste;
            NotifyEnabled = dto.NotifyEnabled ?? NotifyEnabled;
            MaxHistory = dto.MaxHistory ?? MaxHistory;
        }
        catch (Exception ex)
        {
            Log.Error("设置加载失败,使用默认值", ex);
        }
        EnsureDefaults();
    }

    public void Save()
    {
        EnsureDefaults();
        try
        {
            Directory.CreateDirectory(Dir);
            var dto = new SettingsDto
            {
                ServerUrl = ServerUrl,
                AuthTokenEncrypted = AuthToken.Length > 0 ? DpapiEncrypt(AuthToken) : null,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                Hotkey = Hotkey,
                HotkeySettings = HotkeySettings,
                ThemeMode = ThemeMode,
                BootStartEnabled = BootStartEnabled,
                StartMinimized = StartMinimized,
                CloseToTray = CloseToTray,
                MonitorEnabled = MonitorEnabled,
                AutoPaste = AutoPaste,
                NotifyEnabled = NotifyEnabled,
                MaxHistory = MaxHistory,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error("设置保存失败", ex);
        }
    }

    // ---- DPAPI ----
    private static string? DpapiEncrypt(string plain)
    {
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string DpapiDecrypt(string encryptedBase64)
    {
        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(encryptedBase64), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>磁盘 JSON 结构(令牌已加密)。</summary>
    private sealed class SettingsDto
    {
        public string? ServerUrl { get; set; }
        public string? AuthTokenEncrypted { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? Hotkey { get; set; }
        public string? HotkeySettings { get; set; }
        public string? ThemeMode { get; set; }
        public bool? BootStartEnabled { get; set; }
        public bool? StartMinimized { get; set; }
        public bool? CloseToTray { get; set; }
        public bool? MonitorEnabled { get; set; }
        public bool? AutoPaste { get; set; }
        public bool? NotifyEnabled { get; set; }
        public int? MaxHistory { get; set; }
    }
}
