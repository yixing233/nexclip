using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
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

    public const int PageSize = 30;
    private int _currentOffset;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    public string EmptyHint => Items.Count == 0
        ? (string.IsNullOrWhiteSpace(SearchText) && FilterIndex == 0 ? "暂无剪贴板历史" : "没有匹配的条目")
        : "";

    /// <summary>空态提示可见性:无提示文本时整行隐藏,不占底部空间。</summary>
    public Visibility EmptyHintVisibility => string.IsNullOrEmpty(EmptyHint)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public HistoryViewModel(AppServices svc)
    {
        _svc = svc;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(EmptyHintVisibility));
        };
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(EmptyHintVisibility));
        _ = RefreshAsync();
    }

    partial void OnFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(EmptyHintVisibility));
        _ = RefreshAsync();
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

    public async Task RefreshAsync()
    {
        if (_engine is null) return;
        IsBusy = true;
        _currentOffset = 0;
        try
        {
            var type = FilterIndex switch { 1 => "Text", 2 => "Image", _ => null };
            var starred = FilterIndex == 3;
            var urlOnly = FilterIndex == 4;
            var search = SearchText?.Trim();
            var items = await Task.Run(() => _engine.History.Query(search, type, starred, PageSize, urlOnly, 0));
            Items.Clear();
            var index = 1;
            foreach (var item in items)
            {
                var vm = new HistoryItemViewModel(item, this)
                {
                    IndexInList = index <= 9 ? index : 0
                };
                index++;
                Items.Add(vm);
            }
            _currentOffset = items.Count;
            HasMore = items.Count >= PageSize;
        }
        finally
        {
            IsBusy = false;
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

    [RelayCommand]
    public void ClearAsync()
    {
        _engine?.History.Clear();
        Items.Clear();
    }
}
