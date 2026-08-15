using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static ClipboardWindow? ClipboardWindow { get; private set; }
    public static SettingsWindow? SettingsWindow { get; private set; }
    public static HotKeyService? Hotkey { get; private set; }

    private static readonly List<Window> Windows = new();
    private static bool _exiting;

    /// <summary>退出流程中(关闭窗口时不再拦截)。</summary>
    public static bool IsExiting => _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("UnhandledException", e.Exception);
            e.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = new AppServices();

        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // 剪贴板主窗口
        ClipboardWindow = new ClipboardWindow();
        RegisterWindow(ClipboardWindow);
        ApplyTheme(Services.Settings.ThemeMode);
        ClipboardWindow.Activate();

        // 托盘(右键菜单 owner = 剪贴板窗口)
        var trayOwner = WinRT.Interop.WindowNative.GetWindowHandle(ClipboardWindow);
        Services.Tray = new TrayIconService(ToggleClipboardWindow, OpenSettings, ExitApp, trayOwner);
        Services.Tray.Initialize();

        // 同步引擎 + 热键
        Services.Engine = new SyncEngine(Services, dispatcher);
        Services.Main.AttachEngine(Services.Engine);
        WireTrayState(Services);

        Hotkey = new HotKeyService(ToggleClipboardWindow);
        if (!Hotkey.Apply(Services.Settings.Hotkey))
        {
            Log.Warn($"全局热键注册失败(可能被占用):{Services.Settings.Hotkey}");
        }

        Services.Engine.Start();

        // 启动即最小化到托盘(设置控制)
        if (Services.Settings.StartMinimized)
        {
            ClipboardWindow.AppWindow.Hide();
        }
    }

    private static void RegisterWindow(Window window)
    {
        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
    }

    /// <summary>切换剪贴板窗口显示/隐藏(托盘左键 / 全局热键)。</summary>
    private static void ToggleClipboardWindow() => ClipboardWindow?.ToggleVisibility();

    /// <summary>打开设置窗口(托盘菜单 / 剪贴板窗口按钮)。</summary>
    public static void OpenSettings()
    {
        if (SettingsWindow is null)
        {
            SettingsWindow = new SettingsWindow();
            RegisterWindow(SettingsWindow);
            ApplyTheme(Services.Settings.ThemeMode);
        }
        SettingsWindow.AppWindow.Show();
        SettingsWindow.Activate();
    }

    /// <summary>退出应用(托盘菜单)。</summary>
    private static void ExitApp()
    {
        _exiting = true;
        Hotkey?.Dispose();
        Services.Engine?.Dispose();
        Services.Tray?.Dispose();
        ClipboardWindow?.Close();
        SettingsWindow?.Close();
        Environment.Exit(0);
    }

    /// <summary>托盘图标联动(设计文档 §3.5)。</summary>
    private static void WireTrayState(AppServices svc)
    {
        if (svc.Engine is null || svc.Tray is null) return;
        var tray = svc.Tray;
        svc.Engine.ConnectionChanged += (state, _) => tray.SetState(
            state is SyncEngine.ConnState.Connected or SyncEngine.ConnState.Reconnecting
                ? TrayIconService.TrayState.Connected
                : TrayIconService.TrayState.Disconnected);
        svc.Engine.TransferChanged += (active, kind) =>
        {
            if (!active)
            {
                tray.SetState(svc.Engine.State is SyncEngine.ConnState.Connected or SyncEngine.ConnState.Reconnecting
                    ? TrayIconService.TrayState.Connected
                    : TrayIconService.TrayState.Disconnected);
                return;
            }
            tray.SetState(kind is SyncEngine.TransferKind.Upload
                ? TrayIconService.TrayState.Uploading
                : TrayIconService.TrayState.Downloading);
        };
        svc.Engine.SyncError += () => tray.SetState(TrayIconService.TrayState.Error);
    }

    /// <summary>应用浅色/深色/跟随系统主题(作用于所有已打开窗口)。</summary>
    public static void ApplyTheme(string mode)
    {
        var requested = mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        foreach (var window in Windows.ToArray())
        {
            if (window.Content is FrameworkElement root)
            {
                root.RequestedTheme = requested;
            }
        }
    }
}
