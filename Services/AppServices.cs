using SyncClipboard.Desktop.ViewModels;

namespace SyncClipboard.Desktop.Services;

/// <summary>组合根:应用级单例。Engine/Tray 在 App.OnLaunched 中创建(需 UI 线程)。</summary>
public sealed class AppServices
{
    public SettingsStore Settings { get; }
    public HistoryStore History { get; } = new();
    public HistoryViewModel HistoryVm { get; }
    public ServerApi Api { get; }
    public MainViewModel Main { get; }
    public SettingsViewModel SettingsVm { get; }
    public SyncEngine? Engine { get; set; }
    public TrayIconService? Tray { get; set; }

    public AppServices()
    {
        Settings = new SettingsStore();
        Settings.Load();
        Api = new ServerApi();
        Main = new MainViewModel(this);
        SettingsVm = new SettingsViewModel(this);
        HistoryVm = new HistoryViewModel(this);
    }
}
