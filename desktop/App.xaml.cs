using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NexClip.Desktop.Services;

namespace NexClip.Desktop;

public partial class App : Application
{
    private const string MutexName = "NexClip_SingleInstance";
    private const string ShowEventName = "NexClip_Show";

    public static AppServices Services { get; private set; } = null!;
    public static ClipboardWindow? ClipboardWindow { get; private set; }
    public static SettingsWindow? SettingsWindow { get; private set; }
    public static HotKeyService? Hotkey { get; private set; }            // 剪贴板呼出(Alt+V)
    public static HotKeyService? HotkeySettings { get; private set; }       // 设置打开(Alt+X)
    public static HotKeyService? HotkeyOpenUrl { get; private set; }        // 打开复制的链接(Ctrl+Alt+O)
    public static LinkToastWindow? LinkToast { get; private set; }          // 右下角链接卡片

    /// <summary>复制到链接时,在屏幕右下角显示链接卡片(UI 线程调用)。</summary>
    public static void ShowLinkToast(string url)
    {
        try
        {
            Log.Debug($"显示链接卡片:{url}");
            LinkToast?.Close();
            LinkToast = new LinkToastWindow(url);
        }
        catch (Exception ex)
        {
            Log.Error("显示链接卡片失败", ex);
        }
    }

