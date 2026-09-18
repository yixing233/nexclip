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
    public IRelayCommand BatchPasteCommand { get; }

    public HistoryViewModel(AppServices svc)
    {
        _svc = svc;
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleMultiSelectCommand = new RelayCommand(ToggleMultiSelect);
        BatchStarCommand = new AsyncRelayCommand(() => ApplyBatchStarAsync(true));
        BatchUnstarCommand = new AsyncRelayCommand(() => ApplyBatchStarAsync(false));
        BatchDeleteCommand = new AsyncRelayCommand(BatchDeleteAsync);
        BatchPasteCommand = new RelayCommand(BatchPaste);
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

    /// <summary>
    /// 通知批量选择计数变化。批量模式的勾选由列表控件自身的选中语义驱动
    /// (见 ClipboardMainPage.ApplySelectionModeForBatch):EntryList_SelectionChanged
    /// 会直接改写条目的 IsBatchSelected,不经过本类,必须由调用方显式调用本方法刷新计数与按钮可用态。
    /// </summary>
    public void NotifyBatchStateChanged()
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

    /// <summary>
    /// 批量粘贴请求:合并好的文本交给窗口执行"写剪贴板 → 隐藏 → 回焦 → 注入粘贴键"。
    /// 经事件转发而非直接调用窗口,保持本类不依赖具体窗口(与现有 ViewModel 一致)。
    /// </summary>
    public event Action<string>? BatchPasteRequested;

    /// <summary>
    /// 批量粘贴:把所选条目中可合并的文本拼接成一段,一次写入剪贴板并粘贴一次。
    /// 采用一次注入而非逐条注入:回焦时序很敏感,逐条注入容易丢条或乱序。
    ///
    /// 合并顺序按时间升序(旧 → 新):列表本身是倒序显示,但拼接结果是被当作一段内容阅读的,
    /// 按事件发生顺序排列更自然。
    ///
    /// 图片与文件条目的内容无法与文本拼成同一段,一律跳过并提示,
    /// 避免用户误以为"选的都粘上了"。
    /// </summary>
    private void BatchPaste()
    {
        var selected = Items.Where(x => x.IsBatchSelected).ToList();
        if (selected.Count == 0) return;

        var texts = selected
            .Select(x => x.Item)
            .Where(i => i.Type == "Text" && !string.IsNullOrEmpty(i.Text))
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Text!.TrimEnd())
            .ToList();
        var skipped = selected.Count - texts.Count;

        if (texts.Count == 0)
        {
            _svc.Tray?.Notify("NexClip 批量粘贴", "所选条目没有可粘贴的文本内容");
            return;
        }

        BatchPasteRequested?.Invoke(string.Join(Environment.NewLine, texts));

        // 跳过情况必须说清楚:混合选中时用户只会看到少了内容,不知道是图片/文件被刻意跳过。
        // 这里只陈述"合并了几条、跳过几条"这一已知事实,不声称粘贴成功——能否落到目标窗口
        // 取决于目标是否还存在,失败情形由窗口侧记日志(与单条粘贴一致,不弹提示)。
        if (skipped > 0)
        {
            _svc.Tray?.Notify("NexClip 批量粘贴", $"已合并 {texts.Count} 条文本，跳过 {skipped} 条非文本条目");
        }
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

    /// <summary>
    /// 同步单条条目的收藏/备注到服务端,只提交显式指定的字段。
    /// 只改了本地库不上报的话,服务端仍认为该条未收藏,一旦服务端到历史上限就会把这条最旧的
    /// 记录连同图片一起淘汰,表现为"收藏过的条目自己消失了"。
    /// 上报失败只记日志:本地已生效,不应因为一次网络抖动回滚用户刚做的操作。
    /// </summary>
    private async Task SyncEntryMetadataAsync(HistoryItemViewModel item, bool sendStar, bool sendRemark)
    {
        var s = _svc.Settings;
        if (!s.IsPaired || string.IsNullOrWhiteSpace(s.ServerUrl)) return;
        if (item.Item.ServerId is not > 0) return;
        try
        {
            await _svc.Api.UpdateEntryMetadataAsync(
                s.ServerUrl, s.DeviceId, s.AuthToken, item.Item.ServerId.Value,
                sendStar ? item.Starred : null,
                item.Remark,
                sendRemark);
        }
        catch (Exception ex)
        {
            Log.Warn($"同步远端条目元数据失败: {ex.Message}");
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
    public async Task ToggleStarAsync(HistoryItemViewModel item)
    {
        if (_engine is null) return;
        item.Starred = !item.Starred;
        _engine.History.ToggleStar(item.Item.Id, item.Starred);
        item.Item.Starred = item.Starred;
        // 只上报 star:本地不缓存服务端备注,一并提交会把别的设备写的备注清掉
        await SyncEntryMetadataAsync(item, sendStar: true, sendRemark: false);
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
    public async Task UpdateRemarkAsync(HistoryItemViewModel item, string? remark)
    {
        if (_engine is null) return;
        var trimmed = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        _engine.History.UpdateRemark(item.Item.Id, trimmed);
        item.ApplyRemark(trimmed);

        var autoStarred = false;
        if (!string.IsNullOrEmpty(trimmed) && !item.Starred)
        {
            // 增加备注后自动收藏
            item.Starred = true;
            item.Item.Starred = true;
            _engine.History.ToggleStar(item.Item.Id, true);
            autoStarred = true;
        }

        // 备注是本次操作的显式目标,必须上报;收藏只在因备注而新变化时才随带上报
        await SyncEntryMetadataAsync(item, sendStar: autoStarred, sendRemark: true);
    }

    [RelayCommand]
    public async Task ClearAsync()
    {
        _engine?.History.Clear(keepStarred: true);
        await RefreshAsync();
    }
}
