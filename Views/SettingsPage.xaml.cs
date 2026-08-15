using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SyncClipboard.Desktop;
using SyncClipboard.Desktop.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace SyncClipboard.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm;

    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.Services.SettingsVm;
        DataContext = _vm;
        // PasswordBox.Password 不可绑定,手动同步
        TokenBox.Password = _vm.AuthToken;
        TokenBox.PasswordChanged += (_, _) => _vm.AuthToken = TokenBox.Password;
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
