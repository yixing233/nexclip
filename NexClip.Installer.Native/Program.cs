using System;
using System.IO;
using System.Runtime.InteropServices;
using NexClip.Installer.Native.Services;
using NexClip.Installer.Native.UI;

namespace NexClip.Installer.Native;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool isUninstall = false;
        bool isSilent = false;

        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (exeName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            isUninstall = true;
        }

        foreach (var arg in args)
        {
            if (string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                isUninstall = true;
            }
            else if (string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-silent", StringComparison.OrdinalIgnoreCase))
            {
                isSilent = true;
            }
        }

        if (isSilent)
        {
            if (isUninstall)
            {
                try
                {
                    ProcessHelper.TerminateRunningInstancesAsync().GetAwaiter().GetResult();
                    ShortcutHelper.RemoveAllShortcuts();
                    RegistryHelper.UnregisterUninstall();
                }
                catch { }
            }
            return;
        }

        try
        {
            var window = new FluentInstallerWindow(isUninstall);
            window.Run();
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "nexclip_installer_error.log");
            File.WriteAllText(logPath, ex.ToString());
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
            MessageBoxW(IntPtr.Zero, $"安装器启动失败:\n{ex.Message}\n\n详细信息已记录至:\n{logPath}", "NexClip 安装错误", 0x10);
        }
    }
}
