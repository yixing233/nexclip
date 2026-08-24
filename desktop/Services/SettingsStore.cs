using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexClip.Desktop.Services;

/// <summary>
/// 设置存储:%APPDATA%/SyncClipboard/settings.json。
/// 设备接入使用配对码，AuthToken 保存服务端签发的设备令牌并通过 DPAPI 加密落盘。
/// IsPaired 表示本机已完成设备接入；旧版仅有此标记时由同步引擎自动迁移令牌。
/// </summary>
public sealed class SettingsStore
{
    private const int CurrentPasteKeyVersion = 1;

    // ---- 设置项(默认值与设计文档 §7 一致) ----
    public string ServerUrl { get; set; } = "http://127.0.0.1:5033";
    public string AuthToken { get; set; } = "";
    public bool IsPaired { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Hotkey { get; set; } = "Alt+V";            // 剪贴板窗口呼出
    public string HotkeySettings { get; set; } = "Alt+X";       // 设置窗口打开
    public string HotkeyOpenUrl { get; set; } = "Ctrl+Alt+O";   // 打开复制的链接
    public string ThemeMode { get; set; } = "system";   // light | dark | system
    /// <summary>窗口背景材质:Mica(云母) | MicaAlt(云母增强) | Acrylic(亚克力)。</summary>
    public string BackdropMode { get; set; } = "Mica";
    /// <summary>亚克力背景不透明度(0~1,越大越不透明/越不透)。默认 0.85 比系统默认更沉稳。</summary>
    public double BackdropTintOpacity { get; set; } = 0.85;
    public bool BootStartEnabled { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;
    public bool AutoPaste { get; set; } = true;
    public bool NotifyEnabled { get; set; } = true;
    /// <summary>复制链接时显示右下角直达卡片。</summary>
    public bool CopyDirectEnabled { get; set; } = true;
    public int MaxHistory { get; set; } = 200;
    /// <summary>剪贴板窗口出现位置:center=屏幕中心 | cursor=跟随鼠标(默认)。</summary>
    public string WindowPositionMode { get; set; } = "cursor";
    /// <summary>历史保留天数上限(0=不限,启动与设置变更时清理)。</summary>
    public int RetentionDays { get; set; } = 0;
    /// <summary>普通应用粘贴键:CtrlV(默认) | ShiftInsert;Chromium/Electron 固定使用 CtrlV。</summary>
    public string PasteKey { get; set; } = "CtrlV";
    /// <summary>数据储存目录(空=默认 %LOCALAPPDATA%/NexClip)。</summary>
    public string StorageDir { get; set; } = "";
    /// <summary>剪贴板窗口尺寸记忆(0=未记忆,首次显示用默认宽度=最小宽度)。</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    private static readonly string LegacyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SyncClipboard");
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexClip");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>默认数据目录:%LOCALAPPDATA%/NexClip。</summary>
    public static string DefaultStorageDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexClip");

    private static readonly string LegacyDefaultStorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncClipboard");

    public static void TryMigrateLegacyDirectories()
    {
        try
        {
            // 迁移 Roaming 配置 (settings.json)
            if (Directory.Exists(LegacyDir) && !Directory.Exists(Dir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Dir)!);
                Directory.Move(LegacyDir, Dir);
            }
            // 迁移 LocalAppData (history.db, app_icons, images)
            if (Directory.Exists(LegacyDefaultStorageDir) && !Directory.Exists(DefaultStorageDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultStorageDir)!);
                Directory.Move(LegacyDefaultStorageDir, DefaultStorageDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"迁移旧数据目录失败: {ex.Message}");
        }
    }

    /// <summary>实际使用的数据目录:设置项优先,为空则用默认目录。</summary>
    public string ResolveStorageDir()
    {
        if (string.IsNullOrWhiteSpace(StorageDir)) return DefaultStorageDir;
        return Path.GetFullPath(StorageDir.Trim());
    }

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
        TryMigrateLegacyDirectories();
        var saveMigratedSettings = false;
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(FilePath));
            if (dto is null) return;

