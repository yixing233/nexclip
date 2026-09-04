using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NexClip.Desktop.Models;
using NexClip.Desktop.Services;

namespace NexClip.Desktop.ViewModels;

public partial class DeviceSelectViewModel : ObservableObject
{
    private static readonly SolidColorBrush OnlineBrush = new(ColorHelper.FromArgb(255, 16, 185, 129));
    private static readonly SolidColorBrush OfflineBrush = new(ColorHelper.FromArgb(255, 156, 163, 175));

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public bool IsOnline { get; set; }

    [ObservableProperty]
    private bool isSelected;

    public Brush StatusBrush => IsOnline ? OnlineBrush : OfflineBrush;
    public string DisplayText => IsOnline ? $"{Name} (在线)" : $"{Name} (离线)";
}

public partial class ChatMessageItem : ObservableObject
{
    private static readonly SolidColorBrush WhiteBrush = new(ColorHelper.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush WhiteTransBrush = new(ColorHelper.FromArgb(200, 255, 255, 255));

    public long Id { get; set; }
    public string Type { get; set; } = "Text";
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public string? ImageRef { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsFromSelf { get; set; }
    public bool IsManual { get; set; }
    public ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    private bool showDateHeader;

    [ObservableProperty]
    private string dateHeaderText = "";

    public bool IsImage => Type == "Image";
    public bool IsText => Type == "Text";

    public HorizontalAlignment Alignment => IsFromSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Visibility FromRemoteVisibility => IsFromSelf ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FromSelfVisibility => IsFromSelf ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => IsText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;

    public Brush BubbleBackground => IsFromSelf
        ? (Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush ?? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212)))
        : (Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush ?? new SolidColorBrush(ColorHelper.FromArgb(255, 243, 243, 243)));

    public Brush BubbleForeground => IsFromSelf
        ? WhiteBrush
        : (Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush ?? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 0, 0)));

    public Brush BubbleBorderBrush => IsFromSelf
        ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        : (Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush ?? new SolidColorBrush(ColorHelper.FromArgb(30, 0, 0, 0)));

    public Brush TimeForeground => IsFromSelf
        ? WhiteTransBrush
        : (Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush ?? new SolidColorBrush(ColorHelper.FromArgb(150, 0, 0, 0)));

    public Thickness BubbleThickness => IsFromSelf ? new Thickness(0) : new Thickness(1);
    public Thickness BubblePadding => IsImage ? new Thickness(4) : new Thickness(12, 8, 12, 8);

    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm");
    public string FullTimeText => CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");

    public static ChatMessageItem FromHistory(HistoryItem item, string selfDeviceId)
    {
        var isFromSelf = item.Origin != 1 &&
                         (string.Equals(item.DeviceId, selfDeviceId, StringComparison.OrdinalIgnoreCase) ||
                          string.IsNullOrEmpty(item.DeviceId));
        return new ChatMessageItem
        {
            Id = item.Id,
            Type = item.Type,
            Text = item.Text,
            ImagePath = item.ImagePath,
            ImageRef = item.ImageRef,
            DeviceId = item.DeviceId ?? "",
            DeviceName = string.IsNullOrEmpty(item.DeviceName) ? (isFromSelf ? "本机" : "远端设备") : item.DeviceName,
            CreatedAt = item.CreatedAt,
            IsFromSelf = isFromSelf,
            IsManual = item.IsManual,
            Thumbnail = BuildThumbnail(item.ImagePath),
        };
    }

    private static ImageSource? BuildThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            // 与 TransferChatPage.xaml 中气泡图片的显示上限（MaxHeight 180 / MaxWidth 240）对齐，
            // 避免按 720px 解码浪费大量位图内存。
            // 按长边约束避免超宽/超高图片被反向放大：BitmapImage 只设一个维度时会按原始比例
            // 推算另一维度，若一律限高 180，超宽长条图会被推算出极大的宽度，解码面积反而暴涨。
            bmp.DecodePixelType = DecodePixelType.Logical;
            var size = ImageCodec.TryReadPngSize(path);
            if (size is { } s && s.Width > s.Height)
            {
                // 横图按气泡宽度上限限宽
                bmp.DecodePixelWidth = 240;
            }
            else
            {
                // 竖图/方图，以及非 PNG 或读取头部失败时的兜底：按气泡高度上限限高
                bmp.DecodePixelHeight = 180;
            }
            bmp.UriSource = new Uri("file:///" + path.Replace('\\', '/'));
            return bmp;
        }
        catch { return null; }
    }
}

