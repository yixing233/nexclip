using System;
using System.IO;
using Microsoft.Win32;

namespace NexClip.Installer.Native.Services;

public static class RegistryHelper
{
    private const string UninstallRootKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexClip";

    public static void RegisterUninstall(string installDir, string version = "20260828.02")
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRootKey, true);
            if (key == null) return;

            var exePath = Path.Combine(installDir, "NexClip.exe");
            var uninstallerPath = Path.Combine(installDir, "Uninstall.exe");

            key.SetValue("DisplayName", "NexClip", RegistryValueKind.String);
            key.SetValue("DisplayVersion", version, RegistryValueKind.String);
            key.SetValue("Publisher", "NexClip", RegistryValueKind.String);
            key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
            key.SetValue("DisplayIcon", $"{exePath},0", RegistryValueKind.String);
            key.SetValue("UninstallString", $"\"{uninstallerPath}\"", RegistryValueKind.String);
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" /silent", RegistryValueKind.String);
            key.SetValue("URLInfoAbout", "https://github.com/yixing233/nexclip", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);

            // 计算安装目录大小(KB)
            long totalBytes = 0;
            if (Directory.Exists(installDir))
            {
                foreach (var file in Directory.GetFiles(installDir, "*.*", SearchOption.AllDirectories))
                {
                    totalBytes += new FileInfo(file).Length;
                }
            }
            key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    public static void UnregisterUninstall()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRootKey, false);
        }
        catch
        {
        }
    }
}

