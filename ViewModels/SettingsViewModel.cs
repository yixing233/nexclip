using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private string hotkey = "Ctrl+Alt+V";

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
        BootStartEnabled = s.BootStartEnabled;
        StartMinimized = s.StartMinimized;
        CloseToTray = s.CloseToTray;
        MonitorEnabled = s.MonitorEnabled;
        AutoPaste = s.AutoPaste;
        NotifyEnabled = s.NotifyEnabled;
        Hotkey = s.Hotkey;
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

    [RelayCommand]
    public void ClearHistory()
    {
        _svc.Engine?.History.Clear();
        TestResult = "本地历史已清空";
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
    partial void OnBootStartEnabledChanged(bool value) { _svc.Settings.BootStartEnabled = value; _svc.Settings.Save(); }
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
        TestResult = ok ? "热键已应用: " + value : "热键格式非法或已被占用: " + value;
    }
    partial void OnMaxHistoryIndexChanged(int value) { _svc.Settings.MaxHistory = MaxHistoryOptions[value]; _svc.Settings.Save(); }
    partial void OnThemeModeIndexChanged(int value)
    {
        _svc.Settings.ThemeMode = value switch { 0 => "light", 1 => "dark", _ => "system" };
        _svc.Settings.Save();
        App.ApplyTheme(_svc.Settings.ThemeMode);
    }
}
