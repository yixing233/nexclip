using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop;

public partial class App : Application
{
    private const string MutexName = "SyncClipboardDesktop_SingleInstance";
    private const string ShowEventName = "SyncClipboardDesktop_Show";

    public static AppServices Services { get; private set; } = null!;
    public static ClipboardWindow? ClipboardWindow { get; private set; }
    public static SettingsWindow? SettingsWindow { get; private set; }
    public static HotKeyService? Hotkey { get; private set; }            // 剪贴板呼出(Alt+V)
    public static HotKeyService? HotkeySettings { get; private set; }       // 设置打开(Alt+X)

    private static readonly List<Window> Windows = new();
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;
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
        // 单实例:已有实例则唤醒其剪贴板窗口后退出本进程
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 开机自启动撞上已运行实例时不弹窗,静默退出
            if (!Environment.GetCommandLineArgs().Contains("--autostart"))
            {
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                    evt.Set();
                }
                catch { /* 旧实例可能尚未就绪 */ }
            }
            Environment.Exit(0);
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var waiter = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent?.WaitOne(); } catch { return; }
                _showEvent?.Reset();
                DispatcherQueue.GetForCurrentThread()?.TryEnqueue(ToggleClipboardWindow);
            }
        })
        { IsBackground = true };
        waiter.Start();

        Services = new AppServices();

        // 静默启动:开机自启动(--autostart 由注册表 Run 键传入)或设置了"启动即最小化"时不显示窗口、不抢焦点
        var silentStart = Environment.GetCommandLineArgs().Contains("--autostart")
                          || Services.Settings.StartMinimized;

        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // 剪贴板主窗口
        ClipboardWindow = new ClipboardWindow();
        RegisterWindow(ClipboardWindow);
        ApplyTheme(Services.Settings.ThemeMode);
        if (!silentStart)
        {
            ClipboardWindow.Activate();
        }

        // 托盘(右键菜单 owner = 剪贴板窗口;静默启动时窗口未显示,但句柄已存在,托盘仍可用)
        var trayOwner = WinRT.Interop.WindowNative.GetWindowHandle(ClipboardWindow);
        Services.Tray = new TrayIconService(ToggleClipboardWindow, OpenSettings, ExitApp, trayOwner);
        Services.Tray.Initialize();

        // 同步引擎 + 热键
        Services.Engine = new SyncEngine(Services, dispatcher);
        Services.Main.AttachEngine(Services.Engine);
        // 历史列表 VM 在窗口构造时可能早于 Engine 创建,这里补挂接并首刷
        Services.HistoryVm.AttachEngine(Services.Engine);
        _ = Services.HistoryVm.RefreshAsync();
        WireTrayState(Services);

        Hotkey = new HotKeyService(ToggleClipboardWindow);
        if (!Hotkey.Apply(Services.Settings.Hotkey))
        {
            Log.Warn($"剪贴板热键注册失败(可能被占用):{Services.Settings.Hotkey}");
        }
        HotkeySettings = new HotKeyService(ToggleSettingsWindow);
        if (!HotkeySettings.Apply(Services.Settings.HotkeySettings))
        {
            Log.Warn($"设置热键注册失败(可能被占用):{Services.Settings.HotkeySettings}");
        }

        Services.Engine.Start();

        // 静默启动:确保主窗口不显示
        if (silentStart)
        {
            ClipboardWindow.AppWindow.Hide();
        }
    }

    private static void RegisterWindow(Window window)
    {
        Windows.Add(window);
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            // 设置窗口关闭即销毁;清引用避免热键/托盘再打开时访问已销毁对象
            if (ReferenceEquals(window, SettingsWindow)) SettingsWindow = null;
        };
    }

    /// <summary>切换剪贴板窗口显示/隐藏(托盘左键 / 全局热键)。</summary>
    private static void ToggleClipboardWindow() => ClipboardWindow?.ToggleVisibility();

    /// <summary>窗口句柄是否仍然有效(Win32 视角,避开 WinUI 对象已销毁但引用残留的问题)。</summary>
    private static bool IsWindowAlive(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            return NativeMethods.IsWindow(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>窗口真实可见性(Win32 视角,避开 AppWindow.IsVisible 在 Hide 后的不可靠行为)。</summary>
    private static bool IsWindowActuallyVisible(Window window)
    {
        return IsWindowAlive(window) &&
               NativeMethods.IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    /// <summary>切换设置窗口显示/隐藏(设置热键)。</summary>
    private static void ToggleSettingsWindow()
    {
        try
        {
            if (SettingsWindow is null || !IsWindowActuallyVisible(SettingsWindow))
            {
                OpenSettings();
                return;
            }
            SettingsWindow.AppWindow.Hide();
        }
        catch (Exception ex)
        {
            Log.Error("切换设置窗口失败", ex);
        }
    }

    /// <summary>打开设置窗口(托盘菜单 / 剪贴板窗口按钮)。</summary>
    public static void OpenSettings()
    {
        try
        {
            if (SettingsWindow is null || !IsWindowAlive(SettingsWindow))
            {
                SettingsWindow = new SettingsWindow();
                RegisterWindow(SettingsWindow);
                ApplyTheme(Services.Settings.ThemeMode);
            }
            SettingsWindow.AppWindow.Show();
            SettingsWindow.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(SettingsWindow);
            if (NativeMethods.GetForegroundWindow() != hwnd)
            {
                NativeMethods.ForceForeground(hwnd);
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开设置窗口失败", ex);
        }
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
