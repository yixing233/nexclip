using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using SyncClipboard.Desktop.Services;

namespace SyncClipboard.Desktop.ViewModels;

/// <summary>历史列表 VM:搜索 + 分类标签(全部/文本/图片/收藏)+ 复制/删除/收藏/清空。</summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private SyncEngine? _engine;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private int filterIndex;   // 0=全部 1=文本 2=图片 3=收藏

    [ObservableProperty]
    private bool isBusy;

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
        try
        {
            var type = FilterIndex switch { 1 => "Text", 2 => "Image", _ => null };
            var starred = FilterIndex == 3;
            var items = _engine.History.Query(SearchText?.Trim(), type, starred);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new HistoryItemViewModel(item, this));
            }
        }
        finally
        {
            IsBusy = false;
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
    }

    [RelayCommand]
    public void ToggleStarAsync(HistoryItemViewModel item)
    {
        if (_engine is null) return;
        item.Starred = !item.Starred;
        _engine.History.ToggleStar(item.Item.Id, item.Starred);
        item.Item.Starred = item.Starred;
    }

    [RelayCommand]
    public void ClearAsync()
    {
        _engine?.History.Clear();
        Items.Clear();
    }
}
