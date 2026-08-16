using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SyncClipboard.Desktop;
using SyncClipboard.Desktop.ViewModels;
using Windows.System;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;

namespace SyncClipboard.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm;

    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.Services.SettingsVm;
        DataContext = _vm;
        // 打开设置页即加载设备列表
        Loaded += async (_, _) => await _vm.RefreshDevicesCommand.ExecuteAsync(null);
        // 配对码生成完成 → 弹出对话框展示(大号码 + 复制);关闭即作废
        _vm.PairingCodeGenerated += async (code, expiresAt) =>
        {
            var codeBox = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                FontSize = 22,
            };
            var copyButton = new Button
            {
                Content = "复制",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            };
            copyButton.Click += (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(code);
                Clipboard.SetContent(pkg);
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(codeBox);
            panel.Children.Add(new TextBlock
            {
                Text = "关闭对话框后此配对码立即失效",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(copyButton);

            var dialog = new ContentDialog
            {
                Title = "配对码",
                Content = panel,
                PrimaryButtonText = "关闭",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
            // 对话框关闭(无论按钮/遮罩)→ 配对码作废
            await _vm.RevokeGeneratedCodeAsync();
        };
    }

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
}
