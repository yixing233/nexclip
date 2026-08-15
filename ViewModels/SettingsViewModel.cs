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

    // ---- 服务端连接(编辑态,点"保存设置"落盘) ----
    [ObservableProperty]
    private string serverUrl = "";

    [ObservableProperty]
    private string authToken = "";

    [ObservableProperty]
    private string deviceName = "";

    [ObservableProperty]
    private string testResult = "";

    [ObservableProperty]
    private bool isTesting;

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

    /// <summary>当前编辑重命名的设备 id(非空时显示重命名输入)。</summary>
    [ObservableProperty]
    private string? renamingDeviceId;

    [ObservableProperty]
    private string renameText = "";

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
        AuthToken = s.AuthToken;
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
        s.AuthToken = AuthToken.Trim();
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

    /// <summary>加载设备列表(GET /api/devices)。</summary>
    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.AuthToken))
        {
            DeviceStatus = "未配置服务器,无法加载设备列表";
            return;
        }
        IsDevicesLoading = true;
        DeviceStatus = "";
        try
        {
            var list = await _svc.Api.GetDevicesAsync(s.ServerUrl, s.AuthToken);
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

    /// <summary>开始重命名(设置编辑态)。</summary>
    [RelayCommand]
    public void StartRename(DeviceInfo device)
    {
        RenamingDeviceId = device.Id;
        RenameText = device.Name ?? "";
    }

    /// <summary>取消重命名。</summary>
    [RelayCommand]
    public void CancelRename() => RenamingDeviceId = null;

    /// <summary>提交重命名。</summary>
    [RelayCommand]
    public async Task ConfirmRenameAsync()
    {
        var id = RenamingDeviceId;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(RenameText)) return;
        try
        {
            await _svc.Api.RenameDeviceAsync(_svc.Settings.ServerUrl, _svc.Settings.AuthToken, id, RenameText.Trim());
            DeviceStatus = "设备已重命名";
        }
        catch (Exception ex)
        {
            DeviceStatus = $"重命名失败:{ex.Message}";
        }
        finally
        {
            RenamingDeviceId = null;
            await RefreshDevicesAsync();
        }
    }

    /// <summary>移除设备。</summary>
    [RelayCommand]
    public async Task RemoveDeviceAsync(DeviceInfo device)
    {
        try
        {
            await _svc.Api.RemoveDeviceAsync(_svc.Settings.ServerUrl, _svc.Settings.AuthToken, device.Id);
            Devices.Remove(device);
            DeviceStatus = "设备已移除";
        }
        catch (Exception ex)
        {
            DeviceStatus = $"移除失败:{ex.Message}";
        }
    }

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
        IsTesting = true;
        TestResult = "正在测试…";
        try
        {
            var (ok, message) = await _svc.Api.TestConnectionAsync(ServerUrl.Trim(), AuthToken.Trim());
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
