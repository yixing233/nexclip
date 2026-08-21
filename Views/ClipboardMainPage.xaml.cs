using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SyncClipboard.Desktop.Services;
using SyncClipboard.Desktop.ViewModels;
using System.Diagnostics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace SyncClipboard.Desktop.Views;

public sealed partial class ClipboardMainPage : Page
{
    private readonly HistoryViewModel _history;
    private const double BackToTopThreshold = 300;
    private const int BackToTopShowDelayMs = 400;
    private const int BackToTopHideDelayMs = 600;
    private ScrollViewer? _listScroller;
    private double _lastOffset;
    private HistoryItemViewModel? _contextItem;
    private Storyboard? _backToTopStoryboard;
    private CancellationTokenSource? _backToTopCts;
    private bool _backToTopReturnInProgress;

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
        CategorySelectorBar.SelectedItem = TabAll;
        // 页面构造时 App.ClipboardWindow 尚未赋值(窗口构造完成后才设置静态属性),
        // 窗口事件订阅放在 Loaded 中;否则置顶图标同步 / 呼出聚焦都不会生效。
        Loaded += (_, _) =>
        {
            if (App.ClipboardWindow is { } win)
            {
                win.TopmostChanged += UpdateTopmostIcon;
                UpdateTopmostIcon(win.IsTopmost);
                // 呼出后聚焦列表:方向键候选 + 回车粘贴立即可用
                win.Shown += FocusEntryList;
            }
        };
        // 列表模板应用后挂接内部 ScrollViewer,跟踪滚动位置
        EntryList.Loaded += (_, _) => AttachScroller();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.OpenSettings();

    /// <summary>置顶按钮:切换窗口置顶,图标随之切换(pin ⇄ pin-off)。</summary>
    private void TopmostButton_Click(object sender, RoutedEventArgs e) =>
        App.ClipboardWindow?.ToggleTopmost();

    private static readonly Windows.UI.Color TopmostBlue = Microsoft.UI.ColorHelper.FromArgb(255, 37, 99, 235);
    private static readonly Windows.UI.Color TopmostHoverBlue = Microsoft.UI.ColorHelper.FromArgb(255, 29, 78, 216);
    private static readonly Windows.UI.Color TopmostPressedBlue = Microsoft.UI.ColorHelper.FromArgb(255, 30, 64, 175);
    private Storyboard? _iconFadeStoryboard;
    private Storyboard? _bgColorStoryboard;

