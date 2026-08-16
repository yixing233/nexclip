using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SyncClipboard.Desktop.Models;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop.ViewModels;

/// <summary>设置页 VM:编辑态字段 + 行为开关(变更即保存)+ 测试连接。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;

    /// <summary>配对码生成完成事件(码 + 过期时间),供页面弹出对话框展示。</summary>
    public event Action<string, DateTime>? PairingCodeGenerated;

    // ---- 服务端连接(编辑态,点"保存设置"落盘) ----
    [ObservableProperty]
    private string serverUrl = "";

    [ObservableProperty]
    private string deviceName = "";

    /// <summary>已配对状态(配对码登记成功即视为已配对;同步接口免认证)。</summary>
    public bool IsPaired => _svc.Settings.IsPaired;

    [ObservableProperty]
    private string testResult = "";

    [ObservableProperty]
    private bool isTesting;

    // ---- 设备配对(配对码 → 设备专属 Token) ----
    [ObservableProperty]
    private string pairingCode = "";

    [ObservableProperty]
    private bool isPairing;

    [ObservableProperty]
    private string pairResult = "";

    // ---- 配对码生成 ----
    [ObservableProperty]
    private string generatedCode = "";

    [ObservableProperty]
    private string codeExpiryText = "";

    [ObservableProperty]
    private bool isGeneratingCode;

    // ---- 行为开关(变更即保存) ----
    [ObservableProperty]
    private bool bootStartEnabled;

    [ObservableProperty]
    private bool startMinimized;

    [ObservableProperty]
    private bool closeToTray;

    [ObservableProperty]
    private bool monitorEnabled;

    [ObservableProperty]
    private bool autoPaste;

    [ObservableProperty]
    private bool notifyEnabled;

    [ObservableProperty]
    private string hotkey = "Alt+V";            // 剪贴板呼出

    [ObservableProperty]
    private string hotkeySettings = "Alt+X";    // 设置打开

    // ---- 设备列表 ----
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    private string deviceStatus = "";

    [ObservableProperty]
    private bool isDevicesLoading;

    // ---- 历史 / 外观 ----
    [ObservableProperty]
    private int maxHistoryIndex;

    [ObservableProperty]
    private int themeModeIndex = 2;   // 0=浅色 1=深色 2=跟随系统

    public int[] MaxHistoryOptions { get; } = { 50, 100, 200, 500, 1000 };

    public string VersionText { get; } = "v" + typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3);

    public SettingsViewModel(AppServices svc)
    {
        _svc = svc;
        var s = svc.Settings;
        ServerUrl = s.ServerUrl;
        DeviceName = s.DeviceName;
        // 开机自启动以注册表实际状态为准(老版本仅存设置未写注册表)
        var bootStart = StartupService.IsEnabled();
        s.BootStartEnabled = bootStart;
        BootStartEnabled = bootStart;
        StartMinimized = s.StartMinimized;
        CloseToTray = s.CloseToTray;
        MonitorEnabled = s.MonitorEnabled;
        AutoPaste = s.AutoPaste;
        NotifyEnabled = s.NotifyEnabled;
        Hotkey = s.Hotkey;
        HotkeySettings = s.HotkeySettings;
        var idx = Array.IndexOf(MaxHistoryOptions, s.MaxHistory);
        MaxHistoryIndex = idx >= 0 ? idx : 2;
        ThemeModeIndex = s.ThemeMode switch { "light" => 0, "dark" => 1, _ => 2 };
    }

    [RelayCommand]
    public void SaveServerConfig()
    {
        var s = _svc.Settings;
        s.ServerUrl = ServerUrl.Trim();
        s.DeviceName = DeviceName.Trim();
        s.Save();
        _svc.Main.RefreshConnectionState();
        TestResult = "设置已保存,正在重新连接…";
        _ = _svc.Engine?.ReconfigureAsync();
    }

    /// <summary>重置剪贴板热键为默认 Alt+V。</summary>
    [RelayCommand]
    public void ResetHotkey()
    {
        if (Hotkey == "Alt+V")
        {
            TestResult = "剪贴板热键已是默认值 Alt+V";
            return;
        }
        Hotkey = "Alt+V";
    }

    /// <summary>重置设置热键为默认 Alt+X。</summary>
    [RelayCommand]
    public void ResetHotkeySettings()
    {
        if (HotkeySettings == "Alt+X")
        {
            TestResult = "设置热键已是默认值 Alt+X";
            return;
        }
        HotkeySettings = "Alt+X";
    }

    [RelayCommand]
    public void ClearHistory()
    {
        _svc.Engine?.History.Clear();
        TestResult = "本地历史已清空";
    }

    /// <summary>
    /// <summary>
    /// 生成一次性配对码(一码一设备,10 分钟有效),把码给另一台设备输入即可接入。
    /// </summary>
    [RelayCommand]
    public async Task GeneratePairingCodeAsync()
    {
        var url = ServerUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            PairResult = "请先填写服务器地址";
            return;
        }
        IsGeneratingCode = true;
        PairResult = "";
        GeneratedCode = "";
        try
        {
            // 携带本设备信息生成:服务端同步登记本设备,设备列表可看到本机
            var result = await _svc.Api.CreatePairingCodeAsync(url, _svc.Settings.DeviceId, _svc.Settings.DeviceName);
            if (result is null || string.IsNullOrWhiteSpace(result.Code))
            {
                PairResult = "生成失败:服务器未返回配对码";
                return;
            }
            GeneratedCode = result.Code;
            // 关闭即失效:不再按时间显示有效期
            CodeExpiryText = "关闭对话框后此配对码立即失效";
            PairResult = "配对码已生成:把此码输入到新设备的配对框即可接入";
            PairingCodeGenerated?.Invoke(result.Code, result.ExpiresAt);
            _ = RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            PairResult = $"生成失败:{ex.Message}";
        }
        finally
        {
            IsGeneratingCode = false;
        }
    }

    /// <summary>
    /// 作废当前配对码(关闭展示对话框后调用),码立即失效。
    /// </summary>
    public async Task RevokeGeneratedCodeAsync()
    {
        var code = GeneratedCode;
        if (string.IsNullOrWhiteSpace(code)) return;
        GeneratedCode = "";
        CodeExpiryText = "";
        try
        {
            await _svc.Api.RevokePairingCodeAsync(ServerUrl.Trim(), code);
            PairResult = "配对码已作废(对话框关闭)";
        }
        catch (Exception ex)
        {
            Log.Error($"配对码作废失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 配对:输入另一台设备生成的配对码,完成本设备接入登记。
    /// 新架构:配对仅登记,同步接口免认证;网页端管理用账密。
    /// </summary>
    [RelayCommand]
    public async Task PairDeviceAsync()
    {
        var code = PairingCode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            PairResult = "请输入配对码";
            return;
        }
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            PairResult = "请先填写服务器地址";
            return;
        }
        IsPairing = true;
        PairResult = "正在配对…";
        try
        {
            var result = await _svc.Api.PairAsync(ServerUrl.Trim(), code, s.DeviceId, s.DeviceName);
            if (result is null)
            {
                PairResult = "配对失败:服务器未返回设备信息";
                return;
            }
            // 配对即登记:同步接口免认证,无需保存任何令牌
            s.IsPaired = true;
            s.Save();
            _svc.Main.RefreshConnectionState();
            PairResult = $"配对成功:设备 {result.DeviceId} 已绑定";
            _ = _svc.Engine?.ReconfigureAsync();
            _ = RefreshDevicesAsync();
            OnPropertyChanged(nameof(IsPaired));
        }
        catch (ApiException ex)
        {
            PairResult = ex.StatusCode == System.Net.HttpStatusCode.BadRequest
                ? "配对失败:配对码无效、已使用或已过期"
                : $"配对失败:{ex.Message}";
        }
        catch (Exception ex)
        {
            PairResult = $"配对失败:{ex.Message}";
        }
        finally
        {
            IsPairing = false;
        }
    }

    /// <summary>加载设备列表(GET /api/devices)。</summary>
    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            DeviceStatus = "未配置服务器,无法加载设备列表";
            return;
        }
        IsDevicesLoading = true;
        DeviceStatus = "";
        try
        {
            // 设备列表 GET 免认证(管理操作在网页端)
            var list = await _svc.Api.GetDevicesAsync(s.ServerUrl, "");
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
            DeviceStatus = list.Count == 0 ? "暂无设备" : $"共 {list.Count} 台设备";
        }
        catch (Exception ex)
        {
            DeviceStatus = $"设备列表加载失败:{ex.Message}";
        }
        finally
        {
            IsDevicesLoading = false;
        }
    }

    // 注:设备移除/重命名属于管理操作,需网页端账密登录后执行;
    // 桌面端设备列表仅作展示与刷新。

    /// <summary>设备展示文本(名称 + 平台/版本/IP)。</summary>
    public static string DeviceSubtitle(DeviceInfo d)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Platform)) parts.Add(d.Platform);
        if (!string.IsNullOrWhiteSpace(d.Version)) parts.Add(d.Version);
        if (!string.IsNullOrWhiteSpace(d.Ip)) parts.Add(d.Ip);
        return string.Join(" · ", parts);
    }

    /// <summary>最后在线时间文案。</summary>
    public static string LastSeenText(DeviceInfo d)
    {
        if (d.Online) return "在线";
        var diff = DateTime.UtcNow - d.LastSeenAt;
        if (diff < TimeSpan.FromMinutes(1)) return "刚刚离线";
        if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} 分钟前离线";
        if (diff < TimeSpan.FromHours(24)) return $"{(int)diff.TotalHours} 小时前离线";
        return $"{(int)diff.TotalDays} 天前离线";
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        var s = _svc.Settings;
        if (!s.IsPaired)
        {
            TestResult = "尚未配对:请先用配对码完成设备配对";
            return;
        }
        IsTesting = true;
        TestResult = "正在测试…";
        try
        {
            var (ok, message) = await _svc.Api.TestConnectionAsync(ServerUrl.Trim(), "");
            TestResult = message;
        }
        catch (Exception ex)
        {
            TestResult = $"连接失败:{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ---- 开关变更即保存 ----
    partial void OnBootStartEnabledChanged(bool value)
    {
        _svc.Settings.BootStartEnabled = value;
        _svc.Settings.Save();
        var ok = StartupService.SetEnabled(value);
        if (!ok)
        {
            TestResult = value ? "开机自启动设置失败(注册表写入被拒)" : "开机自启动已取消,但注册表清理失败";
        }
    }
    partial void OnStartMinimizedChanged(bool value) { _svc.Settings.StartMinimized = value; _svc.Settings.Save(); }
    partial void OnCloseToTrayChanged(bool value) { _svc.Settings.CloseToTray = value; _svc.Settings.Save(); }
    partial void OnMonitorEnabledChanged(bool value) { _svc.Settings.MonitorEnabled = value; _svc.Settings.Save(); }
    partial void OnAutoPasteChanged(bool value) { _svc.Settings.AutoPaste = value; _svc.Settings.Save(); }
    partial void OnNotifyEnabledChanged(bool value) { _svc.Settings.NotifyEnabled = value; _svc.Settings.Save(); }
    partial void OnHotkeyChanged(string value)
    {
        _svc.Settings.Hotkey = value;
        _svc.Settings.Save();
        var ok = App.Hotkey?.Apply(value) ?? false;
        TestResult = ok ? "剪贴板热键已应用: " + value : "热键格式非法或已被占用: " + value;
    }

    partial void OnHotkeySettingsChanged(string value)
    {
        _svc.Settings.HotkeySettings = value;
        _svc.Settings.Save();
        var ok = App.HotkeySettings?.Apply(value) ?? false;
        TestResult = ok ? "设置热键已应用: " + value : "热键格式非法或已被占用: " + value;
    }
    partial void OnMaxHistoryIndexChanged(int value) { _svc.Settings.MaxHistory = MaxHistoryOptions[value]; _svc.Settings.Save(); }
    partial void OnThemeModeIndexChanged(int value)
    {
        _svc.Settings.ThemeMode = value switch { 0 => "light", 1 => "dark", _ => "system" };
        _svc.Settings.Save();
        App.ApplyTheme(_svc.Settings.ThemeMode);
    }
}
