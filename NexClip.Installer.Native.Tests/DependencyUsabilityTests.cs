using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 对齐 CrabDesk 安装器加固设计的“可用包”判定：
/// 只有健康状态（Ok）的包才能参与 Windows App Runtime 依赖检测，
/// 且兜底查询必须覆盖 Framework、Main/Singleton 与 DDLM 三类包模式。
/// </summary>
public sealed class DependencyUsabilityTests
{
    [Theory]
    [InlineData("Ok", true)]
    [InlineData("ok", true)]
    [InlineData(" Ok ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("Tampered", false)]
    [InlineData("Modified", false)]
    [InlineData("NotAvailable", false)]
    [InlineData("LicenseIssue", false)]
    [InlineData("Disabled", false)]
    public void PackageStatusPolicyOnlyAcceptsHealthyPackages(string? status, bool expected)
    {
        Assert.Equal(expected, DependencyService.IsUsablePackageStatus(status));
    }

    [Fact]
    public void PackageQueryParsingKeepsOnlyUsablePackages()
    {
        const string output = """
            [{"PackageFullName":"Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe","Status":"Ok"},
             {"PackageFullName":"MicrosoftCorporationII.WinAppRuntime.Main.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe","Status":"Tampered"},
             {"PackageFullName":"MicrosoftCorporationII.WinAppRuntime.Singleton_8000.921.1539.0_x64__8wekyb3d8bbwe","Status":"Modified"},
             {"PackageFullName":"Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8000.921.1539.0_x64__8wekyb3d8bbwe","Status":"Ok"}]
            """;

        var names = DependencyService.ParseUsablePackageFullNames(output);

        Assert.Equal(2, names.Count);
        Assert.Contains("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe", names);
        Assert.Contains("Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8000.921.1539.0_x64__8wekyb3d8bbwe", names);
    }

    [Fact]
    public void PackageQueryParsingHandlesSingleObjectMissingStatusAndInvalidJson()
    {
        var single = DependencyService.ParseUsablePackageFullNames(
            """{"PackageFullName":"Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe","Status":"Ok"}""");
        Assert.Single(single);

        var missingStatus = DependencyService.ParseUsablePackageFullNames(
            """{"PackageFullName":"Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8000.921.1539.0_x64__8wekyb3d8bbwe"}""");
        Assert.Single(missingStatus);

        Assert.Empty(DependencyService.ParseUsablePackageFullNames("not-json"));
        Assert.Empty(DependencyService.ParseUsablePackageFullNames(""));
    }

    [Theory]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Singleton_8000.921.1539.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Singleton_8002.4.0.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Singleton_8000.500.100.0_x64__8wekyb3d8bbwe", false)]
    public void SingletonPolicyAcceptsForwardServicedVersions(string packageFullName, bool expected)
    {
        // Singleton 全机唯一且向前服务：被 2.x 服务化为 8002.* 后仍满足 1.8 依赖，
        // 只拒绝低于清单最低版本（8000.879.2017.0）的包。
        var dependency = DependencyService.Dependencies.Single(
            item => item.Kind == DependencyKind.WindowsAppRuntime);

        Assert.Equal(expected, DependencyService.IsSupportedWindowsAppRuntimePackage(
            packageFullName,
            dependency.RequiredSingletonPackageName,
            dependency.MinimumVersion));
    }

    [Fact]
    public void WindowsAppRuntimeDependencyDeclaresAllPackageRoles()
    {
        var dependency = DependencyService.Dependencies.Single(
            item => item.Kind == DependencyKind.WindowsAppRuntime);

        Assert.Equal("MicrosoftCorporationII.WinAppRuntime.Singleton", dependency.RequiredSingletonPackageName);
        Assert.Equal("Microsoft.WinAppRuntime.DDLM.", dependency.RequiredDdlmPackagePrefix);
        Assert.True(DependencyService.IsSupportedDdlmPackage(
            "Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8000.921.1539.0_x64__8wekyb3d8bbwe",
            dependency.RequiredDdlmPackagePrefix,
            dependency.MinimumVersion));
        // DDLM 与运行时大版本强绑定：8002.* 不能充当 1.8 运行时的引导包
        Assert.False(DependencyService.IsSupportedDdlmPackage(
            "Microsoft.WinAppRuntime.DDLM.8002.500.100.0-x6_8002.500.100.0_x64__8wekyb3d8bbwe",
            dependency.RequiredDdlmPackagePrefix,
            dependency.MinimumVersion));
    }
}
