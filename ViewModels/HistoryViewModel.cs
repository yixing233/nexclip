using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NexClip.Desktop.Services;

namespace NexClip.Desktop.ViewModels;

/// <summary>历史列表 VM:搜索 + 分类标签(全部/文本/图片/文件/收藏/链接)+ 复制/删除/收藏/清空。</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private SyncEngine? _engine;

    [ObservableProperty]
    private string searchText = "";

    /// <summary>
    /// 分类索引。取值与 SelectorBarItem 的 Tag 一一对应:
    /// 0=全部 1=文本 2=图片 3=收藏 4=链接 6=文件(5 为"即时互传"页签,不落到本属性)。
    /// </summary>
    [ObservableProperty]
    private int filterIndex;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool hasMore = true;

    [ObservableProperty]
    private bool isLoadingMore;

    public const int PageSize = 50;

    /// <summary>下拉加载在运行期最多保留的条目数，达到上限后不再继续追加，避免列表与缩略图无上限堆积</summary>
    private const int MaxLoadedItems = 300;

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
        6 => "暂无文件历史",
        _ => "暂无剪贴板历史"
    });

    public string EmptySubtitle => IsSearching
        ? "请尝试更换其他关键词搜索"
        : FilterIndex == 6
            ? "在资源管理器复制文件即可自动记录，文件仅保存在本地，不会同步到服务器"
            : "在任意应用按 Ctrl+C 复制文本或截图即可自动同步";

    /// <summary>分类索引到存储层 type 过滤值的映射(null 表示不过滤)。</summary>
    private string? TypeFilter => FilterIndex switch
    {
        1 => "Text",
        2 => "Image",
        6 => "File",
        _ => null,
    };

    public IRelayCommand ClearSearchCommand { get; }

    /// <summary>是否处于批量选择模式。</summary>
    [ObservableProperty]
    private bool isMultiSelectMode;

    public int BatchSelectedCount => Items.Count(x => x.IsBatchSelected);
    public bool HasBatchSelection => BatchSelectedCount > 0;

    public IRelayCommand ToggleMultiSelectCommand { get; }
    public IRelayCommand BatchStarCommand { get; }
    public IRelayCommand BatchUnstarCommand { get; }
    public IRelayCommand BatchDeleteCommand { get; }

    public HistoryViewModel(AppServices svc)
    {
        _svc = svc;
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleMultiSelectCommand = new RelayCommand(ToggleMultiSelect);
        BatchStarCommand = new AsyncRelayCommand(() => ApplyBatchStarAsync(true));
        BatchUnstarCommand = new AsyncRelayCommand(() => ApplyBatchStarAsync(false));
        BatchDeleteCommand = new AsyncRelayCommand(BatchDeleteAsync);
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
        // 先取出旧实例再替换，最后取消并释放旧实例：
        // 避免新建的 token 被误释放，同时保证旧 CancellationTokenSource 的内部资源被及时回收
        var old = _searchCts;
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try
        {
            old?.Cancel();
            old?.Dispose();
        }
        catch (ObjectDisposedException) { /* 已被释放，忽略 */ }
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
                var type = TypeFilter;
                var starred = FilterIndex == 3;
                var urlOnly = FilterIndex == 4;
                var search = SearchText?.Trim();
                var items = await Task.Run(() => _engine.History.Query(search, type, starred, PageSize, urlOnly, 0));

                // 复用内容未变化的条目:按 Id 建索引,命中且展示字段一致时沿用原 VM,
                // 从而保留其缩略图与来源图标 BitmapImage,不再重新解码。
                var reusable = new Dictionary<long, HistoryItemViewModel>(Items.Count);
                foreach (var vm in Items) reusable[vm.Item.Id] = vm;

                var target = new List<HistoryItemViewModel>(items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    var raw = items[i];
                    var shortcutIndex = (i < 9) ? (i + 1) : 0;
                    if (reusable.Remove(raw.Id, out var existing) && IsUnchanged(existing.Item, raw))
                    {
                        existing.IndexInList = shortcutIndex;
                        target.Add(existing);
                    }
                    else
                    {
                        target.Add(new HistoryItemViewModel(raw, this)
                        {
                            IndexInList = shortcutIndex
                        });
                    }
                }

                ApplyMinimalDiff(target);

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

    /// <summary>条目展示字段是否与已有 VM 完全一致(一致则复用原 VM,不重建缩略图)。</summary>
    private static bool IsUnchanged(Models.HistoryItem a, Models.HistoryItem b) =>
        a.Id == b.Id &&
        a.Text == b.Text &&
        a.Starred == b.Starred &&
        a.Remark == b.Remark &&
        a.ImagePath == b.ImagePath &&
        a.FilePathsJson == b.FilePathsJson;

    /// <summary>
    /// 求出新旧列表的公共前缀与公共后缀,只对中间差异区间做增删。
    /// 旧实现按下标逐项比较,列表头部插入一条新记录就会让后续每一项都判定为"已变化"而整段重建,
    /// 每次复制都要重新构造约 50 个 ViewModel 并重新解码 50 张缩略图,既造成内存抖动也拖慢 UI。
    /// 采用最小差异后,新增一条记录只产生 1 次 Insert。
    /// </summary>
    private void ApplyMinimalDiff(List<HistoryItemViewModel> target)
    {
        var oldCount = Items.Count;
        var newCount = target.Count;

        var prefix = 0;
        while (prefix < oldCount && prefix < newCount && ReferenceEquals(Items[prefix], target[prefix])) prefix++;

        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix &&
               ReferenceEquals(Items[oldCount - 1 - suffix], target[newCount - 1 - suffix])) suffix++;

        var removeCount = oldCount - prefix - suffix;
        for (var i = 0; i < removeCount; i++) Items.RemoveAt(prefix);

        for (var i = prefix; i < newCount - suffix; i++) Items.Insert(i, target[i]);
    }

    public async Task LoadMoreAsync()
    {
        if (_engine is null || IsLoadingMore || !HasMore || IsBusy) return;

        // 已达运行期保留上限：停止继续加载，防止 Items 与其缩略图无上限增长
        if (Items.Count >= MaxLoadedItems)
        {
            HasMore = false;
            return;
        }

        IsLoadingMore = true;
        try
        {
            var type = TypeFilter;
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

    private void ToggleMultiSelect()
    {
        IsMultiSelectMode = !IsMultiSelectMode;
        if (!IsMultiSelectMode)
        {
            foreach (var item in Items) item.IsBatchSelected = false;
        }
        NotifyBatchStateChanged();
    }

    public void ToggleBatchSelection(HistoryItemViewModel item)
    {
        if (!IsMultiSelectMode) return;
        item.IsBatchSelected = !item.IsBatchSelected;
        NotifyBatchStateChanged();
    }

    private void NotifyBatchStateChanged()
    {
        OnPropertyChanged(nameof(BatchSelectedCount));
        OnPropertyChanged(nameof(HasBatchSelection));
    }

    private async Task ApplyBatchStarAsync(bool starred)
    {
        if (_engine is null) return;
        var selected = Items.Where(x => x.IsBatchSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var item in selected)
        {
            item.Starred = starred;
            item.Item.Starred = starred;
            _engine.History.ToggleStar(item.Item.Id, starred);
        }
        await SyncBatchMetadataAsync(starred ? "star" : "unstar", selected);
        NotifyBatchStateChanged();
    }

    private async Task BatchDeleteAsync()
    {
        if (_engine is null) return;
        var selected = Items.Where(x => x.IsBatchSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var item in selected)
        {
            _engine.History.Delete(item.Item.Id);
            Items.Remove(item);
        }
        await SyncBatchMetadataAsync("delete", selected);
        UpdateShortcutIndices();
        NotifyBatchStateChanged();
    }

    private async Task SyncBatchMetadataAsync(string action, IReadOnlyList<HistoryItemViewModel> items)
    {
        var s = _svc.Settings;
        if (!s.IsPaired || string.IsNullOrWhiteSpace(s.ServerUrl)) return;
        var ids = items.Where(x => x.Item.ServerId is > 0).Select(x => x.Item.ServerId!.Value).ToArray();
        if (ids.Length == 0) return;
        try
        {
            await _svc.Api.BatchUpdateEntriesAsync(s.ServerUrl, s.DeviceId, s.AuthToken, action, ids);
        }
        catch (Exception ex)
        {
            Log.Warn($"批量同步远端元数据失败: {ex.Message}");
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
    public async Task ClearAsync()
    {
        _engine?.History.Clear(keepStarred: true);
        await RefreshAsync();
    }
}
