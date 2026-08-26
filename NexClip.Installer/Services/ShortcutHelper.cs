using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NexClip.Installer.Services;

public static class ShortcutHelper
{
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>
    /// 创建 .lnk 快捷方式
    /// </summary>
    public static void CreateShortcut(string targetExePath, string lnkFilePath, string description = "", string workingDir = "")
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(targetExePath);
            link.SetDescription(description);
            link.SetWorkingDirectory(string.IsNullOrEmpty(workingDir) ? Path.GetDirectoryName(targetExePath)! : workingDir);
            link.SetIconLocation(targetExePath, 0);

            var file = (IPersistFile)link;
            file.Save(lnkFilePath, false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 创建桌面快捷方式
    /// </summary>
    public static void CreateDesktopShortcut(string installDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var targetExe = Path.Combine(installDir, "NexClip.exe");
        var lnkPath = Path.Combine(desktop, "NexClip.lnk");
        CreateShortcut(targetExe, lnkPath, "NexClip - 现代化跨平台剪贴板流转工具", installDir);
    }

    /// <summary>
    /// 创建开始菜单快捷方式
    /// </summary>
    public static void CreateStartMenuShortcut(string installDir)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var targetExe = Path.Combine(installDir, "NexClip.exe");
        var lnkPath = Path.Combine(programs, "NexClip.lnk");
        CreateShortcut(targetExe, lnkPath, "NexClip - 现代化跨平台剪贴板流转工具", installDir);
    }

    /// <summary>
    /// 设置开机启动快捷方式
    /// </summary>
    public static void SetStartupShortcut(string installDir, bool enable)
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var lnkPath = Path.Combine(startup, "NexClip.lnk");
        if (enable)
        {
            var targetExe = Path.Combine(installDir, "NexClip.exe");
            CreateShortcut(targetExe, lnkPath, "NexClip 自启动", installDir);
        }
        else
        {
            if (File.Exists(lnkPath)) File.Delete(lnkPath);
        }
    }

    /// <summary>
    /// 移除所有创建的快捷方式
    /// </summary>
    public static void RemoveAllShortcuts()
    {
        try
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NexClip.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);

            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NexClip.lnk");
            if (File.Exists(startMenu)) File.Delete(startMenu);

            var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NexClip.lnk");
            if (File.Exists(startup)) File.Delete(startup);
        }
        catch
        {
        }
    }
}
