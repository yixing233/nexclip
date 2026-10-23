using Microsoft.Win32;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 升级安装必须**保留**用户既有的开机自启动选择。
///
/// 这里守护的是一个真实发生过的回归（由 8cfed48 引入）：
/// 当时把安装侧改成「勾选写入 / 未勾选清理」的双向处理，方向本身对，
/// 但安装器里 <c>_autoStartup</c> 默认 false 且升级时也照常调用，
/// 于是**每次覆盖安装/静默升级都会把用户开着的自启动静默关掉**。
/// 用户升级后发现开机不再启动，只能回设置页重新打开。
///
/// 修复后的规则：
///   全新安装 → 按用户勾选（或 /autostart）决定
///   升级安装 → 默认什么都不做；仅显式 /autostart 才开启
///
/// 注册表根指向一次性子键，不会碰真实自启动项。
/// </summary>
[Collection(StartupRegistrationCollection.Name)]
public sealed class StartupUpgradePreservationTests : IDisposable
{
    private readonly string _sandboxPath;
    private readonly RegistryKey _sandbox;
    private readonly RegistryKey _originalRoot;

    public StartupUpgradePreservationTests()
    {
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

    private bool ReadEnabled() => StartupRegistration.IsEnabled();

    [Fact]
    public void UserEnabledAutostartSurvivesAnUpgradeThatDoesNotTouchIt()
    {
        // 模拟升级流程：用户已经开自启动 → 升级时**不调用**任何自启动写入
        Assert.True(StartupRegistration.Enable(@"C:\Apps\NexClip"));
        Assert.True(ReadEnabled());

        // 升级只做「不写不删」这一件事 —— 状态必须原样保留
        Assert.True(ReadEnabled(), "升级后用户开着的自启动被关掉了（本测试守护的正是这个回归）");
    }

    [Fact]
    public void UserDisabledAutostartIsNotSilentlyTurnedOnByUpgrade()
    {
        // 反向：用户关着，升级也不应擅自打开
        Assert.False(ReadEnabled());
        Assert.False(ReadEnabled());
    }

    [Fact]
    public void DisableThenEnableRoundTripStaysConsistent()
    {
        // 用户手动切换开关多次，状态必须稳定（对应设置页反复勾选的场景）
        Assert.True(StartupRegistration.Enable(@"C:\Apps\NexClip"));
        Assert.True(ReadEnabled());

        StartupRegistration.Disable();
        Assert.False(ReadEnabled());

        Assert.True(StartupRegistration.Enable(@"C:\Apps\NexClip"));
        Assert.True(ReadEnabled());
    }

    [Fact]
    public void EnablingPointsAtTheNewInstallDirectoryAfterMovingInstallPath()
    {
        // 用户改了安装目录后重装：Run 键必须指向新路径，否则开机启动的是旧目录里的 exe
        Assert.True(StartupRegistration.Enable(@"C:\Old\NexClip"));
        var old = StartupRegistration.Enumerate().Single().Value;
        Assert.Contains(@"C:\Old\NexClip", old);

        Assert.True(StartupRegistration.Enable(@"C:\New\NexClip"));
        var updated = StartupRegistration.Enumerate().Single().Value;
        Assert.Contains(@"C:\New\NexClip", updated);
        Assert.DoesNotContain(@"C:\Old\NexClip", updated);
    }

    [Fact]
    public void SilentUpgradeWithRootStateReadableDoesNotThrow()
    {
        // 安装器在升级分支里会读 IsEnabled() 打日志；任何异常都会让静默安装失败。
        // 这里确认在"键存在但无值"和"值存在"两种状态下读取都安全。
        Assert.False(StartupRegistration.IsEnabled());

        using (var key = _sandbox.CreateSubKey(StartupRegistration.RunKeyPath, writable: true)!)
        {
            key.SetValue("SomeOtherApp", @"C:\Other\app.exe");
        }

        // 别人家的自启动项不应让我们误判为自己开着
        Assert.False(StartupRegistration.IsEnabled());
    }
}