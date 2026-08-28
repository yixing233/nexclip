using System;
using System.Diagnostics;
using System.IO;

namespace NexClip.Installer.Native.Services;

public static class ShortcutHelper
{
    public static void CreateShortcut(string targetExePath, string lnkFilePath, string description = "", string workingDir = "")
    {
        try
        {
            var dir = Path.GetDirectoryName(lnkFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var wDir = string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(targetExePath) : workingDir;
            
            var script = "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + lnkFilePath.Replace("'", "''") + "'); " +
                         "$s.TargetPath = '" + targetExePath.Replace("'", "''") + "'; " +
                         "$s.WorkingDirectory = '" + (wDir ?? "").Replace("'", "''") + "'; " +
                         "$s.Description = '" + description.Replace("'", "''") + "'; " +
                         "$s.IconLocation = '" + targetExePath.Replace("'", "''") + ",0'; " +
                         "$s.Save()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch
        {
        }
    }

    public static void CreateDesktopShortcut(string installDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desktop, "NexClip.lnk");
        var exe = Path.Combine(installDir, "NexClip.exe");
        CreateShortcut(exe, lnk, "NexClip 现代化跨平台剪贴板同步工具");
    }

    public static void CreateStartMenuShortcut(string installDir)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "NexClip");
        if (!Directory.Exists(startMenu)) Directory.CreateDirectory(startMenu);

        var lnk = Path.Combine(startMenu, "NexClip.lnk");
        var exe = Path.Combine(installDir, "NexClip.exe");
        CreateShortcut(exe, lnk, "NexClip 现代化跨平台剪贴板同步工具");

        var uninstallLnk = Path.Combine(startMenu, "卸载 NexClip.lnk");
        var uninstallExe = Path.Combine(installDir, "Uninstall.exe");
        CreateShortcut(uninstallExe, uninstallLnk, "卸载 NexClip");
    }

    public static void SetStartupShortcut(string installDir, bool enable)
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var lnk = Path.Combine(startup, "NexClip.lnk");

        if (enable)
        {
            var exe = Path.Combine(installDir, "NexClip.exe");
            CreateShortcut(exe, lnk, "NexClip 自启动服务");
        }
        else
        {
            if (File.Exists(lnk)) File.Delete(lnk);
        }
    }

    public static void RemoveAllShortcuts()
    {
        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NexClip.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);

            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "NexClip");
            if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);

            var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NexClip.lnk");
            if (File.Exists(startup)) File.Delete(startup);
        }
        catch
        {
        }
    }
}
