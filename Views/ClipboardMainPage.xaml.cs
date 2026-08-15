using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SyncClipboard.Desktop.ViewModels;

namespace SyncClipboard.Desktop.Views;

public sealed partial class ClipboardMainPage : Page
{
    private readonly HistoryViewModel _history;

    public ClipboardMainPage()
    {
        InitializeComponent();
        DataContext = App.Services.Main;
        _history = App.Services.HistoryVm;
        ListHost.DataContext = _history;
        if (App.Services.Engine is not null)
        {
            _history.AttachEngine(App.Services.Engine);
        }
        _ = _history.RefreshAsync();
        _ = App.Services.Main.RefreshCommand.ExecuteAsync(null);
        UpdateTabStyles();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.OpenSettings();

    private void Item_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm) vm.IsHovered = true;
    }

    private void Item_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm) vm.IsHovered = false;
    }

    private void FilterTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement el && int.TryParse(el.Tag as string, out var index))
        {
            _history.FilterIndex = index;
            UpdateTabStyles();
        }
    }

    private void UpdateTabStyles()
    {
        SetTabStyle(TabAll, TabAllText, _history.FilterIndex == 0);
        SetTabStyle(TabText, TabTextText, _history.FilterIndex == 1);
        SetTabStyle(TabImage, TabImageText, _history.FilterIndex == 2);
        SetTabStyle(TabStar, TabStarText, _history.FilterIndex == 3);
    }

    private static void SetTabStyle(Border border, TextBlock text, bool selected)
    {
        border.Background = selected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 37, 99, 235))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        text.Foreground = selected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count > 0)
        {
            foreach (var item in e.RemovedItems)
            {
                if (item is HistoryItemViewModel vm) vm.IsSelected = false;
            }
        }
        if (e.AddedItems.Count > 0)
        {
            foreach (var item in e.AddedItems)
            {
                if (item is HistoryItemViewModel vm) vm.IsSelected = true;
            }
        }
    }
}
