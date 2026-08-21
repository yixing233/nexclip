using SyncClipboard.Desktop.ViewModels;

namespace SyncClipboard.Desktop.Services;

/// <summary>组合根:应用级单例。Engine/Tray 在 App.OnLaunched 中创建(需 UI 线程)。</summary>
public sealed class AppServices
{
    public SettingsStore Settings { get; }
    public HistoryStore History { get; }
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
        // 先解析储存目录:历史库与图片缓存共用该目录(默认 %LOCALAPPDATA%/SyncClipboard)
        var storageDir = Settings.ResolveStorageDir();
        ImageCodec.Initialize(storageDir);
        History = new HistoryStore(storageDir);
        Api = new ServerApi();
        Main = new MainViewModel(this);
        // HistoryVm 先于 SettingsVm 创建:设置项变更处理器(上限/保留期)会立即刷新历史列表
        HistoryVm = new HistoryViewModel(this);
        SettingsVm = new SettingsViewModel(this);
    }
}
