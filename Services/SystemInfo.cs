using System.Runtime.InteropServices;

namespace NexClip.Desktop.Services;

/// <summary>
/// 桌面端系统信息(服务端设备登记用,轻量实现,无 WMI 依赖)。
/// 上报 Platform=Windows、版本号(如 10.0.26100)、架构。
/// </summary>
public static class SystemInfo
{
    public static string Platform => "Windows";

    /// <summary>Windows 版本号,如 "10.0.26100"。</summary>
    public static string Version
    {
        get
        {
            var v = Environment.OSVersion.Version;
            return $"{(int)v.Major}.{(int)v.Minor}.{v.Build}";
        }
    }

    /// <summary>Windows 产品名(注册表,如 "Windows 11 专业版";失败返回 null)。</summary>
    public static string? WindowsName
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return key?.GetValue("ProductName") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>架构:AMD64 / ARM64 / x86。</summary>
    public static string Architecture => RuntimeInformation.OSArchitecture.ToString();

    /// <summary>设备副标题:平台 · 版本(架构)。</summary>
    public static string DeviceSubtitle =>
        $"{Platform} {Version} ({Architecture})";
}
