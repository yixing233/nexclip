using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NexClip.Desktop.Services;
using NexClip.Desktop.ViewModels;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace NexClip.Desktop.Views;

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
        _history = App.Services.HistoryVm;
        DataContext = _history;
        if (App.Services.Engine is not null)
        {
            _history.AttachEngine(App.Services.Engine);
            App.Services.ChatVm.AttachEngine(App.Services.Engine);
        }
        _ = _history.RefreshAsync();
        _ = App.Services.Main.RefreshCommand.ExecuteAsync(null);
        CategorySelectorBar.SelectedItem = TabAll;
        // 页面构造时 App.ClipboardWindow 尚未赋值(窗口构造完成后才设置静态属性),
        // 窗口事件订阅放在 Loaded 中;否则置顶图标同步 / 呼出聚焦都不会生效。
        Loaded += (_, _) =>
        {
            RefreshThemeIcons();
            if (App.ClipboardWindow is { } win)
            {
                win.TopmostChanged += UpdateTopmostIcon;
                UpdateTopmostIcon(win.IsTopmost);
                // 呼出后聚焦列表:方向键候选 + 回车粘贴立即可用
                win.Shown += FocusEntryList;
                // 每次呼出时刷新置顶提示:用户在设置里改过置顶热键后提示随之更新
                win.Shown += () => ToolTipService.SetToolTip(TopmostButton, BuildTopmostTooltip(win.IsTopmost));
            }
        };
        ActualThemeChanged += (_, _) => RefreshThemeIcons();
        // 列表模板应用后挂接内部 ScrollViewer,跟踪滚动位置
        EntryList.Loaded += (_, _) => AttachScroller();

        TransferChatHost.ImagePreviewRequested += (path, thumb) =>
        {
            OpenImageViewer(path, thumb, "互传图片预览");
        };
    }

    /// <summary>动态刷新顶栏矢量图标的主题颜色(深/浅色模式切换时秒级重绘)。</summary>
    public void RefreshThemeIcons()
    {
        TopmostIconPin.Source = Lucide.Pin;
        TopmostIconPinOff.Source = Lucide.PinOffActive;
        SettingsButtonImage.Source = Lucide.Settings;
        ClearSearchImage.Source = Lucide.X;
        ClearHistoryButtonImage.Source = Lucide.Trash;
    }

    /// <summary>清空剪贴板历史记录(始终保留收藏的条目)。</summary>
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var starredCount = App.Services.History.CountStarred();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = starredCount > 0
                ? $"确定要清空历史记录吗？\n（已收藏的 {starredCount} 条记录将自动保留，此操作不可恢复）"
                : "确定要清空所有未收藏的历史记录吗？\n（此操作不可恢复）",
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "清空历史",
            Content = panel,
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _history.ClearAsync();
        }
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
        ToolTipService.SetToolTip(TopmostButton, BuildTopmostTooltip(topmost));
    }

    /// <summary>置顶按钮提示文案：附带当前生效的置顶热键（未设置或被占用时只显示动作名）。</summary>
    private static string BuildTopmostTooltip(bool topmost)
    {
        var action = topmost ? "取消置顶" : "置顶窗口";
        var hotkey = App.Services.Settings.HotkeyTopmost;
        return App.HotkeyTopmost is { IsRegistered: true } && !string.IsNullOrWhiteSpace(hotkey)
            ? $"{action} ({hotkey})"
            : action;
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

    /// <summary>程序化切换分类或互传标签页(0:全部, 1:文本, 2:图片, 3:文件, 4:收藏, 5:即时互传)。</summary>
    public void SelectTab(int filterIndex)
    {
        foreach (var item in CategorySelectorBar.Items)
        {
            if (item.Tag as string == filterIndex.ToString())
            {
                CategorySelectorBar.SelectedItem = item;
                break;
            }
        }
    }

    private void CategorySelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem selectedItem) return;
        if (!int.TryParse(selectedItem.Tag as string, out var filterIndex)) return;

        var visualIndex = sender.Items.IndexOf(selectedItem);
        if (visualIndex < 0) visualIndex = 0;

        var oldVisualIndex = _lastVisualIndex;
        _lastVisualIndex = visualIndex;

        if (filterIndex == 5)
        {
            ListHost.Visibility = Visibility.Collapsed;
            TransferChatHost.Visibility = Visibility.Visible;
            PlayTransition(TransferChatHost, ChatTranslateTransform, oldVisualIndex, visualIndex);
            TransferChatHost.OnActivated();
            return;
        }
        else
        {
            ListHost.Visibility = Visibility.Visible;
            TransferChatHost.Visibility = Visibility.Collapsed;
            PlayTransition(ListHost, ListTranslateTransform, oldVisualIndex, visualIndex);

            // 重置列表滚动条到顶部
            if (_listScroller is not null && _listScroller.VerticalOffset > 0)
            {
                _listScroller.ChangeView(null, 0, null, true);
            }

            _history.FilterIndex = filterIndex;
        }
    }

    /// <summary>分类切换转场动效:根据左右切换方向,执行平滑位移 + 淡入淡出动画。</summary>
    private void PlayTransition(UIElement target, TranslateTransform transform, int oldVisualIndex, int newVisualIndex)
    {
        if (oldVisualIndex == newVisualIndex) return;

        double fromX = newVisualIndex > oldVisualIndex ? 26.0 : -26.0;

        _transitionStoryboard?.Stop();

        var slideAnim = new DoubleAnimation
        {
            From = fromX,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(slideAnim, transform);
        Storyboard.SetTargetProperty(slideAnim, "X");

        var fadeAnim = new DoubleAnimation
        {
            From = 0.2,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fadeAnim, target);
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

    /// <summary>呼出窗口后聚焦列表并选中条目(回车即粘贴)。若开启保持位置则不强制滚回首项。</summary>
    private void FocusEntryList()
    {
        var rememberPos = App.Services?.Settings?.RememberScrollPosition ?? false;
        if (EntryList.Items.Count > 0)
        {
            if (!rememberPos)
            {
                EntryList.SelectedIndex = 0;
                EntryList.ScrollIntoView(EntryList.SelectedItem);
            }
            else if (EntryList.SelectedIndex < 0)
            {
                EntryList.SelectedIndex = 0;
            }
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

        // 触底自动触发懒加载 (仅在用户主动向下滚动超过顶部区域且距离底部小于 200px 时提前预加载，避免打开窗口或处于顶部时误触发)
        if (_listScroller.ScrollableHeight > 0 &&
            offset > 80 &&
            offset >= _listScroller.ScrollableHeight - 200 &&
            !_history.IsLoadingMore &&
            _history.HasMore)
        {
            _ = _history.LoadMoreAsync();
        }
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
    private string? _currentViewerImagePath;
    private double _currentRotation;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _imageTapTimer;
    private HistoryItemViewModel? _pendingPreviewVm;

    /// <summary>单击图片缩略图延时呼出大图查看器（留出双击直接粘贴判定时间窗口）。</summary>
    private void ImageThumbnail_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            e.Handled = true;
            _imageTapTimer?.Stop();
            _pendingPreviewVm = vm;

            _imageTapTimer = DispatcherQueue.CreateTimer();
            _imageTapTimer.Interval = TimeSpan.FromMilliseconds(220);
            _imageTapTimer.IsRepeating = false;
            _imageTapTimer.Tick += (_, _) =>
            {
                _imageTapTimer?.Stop();
                var target = _pendingPreviewVm;
                _pendingPreviewVm = null;
                if (target is not null && ImageViewerOverlay.Visibility != Visibility.Visible)
                {
                    OpenImageViewer(target);
                }
            };
            _imageTapTimer.Start();
        }
    }

    /// <summary>双击图片缩略图 → 立即取消单击看图计时，触发直接粘贴。</summary>
    private async void ImageThumbnail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _imageTapTimer?.Stop();
        _pendingPreviewVm = null;

        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            Log.Debug($"图片缩略图双击直接粘贴:id={vm.Item.Id}");
            await (App.ClipboardWindow?.PasteItemAsync(vm) ?? Task.CompletedTask);
        }
    }

    private SmartAction? _contextSmartAction;

    /// <summary>菜单打开时兜底取选中项(兼容 Shift+F10 键盘呼出),动态控制智能直达、编辑/看图菜单显隐,并清除残留悬停态。</summary>
    private void EntryMenu_Opening(object? sender, object e)
    {
        if (EntryList.SelectedItem is HistoryItemViewModel vm) _contextItem = vm;
        if (sender is MenuFlyout flyout)
        {
            var isImage = _contextItem?.IsImage ?? false;
            var text = _contextItem?.Item.Text;
            _contextSmartAction = (!isImage && !string.IsNullOrWhiteSpace(text)) ? SmartActionService.Detect(text) : null;

            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem)
                {
                    if (menuItem.Name == "SmartPrimaryMenuItem")
                    {
                        if (_contextSmartAction != null)
                        {
                            menuItem.Text = _contextSmartAction.PrimaryButtonText;
                            if (menuItem.Icon is ImageIcon imgIcon)
                            {
                                imgIcon.Source = _contextSmartAction.PrimaryButtonIcon ?? _contextSmartAction.Icon ?? Lucide.ExternalLink;
                            }
                            menuItem.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            menuItem.Visibility = Visibility.Collapsed;
                        }
                    }
                    else if (menuItem.Name == "SmartSecondaryMenuItem")
                    {
                        if (_contextSmartAction?.SecondaryButtonText != null)
                        {
                            menuItem.Text = _contextSmartAction.SecondaryButtonText;
                            if (menuItem.Icon is ImageIcon imgIcon)
                            {
                                imgIcon.Source = _contextSmartAction.SecondaryButtonIcon ?? Lucide.Copy;
                            }
                            menuItem.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            menuItem.Visibility = Visibility.Collapsed;
                        }
                    }
                    else if (menuItem.Name == "RemarkMenuItem")
                    {
                        menuItem.Text = (_contextItem?.HasRemark == true) ? "修改备注" : "添加备注";
                    }
                    else if (menuItem.Name == "EditMenuItem" || menuItem.Name == "PastePlainTextMenuItem" || menuItem.Name == "CopyPlainTextMenuItem")
                    {
                        menuItem.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
                    }
                    else if (menuItem.Name is "ViewImageMenuItem" or "OpenSystemViewerMenuItem" or "SaveImageMenuItem" or "LocateFileMenuItem")
                    {
                        menuItem.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
                    }
                    else if (menuItem.Name == "CopyAppNameMenuItem")
                    {
                        menuItem.Visibility = (_contextItem?.HasSourceApp ?? false) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                else if (item is MenuFlyoutSeparator separator)
                {
                    if (separator.Name == "SmartActionSeparator")
                    {
                        separator.Visibility = _contextSmartAction != null ? Visibility.Visible : Visibility.Collapsed;
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

    private void SmartPrimaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _contextSmartAction?.PrimaryAction();
    }

    private void SmartSecondaryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _contextSmartAction?.SecondaryAction?.Invoke();
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

    private async void PushToDevicesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm && App.Services.Engine is { } engine)
        {
            try
            {
                var ok = await engine.PushHistoryItemAsync(vm.Item);
                if (ok)
                {
                    App.Services.Tray?.Notify("NexClip 同步", "已成功推送到所有设备");
                }
            }
            catch (Exception ex)
            {
                Log.Error("推送到设备失败", ex);
                App.Services.Tray?.Notify("NexClip 推送失败", ex.Message);
            }
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

    private async void PastePlainTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) await (App.ClipboardWindow?.PasteItemAsync(vm, plainText: true) ?? Task.CompletedTask);
    }

    private void CopyPlainTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm && vm.Item.Text is not null)
        {
            ImageCodec.WriteClipboardText(vm.Item.Text);
        }
    }

    private async void RemarkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } vm) await ShowRemarkDialogAsync(vm);
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

    private void CopyAppNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { HasSourceApp: true } vm)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(vm.SourceAppName);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
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
    private static void LocateInExplorer(string? imagePath) => NativeMethods.LocateInExplorer(imagePath);

    private void OpenInSystemApp_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentViewerVm?.Item.ImagePath ?? _currentViewerImagePath;
        if (!string.IsNullOrEmpty(path)) OpenWithSystemViewer(path);
    }

    private double _currentZoom = 1.0;
    private bool _isDraggingViewer;
    private Windows.Foundation.Point _lastViewerDragPoint;

    private void ResetViewerTransform()
    {
        _currentZoom = 1.0;
        _currentRotation = 0;
        if (ViewerRotateTransform != null) ViewerRotateTransform.Angle = 0;
        if (ViewerScaleTransform != null)
        {
            ViewerScaleTransform.ScaleX = 1.0;
            ViewerScaleTransform.ScaleY = 1.0;
        }
        if (ViewerTranslateTransform != null)
        {
            ViewerTranslateTransform.X = 0;
            ViewerTranslateTransform.Y = 0;
        }
        if (ViewerZoomText != null) ViewerZoomText.Text = "100%";
    }

    /// <summary>打开全屏大图查看器浮层 (历史记录条目)。</summary>
    private void OpenImageViewer(HistoryItemViewModel vm)
    {
        _currentViewerVm = vm;
        _currentViewerImagePath = vm.Item.ImagePath;
        ResetViewerTransform();

        if (!string.IsNullOrEmpty(vm.Item.ImagePath) && File.Exists(vm.Item.ImagePath))
        {
            try
            {
                // 解码尺寸必须先于 UriSource 设置才会生效；查看器 1600 逻辑像素宽已足够，避免超大原图整幅解码。
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical;
                bmp.DecodePixelWidth = 1600;
                bmp.UriSource = new Uri("file:///" + vm.Item.ImagePath.Replace('\\', '/'));
                ViewerImage.Source = bmp;
            }
            catch
            {
                ViewerImage.Source = vm.Thumbnail;
            }
        }
        else
        {
            ViewerImage.Source = vm.Thumbnail;
        }

        ViewerInfoText.Text = $"图片预览 · {vm.RelativeTime}";
        ImageViewerOverlay.Visibility = Visibility.Visible;
        ImageViewerOverlay.Focus(FocusState.Programmatic);
    }

    /// <summary>打开全屏大图查看器浮层 (通用图片路径/缩略图)。</summary>
    public void OpenImageViewer(string? imagePath, ImageSource? thumbnail, string title = "图片预览")
    {
        _currentViewerVm = null;
        _currentViewerImagePath = imagePath;
        ResetViewerTransform();

        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            try
            {
                // 解码尺寸必须先于 UriSource 设置才会生效；查看器 1600 逻辑像素宽已足够，避免超大原图整幅解码。
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical;
                bmp.DecodePixelWidth = 1600;
                bmp.UriSource = new Uri("file:///" + imagePath.Replace('\\', '/'));
                ViewerImage.Source = bmp;
            }
            catch
            {
                ViewerImage.Source = thumbnail;
            }
        }
        else if (thumbnail != null)
        {
            ViewerImage.Source = thumbnail;
        }

        ViewerInfoText.Text = title;
        ImageViewerOverlay.Visibility = Visibility.Visible;
        ImageViewerOverlay.Focus(FocusState.Programmatic);
    }

    private void CloseViewer_Click(object sender, RoutedEventArgs e) => CloseImageViewer();

    /// <summary>关闭大图查看器浮层。</summary>
    private void CloseImageViewer()
    {
        ImageViewerOverlay.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        _currentViewerVm = null;
        _currentViewerImagePath = null;
        ResetViewerTransform();
        EntryList.Focus(FocusState.Programmatic);
    }

    private void ViewerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewerCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private void ViewerCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var pt = e.GetCurrentPoint(ViewerCanvas);
        var delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;

        var factor = delta > 0 ? 1.25 : 0.8;
        var newZoom = Math.Clamp(_currentZoom * factor, 0.1, 10.0);
        if (Math.Abs(newZoom - _currentZoom) < 0.001) return;

        if (newZoom <= 1.05 && delta < 0)
        {
            ResetViewerTransform();
            return;
        }

        var centerX = ViewerCanvas.ActualWidth / 2.0;
        var centerY = ViewerCanvas.ActualHeight / 2.0;
        var mouseRelX = pt.Position.X - centerX;
        var mouseRelY = pt.Position.Y - centerY;

        var curTx = ViewerTranslateTransform.X;
        var curTy = ViewerTranslateTransform.Y;
        var zoomRatio = newZoom / _currentZoom;
        var newTx = mouseRelX - (mouseRelX - curTx) * zoomRatio;
        var newTy = mouseRelY - (mouseRelY - curTy) * zoomRatio;

        _currentZoom = newZoom;
        ViewerScaleTransform.ScaleX = newZoom;
        ViewerScaleTransform.ScaleY = newZoom;
        ViewerTranslateTransform.X = newTx;
        ViewerTranslateTransform.Y = newTy;
        ViewerZoomText.Text = $"{(int)Math.Round(newZoom * 100)}%";
    }

    private void ViewerCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(ViewerCanvas);
        if (pt.Properties.IsLeftButtonPressed || pt.Properties.IsMiddleButtonPressed)
        {
            _isDraggingViewer = true;
            _lastViewerDragPoint = pt.Position;
            ViewerCanvas.CapturePointer(e.Pointer);
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
            e.Handled = true;
        }
    }

    private void ViewerCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingViewer) return;

        var pt = e.GetCurrentPoint(ViewerCanvas);
        var deltaX = pt.Position.X - _lastViewerDragPoint.X;
        var deltaY = pt.Position.Y - _lastViewerDragPoint.Y;

        if (Math.Abs(deltaX) > 0.1 || Math.Abs(deltaY) > 0.1)
        {
            ViewerTranslateTransform.X += deltaX;
            ViewerTranslateTransform.Y += deltaY;
            _lastViewerDragPoint = pt.Position;
        }
        e.Handled = true;
    }

    private void ViewerCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingViewer)
        {
            _isDraggingViewer = false;
            try { ViewerCanvas.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            e.Handled = true;
        }
    }

    private void ViewerCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_currentZoom >= 1.9)
        {
            ResetViewerTransform();
        }
        else
        {
            var pt = e.GetPosition(ViewerCanvas);
            var centerX = ViewerCanvas.ActualWidth / 2.0;
            var centerY = ViewerCanvas.ActualHeight / 2.0;
            var mouseRelX = pt.X - centerX;
            var mouseRelY = pt.Y - centerY;

            var newZoom = 2.0;
            var zoomRatio = newZoom / _currentZoom;
            var curTx = ViewerTranslateTransform.X;
            var curTy = ViewerTranslateTransform.Y;
            var newTx = mouseRelX - (mouseRelX - curTx) * zoomRatio;
            var newTy = mouseRelY - (mouseRelY - curTy) * zoomRatio;

            _currentZoom = newZoom;
            ViewerScaleTransform.ScaleX = newZoom;
            ViewerScaleTransform.ScaleY = newZoom;
            ViewerTranslateTransform.X = newTx;
            ViewerTranslateTransform.Y = newTy;
            ViewerZoomText.Text = $"{(int)Math.Round(newZoom * 100)}%";
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        var newZoom = Math.Min(_currentZoom * 1.25, 10.0);
        var zoomRatio = newZoom / _currentZoom;
        _currentZoom = newZoom;
        ViewerScaleTransform.ScaleX = newZoom;
        ViewerScaleTransform.ScaleY = newZoom;
        ViewerTranslateTransform.X *= zoomRatio;
        ViewerTranslateTransform.Y *= zoomRatio;
        ViewerZoomText.Text = $"{(int)Math.Round(newZoom * 100)}%";
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        var newZoom = Math.Max(_currentZoom / 1.25, 0.1);
        if (newZoom <= 1.05)
        {
            ResetViewerTransform();
            return;
        }
        var zoomRatio = newZoom / _currentZoom;
        _currentZoom = newZoom;
        ViewerScaleTransform.ScaleX = newZoom;
        ViewerScaleTransform.ScaleY = newZoom;
        ViewerTranslateTransform.X *= zoomRatio;
        ViewerTranslateTransform.Y *= zoomRatio;
        ViewerZoomText.Text = $"{(int)Math.Round(newZoom * 100)}%";
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ResetViewerTransform();
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        _currentRotation = (_currentRotation + 90) % 360;
        ViewerRotateTransform.Angle = _currentRotation;
    }

    private void CopyViewerImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentViewerVm is { } vm)
        {
            _ = _history.CopyAsync(vm);
        }
        else if (!string.IsNullOrEmpty(_currentViewerImagePath) && File.Exists(_currentViewerImagePath))
        {
            ImageCodec.WriteClipboardImage(_currentViewerImagePath);
            App.Services.Tray?.Notify("NexClip", "已复制图片");
        }
    }

    private void SaveViewerImage_Click(object sender, RoutedEventArgs e)
    {
        var path = _currentViewerVm?.Item.ImagePath ?? _currentViewerImagePath;
        if (!string.IsNullOrEmpty(path)) _ = SaveImageAsync(path);
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
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 180,
            MaxHeight = 360,
            MinWidth = 380,
            Text = vm.Item.Text ?? "",
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

    /// <summary>为条目添加/修改备注弹窗。添加有效备注后自动收藏该条目。</summary>
    private async Task ShowRemarkDialogAsync(HistoryItemViewModel vm)
    {
        var box = new TextBox
        {
            Text = vm.Remark ?? "",
            PlaceholderText = "输入备注内容 (例如: 常用密码、发票抬头、重要配置等)",
            AcceptsReturn = false,
            MaxLength = 200,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var promptText = new TextBlock
        {
            Text = "添加备注后将自动收藏该条目，并可通过顶栏搜索框直接检索备注。",
            FontSize = 12,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel
        {
            Spacing = 8,
            Children = { promptText, box }
        };

        var dialog = new ContentDialog
        {
            Title = vm.HasRemark ? "修改备注" : "添加备注",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (vm.HasRemark)
        {
            dialog.SecondaryButtonText = "清除备注";
        }

        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        box.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
                _history.UpdateRemark(vm, box.Text);
            }
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _history.UpdateRemark(vm, box.Text);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            _history.UpdateRemark(vm, null);
        }
    }

    /// <summary>双击条目 → 粘贴到呼出前的窗口(按住 Shift 时触发纯文本粘贴，按钮区域不触发)。</summary>
    private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsOverButton(e.OriginalSource)) return;
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm)
        {
            var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Log.Debug($"双击粘贴:id={vm.Item.Id}, plainText={isShift}, type={vm.Item.Type}, text={vm.Item.Text?.Substring(0, Math.Min(20, vm.Item.Text?.Length ?? 0))}");
            await (App.ClipboardWindow?.PasteItemAsync(vm, plainText: isShift) ?? Task.CompletedTask);
        }
    }

    /// <summary>回车 → 粘贴选中条目(按住 Shift 时触发纯文本粘贴);方向键由 ListView 原生支持(候选移动)。</summary>
    private async void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && EntryList.SelectedItem is HistoryItemViewModel vm)
        {
            e.Handled = true;
            var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            await (App.ClipboardWindow?.PasteItemAsync(vm, plainText: isShift) ?? Task.CompletedTask);
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

    /// <summary>页面级快捷键拦截:为前 9 项历史记录提供 Ctrl+1~9 快速选定并直接粘贴(按住 Shift 为纯文本); 空格键快速预览/关闭大图。</summary>
    private async void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;

        // 空格键极速看图/关图 (类似 macOS QuickLook / PowerToys Peek，搜索框聚焦输入时不拦截)
        if (e.Key == VirtualKey.Space && SearchBox.FocusState == FocusState.Unfocused)
        {
            if (ImageViewerOverlay.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseImageViewer();
                return;
            }
            else if (EntryList.SelectedItem is HistoryItemViewModel { IsImage: true } selectedImageVm)
            {
                e.Handled = true;
                OpenImageViewer(selectedImageVm);
                return;
            }
        }

        // 若大图查看器处于激活态，不拦截数字键
        if (ImageViewerOverlay.Visibility == Visibility.Visible) return;

        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (isCtrl)
        {
            var targetIndex = e.Key switch
            {
                VirtualKey.Number1 or VirtualKey.NumberPad1 => 0,
                VirtualKey.Number2 or VirtualKey.NumberPad2 => 1,
                VirtualKey.Number3 or VirtualKey.NumberPad3 => 2,
                VirtualKey.Number4 or VirtualKey.NumberPad4 => 3,
                VirtualKey.Number5 or VirtualKey.NumberPad5 => 4,
                VirtualKey.Number6 or VirtualKey.NumberPad6 => 5,
                VirtualKey.Number7 or VirtualKey.NumberPad7 => 6,
                VirtualKey.Number8 or VirtualKey.NumberPad8 => 7,
                VirtualKey.Number9 or VirtualKey.NumberPad9 => 8,
                _ => -1
            };

            if (targetIndex >= 0 && targetIndex < _history.Items.Count)
            {
                e.Handled = true;
                var targetItem = _history.Items[targetIndex];
                EntryList.SelectedItem = targetItem;
                var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                Log.Info($"触发快捷键 Ctrl+{(isShift ? "Shift+" : "")}{targetIndex + 1}: 粘贴条目 id={targetItem.Item.Id}, plainText={isShift}");
                await (App.ClipboardWindow?.PasteItemAsync(targetItem, plainText: isShift) ?? Task.CompletedTask);
            }
        }
    }

    // ==================== 全窗口拖拽发送覆盖层 (Drag & Drop Overlay) ====================

    private void ShowDragDropOverlay(bool show)
    {
        if (show)
        {
            DragDropOverlay.Visibility = Visibility.Visible;
            DragDropOverlay.Opacity = 1;
        }
        else
        {
            DragDropOverlay.Opacity = 0;
            DragDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Bitmap) ||
            e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "发送到 NexClip";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            ShowDragDropOverlay(true);
        }
    }

    private void Page_DragLeave(object sender, DragEventArgs e)
    {
        ShowDragDropOverlay(false);
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        ShowDragDropOverlay(false);
        var def = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file)
                    {
                        var ext = file.FileType.ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                        {
                            var bytes = await File.ReadAllBytesAsync(file.Path);
                            await UploadDroppedImageAsync(bytes, file.Name);
                        }
                        else
                        {
                            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                await UploadDroppedTextAsync(text);
                            }
                        }
                    }
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.Bitmap))
            {
                var bmpStreamRef = await e.DataView.GetBitmapAsync();
                using var stream = await bmpStreamRef.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                await UploadDroppedImageAsync(ms.ToArray(), "screenshot.png");
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await UploadDroppedTextAsync(text);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("拖拽发送处理失败", ex);
        }
        finally
        {
            def.Complete();
        }
    }

    private async Task UploadDroppedImageAsync(byte[] bytes, string fileName)
    {
        var s = App.Services.Settings;
        if (!s.IsPaired || string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            App.Services.Tray?.Notify("NexClip", "未配对服务器，无法发送拖拽内容");
            return;
        }
        try
        {
            await App.Services.Api.UploadImageAsync(s.ServerUrl, s.AuthToken, bytes, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, isManual: true);
            App.Services.Tray?.Notify("NexClip 拖拽发送", $"已发送图片: {fileName}");
        }
        catch (Exception ex)
        {
            Log.Error("拖拽发送图片失败", ex);
            App.Services.Tray?.Notify("NexClip 发送失败", ex.Message);
        }
    }

    private async Task UploadDroppedTextAsync(string text)
    {
        var s = App.Services.Settings;
        if (!s.IsPaired || string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            App.Services.Tray?.Notify("NexClip", "未配对服务器，无法发送拖拽内容");
            return;
        }
        try
        {
            await App.Services.Api.PutTextAsync(s.ServerUrl, s.AuthToken, text, s.DeviceId, s.DeviceName, "Windows", Environment.OSVersion.VersionString, isManual: true);
            App.Services.Tray?.Notify("NexClip 拖拽发送", "已发送文本内容");
        }
        catch (Exception ex)
        {
            Log.Error("拖拽发送文本失败", ex);
            App.Services.Tray?.Notify("NexClip 发送失败", ex.Message);
        }
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 37, 99, 235));
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.ClearValue(TextBox.BorderBrushProperty);
        }
    }
}
