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

    /// <summary>
    /// 设置开机自启动。**自 20261023.03 起不再写启动文件夹快捷方式**,
    /// 统一改为注册表 Run 键(见 <see cref="StartupRegistration"/>)。
    ///
    /// 改动原因:此前安装器写快捷方式、主程序设置页写 Run 键,两条实现互不相通,
    /// 导致同一台机器出现两份自启动项,且卸载时 Run 键永久残留 ——
    /// 用户卸载后每次开机仍会被拉起到一个不存在的程序。
    ///
    /// 无论开启还是关闭,这里都会顺带清掉历史遗留的启动文件夹快捷方式,
    /// 让从旧版本升级上来的机器收敛到单一实现。
    /// </summary>
    /// <returns>是否成功启用(关闭操作总是返回成功,因为它只做删除)。</returns>
    public static bool SetStartupShortcut(string installDir, bool enable)
    {
        RemoveLegacyStartupShortcut();
        return enable ? StartupRegistration.Enable(installDir) : StartupRegistration.Disable() || true;
    }

    /// <summary>删除启动文件夹里的历史自启动快捷方式(旧版安装器写过),幂等。</summary>
    internal static void RemoveLegacyStartupShortcut()
    {
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup)) return;

            var lnk = Path.Combine(startup, "NexClip.lnk");
            if (File.Exists(lnk))
            {
                File.Delete(lnk);
                SetupLog.Write("已清理启动文件夹中的旧自启动快捷方式: NexClip.lnk");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetupLog.Write($"清理旧自启动快捷方式失败:{exception.Message}");
        }
    }

    /// <summary>
    /// 移除本应用创建的全部快捷方式与自启动项。
    /// 卸载路径的关键一步:除桌面/开始菜单/启动文件夹之外,**必须**同时清理注册表 Run 键,
    /// 否则卸载不干净(详见 <see cref="StartupRegistration"/> 的说明)。
    /// </summary>
    public static void RemoveAllShortcuts()
    {
        // 自启动项与快捷方式分开处理:前者在注册表,失败原因不同,不该被同一个 catch 吞掉
        StartupRegistration.Disable();
        RemoveLegacyStartupShortcut();

        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NexClip.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetupLog.Write($"删除桌面快捷方式失败:{exception.Message}");
        }

        try
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "NexClip");
            if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetupLog.Write($"删除开始菜单快捷方式失败:{exception.Message}");
        }
    }
}
