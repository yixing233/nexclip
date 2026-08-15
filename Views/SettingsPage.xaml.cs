using Microsoft.UI.Xaml.Controls;
using SyncClipboard.Desktop;
using SyncClipboard.Desktop.ViewModels;

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
}
