using System;
using System.IO;
using System.Windows;
using NexClip.Installer.Services;

namespace NexClip.Installer;

public partial class App : Application
{
    public static bool IsUninstallMode { get; private set; }
    public static bool IsSilentMode { get; private set; }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (exeName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            IsUninstallMode = true;
        }

        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                IsUninstallMode = true;
            }
            else if (string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase))
            {
                IsSilentMode = true;
            }
        }

        if (IsSilentMode)
        {
            // 静默模式执行
            if (IsUninstallMode)
            {
                PerformSilentUninstall();
            }
            Shutdown();
            return;
        }

        var mainWin = new MainWindow();
        mainWin.Show();
    }

    private static void PerformSilentUninstall()
    {
        try
        {
            ProcessHelper.TerminateRunningInstancesAsync().GetAwaiter().GetResult();
            ShortcutHelper.RemoveAllShortcuts();
            RegistryHelper.UnregisterUninstall();
        }
        catch
        {
        }
    }
}