public partial class TransferChatViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private string inputText = "";

    [ObservableProperty]
    private bool isSending;

    [ObservableProperty]
    private bool selectAllDevices = true;

    [ObservableProperty]
    private byte[]? selectedImageBytes;

    [ObservableProperty]
    private ImageSource? selectedImageThumbnail;

    [ObservableProperty]
    private string onlineSummary = "正在获取在线设备…";

    public bool HasSelectedImage => SelectedImageBytes != null && SelectedImageBytes.Length > 0;
    public Visibility HasSelectedImageVisibility => HasSelectedImage ? Visibility.Visible : Visibility.Collapsed;
    public string SelectedImageInfoText => HasSelectedImage ? $"{(SelectedImageBytes!.Length > 1024 * 1024 ? $"{SelectedImageBytes.Length / (1024.0 * 1024.0):F1} MB" : $"{SelectedImageBytes.Length / 1024} KB")}" : "";

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();
    public ObservableCollection<DeviceSelectViewModel> Devices { get; } = new();

    public IRelayCommand SendMessageCommand { get; }
    public IRelayCommand RefreshDevicesCommand { get; }
    public IRelayCommand ClearSelectedImageCommand { get; }

    /// <summary>聊天流在运行期最多保留的消息条数，超出后淘汰最旧的消息以释放缩略图内存</summary>
    private const int MaxRetainedMessages = 200;

    private readonly List<ChatMessageItem> _allMessages = new();
    private bool _isUpdatingDeviceSelection;
    private SyncEngine? _attachedEngine;

    /// <summary>历史消息是否已完整加载过一次（页面每次激活都会调用加载方法，需要短路避免重复解码缩略图）</summary>
    private bool _historyLoaded;

    /// <summary>上次完整加载时历史库中最新一条的 Id（Query 按 created_at 倒序，首条即最新），用于判断历史是否变更</summary>
    private long _historyMaxId;

    public TransferChatViewModel(AppServices services)
    {
        _services = services;
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        ClearSelectedImageCommand = new RelayCommand(ClearSelectedImage);

        LoadCachedDevicesFromStore();

        if (_services.Engine is not null)
        {
            AttachEngine(_services.Engine);
        }
    }

    private void LoadCachedDevicesFromStore()
    {
        try
        {
            var cached = _services.Settings.LoadCachedDevices();
            if (cached.Count == 0) return;
            var selfId = _services.Settings.DeviceId;
            var others = cached.Where(d => !string.Equals(d.Id, selfId, StringComparison.OrdinalIgnoreCase)).ToList();
            Devices.Clear();
            foreach (var d in others)
            {
                var devVm = new DeviceSelectViewModel
                {
                    Id = d.Id,
                    Name = d.Name,
                    Platform = d.Platform ?? "Unknown",
                    IsOnline = d.Online,
                    IsSelected = SelectAllDevices ? d.Online : false,
                };
                Devices.Add(devVm);
            }
            var onlineCount = others.Count(d => d.Online);
            OnlineSummary = $"{onlineCount} 台设备在线";
        }
        catch (Exception ex)
        {
            Log.Debug($"互传面板初始化设备缓存失败: {ex.Message}");
        }
    }

    public void AttachEngine(SyncEngine engine)
    {
        if (_attachedEngine == engine) return;
        if (_attachedEngine is not null)
        {
            _attachedEngine.EntryUpdated -= OnEntryUpdated;
        }
        _attachedEngine = engine;
        _attachedEngine.EntryUpdated += OnEntryUpdated;
    }

    public async Task InitializeAsync()
    {
        // 历史消息统一由页面激活（TransferChatPage.OnActivated / ClipboardMainPage 切换到互传页）触发加载，
        // 此处不再重复调用 LoadHistoryMessagesAsync，避免同一批消息与图片缩略图被解码两遍
        await RefreshDevicesAsync();
    }

    public async Task LoadHistoryMessagesAsync()
    {
        try
        {
            // 页面每次激活（TransferChatPage.OnActivated / ClipboardMainPage 切到互传页）都会调用本方法，
            // 若历史未发生变更就直接复用现有 ChatMessageItem，避免 100 条消息与其缩略图被反复重建、反复解码。
            // 新消息到达时走 OnEntryUpdated -> AppendMessage 路径，不受此短路影响。
            if (_historyLoaded)
            {
                var latest = _services.History.Query(limit: 1);
                var latestId = latest.Count > 0 ? latest[0].Id : 0L;
                if (latestId == _historyMaxId) return;
            }

            var historyItems = _services.History.Query(limit: 100);
            var selfDeviceId = _services.Settings.DeviceId;
            _allMessages.Clear();

            // 按照时间正序排列展示在聊天流中（仅展示手动互传/手动发送的消息）
            var sorted = historyItems.Where(h => h.IsManual).OrderBy(h => h.CreatedAt);
            foreach (var item in sorted)
            {
                var msg = ChatMessageItem.FromHistory(item, selfDeviceId);
                _allMessages.Add(msg);
            }

            ApplyFilter();

            // 记录本次加载对应的历史水位线（Query 按 created_at 倒序，首条即最新条目）
            _historyMaxId = historyItems.Count > 0 ? historyItems[0].Id : 0L;
            _historyLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("加载互传历史消息失败", ex);
        }
    }

    public static void UpdateDateHeaders(IList<ChatMessageItem> list)
    {
        DateTime? lastDate = null;
        foreach (var msg in list)
        {
            var msgDate = msg.CreatedAt.ToLocalTime().Date;
            if (lastDate == null || msgDate != lastDate.Value)
            {
                msg.ShowDateHeader = true;
                msg.DateHeaderText = FormatDateHeader(msg.CreatedAt.ToLocalTime());
                lastDate = msgDate;
            }
            else
            {
                msg.ShowDateHeader = false;
                msg.DateHeaderText = "";
            }
        }
    }

    public static string FormatDateHeader(DateTime localTime)
    {
        var now = DateTime.Now;
        var date = localTime.Date;
        if (date == now.Date) return "今天";
        if (date == now.Date.AddDays(-1)) return "昨天";
        if (date == now.Date.AddDays(-2)) return "前天";
        if (date.Year == now.Year) return localTime.ToString("M月d日");
        return localTime.ToString("yyyy年M月d日");
    }

    private void AppendMessage(ChatMessageItem msg)
    {
        var lastMsg = _allMessages.LastOrDefault();
        var msgDate = msg.CreatedAt.ToLocalTime().Date;
        if (lastMsg == null || lastMsg.CreatedAt.ToLocalTime().Date != msgDate)
        {
            msg.ShowDateHeader = true;
            msg.DateHeaderText = FormatDateHeader(msg.CreatedAt.ToLocalTime());
        }
        else
        {
            msg.ShowDateHeader = false;
            msg.DateHeaderText = "";
        }
        _allMessages.Add(msg);
        Messages.Add(msg);
        // 同步推进历史水位线，避免下次页面激活时因水位线落后而整表重建
        if (msg.Id > _historyMaxId) _historyMaxId = msg.Id;
        TrimRetainedMessages();
    }

    /// <summary>
    /// 限制聊天流的运行期长度：超过 MaxRetainedMessages 时从头部淘汰最旧的消息，
    /// 并把被淘汰项的缩略图置 null，让对应 BitmapImage 尽快被回收
    /// </summary>
    private void TrimRetainedMessages()
    {
        var trimmed = false;
        while (_allMessages.Count > MaxRetainedMessages)
        {
            var oldest = _allMessages[0];
            _allMessages.RemoveAt(0);
            // 被筛选条件过滤掉的消息可能不在 Messages 中，Remove 未命中时安全返回 false
            Messages.Remove(oldest);
            oldest.Thumbnail = null;
            trimmed = true;
        }

        // 头部淘汰后新的首条消息可能带着 ShowDateHeader=false，重算一遍避免聊天流顶部丢失日期分隔符
        if (trimmed) UpdateDateHeaders(Messages);
    }

    public void ClearMessages()
    {
        var manualItems = _services.History.Query(limit: 1000).Where(h => h.IsManual).ToList();
        foreach (var item in manualItems)
        {
            _services.History.Delete(item.Id);
        }
        _allMessages.Clear();
        Messages.Clear();
        // 历史已被清空，重置水位线让下次激活重新完整加载
        _historyLoaded = false;
        _historyMaxId = 0;
    }

    private void OnEntryUpdated(ClipboardEntry entry, string? localImagePath, bool fromRemote)
    {
        if (entry == null || !entry.IsManual) return;

        var selfDeviceId = _services.Settings.DeviceId;
        var isFromSelf = !string.IsNullOrEmpty(entry.DeviceId) &&
                         string.Equals(entry.DeviceId, selfDeviceId, StringComparison.OrdinalIgnoreCase);

        var existing = _allMessages.FirstOrDefault(m => m.Id == entry.Id);
        if (existing != null) return;

        var msg = new ChatMessageItem
        {
            Id = entry.Id,
            Type = entry.Type,
            Text = entry.Text,
            ImagePath = localImagePath,
            ImageRef = entry.ImageRef,
            DeviceId = entry.DeviceId ?? "",
            DeviceName = string.IsNullOrEmpty(entry.DeviceName) ? (isFromSelf ? "本机" : "远端设备") : entry.DeviceName,
            CreatedAt = entry.CreatedAt != default ? entry.CreatedAt : DateTime.UtcNow,
            IsFromSelf = isFromSelf,
            IsManual = true,
            Thumbnail = BuildThumbnail(localImagePath),
        };

        AppendMessage(msg);
    }

    private static ImageSource? BuildThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            // 与 TransferChatPage.xaml 中气泡图片的显示上限（MaxHeight 180 / MaxWidth 240）对齐，
            // 避免按 720px 解码浪费大量位图内存。
            // 按长边约束避免超宽/超高图片被反向放大：BitmapImage 只设一个维度时会按原始比例
            // 推算另一维度，若一律限高 180，超宽长条图会被推算出极大的宽度，解码面积反而暴涨。
            bmp.DecodePixelType = DecodePixelType.Logical;
            var size = ImageCodec.TryReadPngSize(path);
            if (size is { } s && s.Width > s.Height)
            {
                // 横图按气泡宽度上限限宽
                bmp.DecodePixelWidth = 240;
            }
            else
            {
                // 竖图/方图，以及非 PNG 或读取头部失败时的兜底：按气泡高度上限限高
                bmp.DecodePixelHeight = 180;
            }
            bmp.UriSource = new Uri("file:///" + path.Replace('\\', '/'));
            return bmp;
        }
        catch { return null; }
    }

    public async Task RefreshDevicesAsync()
    {
        var s = _services.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            OnlineSummary = "未配对";
            return;
        }

        try
        {
            var list = await _services.Api.GetDevicesAsync(s.ServerUrl, s.DeviceId, s.AuthToken);
            var selfId = s.DeviceId;

            // 过滤掉本机
            var others = list.Where(d => !string.Equals(d.Id, selfId, StringComparison.OrdinalIgnoreCase)).ToList();

            var prevSelection = Devices.Where(d => d.IsSelected).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Devices.Clear();
            foreach (var d in others)
            {
                var devVm = new DeviceSelectViewModel
                {
                    Id = d.Id,
                    Name = d.Name,
                    Platform = d.Platform ?? "Unknown",
                    IsOnline = d.Online,
                    IsSelected = SelectAllDevices ? d.Online : prevSelection.Contains(d.Id),
                };
                Devices.Add(devVm);
            }

            var onlineCount = others.Count(d => d.Online);
            OnlineSummary = $"{onlineCount} 台设备在线";
            s.SaveCachedDevices(list);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Error("获取在线设备列表失败", ex);
            OnlineSummary = "获取设备列表失败";
        }
    }

    public void ApplyFilter()
    {
        var selectedDevices = Devices.Where(d => d.IsSelected).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAll = SelectAllDevices || selectedDevices.Count == 0 || selectedDevices.Count == Devices.Count;

        var filtered = _allMessages.Where(m => isAll || m.IsFromSelf || selectedDevices.Contains(m.DeviceId)).ToList();

        UpdateDateHeaders(filtered);

        Messages.Clear();
        foreach (var msg in filtered)
        {
            Messages.Add(msg);
        }
    }

    public void ToggleSelectAll(bool selectAll)
    {
        _isUpdatingDeviceSelection = true;
        try
        {
            SelectAllDevices = selectAll;
            foreach (var dev in Devices)
            {
                dev.IsSelected = selectAll && dev.IsOnline;
            }
        }
        finally
        {
            _isUpdatingDeviceSelection = false;
        }

        ApplyFilter();
    }

    public void OnDevicePillClicked(DeviceSelectViewModel target)
    {
        _isUpdatingDeviceSelection = true;
        try
        {
            if (SelectAllDevices)
            {
                SelectAllDevices = false;
                foreach (var dev in Devices)
                {
                    dev.IsSelected = string.Equals(dev.Id, target.Id, StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                if (target.IsSelected && Devices.Count(d => d.IsSelected) == 1)
                {
                    SelectAllDevices = true;
                    foreach (var dev in Devices)
                    {
                        dev.IsSelected = dev.IsOnline;
                    }
                }
                else
                {
                    target.IsSelected = !target.IsSelected;
                    var selectedCount = Devices.Count(d => d.IsSelected);
                    if (selectedCount == 0 || selectedCount == Devices.Count)
                    {
                        SelectAllDevices = true;
                        foreach (var dev in Devices) dev.IsSelected = dev.IsOnline;
                    }
                    else
                    {
                        SelectAllDevices = false;
                    }
                }
            }
        }
        finally
        {
            _isUpdatingDeviceSelection = false;
        }

        ApplyFilter();
    }

    public void SetSelectedImage(byte[] bytes, string? localPath = null)
    {
        SelectedImageBytes = bytes;
        if (localPath != null && File.Exists(localPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.DecodePixelHeight = 120;
                bmp.UriSource = new Uri("file:///" + localPath.Replace('\\', '/'));
                SelectedImageThumbnail = bmp;
            }
            catch { }
        }
        else
        {
            SelectedImageThumbnail = null;
        }
        OnPropertyChanged(nameof(HasSelectedImage));
        OnPropertyChanged(nameof(HasSelectedImageVisibility));
        OnPropertyChanged(nameof(SelectedImageInfoText));
    }

    public void ClearSelectedImage()
    {
        SelectedImageBytes = null;
        SelectedImageThumbnail = null;
        OnPropertyChanged(nameof(HasSelectedImage));
        OnPropertyChanged(nameof(HasSelectedImageVisibility));
        OnPropertyChanged(nameof(SelectedImageInfoText));
    }

    public async Task SendMessageAsync()
    {
        var text = InputText.Trim();
        var hasImg = SelectedImageBytes != null && SelectedImageBytes.Length > 0;
        if (string.IsNullOrEmpty(text) && !hasImg) return;

        var s = _services.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            _services.Tray?.Notify("NexClip 互传", "未连接服务器，无法发送");
            return;
        }

        IsSending = true;
        try
        {
            var selfDeviceId = s.DeviceId;
            if (hasImg)
            {
                var selfImgMsg = new ChatMessageItem
                {
                    Id = DateTime.UtcNow.Ticks,
                    Type = "Image",
                    Text = !string.IsNullOrEmpty(text) ? text : "已发送图片",
                    DeviceId = selfDeviceId,
                    DeviceName = s.DeviceName,
                    CreatedAt = DateTime.UtcNow,
                    IsFromSelf = true,
                    IsManual = true,
                    Thumbnail = SelectedImageThumbnail,
                };
                AppendMessage(selfImgMsg);
            }
            else if (!string.IsNullOrEmpty(text))
            {
                var selfTextMsg = new ChatMessageItem
                {
                    Id = DateTime.UtcNow.Ticks,
                    Type = "Text",
                    Text = text,
                    DeviceId = selfDeviceId,
                    DeviceName = s.DeviceName,
                    CreatedAt = DateTime.UtcNow,
                    IsFromSelf = true,
                    IsManual = true,
                };
                AppendMessage(selfTextMsg);
            }

            // 清理输入框
            InputText = "";
            var imgBytesToSend = SelectedImageBytes;
            ClearSelectedImage();

            // 发送网络请求
            if (hasImg && imgBytesToSend != null)
            {
                await _services.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, imgBytesToSend, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, isManual: true);
                if (!string.IsNullOrEmpty(text))
                {
                    await _services.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, isManual: true);
                }
            }
            else if (!string.IsNullOrEmpty(text))
            {
                var selectedTargets = Devices.Where(d => d.IsSelected).Select(d => d.Id).ToList();
                if (selectedTargets.Count > 0 && selectedTargets.Count < Devices.Count)
                {
                    await _services.Api.SendToDevicesAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, selectedTargets.ToArray());
                }
                else
                {
                    await _services.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, isManual: true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("即时互传发送失败", ex);
            _services.Tray?.Notify("NexClip 互传失败", ex.Message);
        }
        finally
        {
            IsSending = false;
        }
    }
}
