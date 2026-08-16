using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SyncClipboard.Desktop.Models;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop.ViewModels;

/// <summary>剪贴板页 VM:连接状态 + 当前剪贴板 + 快速同步 + 手动同步当前剪贴板。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private SyncEngine? _engine;

    [ObservableProperty]
    private string connectionText = "未配置";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private ClipboardEntry? currentEntry;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private string syncText = "";

    [ObservableProperty]
    private BitmapImage? currentImage;

    public MainViewModel(AppServices svc)
    {
        _svc = svc;
        RefreshConnectionState();
    }

    /// <summary>挂接同步引擎事件(在引擎创建后调用)。</summary>
    public void AttachEngine(SyncEngine engine)
    {
        _engine = engine;
        engine.EntryUpdated += (entry, imagePath, fromPush) =>
        {
            CurrentEntry = entry;
            CurrentImage = BuildImage(imagePath);
            if (fromPush)
            {
                StatusMessage = $"收到来自 {entry.DeviceName ?? "未知设备"} 的新剪贴板";
            }
        };
        engine.ConnectionChanged += (state, message) =>
        {
            IsConnected = state is SyncEngine.ConnState.Connected or SyncEngine.ConnState.Reconnecting;
            ConnectionText = state switch
            {
                SyncEngine.ConnState.NotConfigured => "未配置或未配对",
                SyncEngine.ConnState.Connecting => "连接中…",
                SyncEngine.ConnState.Connected => "已连接",
                SyncEngine.ConnState.Reconnecting => "重连中…",
                _ => "离线",
            };
            if (!string.IsNullOrEmpty(message))
            {
                StatusMessage = message;
            }
        };
        engine.TransferChanged += (active, _) => IsBusy = active;
    }

    public void RefreshConnectionState()
    {
        var s = _svc.Settings;
        IsConnected = !string.IsNullOrWhiteSpace(s.ServerUrl) && s.IsPaired;
        ConnectionText = IsConnected ? "已连接" : "未配置或未配对";
    }

    private static BitmapImage? BuildImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
    }

    public string CurrentSummary => CurrentEntry?.Type == "Image"
        ? "[图片]"
        : (CurrentEntry?.Text?.ReplaceLineEndings(" ").Trim() ?? "");

    public string EmptyHint => CurrentEntry is null ? "服务器当前没有剪贴板内容" : "";

    partial void OnCurrentEntryChanged(ClipboardEntry? value)
    {
        OnPropertyChanged(nameof(CurrentSummary));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(IsImageEntry));
    }

    public bool IsImageEntry => CurrentEntry?.Type == "Image";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var s = _svc.Settings;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || !s.IsPaired)
        {
            IsConnected = false;
            ConnectionText = "未配置或未配对";
            StatusMessage = "请先在「设置」页填写服务器地址并完成设备配对";
            return;
        }
        IsBusy = true;
        StatusMessage = "";
        try
        {
            await (_engine?.PullCurrentAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionText = "连接失败";
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>快速同步:把文本框内容 PUT 到服务器。</summary>
    [RelayCommand]
    public async Task SyncTextAsync()
    {
        var text = SyncText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = "请输入要同步的内容";
            return;
        }
        var s = _svc.Settings;
        if (!IsConnected)
        {
            StatusMessage = "未连接服务器,请先在「设置」页配置";
            return;
        }

        IsBusy = true;
        StatusMessage = "";
        try
        {
            var entry = await _svc.Api.PutTextAsync(s.ServerUrl, "", text, s.DeviceId, s.DeviceName);
            SyncText = "";
            if (entry is not null)
            {
                // 快速同步同样写入本地历史
                _engine?.History.Insert(new Models.HistoryItem
                {
                    ServerId = entry.Id,
                    Type = "Text",
                    Text = text,
                    DeviceId = s.DeviceId,
                    DeviceName = s.DeviceName,
                    CreatedAt = DateTime.UtcNow,
                    Origin = 2,
                });
                StatusMessage = "已同步到服务器";
            }
            else
            {
                StatusMessage = "内容未变化,已跳过";
            }
            await (_engine?.PullCurrentAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            StatusMessage = $"同步失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>同步本机当前剪贴板(文本或图片)。</summary>
    [RelayCommand]
    public async Task SyncCurrentClipboardAsync()
    {
        if (!IsConnected)
        {
            StatusMessage = "未连接服务器,请先在「设置」页配置";
            return;
        }
        IsBusy = true;
        StatusMessage = "正在读取本机剪贴板…";
        try
        {
            if (_engine is not null)
            {
                await _engine.SyncCurrentClipboardAsync();
                StatusMessage = "本机剪贴板已检查并同步";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"同步失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
