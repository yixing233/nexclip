using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 卸载器部署的回归测试。
///
/// 守护的是一个真实的空间浪费:安装器此前把**自己整份**复制成 Uninstall.exe,
/// 于是每台机器的安装目录里都躺着一份 19MB 的完整安装包(含 12.5MB payload),
/// 而卸载路径根本不用 payload —— 它只结束进程、删快捷方式与自启动项、删注册表、删目录。
///
/// 现在改为落盘构建期嵌入的「轻量变体」(同一程序集,无 payload,实测 6.48MB)。
/// 这里验证的是**降级安全性**:任何情况下都不能让卸载入口缺失。
/// </summary>
public sealed class UninstallerDeploymentTests : IDisposable
{
    private readonly string _directory;

    public UninstallerDeploymentTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NexClip.UninstallerTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void UninstallerFileNameIsStable()
    {
        // 桌面端与卸载注册表项都按这个名字查找,不能随意改
        Assert.Equal("Uninstall.exe", PayloadService.UninstallerFileName);
    }

    [Fact]
    public void DeployUninstaller_AlwaysProducesAnEntryPoint()
    {
        // 最关键的不变量:无论轻量变体是否嵌入,部署完成后必须存在可执行的卸载入口。
        // 瘦身可以失效(退回复制自身),但卸载入口绝不能缺失。
        var result = PayloadService.DeployUninstaller(_directory);

        Assert.True(result);
        var deployed = Path.Combine(_directory, PayloadService.UninstallerFileName);
        Assert.True(File.Exists(deployed));
        Assert.True(new FileInfo(deployed).Length > 0);
    }

    [Fact]
    public void DeployUninstaller_IsRepeatableOnUpgrade()
    {
        // 覆盖安装会重复部署:第二次必须能覆盖已存在的 Uninstall.exe,且不残留临时文件
        Assert.True(PayloadService.DeployUninstaller(_directory));
        var first = new FileInfo(Path.Combine(_directory, PayloadService.UninstallerFileName)).Length;

        Assert.True(PayloadService.DeployUninstaller(_directory));
        var second = new FileInfo(Path.Combine(_directory, PayloadService.UninstallerFileName)).Length;

        Assert.Equal(first, second);
        Assert.False(File.Exists(Path.Combine(_directory, PayloadService.UninstallerFileName + ".new")),
            "部署过程中的临时文件必须被清理");
    }

    [Fact]
    public void SlimUninstallerResourceNameIsStable()
    {
        // 资源名与 csproj 的 LogicalName 必须对应,否则运行时取不到会静默退回大体积方案
        Assert.Equal(
            "NexClip.Installer.Native.Resources.uninstaller.exe",
            PayloadService.SlimUninstallerResourceName);
    }

    [Fact]
    public void DeployedUninstallerIsSmallerThanAFullPayloadCopy()
    {
        // 这条是「瘦身是否真的生效」的验收断言。
        // 若构建脚本没有把变体嵌进来,部署结果会等于安装器自身大小(约 19~22MB),
        // 本测试会失败,提醒打包链路断了 —— 而不是悄悄回退、无人察觉。
        Assert.True(PayloadService.DeployUninstaller(_directory));

        var deployed = new FileInfo(Path.Combine(_directory, PayloadService.UninstallerFileName));
        var payloadBytes = PayloadService.GetExpandedPayloadSizeBytes();

        // payload 展开约 60MB;卸载器若包含它,体积必定远超 12MB
        Assert.True(deployed.Length < 12L * 1024 * 1024,
            $"卸载器体积 {deployed.Length / 1024}KB 异常偏大,可能未使用轻量变体(回退复制了安装器自身)");
        Assert.True(payloadBytes == 0 || deployed.Length < payloadBytes,
            "卸载器不应大于 payload 展开体积");
    }
}
