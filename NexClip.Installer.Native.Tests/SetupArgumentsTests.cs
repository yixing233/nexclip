using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

public sealed class SetupArgumentsTests
{
    [Fact]
    public void DefaultArgumentsRunInteractiveInstall()
    {
        var options = SetupArguments.Parse([], @"C:\Downloads\NexClip_Setup.exe");

        Assert.False(options.Uninstall);
        Assert.False(options.Silent);
        Assert.False(options.Diagnose);
        Assert.True(options.CreateDesktopShortcut);
        Assert.False(options.AutoStartup);
        Assert.True(Path.IsPathFullyQualified(options.InstallDirectory));
    }

    [Theory]
    [InlineData("/silent")]
    [InlineData("-SILENT")]
    [InlineData("/VerySilent")]
    [InlineData("/quiet")]
    public void SilentSwitchesAreRecognisedCaseInsensitively(string argument)
    {
        Assert.True(SetupArguments.Parse([argument], null).Silent);
    }

    [Fact]
    public void UninstallIsInferredFromExecutableName()
    {
        Assert.True(SetupArguments.Parse([], @"D:\Program Files\NexClip\Uninstall.exe").Uninstall);
        Assert.False(SetupArguments.Parse([], @"D:\Program Files\NexClip\NexClip_Setup.exe").Uninstall);
    }

    [Fact]
    public void InstallDirectoryOverrideAcceptsQuotedAbsolutePath()
    {
        var options = SetupArguments.Parse(["/dir=\"C:\\Apps\\NexClip\""], null);

        Assert.Equal(@"C:\Apps\NexClip", options.InstallDirectory);
    }

    [Fact]
    public void RelativeInstallDirectoryFallsBackToDefaultLocation()
    {
        var options = SetupArguments.Parse(["/dir=relative\\NexClip"], null);

        Assert.Equal(InstallerPathHelper.GetDefaultInstallDirectory(), options.InstallDirectory);
    }

    [Fact]
    public void ShortcutSwitchesToggleDeploymentOptions()
    {
        var options = SetupArguments.Parse(["/silent", "/nodesktopicon", "/autostart"], null);

        Assert.True(options.Silent);
        Assert.False(options.CreateDesktopShortcut);
        Assert.True(options.AutoStartup);
    }

    [Fact]
    public void DiagnoseSupportsBareSwitchAndExplicitReportPath()
    {
        Assert.True(SetupArguments.Parse(["/diagnose"], null).Diagnose);

        var reportPath = Path.Combine(Path.GetTempPath(), "nexclip-report.txt");
        var options = SetupArguments.Parse([$"/diagnose={reportPath}"], null);

        Assert.True(options.Diagnose);
        Assert.Equal(reportPath, options.DiagnosticsPath);
    }

    [Fact]
    public void UnknownArgumentsAreIgnored()
    {
        var options = SetupArguments.Parse(["--experimental", "plain-text", "/norestart"], null);

        Assert.False(options.Silent);
        Assert.False(options.Diagnose);
        Assert.True(options.CreateDesktopShortcut);
    }
}