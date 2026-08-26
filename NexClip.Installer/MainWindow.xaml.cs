using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NexClip.Installer.Services;

namespace NexClip.Installer;

public partial class MainWindow : Window
{
    private string _installDir = "";
    private bool _isCustomExpanded = false;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DwmHelper.ApplyModernWindowStyles(this, isDark: true);

        _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexClip");
        InstallPathTextBox.Text = _installDir;

        if (App.IsUninstallMode)
        {
            TitleTextBlock.Text = "NexClip 卸载向导";
            WelcomePanel.Visibility = Visibility.Collapsed;
            UninstallPanel.Visibility = Visibility.Visible;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleCustom_Click(object sender, RoutedEventArgs e)
    {
        _isCustomExpanded = !_isCustomExpanded;
        CustomInstallCard.Visibility = _isCustomExpanded ? Visibility.Visible : Visibility.Collapsed;
        CustomToggleText.Text = _isCustomExpanded ? "收起" : "自定义";
        
        if (TryFindResource(_isCustomExpanded ? "LucideChevronUp" : "LucideSettings") is Geometry geom)
        {
            CustomToggleIcon.Data = geom;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 NexClip 安装目录",
            InitialDirectory = _installDir
        };

        if (dialog.ShowDialog() == true)
        {
            _installDir = dialog.FolderName;
            InstallPathTextBox.Text = _installDir;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        _installDir = InstallPathTextBox.Text.Trim();
        if (string.IsNullOrEmpty(_installDir))
        {
            _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexClip");
        }

        WelcomePanel.Visibility = Visibility.Collapsed;
        InstallingPanel.Visibility = Visibility.Visible;

        try
        {
            // 1. 平滑释放可能正在运行的 NexClip 进程
            InstallStatusTextBlock.Text = "正在检查并释放后台运行中的旧版本进程...";
            await ProcessHelper.TerminateRunningInstancesAsync();

            // 2. 解压核心 Payload 文件
            InstallStatusTextBlock.Text = "正在解压核心组件...";
            await PayloadService.ExtractPayloadAsync(_installDir, (progress, fileName) =>
            {
                Dispatcher.Invoke(() =>
                {
                    InstallProgressBar.Value = progress * 100;
                    InstallDetailTextBlock.Text = $"{(int)(progress * 100)}%";
                    InstallStatusTextBlock.Text = $"正在释放: {fileName}";
                });
            });

            // 3. 部署独立卸载器
            InstallStatusTextBlock.Text = "正在部署卸载组件...";
            PayloadService.DeployUninstaller(_installDir);

            // 4. 创建快捷方式
            InstallStatusTextBlock.Text = "正在配置系统快捷方式...";
            ShortcutHelper.CreateStartMenuShortcut(_installDir);

            if (DesktopShortcutCheckBox.IsChecked == true)
            {
                ShortcutHelper.CreateDesktopShortcut(_installDir);
            }

            if (StartupCheckBox.IsChecked == true)
            {
                ShortcutHelper.SetStartupShortcut(_installDir, true);
            }

            // 5. 写入注册表
            InstallStatusTextBlock.Text = "正在完成系统注册...";
            RegistryHelper.RegisterUninstall(_installDir, "20260825.02");

            await Task.Delay(300);

            // 6. 进入完成界面
            InstallingPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装过程遇到错误:\n{ex.Message}", "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallingPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        ProcessHelper.LaunchApp(_installDir);
        Close();
    }

    private async void ConfirmUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        UninstallPanel.Visibility = Visibility.Collapsed;
        InstallingPanel.Visibility = Visibility.Visible;
        InstallStatusTextBlock.Text = "正在终止运行中进程与卸载组件...";
        InstallProgressBar.IsIndeterminate = true;
        InstallDetailTextBlock.Text = "";

        try
        {
            await ProcessHelper.TerminateRunningInstancesAsync();
            ShortcutHelper.RemoveAllShortcuts();
            RegistryHelper.UnregisterUninstall();

            // 如果不保留用户配置，清理 LocalAppData
            if (KeepUserDataCheckBox.IsChecked != true)
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexClip");
                if (Directory.Exists(appData))
                {
                    try { Directory.Delete(appData, true); } catch { }
                }
            }

            // 延迟删除安装目录文件（通过批处理在卸载器退出后删除剩余空目录）
            var currentExe = Environment.ProcessPath ?? "";
            var installFolder = Path.GetDirectoryName(currentExe);
            if (!string.IsNullOrEmpty(installFolder) && Directory.Exists(installFolder))
            {
                foreach (var file in Directory.GetFiles(installFolder, "*.*", SearchOption.AllDirectories))
                {
                    if (!string.Equals(file, currentExe, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }

            await Task.Delay(500);

            InstallingPanel.Visibility = Visibility.Collapsed;
            UninstallCompletePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"卸载遇到错误:\n{ex.Message}", "卸载提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }
}
