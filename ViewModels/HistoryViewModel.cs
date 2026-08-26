using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NexClip.Desktop.Services;

namespace NexClip.Desktop.ViewModels;

/// <summary>历史列表 VM:搜索 + 分类标签(全部/文本/图片/收藏)+ 复制/删除/收藏/清空。</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private SyncEngine? _engine;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private int filterIndex;   // 0=全部 1=文本 2=图片 3=收藏 4=链接

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool hasMore = true;

    [ObservableProperty]
    private bool isLoadingMore;

    public const int PageSize = 50;
    private int _currentOffset;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);
    public bool IsEmpty => Items.Count == 0 && !IsBusy;

    public string EmptyTitle => IsSearching ? "未找到匹配条目" : (FilterIndex switch
    {
        1 => "暂无文本历史",
        2 => "暂无图片历史",
        3 => "暂无收藏条目",
        4 => "暂无链接历史",
        _ => "暂无剪贴板历史"
    });

    public string EmptySubtitle => IsSearching
        ? "请尝试更换其他关键词搜索"
        : "在任意应用按 Ctrl+C 复制文本或截图即可自动同步";

    public IRelayCommand ClearSearchCommand { get; }

    public HistoryViewModel(AppServices svc)
    {
        _svc = svc;
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        Items.CollectionChanged += (_, _) => NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptySubtitle));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(HasSearchText));
    }

    private CancellationTokenSource? _searchCts;

    partial void OnSearchTextChanged(string value)
    {
        NotifyStateChanged();
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = DebounceRefreshAsync(token);
    }

    private async Task DebounceRefreshAsync(CancellationToken token)
    {
        try
        {
            // 80ms 防抖，兼顾打字流畅度与即时响应
            await Task.Delay(80, token);
            if (!token.IsCancellationRequested)
            {
                await RefreshAsync();
            }
        }
        catch (TaskCanceledException) { }
    }

    partial void OnFilterIndexChanged(int value)
    {
        NotifyStateChanged();
        _ = RefreshAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyStateChanged();
    }

    private bool _attached;

    public void AttachEngine(SyncEngine engine)
    {
        if (_attached) return;
        _attached = true;
        _engine = engine;
        // 上传/推送后自动刷新列表
        engine.EntryUpdated += (_, _, _) => _ = RefreshAsync();
    }

    private int _refreshing;
    private bool _refreshPending;

    public async Task RefreshAsync()
    {
        if (_engine is null) return;
        // 若当前有刷新任务在进行，标记 pending 并在当前批次完成后自动以最新参数重试
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;
                IsBusy = true;
                _currentOffset = 0;
                var type = FilterIndex switch { 1 => "Text", 2 => "Image", _ => null };
                var starred = FilterIndex == 3;
                var urlOnly = FilterIndex == 4;
                var search = SearchText?.Trim();
                var items = await Task.Run(() => _engine.History.Query(search, type, starred, PageSize, urlOnly, 0));

                // 平滑就地更新，不调用 Items.Clear()，杜绝列表空白与闪烁
                var targetCount = items.Count;
                for (var i = 0; i < targetCount; i++)
                {
                    var raw = items[i];
                    var shortcutIndex = (i < 9) ? (i + 1) : 0;
                    if (i < Items.Count)
                    {
                        var existing = Items[i];
                        if (existing.Item.Id == raw.Id &&
                            existing.Item.Text == raw.Text &&
                            existing.Item.Starred == raw.Starred &&
                            existing.Item.Remark == raw.Remark &&
                            existing.Item.ImagePath == raw.ImagePath)
                        {
                            if (existing.IndexInList != shortcutIndex)
                            {
                                existing.IndexInList = shortcutIndex;
                            }
                            continue;
                        }
                        Items[i] = new HistoryItemViewModel(raw, this)
                        {
                            IndexInList = shortcutIndex
                        };
                    }
                    else
                    {
                        Items.Add(new HistoryItemViewModel(raw, this)
                        {
                            IndexInList = shortcutIndex
                        });
                    }
                }
                while (Items.Count > targetCount)
                {
                    Items.RemoveAt(Items.Count - 1);
                }

                _currentOffset = items.Count;
                HasMore = items.Count >= PageSize;
                NotifyStateChanged();
            } while (_refreshPending);
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_engine is null || IsLoadingMore || !HasMore || IsBusy) return;
        IsLoadingMore = true;
        try
        {
            var type = FilterIndex switch { 1 => "Text", 2 => "Image", _ => null };
            var starred = FilterIndex == 3;
            var urlOnly = FilterIndex == 4;
            var search = SearchText?.Trim();
            var offset = _currentOffset;
            var items = await Task.Run(() => _engine.History.Query(search, type, starred, PageSize, urlOnly, offset));

            if (items.Count == 0)
            {
                HasMore = false;
                return;
            }

            foreach (var item in items)
            {
                var vm = new HistoryItemViewModel(item, this)
                {
                    IndexInList = 0
                };
                Items.Add(vm);
            }

            _currentOffset += items.Count;
            HasMore = items.Count >= PageSize;
            UpdateShortcutIndices();
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private void UpdateShortcutIndices()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IndexInList = (i < 9) ? (i + 1) : 0;
        }
    }

    [RelayCommand]
    public async Task CopyAsync(HistoryItemViewModel item)
    {
        if (_engine is null) return;
        await _engine.CopyHistoryItemAsync(item.Item);
    }

    [RelayCommand]
    public void DeleteAsync(HistoryItemViewModel item)
    {
        _engine?.History.Delete(item.Item.Id);
        Items.Remove(item);
        UpdateShortcutIndices();
    }

    [RelayCommand]
    public void ToggleStarAsync(HistoryItemViewModel item)
    {
        if (_engine is null) return;
        item.Starred = !item.Starred;
        _engine.History.ToggleStar(item.Item.Id, item.Starred);
        item.Item.Starred = item.Starred;
    }

    public void UpdateText(HistoryItemViewModel item, string text)
    {
        if (_engine is null) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed == item.Item.Text) return;
        _engine.History.UpdateText(item.Item.Id, trimmed);
        item.ApplyText(trimmed);
    }

    /// <summary>更新条目备注。若输入了有效非空备注，则自动收藏该条目。</summary>
    public void UpdateRemark(HistoryItemViewModel item, string? remark)
    {
        if (_engine is null) return;
        var trimmed = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        _engine.History.UpdateRemark(item.Item.Id, trimmed);
        item.ApplyRemark(trimmed);

        if (!string.IsNullOrEmpty(trimmed))
        {
            // 增加备注后自动收藏
            if (!item.Starred)
            {
                item.Starred = true;
                item.Item.Starred = true;
                _engine.History.ToggleStar(item.Item.Id, true);
            }
        }
    }

    [RelayCommand]
    public void ClearAsync()
    {
        _engine?.History.Clear();
        Items.Clear();
    }
}
