using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using NexClip.Desktop.Services;
using NexClip.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.Foundation;

namespace NexClip.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    /// <summary>对外暴露 NavigationView(供设置窗口标题栏汉堡切换侧边栏)。</summary>
    public Microsoft.UI.Xaml.Controls.NavigationView SettingsNav => NavView;

    public SettingsViewModel ViewModel => _vm;

    private readonly SettingsViewModel _vm;
    private ContentDialog? _appFilterDialog;

    /// <summary>应用过滤搜索框建议列表的最大高度(超出后在弹出层内滚动,不截断条目)。</summary>
    private const double ProcessSuggestionListHeight = 360;

    /// <summary>
    /// 应用过滤搜索框的建议项:进程 + 按需解码的图标。
    /// BitmapImage 是 DependencyObject,只能在 UI 线程创建,所以不能塞进后台线程产出的
    /// RunningProcessOption 里,由这层在 UI 线程按 IconPath 现解码。
    /// 模板见 SettingsPage.xaml 的 AppFilterProcessTemplate。
    /// </summary>
    public sealed class ProcessSuggestion(ClipboardAppFilter.RunningProcessOption option, Func<string?, BitmapImage?> iconResolver)
    {
        private BitmapImage? _icon;
        private bool _iconResolved;

        public ClipboardAppFilter.RunningProcessOption Option { get; } = option;

        /// <summary>
        /// 图标延迟解码:候选项可能有两三百个,而绑定只在虚拟化列表真正生成这一行时才读这个属性,
        /// 所以只有滚到眼前的行会去读磁盘。
        /// </summary>
        public BitmapImage? Icon
        {
            get
            {
                if (_iconResolved) return _icon;
                _iconResolved = true;
                _icon = iconResolver(Option.IconPath);
                return _icon;
            }
        }

        public string Label => Option.Label;
        public override string ToString() => Label;
    }

    /// <summary>悬浮通知自动关闭计时器(显示后 4 秒消失)。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _toastTimer;

    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.Services.SettingsVm;
        DataContext = _vm;
        // 悬浮通知:消息打开/替换时重新计时;页面卸载时取消订阅并停止计时
        _vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _toastTimer?.Stop();
        };
        // 打开设置页即加载设备列表 + 刷新数据统计;默认选中"同步"
        Loaded += async (_, _) =>
        {
            if (NavView.SelectedItem is null && NavView.MenuItems.Count > 0)
            {
                NavView.SelectedItem = NavView.MenuItems[0];
            }
            else if (NavView.SelectedItem is NavigationViewItem selectedItem && selectedItem.Tag is string selectedTag)
            {
                ShowGroup(selectedTag);
            }

            _vm.RefreshDataStatus();

            // 消息可能在设置页实例化前就已经产生(例如启动时热键注册反馈),
            // 页面加载后补启动一次计时，避免提示因错过 PropertyChanged 而常驻。
            if (_vm.MessageOpen)
            {
                StartToastAutoClose();
            }

            // 先完成页面结构和滚动定位。
            ResetContentScroll();
            // 采用 SWR 策略: 首屏已立即渲染本地缓存的旧列表, 此处在后台异步静默校验刷新最新状态
            _ = _vm.RefreshDevicesCommand.ExecuteAsync(null);
        };
        // 配对码生成完成 → 弹出对话框展示(二维码扫码直连 + 6位纯数字验证码);关闭即作废
        _vm.PairingCodeGenerated += async (result) =>
        {
            var serverUrl = _vm.ServerUrl?.Trim() ?? "";
            var qrPayload = !string.IsNullOrWhiteSpace(result.QrPayload)
                ? result.QrPayload
                : (!string.IsNullOrWhiteSpace(serverUrl) ? $"{serverUrl.TrimEnd('/')}/index?pairCode={result.Code}" : result.Code);

            var qrBitmap = await GenerateQrCodeBitmapAsync(qrPayload);

            var panel = new StackPanel { Spacing = 12, MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Center };

            // 1. 方案 1: 二维码扫码直连卡片
            if (qrBitmap != null)
            {
                var qrBorder = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(12),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var qrImg = new Image
                {
                    Source = qrBitmap,
                    Width = 160,
                    Height = 160,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                };
                qrBorder.Child = qrImg;
                panel.Children.Add(qrBorder);

                panel.Children.Add(new TextBlock
                {
                    Text = "手机使用系统相机或扫一扫即可一秒直连",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                });

                // 分隔提示
                var dividerGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                dividerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dividerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                dividerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var div1 = new Border { Height = 1, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };
                var divText = new TextBlock { Text = " 或输入 6 位验证码 ", FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
                var div2 = new Border { Height = 1, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], VerticalAlignment = VerticalAlignment.Center };

                Grid.SetColumn(div1, 0);
                Grid.SetColumn(divText, 1);
                Grid.SetColumn(div2, 2);
                dividerGrid.Children.Add(div1);
                dividerGrid.Children.Add(divText);
                dividerGrid.Children.Add(div2);
                panel.Children.Add(dividerGrid);
            }

            // 2. 方案 2: 6 位纯数字验证码卡片
            var codeContainer = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var codeGrid = new Grid();
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeLabel = new TextBlock
            {
                Text = "配对验证码",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var codeBox = new TextBlock
            {
                Text = result.Code,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 24,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            var copyCodeBtn = new Button
            {
                Content = "复制验证码",
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            copyCodeBtn.Click += (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(result.Code);
                Clipboard.SetContent(pkg);
                copyCodeBtn.Content = "已复制";
            };

            Grid.SetColumn(codeLabel, 0);
            Grid.SetColumn(codeBox, 1);
            Grid.SetColumn(copyCodeBtn, 2);
            codeGrid.Children.Add(codeLabel);
            codeGrid.Children.Add(codeBox);
            codeGrid.Children.Add(copyCodeBtn);
            codeContainer.Child = codeGrid;
            panel.Children.Add(codeContainer);

            // 3. 提示说明
            panel.Children.Add(new TextBlock
            {
                Text = "在另一台设备上扫码或输入 6 位数字验证码即可直接连接。\n关闭对话框后验证码立即失效。",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });

            var dialog = new ContentDialog
            {
                Title = "添加新设备 (配对)",
                Content = panel,
                PrimaryButtonText = "完成并关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            await dialog.ShowAsync();
            // 对话框关闭(无论按钮/遮罩)→ 配对码作废
            await _vm.RevokeGeneratedCodeAsync();
        };
    }

    // ========== 侧边栏导航 ==========

    /// <summary>统一消息打开或内容被替换时,重新启动 4 秒自动关闭。</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.MessageOpen))
        {
            if (_vm.MessageOpen) StartToastAutoClose();
            else _toastTimer?.Stop();
            return;
        }

        if (e.PropertyName == nameof(_vm.MessageText) &&
            _vm.MessageOpen &&
            !string.IsNullOrWhiteSpace(_vm.MessageText))
        {
            StartToastAutoClose();
        }
    }

    private void StartToastAutoClose()
    {
        if (_toastTimer is null)
        {
            _toastTimer = DispatcherQueue.CreateTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(4);
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += (_, _) =>
            {
                _vm.MessageOpen = false;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>NavigationView 选中变化 → 切换分组。</summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowGroup(tag);
            _vm.RefreshDataStatus();
        }
    }

    private void ShowGroup(string tag)
    {
        GroupServer.Visibility = tag == "server" ? Visibility.Visible : Visibility.Collapsed;
        GroupGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        GroupClipboard.Visibility = tag == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        GroupHotkey.Visibility = tag == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
        GroupData.Visibility = tag == "data" ? Visibility.Visible : Visibility.Collapsed;
        GroupAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        ResetContentScroll();   // 切换分组回到顶部
    }

    /// <summary>对外提供快速跳转到“关于”分组的能力。</summary>
    public void NavigateToAbout()
    {
        foreach (var item in NavView.FooterMenuItems)
        {
            if (item is NavigationViewItem nvi && (nvi.Tag as string) == "about")
            {
                NavView.SelectedItem = nvi;
                ShowGroup("about");
                return;
            }
        }
    }

    /// <summary>
    /// 将当前分组滚动到顶部。窗口隐藏后再次打开时也会调用，使用低优先级队列
    /// 确保内容已经完成测量，避免 ChangeView 在布局前被忽略。
    /// </summary>
    public void ResetContentScroll()
    {
        if (!IsLoaded) return;

        ContentScroller.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            ContentScroller.ChangeView(null, 0, null, true);
        });
    }

    // ========== 关于 ==========

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/yixing233/nexclip") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开项目主页失败", ex);
            _vm.ShowMessage($"打开项目主页失败：{ServerApi.DescribeException(ex, "请检查默认浏览器设置。")}", InfoBarSeverity.Error);
        }
    }

    private void CopyVersionInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = $"NexClip {_vm.VersionText} | 框架: WinUI 3 / .NET 9 | 服务端: NexClip Server (node) | 数据位置: {_vm.ConfiguredStorageDir} | 项目: github.com/yixing233/nexclip";
            var pkg = new DataPackage();
            pkg.SetText(info);
            Clipboard.SetContent(pkg);
            _vm.ShowMessage("版本信息已复制到剪贴板", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("复制版本信息失败", ex);
            _vm.ShowMessage($"复制版本信息失败：{ServerApi.DescribeException(ex, "请稍后重试。")}", InfoBarSeverity.Error);
        }
    }

    private async void ManageAppFilters_Click(object sender, RoutedEventArgs e)
    {
        if (_appFilterDialog is not null) return;
        try
        {
            Border CreateTag(string text, bool removable, Action? remove)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                if (removable && remove is not null)
                {
                    var close = new Button
                    {
                        Content = new Image { Source = Lucide.X, Width = 12, Height = 12 },
                        Padding = new Thickness(2),
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        MinWidth = 0,
                        MinHeight = 0,
                    };
                    ToolTipService.SetToolTip(close, "移除过滤进程");
                    close.Click += (_, _) => remove();
                    content.Children.Add(close);
                }
                return new Border
                {
                    Child = content,
                    Height = 32,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 6, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(12),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                };
            }

            var builtInTags = new AppFilterFlowPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var app in ClipboardAppFilter.BuiltInRemoteControlApps)
                builtInTags.Children.Add(CreateTag(app, false, null));
            var builtInScroll = new ScrollViewer { Content = builtInTags, Height = 118, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var customTags = new AppFilterFlowPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            void RenderCustomTags()
            {
                customTags.Children.Clear();
                foreach (var process in _vm.CustomFilteredProcesses.ToArray())
                {
                    var captured = process;
                    customTags.Children.Add(CreateTag(captured, true, () =>
                    {
                        _vm.RemoveCustomFilterProcess(captured);
                        RenderCustomTags();
                    }));
                }
            }
            RenderCustomTags();

            var processStatus = new TextBlock
            {
                Text = "正在获取当前运行的应用...",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 4, 0, 0),
            };
            // 搜索框:输入关键字筛选正在运行的应用;也允许直接键入当前没在运行的进程名
            var processPicker = new AutoSuggestBox
            {
                PlaceholderText = "搜索当前运行的应用，或直接输入进程名",
                TextMemberPath = nameof(ProcessSuggestion.Label),
                ItemTemplate = (DataTemplate)Resources["AppFilterProcessTemplate"],
                QueryIcon = new ImageIcon { Source = Lucide.Search, Width = 14, Height = 14 },
                // 选中建议即添加,所以不把 Label 回填到输入框(回填会让下一次筛选拿整条 Label 当关键字)
                UpdateTextOnSelect = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 360,
                // 不限制条目数,只限制弹出层高度:列表内部自己滚动,保证运行中的应用都能翻到
                MaxSuggestionListHeight = ProcessSuggestionListHeight,
                IsEnabled = false,
            };
            // 建议列表里被选中的项;为空表示"用户手打了一个名字",按输入框文本处理
            ClipboardAppFilter.RunningProcessOption? chosen = null;
            // 最近一次添加的回执:一直顶在状态行上,直到用户继续输入或刷新
            // (焦点回弹等情况会再次触发 UpdateSuggestions,回执不能一冲就没)
            string? addedNotice = null;
            // 同一个图标文件只解码一次(每次按键都会重建建议列表)
            var iconCache = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

            BitmapImage? ResolveIcon(string? iconPath)
            {
                if (string.IsNullOrEmpty(iconPath)) return null;
                if (iconCache.TryGetValue(iconPath, out var cached)) return cached;
                if (!System.IO.File.Exists(iconPath)) return null;
                try
                {
                    // 解码尺寸必须先于 UriSource 设置才生效(同 HistoryItemViewModel.BuildAppIcon)
                    var bmp = new BitmapImage { DecodePixelWidth = 32, DecodePixelHeight = 32 };
                    bmp.UriSource = new Uri("file:///" + iconPath.Replace('\\', '/'));
                    iconCache[iconPath] = bmp;
                    return bmp;
                }
                catch (Exception ex)
                {
                    Log.Debug($"加载应用图标失败({iconPath})：{ex.Message}");
                    return null;
                }
            }

            static bool MatchesKeyword(ClipboardAppFilter.RunningProcessOption option, string keyword) =>
                option.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || option.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                // Label 是"名称 (进程名)",输入框里出现整条 Label 时(粘贴、或框架回填)也要能命中
                || option.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (option.ExecutablePath?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);

            void UpdateSuggestions()
            {
                var keyword = processPicker.Text?.Trim() ?? string.Empty;
                // 不做数量截断:全部候选都进列表,靠弹出层滚动 + 列表虚拟化 + 图标懒加载扛住体量
                var matches = (keyword.Length == 0
                        ? _vm.RunningProcesses.AsEnumerable()
                        : _vm.RunningProcesses.Where(p => MatchesKeyword(p, keyword)))
                    .Select(p => new ProcessSuggestion(p, ResolveIcon))
                    .ToList();
                processPicker.ItemsSource = matches;
                processStatus.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                processStatus.Text = addedNotice ?? (_vm.RunningProcesses.Count == 0
                    ? "未检测到可用应用，可直接输入进程名后点击添加"
                    : keyword.Length == 0
                        ? $"已加载 {_vm.RunningProcesses.Count} 个正在运行的应用，输入关键字可筛选"
                        : matches.Count == 0
                            ? $"没有匹配“{keyword}”的运行中应用，可直接添加该进程名"
                            : $"匹配到 {matches.Count} 个应用");
            }

            ClipboardAppFilter.RunningProcessOption? ResolveTypedProcess()
            {
                var text = processPicker.Text?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                return _vm.RunningProcesses.FirstOrDefault(p =>
                    p.Label.Equals(text, StringComparison.OrdinalIgnoreCase)
                    || p.ProcessName.Equals(text, StringComparison.OrdinalIgnoreCase)
                    || p.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase));
            }

            void AddCurrentSelection()
            {
                // 选中项优先;没选中就把输入框文本当进程名(允许添加当前没在运行的应用)
                var option = chosen ?? ResolveTypedProcess();
                _vm.SelectedRunningProcess = option;
                var display = option?.Label ?? processPicker.Text?.Trim();
                var name = option?.ProcessName ?? processPicker.Text;
                var before = _vm.CustomFilteredProcesses.Count;
                _vm.AddCustomFilterProcess(name);
                RenderCustomTags();
                chosen = null;
                processPicker.Text = string.Empty;
                // 明确给回执:否则从列表里选完只看到"没有匹配…",看着像没添加成功
                addedNotice = string.IsNullOrWhiteSpace(display)
                    ? null
                    : _vm.CustomFilteredProcesses.Count > before
                        ? $"已添加过滤进程：{display}"
                        : $"“{display}”已在自定义过滤进程中";
                UpdateSuggestions();
            }

            processPicker.TextChanged += (_, args) =>
            {
                // 只响应用户输入;清空输入框等程序化改动不重算,避免递归
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                chosen = null;
                addedNotice = null;
                UpdateSuggestions();
            };
            processPicker.SuggestionChosen += (_, args) => chosen = (args.SelectedItem as ProcessSuggestion)?.Option;
            processPicker.QuerySubmitted += (_, args) =>
            {
                if (args.ChosenSuggestion is ProcessSuggestion suggestion) chosen = suggestion.Option;
                AddCurrentSelection();
            };

            var pickerFocused = false;
            // 正在执行"点输入框外侧 → 失焦"期间为 true:此时到来的程序化回焦要拦掉
            var defocusing = false;
            // 本轮失焦是否已经补救过一次(防止和框架来回抢焦点)
            var defocusRetried = false;
            // 失焦后焦点的落脚点,取对话框的"完成"按钮;模板部件要等对话框打开后才拿得到,所以延迟取
            Button? focusFallback = null;

            // 收起建议列表时,AutoSuggestBox 会把焦点还给它内部的 TextBox(Programmatic),
            // 这就是"失焦后立马又自动聚焦"的来源。在焦点落地之前先把它改道到"完成"按钮。
            processPicker.GettingFocus += (_, args) =>
            {
                if (!defocusing) return;
                // 用户自己点回来或用 Tab 走回来(Pointer / Keyboard)一律放行
                if (args.FocusState != FocusState.Programmatic)
                {
                    defocusing = false;
                    return;
                }
                if (focusFallback is not null && args.TrySetNewFocusedElement(focusFallback)) return;
                args.TryCancel();
            };
            // 聚焦就直接展开当前运行的应用列表,不用先打字
            processPicker.GotFocus += (_, _) =>
            {
                if (defocusing)
                {
                    // 回焦没拦住:不要把列表再弹开,并在下一帧补一次焦点转移(只补一次)
                    if (!defocusRetried && focusFallback is not null)
                    {
                        defocusRetried = true;
                        DispatcherQueue.TryEnqueue(() => { if (defocusing) focusFallback?.Focus(FocusState.Programmatic); });
                    }
                    return;
                }
                pickerFocused = true;
                UpdateSuggestions();
                processPicker.IsSuggestionListOpen = true;
            };
            processPicker.LostFocus += (_, _) => pickerFocused = false;

            var addButton = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { new Image { Source = Lucide.Plus, Width = 14, Height = 14 }, new TextBlock { Text = "添加" } },
                },
                IsEnabled = false,
            };
            ToolTipService.SetToolTip(addButton, "添加到自定义过滤进程");
            addButton.Click += (_, _) => AddCurrentSelection();

            var refreshButton = new Button
            {
                Content = new Image { Source = Lucide.RefreshCw, Width = 14, Height = 14 },
            };
            ToolTipService.SetToolTip(refreshButton, "刷新当前运行进程");
            refreshButton.Click += async (_, _) =>
            {
                addedNotice = null;
                await _vm.RefreshRunningProcessesAsync();
                UpdateSuggestions();
            };

            var pickerGrid = new Grid { ColumnSpacing = 8 };
            pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(processPicker, 0);
            Grid.SetColumn(addButton, 1);
            Grid.SetColumn(refreshButton, 2);
            pickerGrid.Children.Add(processPicker);
            pickerGrid.Children.Add(addButton);
            pickerGrid.Children.Add(refreshButton);

            var panel = new StackPanel { Spacing = 12, MinWidth = 500 };
            panel.Children.Add(new TextBlock { Text = "内置远程控制软件", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(builtInScroll);
            panel.Children.Add(new TextBlock { Text = "添加自定义过滤进程", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(pickerGrid);
            panel.Children.Add(processStatus);
            panel.Children.Add(new TextBlock { Text = "自定义过滤进程", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(new ScrollViewer { Content = customTags, MaxHeight = 120, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var dialog = new ContentDialog
            {
                Title = "管理应用过滤",
                Content = panel,
                CloseButtonText = "完成",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            _appFilterDialog = dialog;
            EnableLightDismiss(dialog);
            // 点输入框外侧就失焦:先把焦点挪到"完成"按钮,再收起建议列表
            // (Programmatic 焦点不画焦点框,所以看上去就是单纯的失焦)
            dialog.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (e.OriginalSource is not DependencyObject source) return;
                for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
                {
                    // 点在输入框自身:解除回焦拦截。PointerPressed 早于焦点落地,所以这里放开正好赶得上
                    if (ReferenceEquals(node, processPicker))
                    {
                        defocusing = false;
                        return;
                    }
                }
                // 走到这里 = 点在输入框之外(建议列表在独立弹出层,事件根本不会冒泡到对话框)
                if (!pickerFocused) return;
                defocusing = true;
                defocusRetried = false;
                focusFallback ??= FindTemplateChild<Button>(dialog, "CloseButton");
                // 顺序很重要:先转移焦点,再收列表,这样收列表引发的回焦已经落不回输入框
                focusFallback?.Focus(FocusState.Programmatic);
                processPicker.IsSuggestionListOpen = false;
            }), true);
            var dialogTask = dialog.ShowAsync().AsTask();
            try
            {
                await _vm.RefreshRunningProcessesAsync();
                processPicker.IsEnabled = true;
                addButton.IsEnabled = true;
                UpdateSuggestions();
            }
            catch (Exception ex)
            {
                Log.Error("读取当前运行进程失败", ex);
                processStatus.Text = "读取当前运行的应用失败，请稍后重试。";
                processStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
            await dialogTask;
        }
        catch (Exception ex)
        {
            Log.Error("打开应用过滤管理失败", ex);
            _vm.ShowMessage("读取当前运行进程失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _appFilterDialog = null;
        }
    }

    /// <summary>
    /// 让 ContentDialog 支持点击遮罩层关闭。ContentDialog 原生没有 light dismiss，
    /// 这里在打开后给对话框挂 PointerPressed(handledEventsToo=true)：
    /// 命中点落在对话框卡片(模板部件 BackgroundElement)之外时调用 Hide()。
    /// </summary>
    private static void EnableLightDismiss(ContentDialog dialog)
    {
        PointerEventHandler? pressed = null;

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            var card = FindTemplateChild<FrameworkElement>(dialog, "BackgroundElement");
            if (card is null)
            {
                // 拿不到模板部件就不启用，对话框仍可用底部按钮关闭
                Log.Debug("对话框未找到 BackgroundElement，跳过点击外部关闭");
                return;
            }
            pressed = (_, e) =>
            {
                if (e.OriginalSource is not DependencyObject source) return;
                for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
                {
                    if (ReferenceEquals(node, card)) return;                  // 点在卡片内部，照常交互
                    if (ReferenceEquals(node, dialog)) { dialog.Hide(); return; } // 走到对话框根都没碰到卡片 → 点在遮罩上
                }
                // 事件来自独立弹出层(如搜索建议列表、下拉菜单)，不当作点击外部
            };
            dialog.AddHandler(UIElement.PointerPressedEvent, pressed, true);
        }

        dialog.Opened += OnOpened;
        dialog.Closed += (_, _) =>
        {
            dialog.Opened -= OnOpened;
            if (pressed is not null) dialog.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            pressed = null;
        };
    }

    /// <summary>按名字在可视树里找元素(ContentDialog 的模板部件不对外暴露，只能自己找)。</summary>
    private static T? FindTemplateChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            var found = FindTemplateChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>按子项实际宽度换行，避免 WrapGrid 的统一单元格截断应用名称。</summary>
    private sealed class AppFilterFlowPanel : Panel
    {
        private const double ItemSpacing = 6;
        private const double LineSpacing = 6;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width);
            var lineWidth = 0d;
            var lineHeight = 0d;
            var totalHeight = 0d;
            var measuredWidth = 0d;

            foreach (var child in Children)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                var desired = child.DesiredSize;
                if (lineWidth > 0 && lineWidth + ItemSpacing + desired.Width > width)
                {
                    measuredWidth = Math.Max(measuredWidth, lineWidth);
                    totalHeight += lineHeight + (totalHeight > 0 ? LineSpacing : 0);
                    lineWidth = 0;
                    lineHeight = 0;
                }

                lineWidth += (lineWidth > 0 ? ItemSpacing : 0) + desired.Width;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            if (lineHeight > 0)
            {
                measuredWidth = Math.Max(measuredWidth, lineWidth);
                totalHeight += lineHeight + (totalHeight > 0 ? LineSpacing : 0);
            }

            return new Size(double.IsInfinity(width) ? measuredWidth : Math.Min(width, measuredWidth), totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var x = 0d;
            var y = 0d;
            var lineHeight = 0d;

            foreach (var child in Children)
            {
                var desired = child.DesiredSize;
                if (x > 0 && x + ItemSpacing + desired.Width > finalSize.Width)
                {
                    x = 0;
                    y += lineHeight + LineSpacing;
                    lineHeight = 0;
                }

                if (x > 0) x += ItemSpacing;
                child.Arrange(new Rect(x, y, desired.Width, desired.Height));
                x += desired.Width;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            return finalSize;
        }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var isDirect = string.Equals(App.Services?.Settings?.UpdateSource, "direct", StringComparison.OrdinalIgnoreCase);
            var url = (isDirect && !string.IsNullOrWhiteSpace(_vm.UpdateDownloadUrl))
                ? _vm.UpdateDownloadUrl
                : (!string.IsNullOrWhiteSpace(_vm.UpdateReleaseUrl) ? _vm.UpdateReleaseUrl : "https://github.com/yixing233/nexclip/releases");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开更新链接失败", ex);
            _vm.ShowMessage($"打开更新链接失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "NexClip_Update");
            if (Directory.Exists(tempDir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", tempDir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("打开下载目录失败", ex);
        }
    }

    // ========== 数据管理:导入 / 导出 ==========

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" });
            picker.SuggestedFileName = $"NexClip-导出-{DateTime.Now:yyyyMMdd-HHmm}";
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await _vm.ExportDataAsync(file.Path);
        }
        catch (Exception ex)
        {
            _vm.ShowMessage($"导出失败：{ServerApi.DescribeException(ex, "请检查文件路径和磁盘空间。")}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            Log.Error("导出对话框失败", ex);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await _vm.ImportDataAsync(file.Path);
        }
        catch (Exception ex)
        {
            _vm.ShowMessage($"导入失败：{ServerApi.DescribeException(ex, "请确认文件可读且格式正确。")}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            Log.Error("导入对话框失败", ex);
        }
    }

    private static void InitializePicker(object picker)
    {
        // unpackaged 应用:Picker 需要窗口句柄
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.SettingsWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ========== 数据管理:打开储存位置 ==========

    private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _vm.ConfiguredStorageDir;
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开数据目录失败", ex);
            _vm.ShowMessage($"打开数据目录失败：{ServerApi.DescribeException(ex, "请检查储存目录是否存在及权限。")}", InfoBarSeverity.Error);
        }
    }

    private void OpenImageFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _vm.ImageCachePath;
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开图片缓存失败", ex);
            _vm.ShowMessage($"打开图片缓存失败：{ServerApi.DescribeException(ex, "请检查储存目录是否存在及权限。")}", InfoBarSeverity.Error);
        }
    }

    /// <summary>选择新的数据储存目录:迁移数据并立即生效。</summary>
    private async void ChangeStorageDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickFolderAsync();
            if (string.IsNullOrEmpty(path)) return;
            _vm.ShowMessage(_vm.ApplyStorageDir(path), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _vm.ShowMessage($"选择储存目录失败：{ServerApi.DescribeException(ex, "请检查目录权限和磁盘空间。")}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            Log.Error("选择储存目录失败", ex);
        }
    }

    /// <summary>优先用 WinRT FolderPicker;提升权限(管理员)下其不可用(E_FAIL),回退经典文件夹选择框。</summary>
    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            InitializePicker(picker);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (COMException)
        {
            return PickFolderClassic();
        }
    }

    private string? PickFolderClassic()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.SettingsWindow!);
        var info = new NativeMethods.BROWSEINFO
        {
            hwndOwner = hwnd,
            lpszTitle = "选择数据储存目录",
            ulFlags = NativeMethods.BifReturnOnlyFsDirs | NativeMethods.BifNewDialogStyle,
        };
        var pidl = NativeMethods.SHBrowseForFolder(ref info);
        if (pidl == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(260);
            return NativeMethods.SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CoTaskMemFree(pidl);
        }
    }

    /// <summary>清空历史前二次确认(含图片缓存,不可恢复,始终保留收藏项)。</summary>
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var starredCount = App.Services.History.CountStarred();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = starredCount > 0
                ? $"确定要清空本地历史与图片缓存吗？\n（已收藏的 {starredCount} 条记录将自动保留，此操作不可恢复）"
                : "确定要清空本地历史与图片缓存吗？\n（此操作不可恢复）",
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
            _vm.ClearHistory(keepStarred: true);
        }
    }

    // ========== 热键捕获 ==========

    /// <summary>剪贴板热键捕获。</summary>
    private void HotKeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var combo = CaptureCombo(e);
        if (combo is null) return;
        _vm.Hotkey = combo;   // 输入框文字由 OneWay 绑定自动更新
    }

    /// <summary>设置热键捕获。</summary>
    private void HotKeySettingsBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var combo = CaptureCombo(e);
        if (combo is null) return;
        _vm.HotkeySettings = combo;   // 输入框文字由 OneWay 绑定自动更新
    }

    /// <summary>打开链接热键捕获。</summary>
    private void HotKeyOpenUrlBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var combo = CaptureCombo(e);
        if (combo is null) return;
        _vm.HotkeyOpenUrl = combo;   // 输入框文字由 OneWay 绑定自动更新
    }

    /// <summary>置顶热键捕获。</summary>
    private void HotKeyTopmostBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var combo = CaptureCombo(e);
        if (combo is null) return;
        _vm.HotkeyTopmost = combo;   // 输入框文字由 OneWay 绑定自动更新
    }

    private void HotKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 37, 99, 235));
            tb.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(25, 37, 99, 235));
            tb.PlaceholderText = "请直接按下组合键…";
        }
    }

    private void HotKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.ClearValue(TextBox.BorderBrushProperty);
            tb.ClearValue(TextBox.BackgroundProperty);
            tb.PlaceholderText = "点击后按组合键";
        }
    }

    private static string? CaptureCombo(KeyRoutedEventArgs e)
    {
        e.Handled = true;   // 不向输入框输入文本

        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");

        var key = e.Key;
        var keyName = key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
            >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (key - VirtualKey.F1 + 1),
            _ => "",
        };
        if (keyName.Length == 0 || parts.Count == 0) return null;   // 需要修饰键 + 字母/数字/F 键
        return string.Join("+", parts.Append(keyName));
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    /// <summary>重新生成本机设备 ID (带确认弹窗)。</summary>
    private async void RegenerateDeviceId_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "重新生成本机设备 ID",
            Content = "重新生成设备 ID 后，本机将作为一个全新设备重新连接服务器。\n确定要重新生成吗？",
            PrimaryButtonText = "确定生成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _vm.RegenerateDeviceId();
        }
    }

    /// <summary>移除指定外部设备 (带确认弹窗)。</summary>
    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NexClip.Desktop.Models.DeviceInfo device)
        {
            if (device.IsCurrent) return;

            var dialog = new ContentDialog
            {
                Title = "移除设备",
                Content = $"确定要移除设备「{device.Name ?? device.Id}」吗？\n移除后该设备将无法继续同步剪贴板。",
                PrimaryButtonText = "确定移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await _vm.RemoveDeviceAsync(device);
            }
        }
    }

    private static async Task<BitmapImage?> GenerateQrCodeBitmapAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(20);

            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("生成二维码位图失败", ex);
            return null;
        }
    }
}