    /// <summary>打开"复制的网址":优先当前剪贴板中的链接,否则取最近一条链接历史。</summary>
    public static void OpenCopiedUrl()
    {
        try
        {
            var url = "";
            try
            {
                if (global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                    is { } content && content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    url = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                        ?.GetTextAsync()?.AsTask()?.GetAwaiter().GetResult() ?? "";
                }
            }
            catch { url = ""; }
            if (!UrlUtil.IsUrl(url))
            {
                // 回退:最近一条链接历史
                var items = Services.History.Query(urlOnly: true, limit: 1);
                url = items.FirstOrDefault()?.Text ?? "";
            }
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                Log.Debug($"打开链接:{uri}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            else
            {
                Log.Warn($"打开链接:剪贴板无有效网址 url='{url}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开复制的链接失败", ex);
        }
    }

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
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var waiter = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent?.WaitOne(); } catch { return; }
                _showEvent?.Reset();
                dispatcher?.TryEnqueue(ShowClipboardWindow);
            }
        })
        { IsBackground = true };
        waiter.Start();

        Services = new AppServices();

        // 静默启动:开机自启动(--autostart 由注册表 Run 键传入)或设置了"启动即最小化"时不显示窗口、不抢焦点
        var silentStart = Environment.GetCommandLineArgs().Contains("--autostart")
                          || Services.Settings.StartMinimized;

        // 剪贴板主窗口
        ClipboardWindow = new ClipboardWindow();
        RegisterWindow(ClipboardWindow);
        ApplyTheme(Services.Settings.ThemeMode);
        if (!silentStart)
        {
            ClipboardWindow.ShowWindow();
        }

        // 托盘(右键菜单 owner = 剪贴板窗口;静默启动时窗口未显示,但句柄已存在,托盘仍可用)
        var trayOwner = WinRT.Interop.WindowNative.GetWindowHandle(ClipboardWindow);
        Services.Tray = new TrayIconService(
            () => dispatcher?.TryEnqueue(ToggleClipboardWindow),
            () => dispatcher?.TryEnqueue(OpenSettings),
            () => dispatcher?.TryEnqueue(ExitApp),
            trayOwner);
        Services.Tray.Initialize();

        // 同步引擎 + 热键
        Services.Engine = new SyncEngine(Services, dispatcher);
        // 应用数据保留策略:条目上限 + 时间上限(启动即清理)
        Services.History.MaxEntries = Services.Settings.MaxHistory;
        Services.History.PruneOlderThan(Services.Settings.RetentionDays);
        Services.Main.AttachEngine(Services.Engine);
        // 历史列表 VM 与 互传 VM 在窗口构造时可能早于 Engine 创建,这里补挂接并首刷
        Services.HistoryVm.AttachEngine(Services.Engine);
        Services.ChatVm.AttachEngine(Services.Engine);
        _ = Services.HistoryVm.RefreshAsync();
        WireTrayState(Services);

        Hotkey = new HotKeyService(() => dispatcher?.TryEnqueue(ToggleClipboardWindow));
        if (!Hotkey.Apply(Services.Settings.Hotkey))
        {
            Log.Warn($"剪贴板热键注册失败(可能被占用):{Services.Settings.Hotkey}");
        }
        HotkeySettings = new HotKeyService(() => dispatcher?.TryEnqueue(ToggleSettingsWindow));
        if (!HotkeySettings.Apply(Services.Settings.HotkeySettings))
        {
            Log.Warn($"设置热键注册失败(可能被占用):{Services.Settings.HotkeySettings}");
        }
        HotkeyOpenUrl = new HotKeyService(() =>
            dispatcher?.TryEnqueue(OpenCopiedUrl));
        if (!HotkeyOpenUrl.Apply(Services.Settings.HotkeyOpenUrl))
        {
            Log.Warn($"打开链接热键注册失败(可能被占用):{Services.Settings.HotkeyOpenUrl}");
        }
        // SettingsViewModel 构造时热键服务尚未创建,此处注册完成后补刷新一次状态,
        // 避免把实际已注册的热键误显示为“被其他程序占用”。
        Services.SettingsVm.RefreshHotkeyStatus();

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

    /// <summary>显式显示剪贴板窗口(如单实例唤醒)。</summary>
    private static void ShowClipboardWindow() => ClipboardWindow?.ShowWindow();

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
            var isNewWindow = false;
            if (SettingsWindow is null || !IsWindowAlive(SettingsWindow))
            {
                SettingsWindow = new SettingsWindow();
                RegisterWindow(SettingsWindow);
                ApplyTheme(Services.Settings.ThemeMode);
                isNewWindow = true;
            }
            SettingsWindow.PrepareForShow(isNewWindow);
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
        HotkeySettings?.Dispose();
        HotkeyOpenUrl?.Dispose();
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

    /// <summary>按设置创建窗口背景材质:云母 / 云母增强(BaseAlt) / 亚克力。</summary>
    public static Microsoft.UI.Xaml.Media.SystemBackdrop CreateBackdrop(string? mode = null)
    {
        return (mode ?? Services.Settings.BackdropMode) switch
        {
            "MicaAlt" => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            "Acrylic" => CreateAcrylicBackdrop(),
            _ => new Microsoft.UI.Xaml.Media.MicaBackdrop(),
        };
    }

    /// <summary>
    /// 创建桌面亚克力背景。WinUI 的 DesktopAcrylicBackdrop 本身没有 tint 属性,
    /// 它的着色来自主题资源 AcrylicBackgroundFillColorDefaultBrush / AcrylicInAppFillColorDefaultBrush。
    /// 这里按设置覆盖这两个资源(保留原主题底色,只改不透明度),新背景创建时即采样。
    /// </summary>
    private static Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop CreateAcrylicBackdrop()
    {
        ApplyAcrylicTint();
        return new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
    }

    /// <summary>把"亚克力不透明度"(0~1,越大越不透明)写入主题资源,供 DesktopAcrylicBackdrop 采样。</summary>
    private static void ApplyAcrylicTint()
    {
        try
        {
            var opacity = Math.Clamp(Services.Settings.BackdropTintOpacity, 0.4, 1.0);
            // 保留当前主题默认的底色(深色约 #2C2C2C;浅色为主题窗口色),只改不透明度
            global::Windows.UI.Color baseColor;
            var existing = Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"];
            switch (existing)
            {
                case Microsoft.UI.Xaml.Media.AcrylicBrush ab:
                    baseColor = ab.TintColor;
                    break;
                case Microsoft.UI.Xaml.Media.SolidColorBrush sb:
                    baseColor = sb.Color;
                    break;
                default:
                    baseColor = ParseColor("#2C2C2C");
                    break;
            }

            var brush = new Microsoft.UI.Xaml.Media.AcrylicBrush
            {
                TintColor = baseColor,
                TintOpacity = opacity,
                TintLuminosityOpacity = 1.0,
                FallbackColor = baseColor,
                AlwaysUseFallback = false,
            };
            Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"] = brush;
            Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"] = brush;
            Log.Debug($"亚克力不透明度已应用: {opacity:0.00}");
        }
        catch (Exception ex)
        {
            Log.Error("应用亚克力不透明度失败", ex);
        }
    }

    private static global::Windows.UI.Color ParseColor(string hex)
    {
        var v = hex.TrimStart('#');
        return global::Windows.UI.Color.FromArgb(255,
            Convert.ToByte(v.Substring(0, 2), 16),
            Convert.ToByte(v.Substring(2, 2), 16),
            Convert.ToByte(v.Substring(4, 2), 16));
    }

    /// <summary>切换窗口背景材质(作用于所有已打开窗口;设置页切换时调用)。</summary>
    public static void ApplyBackdrop(string mode)
    {
        foreach (var window in Windows.ToArray())
        {
            window.SystemBackdrop = CreateBackdrop(mode);
        }
    }
}
