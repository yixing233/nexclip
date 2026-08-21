using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SyncClipboard.Desktop.Services;
using SyncClipboard.Desktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace SyncClipboard.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    /// <summary>对外暴露 NavigationView(供设置窗口标题栏汉堡切换侧边栏)。</summary>
    public Microsoft.UI.Xaml.Controls.NavigationView SettingsNav => NavView;

    public SettingsViewModel ViewModel => _vm;

    private readonly SettingsViewModel _vm;

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

            // 先完成页面结构和滚动定位，再等待网络请求，避免慢连接让窗口停在旧焦点位置。
            ResetContentScroll();
            await _vm.RefreshDevicesCommand.ExecuteAsync(null);
            // 网络请求期间可能触发了焦点恢复/布局重算，再补一次定位。
            ResetContentScroll();
        };
        // 配对码生成完成 → 弹出对话框展示(大号配对码 + 用户 ID + 专属复制 + 待确认请求实时交互);关闭即作废
        _vm.PairingCodeGenerated += async (result) =>
        {
            var serverUrl = _vm.ServerUrl?.Trim() ?? "";
            var generatorId = App.Services.Settings.DeviceId;
            var panel = new StackPanel { Spacing = 14, MinWidth = 360 };

            // 1. 配对码卡片
            var codeContainer = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
            };
            var codeGrid = new Grid();
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeLabel = new TextBlock
            {
                Text = "配对码",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var codeBox = new TextBlock
            {
                Text = result.Code,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var copyCodeBtn = new Button
            {
                Content = "复制配对码",
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center,
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

            // 2. 用户 ID 卡片 (配对必需信息)
            var uidContainer = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
            };
            var uidGrid = new Grid();
            uidGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            uidGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            uidGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var uidLabel = new TextBlock
            {
                Text = "用户 ID",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var uidVal = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(result.UserId) ? "（未分配）" : result.UserId,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var copyUidBtn = new Button
            {
                Content = "复制用户 ID",
                Padding = new Thickness(10, 5, 10, 5),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !string.IsNullOrWhiteSpace(result.UserId),
            };
            copyUidBtn.Click += (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(result.UserId ?? "");
                Clipboard.SetContent(pkg);
                copyUidBtn.Content = "已复制";
            };

            Grid.SetColumn(uidLabel, 0);
            Grid.SetColumn(uidVal, 1);
            Grid.SetColumn(copyUidBtn, 2);
            uidGrid.Children.Add(uidLabel);
            uidGrid.Children.Add(uidVal);
            uidGrid.Children.Add(copyUidBtn);
            uidContainer.Child = uidGrid;
            panel.Children.Add(uidContainer);

            // 3. 一键复制快捷按钮
            var copyAllBtn = new Button
            {
                Content = "一键复制 (配对码 + 用户 ID)",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6, 10, 6),
            };
            copyAllBtn.Click += (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText($"配对码: {result.Code}\n用户 ID: {result.UserId}");
                Clipboard.SetContent(pkg);
                copyAllBtn.Content = "已复制配对信息";
            };
            panel.Children.Add(copyAllBtn);

            // 4. 实时配对状态与待确认请求交互容器
            var requestContainer = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
            };

            // 默认等待状态
            var waitingPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            waitingPanel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
            waitingPanel.Children.Add(new TextBlock
            {
                Text = "等待另一台设备输入配对码并接入…",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            requestContainer.Child = waitingPanel;
            panel.Children.Add(requestContainer);

            // 5. 提示说明
            panel.Children.Add(new TextBlock
            {
                Text = "在另一台设备上输入上述用户 ID 与配对码即可发起配对。\n关闭对话框后配对码立即失效。",
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });

            ContentDialog? dialog = null;
            var isDialogActive = true;

            // 启动定时器轮询待确认配对请求 (每 1.5 秒轮询一次)
            var pollTimer = DispatcherQueue.CreateTimer();
            pollTimer.Interval = TimeSpan.FromMilliseconds(1500);
            pollTimer.Tick += async (_, _) =>
            {
                if (!isDialogActive || dialog is null)
                {
                    pollTimer.Stop();
                    return;
                }
                try
                {
                    var reqs = await App.Services.Api.GetPairingRequestsAsync(serverUrl, result.Code, generatorId, App.Services.Settings.AuthToken);
                    var pending = reqs.FirstOrDefault(r => string.Equals(r.Status, "pending", StringComparison.OrdinalIgnoreCase));
                    if (pending != null && isDialogActive)
                    {
                        // 渲染待确认卡片
                        var confirmStack = new StackPanel { Spacing = 10 };
                        var tipText = new TextBlock
                        {
                            Text = $"🔔 设备「{pending.DeviceName ?? pending.DeviceId ?? "新设备"}」请求接入同步",
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            FontSize = 13,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                        };
                        var btnGrid = new Grid { ColumnSpacing = 10 };
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var approveBtn = new Button
                        {
                            Content = "同意加入",
                            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        };
                        approveBtn.Click += async (_, _) =>
                        {
                            approveBtn.IsEnabled = false;
                            try
                            {
                                await App.Services.Api.ConfirmPairingRequestAsync(serverUrl, result.Code, "approve", generatorId, App.Services.Settings.AuthToken);
                                isDialogActive = false;
                                pollTimer.Stop();
                                dialog.Hide();
                                _vm.ShowMessage($"已同意配对！设备「{pending.DeviceName ?? pending.DeviceId}」已成功加入", InfoBarSeverity.Success);
                                await _vm.RefreshDevicesCommand.ExecuteAsync(null);
                            }
                            catch (Exception ex)
                            {
                                _vm.ShowMessage($"确认配对失败: {ex.Message}", InfoBarSeverity.Error);
                                approveBtn.IsEnabled = true;
                            }
                        };

                        var rejectBtn = new Button
                        {
                            Content = "拒绝",
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                        };
                        rejectBtn.Click += async (_, _) =>
                        {
                            rejectBtn.IsEnabled = false;
                            try
                            {
                                await App.Services.Api.ConfirmPairingRequestAsync(serverUrl, result.Code, "reject", generatorId, App.Services.Settings.AuthToken);
                                requestContainer.Child = waitingPanel;
                            }
                            catch (Exception ex)
                            {
                                _vm.ShowMessage($"拒绝配对失败: {ex.Message}", InfoBarSeverity.Error);
                            }
                        };

                        Grid.SetColumn(approveBtn, 0);
                        Grid.SetColumn(rejectBtn, 1);
                        btnGrid.Children.Add(approveBtn);
                        btnGrid.Children.Add(rejectBtn);

                        confirmStack.Children.Add(tipText);
                        confirmStack.Children.Add(btnGrid);
                        requestContainer.Child = confirmStack;
                    }
                }
                catch
                {
                    // 轮询异常静默处理
                }
            };
            pollTimer.Start();

            dialog = new ContentDialog
            {
                Title = "配对码已生成",
                Content = panel,
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            await dialog.ShowAsync();
            isDialogActive = false;
            pollTimer.Stop();
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
            Process.Start(new ProcessStartInfo("https://github.com/yixing233/easy-clip") { UseShellExecute = true });
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
            var info = $"NexClip {_vm.VersionText} | 框架: WinUI 3 / .NET 9 | 服务端: NexClip Server (node) | 数据位置: {_vm.ConfiguredStorageDir} | 项目: github.com/yixing233/easy-clip";
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

    /// <summary>清空历史前二次确认(含图片缓存,不可恢复)。</summary>
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空历史",
            Content = "确定要清空全部本地历史与图片缓存吗?此操作不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _vm.ClearHistory();
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
        if ((sender as FrameworkElement)?.DataContext is SyncClipboard.Desktop.Models.DeviceInfo device)
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
}
