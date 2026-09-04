using System.Text;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

public sealed class DependencyManifestTests
{
    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "visualCppRuntime": {
            "displayName": "Microsoft Visual C++ x64 运行库",
            "minimumVersion": "14.40.33810",
            "fileName": "vc_redist.x64.exe",
            "sizeBytes": 25635768,
            "url": "https://download.visualstudio.microsoft.com/download/pr/id/HASH/VC_redist.x64.exe",
            "sha256": "cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b",
            "fallbackUrl": "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            "manualDownloadPage": "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            "silentArguments": "/install /quiet /norestart",
            "repairArguments": "/repair /quiet /norestart"
          },
          "dotNetDesktopRuntime": {
            "displayName": ".NET 9 Desktop Runtime x64",
            "minimumVersion": "9.0.0",
            "majorVersion": 9,
            "fileName": "windowsdesktop-runtime-9-win-x64.exe",
            "sizeBytes": 60405064,
            "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/9.0.19/windowsdesktop-runtime-9.0.19-win-x64.exe",
            "sha256": "4bee05aa0637468a19cd82490858fc69e93fce8d22c0aeb272a76b71f0dc93e9",
            "fallbackUrl": "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe",
            "manualDownloadPage": "https://dotnet.microsoft.com/download/dotnet/9.0",
            "silentArguments": "/install /quiet /norestart",
            "repairArguments": "/repair /quiet /norestart"
          },
          "windowsAppRuntime": {
            "displayName": "Windows App SDK 1.8 Runtime x64",
            "minimumVersion": "8000.879.2017.0",
            "packageName": "Microsoft.WindowsAppRuntime.1.8",
            "mainPackageName": "MicrosoftCorporationII.WinAppRuntime.Main.1.8",
            "fileName": "windowsappruntimeinstall-x64.exe",
            "sizeBytes": 106920248,
            "url": "https://download.microsoft.com/download/id/WindowsAppRuntimeInstall-x64.exe",
            "sha256": "b8cda840267ab72797f654f801f9a064ab6d9e508cedee3df79f772f104db6d6",
            "fallbackUrl": "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe",
            "manualDownloadPage": "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads",
            "silentArguments": "--quiet",
            "repairArguments": "--quiet --force"
          }
        }
        """;

    [Fact]
    public void EmbeddedManifestMatchesTheDependenciesUsedAtRuntime()
    {
        var dependencies = DependencyManifest.Load();

        Assert.Equal(3, dependencies.Count);
        Assert.Equal(
            DependencyService.Dependencies.Select(dependency => dependency.Kind),
            dependencies.Select(dependency => dependency.Kind));
        Assert.All(dependencies, dependency =>
        {
            Assert.True(dependency.Sources[0].HasPinnedHash);
            Assert.Equal(2, dependency.Sources.Count);
            Assert.False(dependency.Sources[1].HasPinnedHash);
            Assert.InRange(dependency.ExpectedDownloadBytes, 1L * 1024 * 1024, dependency.MaximumDownloadBytes);
            Assert.NotEmpty(dependency.RepairArguments);
        });

        var windowsAppRuntime = dependencies.Single(item => item.Kind == DependencyKind.WindowsAppRuntime);
        Assert.Equal("Microsoft.WindowsAppRuntime.1.8", windowsAppRuntime.RequiredPackageName);
        Assert.Equal("MicrosoftCorporationII.WinAppRuntime.Main.1.8", windowsAppRuntime.RequiredMainPackageName);
        Assert.Equal(9, dependencies.Single(item => item.Kind == DependencyKind.DotNetDesktopRuntime).RequiredMajorVersion);
    }

    [Fact]
    public void ManifestParsingKeepsPinnedPrimaryAndEvergreenFallback()
    {
        var dependencies = Parse(ValidManifest);

        var visualCpp = dependencies.Single(item => item.Kind == DependencyKind.VisualCppRuntime);
        Assert.Equal("vc_redist.x64.exe", visualCpp.FileName);
        Assert.Equal(new Version(14, 40, 33810), visualCpp.MinimumVersion);
        Assert.Equal("download.visualstudio.microsoft.com", visualCpp.Sources[0].Uri.Host);
        Assert.Equal("aka.ms", visualCpp.Sources[1].Uri.Host);
        Assert.Equal(SetupPolicy.VisualCppDownloadLimitBytes, visualCpp.MaximumDownloadBytes);
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [InlineData("https://download.microsoft.com/download/id/WindowsAppRuntimeInstall-x64.exe", "https://cdn.example.com/WindowsAppRuntimeInstall-x64.exe")]
    [InlineData("https://download.microsoft.com/download/id/WindowsAppRuntimeInstall-x64.exe", "http://download.microsoft.com/download/id/WindowsAppRuntimeInstall-x64.exe")]
    [InlineData("\"sha256\": \"b8cda840267ab72797f654f801f9a064ab6d9e508cedee3df79f772f104db6d6\"", "\"sha256\": \"deadbeef\"")]
    [InlineData("\"fileName\": \"windowsappruntimeinstall-x64.exe\"", "\"fileName\": \"..\\\\evil.exe\"")]
    [InlineData("\"sizeBytes\": 106920248", "\"sizeBytes\": 0")]
    [InlineData("\"sizeBytes\": 106920248", "\"sizeBytes\": 999999999")]
    [InlineData("\"minimumVersion\": \"8000.879.2017.0\"", "\"minimumVersion\": \"latest\"")]
    [InlineData("\"repairArguments\": \"--quiet --force\"", "\"repairArguments\": \"  \"")]
    public void InvalidManifestValuesAreRejected(string original, string replacement)
    {
        var manifest = ValidManifest.Replace(original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(ValidManifest, manifest);
        Assert.Throws<InvalidDataException>(() => Parse(manifest));
    }

    [Fact]
    public void MissingDependencyNodeIsRejected()
    {
        var manifest = ValidManifest.Replace("\"windowsAppRuntime\"", "\"windowsAppRuntimeLegacy\"", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => Parse(manifest));
    }

    private static IReadOnlyList<DependencyDefinition> Parse(string manifest)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        return DependencyManifest.Parse(stream);
    }
}