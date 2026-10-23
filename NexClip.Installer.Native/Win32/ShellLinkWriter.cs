using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Win32;

/// <summary>
/// 通过 IShellLink / IPersistFile 创建 .lnk 快捷方式。
///
/// <para><b>为什么不用 PowerShell</b>：此前用
/// <c>powershell.exe -Command "(New-Object -ComObject WScript.Shell).CreateShortcut(...)"</c>
/// 创建快捷方式，安装一次要拉起最多 3 个 PowerShell 进程
/// （开始菜单 NexClip、开始菜单「卸载 NexClip」、桌面），每个 300~800ms，
/// 且会被杀软拦成"安装程序试图运行 PowerShell 脚本"——
/// 用户反馈火绒每次都弹拦截，正是这个来源。</para>
///
/// <para><b>为什么用 GeneratedComInterface 而不是普通 ComImport</b>：
/// 安装器是 Native AOT 编译的（单文件、无运行时依赖）。实测在 AOT 下
/// 经典 <c>[ComImport]</c> 会抛
/// <c>NotSupportedException: COM Interop is not supported on this platform</c>，
/// 即使开启 <c>BuiltInComInteropSupport</c> 也一样。
/// .NET 8+ 的 <see cref="GeneratedComInterfaceAttribute"/> 是**源生成**的 COM 互操作，
/// 官方支持 AOT —— 实测可用。这条路还避免了手写 .lnk 二进制格式
/// （那样极易写错：LinkInfo 的 VolumeLabelOffset / 结构错位都会导致
/// 快捷方式文件正常生成但 TargetPath 为空、双击无反应）。</para>
/// </summary>
internal static partial class ShellLinkWriter
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-C000-000000000046");

    /// <summary>IShellLinkW 的 vtable 顺序必须与原生定义严格一致，否则会调错虚表项。</summary>
    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /// <summary>IPersistFile：用于把 IShellLink 保存成 .lnk 文件。</summary>
    [GeneratedComInterface]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>
    /// 创建快捷方式。失败返回 false（已写日志），调用方不应因此中断安装。
    /// </summary>
    internal static bool TryCreate(
        string targetPath,
        string shortcutPath,
        string? description = null,
        string? workingDirectory = null)
    {
        IntPtr linkPtr = IntPtr.Zero;
        IntPtr persistPtr = IntPtr.Zero;
        try
        {
            var directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var clsid = ClsidShellLink;
            var iidLink = IidShellLinkW;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iidLink, out linkPtr);
            if (hr != 0 || linkPtr == IntPtr.Zero)
            {
                SetupLog.Write($"创建快捷方式失败:CoCreateInstance hr=0x{hr:X8}");
                return false;
            }

            var wrappers = new StrategyBasedComWrappers();
            if (wrappers.GetOrCreateObjectForComInstance(linkPtr, CreateObjectFlags.None) is not IShellLinkW link)
            {
                SetupLog.Write("创建快捷方式失败:无法获取 IShellLinkW");
                return false;
            }

            link.SetPath(targetPath);
            var workDir = string.IsNullOrEmpty(workingDirectory)
                ? Path.GetDirectoryName(targetPath) ?? string.Empty
                : workingDirectory;
            if (!string.IsNullOrEmpty(workDir))
            {
                link.SetWorkingDirectory(workDir);
            }
            if (!string.IsNullOrEmpty(description))
            {
                link.SetDescription(description);
            }
            link.SetIconLocation(targetPath, 0);

            // 同一个 COM 对象上的 IPersistFile
            var iidPersist = IidPersistFile;
            hr = Marshal.QueryInterface(linkPtr, in iidPersist, out persistPtr);
            if (hr != 0 || persistPtr == IntPtr.Zero)
            {
                SetupLog.Write($"创建快捷方式失败:QueryInterface(IPersistFile) hr=0x{hr:X8}");
                return false;
            }

            if (wrappers.GetOrCreateObjectForComInstance(persistPtr, CreateObjectFlags.None) is not IPersistFile persist)
            {
                SetupLog.Write("创建快捷方式失败:无法获取 IPersistFile");
                return false;
            }

            persist.Save(shortcutPath, true);
            return File.Exists(shortcutPath);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidCastException or IOException or
                UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SetupLog.Write($"创建快捷方式失败({shortcutPath}):{exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            if (persistPtr != IntPtr.Zero)
            {
                Marshal.Release(persistPtr);
            }
            if (linkPtr != IntPtr.Zero)
            {
                Marshal.Release(linkPtr);
            }
        }
    }
}