            ServerUrl = dto.ServerUrl ?? ServerUrl;
            AuthToken = dto.AuthTokenEncrypted is { Length: > 0 }
                ? DpapiDecrypt(dto.AuthTokenEncrypted)
                : "";
            // 迁移兼容:旧版用 AuthToken 存在与否表示已配对
            IsPaired = dto.IsPaired ?? (dto.AuthTokenEncrypted is { Length: > 0 });
            DeviceId = dto.DeviceId ?? DeviceId;
            DeviceName = dto.DeviceName ?? DeviceName;
            Hotkey = dto.Hotkey ?? Hotkey;
            HotkeySettings = dto.HotkeySettings ?? HotkeySettings;
            HotkeyOpenUrl = dto.HotkeyOpenUrl ?? HotkeyOpenUrl;
            ThemeMode = dto.ThemeMode ?? ThemeMode;
            BackdropMode = dto.BackdropMode ?? BackdropMode;
            BackdropTintOpacity = dto.BackdropTintOpacity ?? BackdropTintOpacity;
            BootStartEnabled = dto.BootStartEnabled ?? BootStartEnabled;
            StartMinimized = dto.StartMinimized ?? StartMinimized;
            CloseToTray = dto.CloseToTray ?? CloseToTray;
            MonitorEnabled = dto.MonitorEnabled ?? MonitorEnabled;
            AutoPaste = dto.AutoPaste ?? AutoPaste;
            NotifyEnabled = dto.NotifyEnabled ?? NotifyEnabled;
            CopyDirectEnabled = dto.CopyDirectEnabled ?? CopyDirectEnabled;
            MaxHistory = dto.MaxHistory ?? MaxHistory;
            WindowPositionMode = dto.WindowPositionMode ?? WindowPositionMode;
            RetentionDays = dto.RetentionDays ?? RetentionDays;
            if ((dto.PasteKeyVersion ?? 0) < CurrentPasteKeyVersion)
            {
                PasteKey = "CtrlV";
                saveMigratedSettings = true;
            }
            else
            {
                PasteKey = dto.PasteKey ?? PasteKey;
            }
            StorageDir = dto.StorageDir ?? StorageDir;
            WindowWidth = dto.WindowWidth ?? WindowWidth;
            WindowHeight = dto.WindowHeight ?? WindowHeight;
        }
        catch (Exception ex)
        {
            Log.Error("设置加载失败,使用默认值", ex);
        }
        EnsureDefaults();
        if (saveMigratedSettings) Save();
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
                IsPaired = IsPaired,
                DeviceId = DeviceId,
                DeviceName = DeviceName,
                Hotkey = Hotkey,
                HotkeySettings = HotkeySettings,
                HotkeyOpenUrl = HotkeyOpenUrl,
                ThemeMode = ThemeMode,
                BackdropMode = BackdropMode,
                BackdropTintOpacity = BackdropTintOpacity,
                BootStartEnabled = BootStartEnabled,
                StartMinimized = StartMinimized,
                CloseToTray = CloseToTray,
                MonitorEnabled = MonitorEnabled,
                AutoPaste = AutoPaste,
                NotifyEnabled = NotifyEnabled,
                CopyDirectEnabled = CopyDirectEnabled,
                MaxHistory = MaxHistory,
                WindowPositionMode = WindowPositionMode,
                RetentionDays = RetentionDays,
                PasteKeyVersion = CurrentPasteKeyVersion,
                PasteKey = PasteKey,
                StorageDir = StorageDir,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
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

    /// <summary>磁盘 JSON 结构(设备凭证已加密)。</summary>
    private sealed class SettingsDto
    {
        public string? ServerUrl { get; set; }
        public string? AuthTokenEncrypted { get; set; }
        public bool? IsPaired { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? Hotkey { get; set; }
        public string? HotkeySettings { get; set; }
        public string? HotkeyOpenUrl { get; set; }
        public string? ThemeMode { get; set; }
        public string? BackdropMode { get; set; }
        public double? BackdropTintOpacity { get; set; }
        public bool? BootStartEnabled { get; set; }
        public bool? StartMinimized { get; set; }
        public bool? CloseToTray { get; set; }
        public bool? MonitorEnabled { get; set; }
        public bool? AutoPaste { get; set; }
        public bool? NotifyEnabled { get; set; }
        public bool? CopyDirectEnabled { get; set; }
        public int? MaxHistory { get; set; }
        public string? WindowPositionMode { get; set; }
        public int? RetentionDays { get; set; }
        public int? PasteKeyVersion { get; set; }
        public string? PasteKey { get; set; }
        public string? StorageDir { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
    }
}