    /// <summary>置顶状态样式:图标交叉淡化 + 背景颜色过渡,避免切换瞬间闪烁。</summary>
    private void UpdateTopmostIcon(bool topmost)
    {
        AnimateIconFade(topmost ? TopmostIconPinOff : TopmostIconPin, 1);
        AnimateIconFade(topmost ? TopmostIconPin : TopmostIconPinOff, 0);
        AnimateBackgroundColor(topmost ? TopmostBlue : Microsoft.UI.Colors.Transparent);
        SetButtonBrushColor(TopmostButton, "ButtonBackgroundPointerOver", topmost ? TopmostHoverBlue : Microsoft.UI.Colors.Transparent);
        SetButtonBrushColor(TopmostButton, "ButtonBackgroundPressed", topmost ? TopmostPressedBlue : Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(TopmostButton, topmost ? "取消置顶" : "置顶窗口");
    }

    private static void SetButtonBrushColor(Button button, string key, Windows.UI.Color color)
    {
        if (button.Resources[key] is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private void AnimateIconFade(UIElement element, double to)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(140),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        // 不 Stop 旧动画:Stop 会让属性瞬间跳回动画前的基础值,快速切换时反而闪烁。
        // 新动画从元素当前值(前一个动画的终值)平滑过渡,后开始者自然接管。
        _iconFadeStoryboard = storyboard;
        storyboard.Begin();
    }

    private void AnimateBackgroundColor(Windows.UI.Color to)
    {
        if (TopmostButton.Background is not SolidColorBrush brush)
        {
            TopmostButton.Background = new SolidColorBrush(to);
            return;
        }
        var animation = new ColorAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(160),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, brush);
        Storyboard.SetTargetProperty(animation, "Color");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        _bgColorStoryboard = storyboard;
        storyboard.Begin();
    }

    private void Item_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm) vm.IsHovered = true;
    }

    private void Item_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm) vm.IsHovered = false;
    }

    private int _lastVisualIndex;
    private Storyboard? _transitionStoryboard;

    private void CategorySelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem selectedItem) return;
        if (!int.TryParse(selectedItem.Tag as string, out var filterIndex)) return;

        var visualIndex = sender.Items.IndexOf(selectedItem);
        if (visualIndex < 0) visualIndex = 0;

        var oldVisualIndex = _lastVisualIndex;
        _lastVisualIndex = visualIndex;

        // 执行平滑转场动画
        PlayListTransition(oldVisualIndex, visualIndex);

        // 重置列表滚动条到顶部
        if (_listScroller is not null && _listScroller.VerticalOffset > 0)
        {
            _listScroller.ChangeView(null, 0, null, true);
        }

        _history.FilterIndex = filterIndex;
    }

    /// <summary>分类切换列表转场动效:根据左右切换方向,执行平滑位移 + 淡入淡出动画。</summary>
    private void PlayListTransition(int oldVisualIndex, int newVisualIndex)
    {
        if (oldVisualIndex == newVisualIndex) return;

        double fromX = newVisualIndex > oldVisualIndex ? 30.0 : -30.0;

        _transitionStoryboard?.Stop();

        var slideAnim = new DoubleAnimation
        {
            From = fromX,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slideAnim, ListTranslateTransform);
        Storyboard.SetTargetProperty(slideAnim, "X");

        var fadeAnim = new DoubleAnimation
        {
            From = 0.25,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fadeAnim, ListHost);
        Storyboard.SetTargetProperty(fadeAnim, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slideAnim);
        storyboard.Children.Add(fadeAnim);
        _transitionStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>Ctrl+F:聚焦搜索框并全选,直接输入即可过滤。</summary>
    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    /// <summary>呼出窗口后聚焦列表并选中最新条目(回车即粘贴)。</summary>
    private void FocusEntryList()
    {
        if (EntryList.Items.Count > 0)
        {
            EntryList.SelectedIndex = 0;
            EntryList.ScrollIntoView(EntryList.SelectedItem);
        }
        EntryList.Focus(FocusState.Programmatic);
    }

    /// <summary>挂接列表内部滚动容器,跟踪滚动方向与距离。</summary>
    private void AttachScroller()
    {
        if (_listScroller is not null) return;
        _listScroller = FindDescendant<ScrollViewer>(EntryList);
        if (_listScroller is null)
        {
            // 模板尚未应用,布局更新后再试一次
            EntryList.LayoutUpdated += OnListLayoutUpdated;
            return;
        }
        _lastOffset = _listScroller.VerticalOffset;
        _listScroller.ViewChanged += EntryListScroller_ViewChanged;
    }

    private void OnListLayoutUpdated(object? sender, object e)
    {
        EntryList.LayoutUpdated -= OnListLayoutUpdated;
        AttachScroller();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// 回到顶部按钮显隐:下滑超过阈值后,上滑时淡入;显示后保持可见,
    /// 悬停期间不隐藏(以 IsPointerOver 实时命中为准),鼠标离开且接近顶部时才收起。
    /// </summary>
    private void EntryListScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroller is null) return;
        var offset = _listScroller.VerticalOffset;

        // 回顶动画期间由 ScrollViewer 自己驱动滚动。此时暂停按钮显隐判断，
        // 否则中间帧会把“点击后立即隐藏”重新排队成一次淡入，造成闪烁。
        if (_backToTopReturnInProgress)
        {
            _lastOffset = offset;
            if (!e.IsIntermediate || offset <= 1)
            {
                _backToTopReturnInProgress = false;
                if (offset <= BackToTopThreshold)
                {
                    HideBackToTopButtonImmediately();
                }
            }
            return;
        }

        var scrolledUp = offset < _lastOffset - 20;   // 20px 死区:避免布局抖动被误判为“上滑”
        _lastOffset = offset;
        var visible = BackToTopButton.Visibility == Visibility.Visible;
        // 动画只改 Opacity(不位移/缩放),按钮命中区域稳定;
        // IsPointerOver 实时查询当前悬停状态,不再依赖可能漏触发的 PointerEntered。
        var hovered = visible && BackToTopButton.IsPointerOver;
        var show = visible
            ? hovered || offset > BackToTopThreshold
            : offset > BackToTopThreshold && scrolledUp;
        if (show) ShowBackToTopButton();
        else HideBackToTopButton();
    }

    /// <summary>点击回到列表顶部并立即收起按钮,滚动由 ScrollViewer 平滑完成。</summary>
    private void BackToTopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listScroller is not { } scroller) return;

        _backToTopReturnInProgress = true;
        HideBackToTopButtonImmediately();
        _lastOffset = scroller.VerticalOffset;

        if (scroller.VerticalOffset <= 1)
        {
            _backToTopReturnInProgress = false;
            _lastOffset = 0;
            return;
        }

        // disableAnimation=false 使用 ScrollViewer 原生缓动,比瞬移到顶部更自然。
        scroller.ChangeView(null, 0, null, false);
    }

    private void BackToTop_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 鼠标离开且滚动位置已接近顶部时收起,避免按钮悬停后残留
        if (_listScroller is { } scroller && scroller.VerticalOffset <= BackToTopThreshold)
        {
            HideBackToTopButton();
        }
    }

    /// <summary>延迟后淡入显示;延迟期间被新的显隐请求取消,避免快速滚动时闪烁。</summary>
    private void ShowBackToTopButton()
    {
        if (_backToTopReturnInProgress) return;
        if (_listScroller is { } scroller && scroller.VerticalOffset <= BackToTopThreshold)
        {
            HideBackToTopButton();
            return;
        }

        _backToTopCts?.Cancel();
        _backToTopCts = null;

        // 已经稳定可见时只需取消待隐藏任务,不要重复启动淡入动画。
        if (BackToTopButton.Visibility == Visibility.Visible && BackToTopButton.Opacity >= 0.99)
        {
            return;
        }

        // 如果正在淡出,保留当前透明度并立即反向淡入,避免先完全消失再出现。
        if (BackToTopButton.Visibility == Visibility.Visible)
        {
            var currentOpacity = StopBackToTopStoryboard();
            FadeBackToTop(currentOpacity, 1, 140, null);
            return;
        }

        _backToTopCts = new CancellationTokenSource();
        var cts = _backToTopCts;
        _ = ShowBackToTopCoreAsync(cts);
    }

    private async Task ShowBackToTopCoreAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(BackToTopShowDelayMs, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_backToTopCts, cts)) return;
            if (_backToTopReturnInProgress) return;
            if (_listScroller is null || _listScroller.VerticalOffset <= BackToTopThreshold) return;

            _backToTopCts = null;
            if (BackToTopButton.Visibility == Visibility.Visible)
            {
                var currentOpacity = StopBackToTopStoryboard();
                FadeBackToTop(currentOpacity, 1, 140, null);
                return;
            }

            StopBackToTopStoryboard();
            BackToTopButton.Visibility = Visibility.Visible;
            BackToTopButton.Opacity = 0;
            FadeBackToTop(0, 1, 140, null);
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>延迟后淡出隐藏;延迟期间鼠标移入按钮则取消隐藏。</summary>
    private void HideBackToTopButton()
    {
        if (_backToTopReturnInProgress) return;
        // 核心修复:悬停中的按钮绝不在滚动事件里被直接隐藏
        if (BackToTopButton.Visibility == Visibility.Visible && BackToTopButton.IsPointerOver) return;
        if (BackToTopButton.Visibility == Visibility.Collapsed) return;
        _backToTopCts?.Cancel();
        _backToTopCts = new CancellationTokenSource();
        var cts = _backToTopCts;
        _ = HideBackToTopCoreAsync(cts);
    }

    private async Task HideBackToTopCoreAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(BackToTopHideDelayMs, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_backToTopCts, cts)) return;
            if (_backToTopReturnInProgress) return;
            if (BackToTopButton.Visibility == Visibility.Collapsed) return;
            if (BackToTopButton.IsPointerOver) return;
            if (_listScroller is { VerticalOffset: > BackToTopThreshold }) return;

            _backToTopCts = null;
            var currentOpacity = StopBackToTopStoryboard();
            if (currentOpacity <= 0.01)
            {
                BackToTopButton.Opacity = 0;
                BackToTopButton.Visibility = Visibility.Collapsed;
                return;
            }

            FadeBackToTop(currentOpacity, 0, 150, () =>
            {
                if (BackToTopButton.IsPointerOver || (_listScroller is { VerticalOffset: > BackToTopThreshold }))
                {
                    // 淡出期间鼠标进入按钮或用户重新向下滚动:恢复显示
                    ShowBackToTopButton();
                }
                else
                {
                    BackToTopButton.Opacity = 0;
                    BackToTopButton.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>纯 Opacity 淡入淡出;不位移/缩放,保证悬停命中区域稳定不闪烁。</summary>
    private void FadeBackToTop(double from, double to, double ms, Action? completed)
    {
        BackToTopButton.Opacity = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, BackToTopButton);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            // 只有当前活跃的动画才执行收尾,避免旧动画完成后误收起新显示的按钮。
            if (!ReferenceEquals(_backToTopStoryboard, storyboard)) return;
            _backToTopStoryboard = null;
            storyboard.Stop();
            BackToTopButton.Opacity = to;
            completed?.Invoke();
        };
        _backToTopStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>停止按钮动画并保留停止瞬间的透明度,避免 Storyboard.Stop 恢复旧基值导致闪烁。</summary>
    private double StopBackToTopStoryboard()
    {
        var currentOpacity = BackToTopButton.Opacity;
        if (_backToTopStoryboard is { } storyboard)
        {
            _backToTopStoryboard = null;
            storyboard.Stop();
            BackToTopButton.Opacity = currentOpacity;
        }
        return currentOpacity;
    }

    /// <summary>回顶点击专用的立即收起,不受鼠标悬停保护和延迟隐藏影响。</summary>
    private void HideBackToTopButtonImmediately()
    {
        _backToTopCts?.Cancel();
        _backToTopCts = null;
        StopBackToTopStoryboard();
        BackToTopButton.Opacity = 0;
        BackToTopButton.Visibility = Visibility.Collapsed;
    }

    private HistoryItemViewModel? _currentViewerVm;
    private double _currentRotation;

    /// <summary>点击图片缩略图直接呼出大图查看器。</summary>
    private void ImageThumbnail_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            e.Handled = true;
            OpenImageViewer(vm);
        }
    }

    /// <summary>菜单打开时兜底取选中项(兼容 Shift+F10 键盘呼出),动态控制编辑/看图菜单显隐,并清除残留悬停态。</summary>
    private void EntryMenu_Opening(object? sender, object e)
    {
        if (EntryList.SelectedItem is HistoryItemViewModel vm) _contextItem = vm;
        if (sender is MenuFlyout flyout)
        {
            var isImage = _contextItem?.IsImage ?? false;
            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem)
                {
                    if (menuItem.Name == "EditMenuItem") menuItem.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
                    if (menuItem.Name is "ViewImageMenuItem" or "OpenSystemViewerMenuItem" or "SaveImageMenuItem" or "LocateFileMenuItem")
                    {
                        menuItem.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }
        // ContextFlyout 弹出时旧条目的 PointerExited 可能不触发,统一清除所有条目的悬停态,
        // 避免"之前悬停的条目"在菜单打开后仍残留悬停样式。
        foreach (var item in EntryList.Items)
        {
            if (item is HistoryItemViewModel hovered) hovered.IsHovered = false;
        }
    }

    /// <summary>右键条目:记录目标并同步到 ListView 真实选中项,便于菜单操作。</summary>
    private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            _contextItem = vm;
            // 同步 ListView 选中项:SelectionChanged 会负责清除旧条目选中态,
            // 否则菜单关闭后切换选中时,该条目永远不会被 SelectionChanged 清理,选中样式残留。
            EntryList.SelectedItem = vm;
            vm.IsSelected = true;
        }
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) _ = _history.CopyAsync(vm);
    }

    private void StarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) _history.ToggleStarAsync(vm);
    }

    private async void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) await ShowEditDialogAsync(vm);
    }

    private void ViewImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) OpenImageViewer(vm);
    }

    private void OpenSystemViewerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) OpenWithSystemViewer(vm.Item.ImagePath);
    }

    private void SaveImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) _ = SaveImageAsync(vm.Item.ImagePath);
    }

    private void LocateFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) LocateInExplorer(vm.Item.ImagePath);
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) _history.DeleteAsync(vm);
    }

    /// <summary>调用系统默认关联看图软件打开原图。</summary>
    private static void OpenWithSystemViewer(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("使用系统默认看图软件打开失败", ex);
        }
    }

    /// <summary>在 Windows 文件资源管理器中定位并选中图片文件。</summary>
    private static void LocateInExplorer(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{imagePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("在资源管理器中定位文件失败", ex);
        }
    }

    private void OpenInSystemApp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentViewerVm is { } vm) OpenWithSystemViewer(vm.Item.ImagePath);
    }

    /// <summary>打开全屏大图查看器浮层。</summary>
    private void OpenImageViewer(HistoryItemViewModel vm)
    {
        _currentViewerVm = vm;
        _currentRotation = 0;
        ViewerRotateTransform.Angle = 0;
        ViewerImage.Source = vm.Thumbnail;
        ViewerInfoText.Text = $"图片预览 · {vm.RelativeTime}";
        ViewerZoomText.Text = "100%";
        ImageViewerOverlay.Visibility = Visibility.Visible;
        ViewerScroller.ChangeView(0, 0, 1.0f, true);
        ImageViewerOverlay.Focus(FocusState.Programmatic);
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseImageViewer();

    /// <summary>关闭大图查看器浮层。</summary>
    private void CloseImageViewer()
    {
        ImageViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        _currentViewerVm = null;
        EntryList.Focus(FocusState.Programmatic);
    }

    private void ViewerScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ViewerZoomText.Text = $"{(int)Math.Round(ViewerScroller.ZoomFactor * 100)}%";
    }

    private void ViewerScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var target = ViewerScroller.ZoomFactor >= 1.9f ? 1.0f : 2.0f;
        ViewerScroller.ChangeView(null, null, target, false);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        var next = Math.Min(ViewerScroller.ZoomFactor * 1.25f, 8.0f);
        ViewerScroller.ChangeView(null, null, next, false);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        var next = Math.Max(ViewerScroller.ZoomFactor / 1.25f, 0.1f);
        ViewerScroller.ChangeView(null, null, next, false);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ViewerScroller.ChangeView(0, 0, 1.0f, false);
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _currentRotation = (_currentRotation + 90) % 360;
        ViewerRotateTransform.Angle = _currentRotation;
    }

    private void CopyViewerImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentViewerVm is { } vm) _ = _history.CopyAsync(vm);
    }

    private void SaveViewerImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentViewerVm is { } vm) _ = SaveImageAsync(vm.Item.ImagePath);
    }

    /// <summary>大图浮层快捷键支持(Esc 退出, Ctrl+C 复制, Ctrl+S 另存为, +/- 缩放)。</summary>
    private void ImageViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseImageViewer();
            return;
        }

        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (isCtrl && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            CopyViewerImage_Click(sender, e);
        }
        else if (isCtrl && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            SaveViewerImage_Click(sender, e);
        }
        else if (e.Key == VirtualKey.Add || e.Key == (VirtualKey)187)
        {
            e.Handled = true;
            ZoomIn_Click(sender, e);
        }
        else if (e.Key == VirtualKey.Subtract || e.Key == (VirtualKey)189)
        {
            e.Handled = true;
            ZoomOut_Click(sender, e);
        }
    }

    /// <summary>保存图片到用户指定文件路径。</summary>
    private async Task SaveImageAsync(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        try
        {
            var picker = new FileSavePicker();
            var win = App.ClipboardWindow;
            if (win is null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG 图片 (*.png)", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG 图片 (*.jpg)", new List<string> { ".jpg", ".jpeg" });
            picker.SuggestedFileName = $"NexClip_{DateTime.Now:yyyyMMdd_HHmmss}";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                File.Copy(imagePath, file.Path, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("另存为图片失败", ex);
        }
    }

    /// <summary>编辑对话框:修改文本条目内容(仅本地记录)。</summary>
    private async Task ShowEditDialogAsync(HistoryItemViewModel vm)
    {
        if (vm.IsImage) return;
        var box = new TextBox
        {
            Text = vm.Item.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 260,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            Title = "编辑条目",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None, // 设为 None 避免 Enter 键被弹窗直接作为确认键拦截,允许输入多行
            XamlRoot = XamlRoot,
        };

        // 支持 Ctrl+Enter 快捷保存
        box.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Enter &&
                (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            {
                e.Handled = true;
                dialog.Hide();
                _history.UpdateText(vm, box.Text);
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _history.UpdateText(vm, box.Text);
        }
    }

    /// <summary>双击条目 → 粘贴到呼出前的窗口(按钮区域不触发)。</summary>
    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsOverButton(e.OriginalSource)) return;
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            Log.Debug($"双击粘贴:id={vm.Item.Id}, type={vm.Item.Type}, text={vm.Item.Text?.Substring(0, Math.Min(20, vm.Item.Text?.Length ?? 0))}");
            await (App.ClipboardWindow?.PasteItemAsync(vm) ?? Task.CompletedTask);
        }
    }

    /// <summary>回车 → 粘贴选中条目;方向键由 ListView 原生支持(候选移动)。</summary>
    private async void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && EntryList.SelectedItem is HistoryItemViewModel vm)
        {
            e.Handled = true;
            await (App.ClipboardWindow?.PasteItemAsync(vm) ?? Task.CompletedTask);
        }
    }

    /// <summary>事件源是否落在条目卡片的操作按钮上(按钮区双击不触发粘贴)。</summary>
    private static bool IsOverButton(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button) return true;
        }
        return false;
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
