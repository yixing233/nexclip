using Microsoft.Win32;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 开机自启动注册/清理的回归测试。
///
/// 守护的是一个**真实发生过的卸载残留 bug**:
/// 安装器历史上写「启动」文件夹快捷方式,而主程序设置页写注册表 Run 键,
/// 两条实现互不相通 —— 卸载时只删快捷方式,Run 键永久残留,
/// 于是用户卸载后每次开机,Windows 都在尝试启动一个已删除的程序。
///
/// 这些测试把注册表根指向一次性子键(<see cref="StartupRegistration.Root"/>),
/// 不会碰到真实的自启动项。
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _sandboxPath;
    private readonly RegistryKey _sandbox;
    private readonly RegistryKey _originalRoot;

    public StartupRegistrationTests()
    {
        // 每次用独立子键隔离,避免测试之间互相影响,也绝不碰真实的 Run 键
        _sandboxPath = @"Software\NexClip.Installer.Tests\" + Guid.NewGuid().ToString("N");
        _sandbox = Registry.CurrentUser.CreateSubKey(_sandboxPath, writable: true)!;
        _originalRoot = StartupRegistration.Root;
        StartupRegistration.Root = _sandbox;
    }

    public void Dispose()
    {
        StartupRegistration.Root = _originalRoot;
        try
        {
            _sandbox.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_sandboxPath, throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    [Fact]
    public void Enable_WritesQuotedPathWithAutostartArgument()
    {
        var installDir = @"C:\Program Files\NexClip";

        Assert.True(StartupRegistration.Enable(installDir));

        using var key = _sandbox.OpenSubKey(StartupRegistration.RunKeyPath);
        Assert.NotNull(key);
        var value = key!.GetValue(StartupRegistration.ValueName) as string;

        // 路径必须加引号:安装目录含空格时不加引号会被拆成多个参数
        Assert.Equal($"\"{Path.Combine(installDir, "NexClip.exe")}\" --autostart", value);
        Assert.StartsWith("\"", value);
        Assert.Contains("--autostart", value);
    }

    [Fact]
    public void Disable_RemovesAllKnownValueNames()
    {
        // 模拟三种历史残留:当前值名 + 两个遗留值名
        using (var key = _sandbox.CreateSubKey(StartupRegistration.RunKeyPath, writable: true)!)
        {
            key.SetValue(StartupRegistration.ValueName, "\"C:\\a\\NexClip.exe\" --autostart");
            foreach (var legacy in StartupRegistration.LegacyValueNames)
            {
                key.SetValue(legacy, "\"C:\\old\\x.exe\"");
            }
        }

        Assert.True(StartupRegistration.Disable());

        using var after = _sandbox.OpenSubKey(StartupRegistration.RunKeyPath);
        foreach (var name in StartupRegistration.AllValueNames())
        {
            Assert.Null(after?.GetValue(name));
        }
    }

    [Fact]
    public void Disable_IsIdempotent_WhenNothingRegistered()
    {
        // 「本来就干净」与「刚清理完」对调用方是同一结果:卸载时不能因为值不存在而报失败。
        Assert.True(StartupRegistration.Disable());
        Assert.True(StartupRegistration.Disable());
    }

    [Fact]
    public void Disable_SucceedsWhenRunKeyItselfMissing()
    {
        // 连 Run 键都不存在的极端情况(全新用户配置)也必须算成功
        Assert.True(StartupRegistration.Disable());
        Assert.False(StartupRegistration.IsEnabled());
    }

    [Fact]
    public void Enable_ClearsLegacyValuesSoOnlyOneEntryRemains()
    {
        // 升级场景:老版本留下 SyncClipboard.Desktop,新版本写 NexClip 时必须一并清掉,
        // 否则一台机器上会有两份自启动项(正是本 bug 的附带问题)
        using (var key = _sandbox.CreateSubKey(StartupRegistration.RunKeyPath, writable: true)!)
        {
            key.SetValue("SyncClipboard.Desktop", "\"C:\\old\\a.exe\"");
            key.SetValue("NexClip.Desktop", "\"C:\\old\\b.exe\"");
        }

        Assert.True(StartupRegistration.Enable(@"C:\Apps\NexClip"));

        var entries = StartupRegistration.Enumerate();
        Assert.Single(entries);
        Assert.Equal(StartupRegistration.ValueName, entries[0].Name);
    }

    [Fact]
    public void IsEnabled_RecognizesLegacyValueNames()
    {
        // 老版本装过、还没升级的用户,自启动是「开着」的,不能误报为关闭
        using (var key = _sandbox.CreateSubKey(StartupRegistration.RunKeyPath, writable: true)!)
        {
            key.SetValue("NexClip.Desktop", "\"C:\\old\\NexClip.exe\"");
        }

        Assert.True(StartupRegistration.IsEnabled());

        StartupRegistration.Disable();
        Assert.False(StartupRegistration.IsEnabled());
    }

    [Fact]
    public void Enumerate_ReturnsOnlyNonEmptyValues()
    {
        using (var key = _sandbox.CreateSubKey(StartupRegistration.RunKeyPath, writable: true)!)
        {
            key.SetValue(StartupRegistration.ValueName, "\"C:\\a\\NexClip.exe\" --autostart");
            key.SetValue("NexClip.Desktop", string.Empty);
        }

        var entries = StartupRegistration.Enumerate();
        Assert.Single(entries);
        Assert.Equal(StartupRegistration.ValueName, entries[0].Name);
        Assert.Contains("--autostart", entries[0].Value);
    }

    [Fact]
    public void AllValueNames_CoversPrimaryAndEveryLegacyName()
    {
        var names = StartupRegistration.AllValueNames().ToArray();

        Assert.Contains(StartupRegistration.ValueName, names);
        foreach (var legacy in StartupRegistration.LegacyValueNames)
        {
            Assert.Contains(legacy, names);
        }
        // 去重:重复清理同一个值名没有意义,也会让「清理了几个」失真
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RunKeyPath_IsTheUserLevelRunKey()
    {
        // 必须是 HKCU 下的用户级 Run 键(与桌面端 StartupService 一致),
        // 不能是 HKLM 或启动文件夹 —— 那会退回本 bug 的前身
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.DoesNotContain("HKLM", StartupRegistration.RunKeyPath, StringComparison.OrdinalIgnoreCase);
    }
}
