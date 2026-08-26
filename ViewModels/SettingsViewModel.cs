using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexClip.Desktop.Models;
using NexClip.Desktop.Services;

namespace NexClip.Desktop.ViewModels;

/// <summary>设置页 VM:编辑态字段 + 行为开关(变更即保存)+ 测试连接。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private bool _initialized;

    /// <summary>配对码生成完成事件,供页面弹出对话框展示(含配对码、过期时间及用户ID)。</summary>
    public event Action<PairingCodeResult>? PairingCodeGenerated;

    // ---- 服务端连接(编辑态,点"保存设置"落盘) ----
    [ObservableProperty]
    private string serverUrl = "";

    [ObservableProperty]
    private string deviceName = "";

    /// <summary>已配对状态；同步接口同时要求有效的设备令牌。</summary>
    public bool IsPaired => _svc.Settings.IsPaired;

    public string PairingStatusText => IsPaired ? "已完成配对" : "尚未配对";

    public string PairingStatusHint => IsPaired
        ? "本机可以同步剪贴板并查看其他设备状态"
        : "生成或输入配对码，完成本机接入";

    public string DeviceIdText => _svc.Settings.DeviceId;

    // ---- 统一消息提示(InfoBar:普通/成功/错误) ----
    [ObservableProperty]
    private string messageText = "";

    [ObservableProperty]
    private InfoBarSeverity messageSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool messageOpen;

    /// <summary>显示消息(空文本关闭)。severity:Informational=普通 Success=成功 Error=错误。</summary>
    public void ShowMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        MessageSeverity = severity;
        MessageText = text;
        MessageOpen = !string.IsNullOrEmpty(text);
    }

    // ---- 快捷键页持久状态(长驻错误如热键占用,显示在对应热键项下方) ----
    [ObservableProperty]
    private string hotkeyStatusText = "";

    [ObservableProperty]
    private bool hasHotkeyIssue;

    [ObservableProperty]
    private string hotkeySettingsStatusText = "";

    [ObservableProperty]
    private bool hasHotkeySettingsIssue;

    // 快捷键反馈也进入统一消息宿主;长久态占用错误仍保留在对应设置项下方。
    public void ShowHotkeyMessage(string text, InfoBarSeverity severity = InfoBarSeverity.Informational) =>
        ShowMessage(text, severity);

    [ObservableProperty]
    private string testResult = "";

    [ObservableProperty]
    private bool isTesting;

    // ---- 设备配对(6 位纯数字验证码单向即入) ----
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
    private bool autoCheckUpdate;

    [ObservableProperty]
    private bool monitorEnabled;

    [ObservableProperty]
    private bool autoPaste;

    [ObservableProperty]
    private bool notifyEnabled;

    [ObservableProperty]
    private bool copyDirectEnabled;

    [ObservableProperty]
    private bool smartColorEnabled;

    [ObservableProperty]
    private bool smartPathEnabled;

    [ObservableProperty]
    private bool smartDeepLinkEnabled;

    [ObservableProperty]
    private bool smartNetDiskEnabled;

    [ObservableProperty]
    private bool smartUrlEnabled;

    [ObservableProperty]
    private string hotkey = "Alt+V";            // 剪贴板呼出

    [ObservableProperty]
    private string hotkeySettings = "Alt+X";    // 设置打开

    [ObservableProperty]
    private string hotkeyOpenUrl = "Ctrl+Alt+O";   // 打开复制的链接

    // ---- 设备列表 ----
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    private string deviceStatus = "";

    [ObservableProperty]
    private bool isDevicesLoading;

    [ObservableProperty]
    private string deviceLoadErrorText = "";

    public bool HasDevices => Devices.Count > 0;

    public bool HasDeviceLoadError => !string.IsNullOrWhiteSpace(DeviceLoadErrorText);

    public bool ShowDeviceEmptyState => !IsDevicesLoading && !HasDeviceLoadError && !HasDevices;

    // ---- 历史 / 外观 ----
    [ObservableProperty]
    private int maxHistoryIndex;

    [ObservableProperty]
    private int themeModeIndex = 2;   // 0=浅色 1=深色 2=跟随系统

    /// <summary>窗口背景材质:0=云母 1=云母增强 2=亚克力。</summary>
    [ObservableProperty]
    private int backdropIndex;

    /// <summary>亚克力背景不透明度(0.5~1,越大越不透明)。</summary>
    [ObservableProperty]
    private double backdropTintOpacity = 0.85;

    /// <summary>剪贴板窗口出现位置:0=跟随鼠标 1=屏幕中心。</summary>
    [ObservableProperty]
    private int windowPositionIndex;

    /// <summary>历史保留天数:0=不限,其余=天数。</summary>
    [ObservableProperty]
    private int retentionDaysIndex;

    /// <summary>粘贴键:0=Shift+Insert 1=Ctrl+V。</summary>
    [ObservableProperty]
    private int pasteKeyIndex;

    [ObservableProperty]
    private string dataStatus = "";

    [ObservableProperty]
    private bool isDataBusy;

    [ObservableProperty]
    private string dataResult = "";

    /// <summary>历史条目总数(数据管理页展示)。</summary>
    public int DataCount => _svc.History.Count;

    public string DataCountText => $"共 {DataCount} 条历史";

    /// <summary>历史数据库路径(数据管理页展示)。</summary>
    public string StoragePath => _svc.History.DbPath;

    /// <summary>图片缓存目录。</summary>
    public string ImageCachePath => Services.ImageCodec.CacheDir;

    /// <summary>配置的数据储存目录(设置项,重启后生效)。</summary>
    public string ConfiguredStorageDir => _svc.Settings.ResolveStorageDir();

    public int[] MaxHistoryOptions { get; } = { 50, 100, 200, 500, 1000 };

    public int[] RetentionDaysOptions { get; } = { 0, 7, 30, 90, 180, 365 };

    public string[] RetentionDaysLabels { get; } = { "不限", "7 天", "30 天", "90 天", "180 天", "365 天" };

    public string VersionText { get; } = "v" + (
        (System.Attribute.GetCustomAttribute(typeof(SettingsViewModel).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute)) as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion?.Split('+')[0]
        ?? "20260825.01");

    private readonly UpdateService _updateService = new();

    public string[] UpdateSourceOptions { get; } = { "GitHub Releases (默认)", "服务端直连加速" };

    [ObservableProperty]
    private int updateSourceIndex;

    [ObservableProperty]
    private bool isCheckingUpdate;

    [ObservableProperty]
    private string updateStatusText = "";

    [ObservableProperty]
    private bool hasNewVersion;

    [ObservableProperty]
    private string latestVersionText = "";

    [ObservableProperty]
    private string updateReleaseNotes = "";

    [ObservableProperty]
    private string updateReleaseUrl = "https://github.com/yixing233/nexclip/releases";

    [ObservableProperty]
    private string? updateDownloadUrl;

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
        AutoCheckUpdate = s.AutoCheckUpdate;
        UpdateSourceIndex = string.Equals(s.UpdateSource, "direct", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MonitorEnabled = s.MonitorEnabled;
        AutoPaste = s.AutoPaste;
        NotifyEnabled = s.NotifyEnabled;
        CopyDirectEnabled = s.CopyDirectEnabled;
        SmartColorEnabled = s.SmartColorEnabled;
        SmartPathEnabled = s.SmartPathEnabled;
        SmartDeepLinkEnabled = s.SmartDeepLinkEnabled;
        SmartNetDiskEnabled = s.SmartNetDiskEnabled;
        SmartUrlEnabled = s.SmartUrlEnabled;
        Hotkey = s.Hotkey;
        HotkeySettings = s.HotkeySettings;
        HotkeyOpenUrl = s.HotkeyOpenUrl;
        var idx = Array.IndexOf(MaxHistoryOptions, s.MaxHistory);
        MaxHistoryIndex = idx >= 0 ? idx : 2;
        ThemeModeIndex = s.ThemeMode switch { "light" => 0, "dark" => 1, _ => 2 };
        BackdropIndex = s.BackdropMode switch { "MicaAlt" => 1, "Acrylic" => 2, _ => 0 };
        BackdropTintOpacity = s.BackdropTintOpacity;
        WindowPositionIndex = s.WindowPositionMode == "center" ? 1 : 0;
        var rIdx = Array.IndexOf(RetentionDaysOptions, s.RetentionDays);
        RetentionDaysIndex = rIdx >= 0 ? rIdx : 0;
        PasteKeyIndex = s.PasteKey == "CtrlV" ? 1 : 0;
        _initialized = true;
        // 快捷键占用等长久态错误:注册失败时在快捷键页对应区域常驻提示
        RefreshHotkeyStatus();
        // 从本地缓存立即装载上次设备列表 (SWR 首屏零延迟直出)
        LoadCachedDevicesFromStore();
    }

    /// <summary>根据当前注册状态刷新热键常驻提示(占用/非法时显示)。</summary>
    public void RefreshHotkeyStatus()
    {
        if (App.Hotkey is null)
        {
            HotkeyStatusText = "";
            HasHotkeyIssue = false;
            HotkeySettingsStatusText = "";
            HasHotkeySettingsIssue = false;
            return;
        }

        HotkeyStatusText = App.Hotkey.IsRegistered
            ? ""
            : $"“{Hotkey}”已被其他程序占用或格式非法,当前无法使用";
        HasHotkeyIssue = !string.IsNullOrEmpty(HotkeyStatusText);

        HotkeySettingsStatusText = App.HotkeySettings is { IsRegistered: true }
            ? ""
            : $"“{HotkeySettings}”已被其他程序占用或格式非法,当前无法使用";
        HasHotkeySettingsIssue = !string.IsNullOrEmpty(HotkeySettingsStatusText);

        var openUrlOk = App.HotkeyOpenUrl is null || App.HotkeyOpenUrl.IsRegistered;
        if (!openUrlOk && string.IsNullOrEmpty(HotkeySettingsStatusText))
        {
            HotkeySettingsStatusText = $"“{HotkeyOpenUrl}”已被其他程序占用或格式非法,当前无法使用";
            HasHotkeySettingsIssue = true;
        }
    }

    private bool IsRegisteredHotkeyTextEmpty() => string.IsNullOrEmpty(HotkeySettingsStatusText);

    /// <summary>刷新数据管理页统计(条目数)。</summary>
    public void RefreshDataStatus()
    {
        OnPropertyChanged(nameof(DataCount));
        OnPropertyChanged(nameof(DataCountText));
        OnPropertyChanged(nameof(StoragePath));
        OnPropertyChanged(nameof(ImageCachePath));
        OnPropertyChanged(nameof(ConfiguredStorageDir));
    }

    [RelayCommand]
    public void SaveServerConfig()
    {
        var s = _svc.Settings;
        s.ServerUrl = ServerUrl.Trim();
        s.DeviceName = DeviceName.Trim();
        s.Save();
        _svc.Main.RefreshConnectionState();
        ShowMessage("设置已保存,正在重新连接…", InfoBarSeverity.Success);
        _ = _svc.Engine?.ReconfigureAsync();
        _ = RefreshDevicesAsync();
    }

    /// <summary>重新生成本机设备 ID (GUID)。</summary>
    [RelayCommand]
    public void RegenerateDeviceId()
    {
        var newId = Guid.NewGuid().ToString("N");
        _svc.Settings.DeviceId = newId;
        _svc.Settings.ClearCachedDevices();
        _svc.Settings.Save();
        OnPropertyChanged(nameof(DeviceIdText));
        ShowMessage("已重新生成本机设备 ID，正在重新连接…", InfoBarSeverity.Success);
        _svc.Main.RefreshConnectionState();
        _ = _svc.Engine?.ReconfigureAsync();
        _ = RefreshDevicesAsync();
    }

    /// <summary>复制本机设备 ID 到剪贴板。</summary>
    [RelayCommand]
    public void CopyDeviceId()
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(DeviceIdText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        ShowMessage("本机设备 ID 已复制到剪贴板", InfoBarSeverity.Success);
    }

    /// <summary>保存设备名称并同步至服务端。</summary>
    [RelayCommand]
    public async Task SaveDeviceNameAsync()
    {
        var newName = DeviceName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            newName = Environment.MachineName;
            DeviceName = newName;
        }
        _svc.Settings.DeviceName = newName;
        _svc.Settings.Save();

        var s = _svc.Settings;
        if (!string.IsNullOrWhiteSpace(s.ServerUrl) && !string.IsNullOrWhiteSpace(s.DeviceId))
        {
            try
            {
                await _svc.Api.RenameDeviceAsync(s.ServerUrl, s.DeviceId, newName, s.DeviceId, s.AuthToken);
            }
            catch (Exception ex)
            {
                Log.Warn($"同步修改设备名称至服务器失败: {ex.Message}");
            }
        }
        _svc.Main.RefreshConnectionState();
        _ = _svc.Engine?.ReconfigureAsync();
        _ = RefreshDevicesAsync();
        ShowMessage($"设备名称已更新为「{newName}」", InfoBarSeverity.Success);
    }

    /// <summary>重置剪贴板热键为默认 Alt+V。</summary>
    [RelayCommand]
    public void ResetHotkey()
    {
        if (Hotkey == "Alt+V")
        {
            ShowHotkeyMessage("剪贴板热键已是默认值 Alt+V");
            return;
        }
        Hotkey = "Alt+V";
    }

    /// <summary>清空剪贴板呼出热键。</summary>
    [RelayCommand]
    public void ClearHotkey()
    {
        Hotkey = "";
        ShowHotkeyMessage("已清除剪贴板呼出热键");
    }

    /// <summary>重置设置热键为默认 Alt+X。</summary>
    [RelayCommand]
    public void ResetHotkeySettings()
    {
        if (HotkeySettings == "Alt+X")
        {
            ShowHotkeyMessage("设置热键已是默认值 Alt+X");
            return;
        }
        HotkeySettings = "Alt+X";
    }

    /// <summary>清空设置热键。</summary>
    [RelayCommand]
    public void ClearHotkeySettings()
    {
        HotkeySettings = "";
        ShowHotkeyMessage("已清除设置热键");
    }

    /// <summary>重置打开链接热键为默认 Ctrl+Alt+O。</summary>
    [RelayCommand]
    public void ResetHotkeyOpenUrl()
    {
        if (HotkeyOpenUrl == "Ctrl+Alt+O")
        {
            ShowHotkeyMessage("打开链接热键已是默认值 Ctrl+Alt+O");
            return;
        }
        HotkeyOpenUrl = "Ctrl+Alt+O";
    }

    /// <summary>清空打开链接热键。</summary>
    [RelayCommand]
    public void ClearHotkeyOpenUrl()
    {
        HotkeyOpenUrl = "";
        ShowHotkeyMessage("已清除打开链接热键");
    }

    [RelayCommand]
    public void ClearHistory(bool keepStarred = false)
    {
        _svc.Engine?.History.Clear(keepStarred);
        RefreshDataStatus();
        _ = _svc.HistoryVm.RefreshAsync();
        ShowMessage(keepStarred ? "已清空未收藏历史" : "本地历史已清空", InfoBarSeverity.Success);
    }

    /// <summary>
    /// 应用新的数据储存位置:把现有数据(数据库 + 图片缓存 + 条目图片路径)迁移到新目录并立即生效。
    /// 返回操作结果文案(供页面显示)。
    /// </summary>
    public string ApplyStorageDir(string newDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newDir)) return "储存位置无效";
            newDir = Path.GetFullPath(newDir.Trim());
            Directory.CreateDirectory(newDir);

            var oldDb = _svc.History.DbPath;
            var oldImages = Services.ImageCodec.CacheDir;
            var newDb = Path.Combine(newDir, "history.db");
            var newImages = Path.Combine(newDir, "images");

            // 迁移:新目录无数据才复制(避免覆盖已有数据)
            if (File.Exists(oldDb) && !File.Exists(newDb))
            {
                File.Copy(oldDb, newDb);
                if (Directory.Exists(oldImages))
                {
                    CopyDirectory(oldImages, newImages);
                }
                _svc.History.UpdateImagePaths(oldImages, newImages);
            }

            _svc.Settings.StorageDir = newDir;
            _svc.Settings.Save();
            _svc.History.Reopen(newDir);
            Services.ImageCodec.Initialize(newDir);
            RefreshDataStatus();
            _ = _svc.HistoryVm.RefreshAsync();
            return $"储存位置已更新:{newDir}";
        }
        catch (Exception ex)
        {
            Log.Error("修改储存位置失败", ex);
            return $"修改储存位置失败：{ServerApi.DescribeException(ex, "请检查目录路径、权限和磁盘空间。")}";
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
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
            ShowMessage("请先填写服务器地址");
            return;
        }
        IsGeneratingCode = true;
        ShowMessage("");
        GeneratedCode = "";
        try
        {
            // 携带本设备信息生成:服务端同步登记本设备,设备列表可看到本机
            var result = await _svc.Api.CreatePairingCodeAsync(url, _svc.Settings.DeviceId, _svc.Settings.DeviceName, _svc.Settings.AuthToken);
            if (result is null || string.IsNullOrWhiteSpace(result.Code))
            {
                ShowMessage("生成失败:服务器未返回配对码", InfoBarSeverity.Error);
                return;
            }
            GeneratedCode = result.Code;
            if (!string.IsNullOrWhiteSpace(result.DeviceToken))
            {
                _svc.Settings.AuthToken = result.DeviceToken;
                _svc.Settings.IsPaired = true;
                _svc.Settings.Save();
                NotifyPairingStateChanged();
            }
            // 关闭即失效:不再按时间显示有效期
            CodeExpiryText = "关闭对话框后此配对码立即失效";
            ShowMessage("配对码已生成:把此码输入到新设备的配对框即可接入");
            PairingCodeGenerated?.Invoke(result);
            _ = RefreshDevicesAsync();
        }
        catch (Exception ex)
        {
            Log.Error("生成配对码失败", ex);
            ShowMessage($"生成失败：{ServerApi.DescribeException(ex, "请检查服务器配置后重试。")}", InfoBarSeverity.Error);
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
            await _svc.Api.RevokePairingCodeAsync(ServerUrl.Trim(), code, _svc.Settings.DeviceId, _svc.Settings.AuthToken);
        }
        catch (Exception ex)
        {
            Log.Debug($"配对码作废静默捕获: {ex.Message}");
        }
    }

    /// <summary>
    /// 配对: 输入 6 位纯数字验证码, 单向直接接入设备组 (无需用户 ID 与二次确认)。
    /// </summary>
    [RelayCommand]
    public async Task PairDeviceAsync()
    {
        var code = PairingCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
        {
            ShowMessage("请输入 6 位配对验证码");
            return;
        }
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ShowMessage("请先填写服务器地址");
            return;
        }
        IsPairing = true;
        ShowMessage("正在连接服务器进行即时配对…");
        try
        {
            var pairResult = await _svc.Api.PairDirectAsync(ServerUrl.Trim(), code, s.DeviceId, s.DeviceName);
            if (pairResult != null && pairResult.Status == "approved" && !string.IsNullOrWhiteSpace(pairResult.DeviceToken))
            {
                s.AuthToken = pairResult.DeviceToken;
                s.IsPaired = true;
                s.Save();
                _svc.Main.RefreshConnectionState();
                ShowMessage("配对成功！本设备已成功加入同步设备组", InfoBarSeverity.Success);
                PairingCode = "";
                _ = _svc.Engine?.ReconfigureAsync();
                _ = RefreshDevicesAsync();
                NotifyPairingStateChanged();
            }
            else
            {
                ShowMessage("配对验证失败，请确认验证码正确且未过期", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("设备配对失败", ex);
            ShowMessage($"配对失败：{ServerApi.DescribeException(ex, "请检查 6 位验证码和服务器配置后重试。")}", InfoBarSeverity.Error);
        }
        finally
        {
            IsPairing = false;
        }
    }

    private SyncEngine? _attachedEngine;

    /// <summary>绑定同步引擎,监听连接状态即时联动本机在线徽章与状态。</summary>
    public void AttachEngine(SyncEngine engine)
    {
        if (_attachedEngine == engine) return;
        if (_attachedEngine is not null)
        {
            _attachedEngine.ConnectionChanged -= OnEngineConnectionChanged;
        }
        _attachedEngine = engine;
        _attachedEngine.ConnectionChanged += OnEngineConnectionChanged;
    }

    private void OnEngineConnectionChanged(SyncEngine.ConnState state, string message)
    {
        var isOnline = state is SyncEngine.ConnState.Connected;
        var myId = _svc.Settings.DeviceId;
        var cur = Devices.FirstOrDefault(d => d.IsCurrent || string.Equals(d.Id, myId, StringComparison.OrdinalIgnoreCase));
        if (cur != null)
        {
            cur.Online = isOnline;
            if (isOnline) cur.LastSeenAt = DateTime.UtcNow;
        }
    }

    /// <summary>从本地持久化缓存装载设备列表 (SWR 策略首屏秒级直出)。</summary>
    private void LoadCachedDevicesFromStore()
    {
        try
        {
            var cached = _svc.Settings.LoadCachedDevices();
            if (cached.Count > 0)
            {
                var myId = _svc.Settings.DeviceId;
                var isClientOnline = _svc.Engine?.State is SyncEngine.ConnState.Connected or SyncEngine.ConnState.Connecting or SyncEngine.ConnState.Reconnecting;
                foreach (var d in cached)
                {
                    d.IsCurrent = string.Equals(d.Id, myId, StringComparison.OrdinalIgnoreCase);
                    if (d.IsCurrent && isClientOnline)
                    {
                        d.Online = true;
                        d.LastSeenAt = DateTime.UtcNow;
                    }
                }
                var sorted = cached.OrderByDescending(d => d.IsCurrent)
                                   .ThenByDescending(d => d.Online)
                                   .ThenByDescending(d => d.LastSeenAt);
                Devices.Clear();
                foreach (var d in sorted) Devices.Add(d);
                DeviceStatus = $"{Devices.Count} 台设备";
                NotifyDeviceStateChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"初始化装载设备缓存失败: {ex.Message}");
        }
    }

    /// <summary>加载设备列表(GET /api/devices,采用 SWR 策略静默刷新并更新本地缓存)。</summary>
    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            Devices.Clear();
            s.ClearCachedDevices();
            DeviceStatus = "等待配置";
            DeviceLoadErrorText = "";
            NotifyDeviceStateChanged();
            return;
        }

        if (IsDevicesLoading) return;

        IsDevicesLoading = true;
        DeviceLoadErrorText = "";
        DeviceStatus = Devices.Count > 0 ? $"正在更新 · {Devices.Count} 台" : "正在加载…";
        NotifyDeviceStateChanged();
        try
        {
            var list = await _svc.Api.GetDevicesAsync(s.ServerUrl, s.DeviceId, s.AuthToken);
            var myId = s.DeviceId;
            var isClientOnline = _svc.Engine?.State is SyncEngine.ConnState.Connected or SyncEngine.ConnState.Connecting or SyncEngine.ConnState.Reconnecting;
            foreach (var d in list)
            {
                d.IsCurrent = string.Equals(d.Id, myId, StringComparison.OrdinalIgnoreCase);
                if (d.IsCurrent && isClientOnline)
                {
                    d.Online = true;
                    d.LastSeenAt = DateTime.UtcNow;
                }
            }
            var sorted = list.OrderByDescending(d => d.IsCurrent)
                             .ThenByDescending(d => d.Online)
                             .ThenByDescending(d => d.LastSeenAt)
                             .ToList();

            Devices.Clear();
            foreach (var d in sorted) Devices.Add(d);
            DeviceStatus = list.Count == 0 ? "暂无设备" : $"{list.Count} 台设备";

            // 请求成功，持久化更新本地缓存
            s.SaveCachedDevices(sorted);
        }
        catch (Exception ex)
        {
            DeviceLoadErrorText = BuildDeviceLoadErrorMessage(ex);
            DeviceStatus = Devices.Count > 0 ? $"更新失败 · 保留 {Devices.Count} 台" : "加载失败";
            Log.Warn($"设备列表加载失败:{ex.Message}");
        }
        finally
        {
            IsDevicesLoading = false;
            NotifyDeviceStateChanged();
        }
    }

    /// <summary>移除/注销指定设备 (DELETE /api/devices/{id})。</summary>
    [RelayCommand]
    public async Task RemoveDeviceAsync(DeviceInfo? device)
    {
        if (device == null || device.IsCurrent) return;
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl)) return;

        try
        {
            await _svc.Api.RemoveDeviceAsync(s.ServerUrl, device.Id, s.DeviceId, s.AuthToken);
            Devices.Remove(device);
            s.SaveCachedDevices(Devices.ToList());
            DeviceStatus = Devices.Count == 0 ? "暂无设备" : $"{Devices.Count} 台设备";
            NotifyDeviceStateChanged();
            ShowMessage($"已成功移除设备「{device.Name ?? device.Id}」", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("移除设备失败", ex);
            ShowMessage($"移除设备失败: {ServerApi.DescribeException(ex, "网络错误")}", InfoBarSeverity.Error);
        }
    }

    private void NotifyPairingStateChanged()
    {
        OnPropertyChanged(nameof(IsPaired));
        OnPropertyChanged(nameof(PairingStatusText));
        OnPropertyChanged(nameof(PairingStatusHint));
    }

    private void NotifyDeviceStateChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasDeviceLoadError));
        OnPropertyChanged(nameof(ShowDeviceEmptyState));
    }

    private static string BuildDeviceLoadErrorMessage(Exception ex) => ex switch
    {
        UriFormatException => "服务器地址格式不正确，请检查地址后保存并重试。",
        TaskCanceledException => "服务器响应超时，请确认服务器正在运行，并检查当前网络连接。",
        HttpRequestException => "无法连接到服务器，请检查服务器地址、网络和防火墙设置。",
        ApiException api => api.Message,
        _ => ServerApi.DescribeException(ex, "暂时无法获取设备列表，请检查服务器配置后重试。"),
    };

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

    /// <summary>设备平台图标(Lucide)。</summary>
    public static Microsoft.UI.Xaml.Media.ImageSource DevicePlatformIcon(string? platform)
    {
        var p = platform?.ToLowerInvariant() ?? "";
        if (p.Contains("android") || p.Contains("ios") || p.Contains("iphone") || p.Contains("mobile") || p.Contains("phone"))
            return Services.Lucide.Smartphone;
        if (p.Contains("win") || p.Contains("mac") || p.Contains("linux") || p.Contains("pc") || p.Contains("laptop"))
            return Services.Lucide.Laptop;
        return Services.Lucide.Monitor;
    }

    /// <summary>设备图标背景色。</summary>
    public static Microsoft.UI.Xaml.Media.Brush DeviceIconBgBrush(bool online) =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(online
            ? Microsoft.UI.ColorHelper.FromArgb(30, 37, 99, 235)   // 浅蓝底
            : Microsoft.UI.ColorHelper.FromArgb(20, 100, 116, 139)); // 浅灰底

    /// <summary>设备状态胶囊背景色。</summary>
    public static Microsoft.UI.Xaml.Media.Brush DeviceStatusBgBrush(bool online) =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(online
            ? Microsoft.UI.ColorHelper.FromArgb(28, 22, 163, 74)   // 浅绿
            : Microsoft.UI.ColorHelper.FromArgb(20, 148, 163, 184)); // 浅灰

    /// <summary>设备状态胶囊边框色。</summary>
    public static Microsoft.UI.Xaml.Media.Brush DeviceStatusBorderBrush(bool online) =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(online
            ? Microsoft.UI.ColorHelper.FromArgb(60, 22, 163, 74)
            : Microsoft.UI.ColorHelper.FromArgb(40, 148, 163, 184));

    /// <summary>设备名称颜色:在线=主题主色,离线=次要灰(字体颜色区分在线/离线)。</summary>
    public static Microsoft.UI.Xaml.Media.Brush DeviceNameBrush(bool online) =>
        (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[
            online ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    /// <summary>设备状态文字颜色:在线=绿色,离线=淡灰。</summary>
    public static Microsoft.UI.Xaml.Media.Brush DeviceStatusBrush(bool online)
    {
        if (online)
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 22, 163, 74)); // 绿
        }
        return (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"];
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            ShowMessage("请先输入服务器地址", InfoBarSeverity.Warning);
            return;
        }
        IsTesting = true;
        ShowMessage("正在测试连接…");
        try
        {
            var (ok, message) = await _svc.Api.TestConnectionAsync(ServerUrl.Trim(), s.DeviceId, s.AuthToken);
            ShowMessage(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("测试连接失败", ex);
            ShowMessage($"连接失败：{ServerApi.DescribeException(ex, "请检查服务器配置后重试。")}", InfoBarSeverity.Error);
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ---- 开关变更即保存 ----
    partial void OnBootStartEnabledChanged(bool value)
    {
        if (!_initialized) return;
        _svc.Settings.BootStartEnabled = value;
        _svc.Settings.Save();
        var ok = StartupService.SetEnabled(value);
        if (!ok)
        {
            ShowMessage(value ? "开机自启动设置失败(注册表写入被拒)" : "开机自启动已取消,但注册表清理失败", InfoBarSeverity.Error);
        }
    }
    partial void OnStartMinimizedChanged(bool value) { if (!_initialized) return; _svc.Settings.StartMinimized = value; _svc.Settings.Save(); }
    partial void OnCloseToTrayChanged(bool value) { if (!_initialized) return; _svc.Settings.CloseToTray = value; _svc.Settings.Save(); }
    partial void OnAutoCheckUpdateChanged(bool value) { if (!_initialized) return; _svc.Settings.AutoCheckUpdate = value; _svc.Settings.Save(); }
    partial void OnUpdateSourceIndexChanged(int value) { if (!_initialized) return; _svc.Settings.UpdateSource = value == 1 ? "direct" : "github"; _svc.Settings.Save(); }
    partial void OnMonitorEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.MonitorEnabled = value; _svc.Settings.Save(); }
    partial void OnAutoPasteChanged(bool value) { if (!_initialized) return; _svc.Settings.AutoPaste = value; _svc.Settings.Save(); }
    partial void OnNotifyEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.NotifyEnabled = value; _svc.Settings.Save(); }
    partial void OnCopyDirectEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.CopyDirectEnabled = value; _svc.Settings.Save(); }
    partial void OnSmartColorEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.SmartColorEnabled = value; _svc.Settings.Save(); }
    partial void OnSmartPathEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.SmartPathEnabled = value; _svc.Settings.Save(); }
    partial void OnSmartDeepLinkEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.SmartDeepLinkEnabled = value; _svc.Settings.Save(); }
    partial void OnSmartNetDiskEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.SmartNetDiskEnabled = value; _svc.Settings.Save(); }
    partial void OnSmartUrlEnabledChanged(bool value) { if (!_initialized) return; _svc.Settings.SmartUrlEnabled = value; _svc.Settings.Save(); }
    partial void OnHotkeyChanged(string value)
    {
        if (!_initialized) return;
        _svc.Settings.Hotkey = value;
        _svc.Settings.Save();
        var ok = App.Hotkey?.Apply(value) ?? false;
        RefreshHotkeyStatus();
        ShowHotkeyMessage(ok ? "剪贴板热键已应用: " + value : "热键格式非法或已被占用: " + value, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    partial void OnHotkeySettingsChanged(string value)
    {
        if (!_initialized) return;
        _svc.Settings.HotkeySettings = value;
        _svc.Settings.Save();
        var ok = App.HotkeySettings?.Apply(value) ?? false;
        RefreshHotkeyStatus();
        ShowHotkeyMessage(ok ? "设置热键已应用: " + value : "热键格式非法或已被占用: " + value, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
    partial void OnHotkeyOpenUrlChanged(string value)
    {
        if (!_initialized) return;
        _svc.Settings.HotkeyOpenUrl = value;
        _svc.Settings.Save();
        var ok = App.HotkeyOpenUrl?.Apply(value) ?? false;
        ShowHotkeyMessage(ok ? "打开链接热键已应用: " + value : "打开链接热键格式非法或已被占用: " + value, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }
    partial void OnThemeModeIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.ThemeMode = value switch { 0 => "light", 1 => "dark", _ => "system" };
        _svc.Settings.Save();
        App.ApplyTheme(_svc.Settings.ThemeMode);
    }
    partial void OnBackdropIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.BackdropMode = value switch { 1 => "MicaAlt", 2 => "Acrylic", _ => "Mica" };
        _svc.Settings.Save();
        App.ApplyBackdrop(_svc.Settings.BackdropMode);
    }
    partial void OnBackdropTintOpacityChanged(double value)
    {
        if (!_initialized) return;
        _svc.Settings.BackdropTintOpacity = Math.Clamp(value, 0.5, 1.0);
        _svc.Settings.Save();
        App.ApplyBackdrop(_svc.Settings.BackdropMode);
    }
    partial void OnWindowPositionIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.WindowPositionMode = value == 1 ? "center" : "cursor";
        _svc.Settings.Save();
    }
    partial void OnMaxHistoryIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.MaxHistory = MaxHistoryOptions[value];
        _svc.Settings.Save();
        _svc.History.MaxEntries = _svc.Settings.MaxHistory; // 立即按新上限清理
        OnPropertyChanged(nameof(DataCount));
        _ = _svc.HistoryVm.RefreshAsync();
    }
    partial void OnRetentionDaysIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.RetentionDays = RetentionDaysOptions[value];
        _svc.Settings.Save();
        var removed = _svc.History.PruneOlderThan(_svc.Settings.RetentionDays); // 立即按时间清理
        OnPropertyChanged(nameof(DataCount));
        _ = _svc.HistoryVm.RefreshAsync();
        if (removed > 0) ShowMessage($"已清理 {removed} 条超过保留期的历史");
    }
    partial void OnPasteKeyIndexChanged(int value)
    {
        if (!_initialized) return;
        _svc.Settings.PasteKey = value == 1 ? "CtrlV" : "ShiftInsert";
        _svc.Settings.Save();
    }

    /// <summary>导出全部历史为 JSON 文件(图片内嵌 base64)。</summary>
    public async Task ExportDataAsync(string path)
    {
        IsDataBusy = true;
        ShowMessage("");
        try
        {
            var items = _svc.History.Query(limit: 100000);
            var list = new List<object>(items.Count);
            foreach (var it in items)
            {
                string? img = null;
                if (it.Type == "Image" && !string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath))
                {
                    img = Convert.ToBase64String(await File.ReadAllBytesAsync(it.ImagePath));
                }
                list.Add(new
                {
                    type = it.Type,
                    text = it.Text,
                    imageBase64 = img,
                    deviceName = it.DeviceName,
                    createdAt = it.CreatedAt.ToString("o"),
                    starred = it.Starred,
                });
            }
            var doc = new
            {
                app = "NexClip",
                version = 1,
                exportedAt = DateTime.UtcNow.ToString("o"),
                items = list,
            };
            await File.WriteAllTextAsync(path,
                System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            ShowMessage($"导出成功:{items.Count} 条历史 → {path}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("导出失败", ex);
            ShowMessage($"导出失败：{ServerApi.DescribeException(ex, "请检查文件路径和磁盘空间。")}", InfoBarSeverity.Error);
        }
        finally
        {
            IsDataBusy = false;
        }
    }

    /// <summary>从导出 JSON 导入历史。返回导入条数。</summary>
    public async Task ImportDataAsync(string path)
    {
        IsDataBusy = true;
        ShowMessage("");
        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var count = 0;
                foreach (var el in items.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var t) ? t.GetString() : "Text";
                    var text = el.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    var imgB64 = el.TryGetProperty("imageBase64", out var ib) ? ib.GetString() : null;
                    var deviceName = el.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null;
                    var createdAt = el.TryGetProperty("createdAt", out var ca) && DateTime.TryParse(ca.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var cdt)
                        ? cdt
                        : DateTime.UtcNow;
                    var starred = el.TryGetProperty("starred", out var st) && st.GetBoolean();

                    string? imagePath = null;
                    if (type == "Image" && !string.IsNullOrEmpty(imgB64))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(imgB64);
                            // 用随机 id 命名,避免与现有缓存冲突
                            imagePath = await Services.ImageCodec.SavePngAsync(bytes, DateTime.UtcNow.Ticks % 1_000_000_000);
                        }
                        catch { /* 图片损坏则跳过图片 */ }
                    }

                    var id = _svc.History.Insert(new Models.HistoryItem
                    {
                        Type = type ?? "Text",
                        Text = text,
                        ImagePath = imagePath,
                        DeviceId = "import",
                        DeviceName = deviceName,
                        CreatedAt = createdAt,
                        Origin = 0,
                    });
                    if (id > 0)
                    {
                        count++;
                        if (starred) _svc.History.ToggleStar(id, true);
                    }
                }
                OnPropertyChanged(nameof(DataCount));
                _ = _svc.HistoryVm.RefreshAsync();
                ShowMessage(count > 0 ? $"导入成功:{count} 条(重复条目已跳过)" : "导入完成:没有新增条目(均为重复)", InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage("导入失败:文件不是有效的 NexClip 导出(缺少 items)", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("导入失败", ex);
            ShowMessage($"导入失败：{ServerApi.DescribeException(ex, "请确认文件可读且格式正确。")}", InfoBarSeverity.Error);
        }
        finally
        {
            IsDataBusy = false;
        }
    }

    [RelayCommand]
    public async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatusText = "正在检查更新...";
        try
        {
            var rawVersion = VersionText.TrimStart('v', 'V');
            var result = await _updateService.CheckForUpdateAsync(rawVersion, _svc.Settings.UpdateSource, ServerUrl);
            if (result.Success)
            {
                if (result.HasUpdate)
                {
                    HasNewVersion = true;
                    LatestVersionText = $"v{result.LatestVersion}";
                    UpdateReleaseNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? "有新版本可用。" : result.ReleaseNotes;
                    UpdateReleaseUrl = string.IsNullOrWhiteSpace(result.ReleaseUrl) ? "https://github.com/yixing233/nexclip/releases" : result.ReleaseUrl;
                    UpdateDownloadUrl = result.DownloadUrl;
                    var isDirect = string.Equals(_svc.Settings.UpdateSource, "direct", StringComparison.OrdinalIgnoreCase);
                    var sourceLabel = isDirect ? "直连加速" : "GitHub";
                    UpdateStatusText = $"发现新版本 v{result.LatestVersion} ({sourceLabel})";
                    ShowMessage($"发现新版本 v{result.LatestVersion} ({sourceLabel})，可点击前往查看下载。", InfoBarSeverity.Informational);
                }
                else
                {
                    HasNewVersion = false;
                    UpdateStatusText = "当前已是最新版本";
                    ShowMessage("当前已是最新版本 (" + VersionText + ")", InfoBarSeverity.Success);
                }
            }
            else
            {
                HasNewVersion = false;
                UpdateStatusText = $"检查更新失败 ({result.ErrorMessage})";
                ShowMessage($"检查更新失败: {result.ErrorMessage}", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            HasNewVersion = false;
            UpdateStatusText = "检查更新出错";
            ShowMessage($"检查更新出错: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }
}
