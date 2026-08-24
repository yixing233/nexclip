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

namespace NexClip.Desktop.Views;

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

    /// <summary>清空历史前二次确认(含图片缓存,不可恢复,支持保留收藏项)。</summary>
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var starredCount = App.Services.History.CountStarred();
        var checkBox = new CheckBox
        {
            Content = $"保留已收藏记录{(starredCount > 0 ? $" ({starredCount} 条)" : "")}",
            IsChecked = true,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "确定要清空本地历史与图片缓存吗？此操作不可恢复。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(checkBox);

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
            var keepStarred = checkBox.IsChecked == true;
            _vm.ClearHistory(keepStarred);
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
