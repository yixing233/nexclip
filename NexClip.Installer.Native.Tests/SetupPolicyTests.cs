using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

public sealed class SetupPolicyTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1638, true, false)]
    [InlineData(1641, true, true)]
    [InlineData(3010, true, true)]
    [InlineData(1603, false, false)]
    public void InstallerExitCodePolicyHandlesSuccessAndRestart(int exitCode, bool successful, bool restart)
    {
        Assert.Equal(successful, SetupPolicy.IsSuccessfulInstallerExitCode(exitCode));
        Assert.Equal(restart, SetupPolicy.RequiresRestart(exitCode));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    public void RetryDelayUsesBoundedExponentialBackoff(int attempt, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SetupPolicy.GetRetryDelay(attempt));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void DownloadStatusPolicyOnlyRetriesTransientFailures(HttpStatusCode statusCode, bool expected)
    {
        Assert.Equal(expected, SetupPolicy.IsTransientDownloadStatus(statusCode));
    }

    [Theory]
    [InlineData("9.0.0", true)]
    [InlineData("9.0.17", true)]
    [InlineData("9.1.0", true)]
    [InlineData("8.0.30", false)]
    [InlineData("10.0.0", false)]
    [InlineData("9.0.0-preview.1", false)]
    [InlineData("invalid", false)]
    public void RuntimeDirectoryPolicyAcceptsStableDotNet9(string value, bool expected)
    {
        Assert.Equal(expected, DependencyService.IsSupportedRuntimeDirectory(value, 9, Version.Parse("9.0.0")));
    }

    [Fact]
    public void DotNetDetectionRequiresCoreAndDesktopFrameworks()
    {
        var root = Path.Combine(Path.GetTempPath(), "NexClip.RuntimeTest", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App", "9.0.17"));
            Assert.False(DependencyService.AreDotNetFrameworksSupported(root, 9, Version.Parse("9.0.0")));

            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.0.17"));
            Assert.True(DependencyService.AreDotNetFrameworksSupported(root, 9, Version.Parse("9.0.0")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("8000.946.1701.0", "8000.0.0.0", true)]
    [InlineData("7999.999.9999.0", "8000.0.0.0", false)]
    [InlineData("invalid", "8000.0.0.0", false)]
    public void WindowsAppRuntimeVersionPolicyEnforcesMinimum(string actual, string minimum, bool expected)
    {
        Assert.Equal(expected, DependencyService.IsSupportedPackageVersion(actual, Version.Parse(minimum)));
    }

    [Fact]
    public void RequiredSpaceIncludesDependenciesAndSafetyMargin()
    {
        var required = SetupPolicy.CalculateRequiredSpaceBytes(100, 2);

        Assert.Equal(100 + 2 * SetupPolicy.DependencyDownloadAllowanceBytes + SetupPolicy.DiskSafetyMarginBytes, required);
        Assert.True(SetupPolicy.HasSufficientSpace(required, required));
        Assert.False(SetupPolicy.HasSufficientSpace(required - 1, required));
    }

    [Fact]
    public void PerDependencyDownloadLimitsAreBounded()
    {
        Assert.InRange(SetupPolicy.VisualCppDownloadLimitBytes, 32L * 1024 * 1024, 128L * 1024 * 1024);
        Assert.InRange(SetupPolicy.DotNetDownloadLimitBytes, 128L * 1024 * 1024, 256L * 1024 * 1024);
        Assert.InRange(SetupPolicy.WindowsAppRuntimeDownloadLimitBytes, 256L * 1024 * 1024, 384L * 1024 * 1024);
    }

    [Theory]
    [InlineData("C:\\Apps\\NexClip", "C:\\Apps\\NexClip")]
    [InlineData("relative\\NexClip", "C:\\Fallback\\NexClip")]
    [InlineData("", "C:\\Fallback\\NexClip")]
    public void InstallDirectoryResolutionUsesOnlyAbsoluteRegisteredPaths(
        string registeredPath,
        string expected)
    {
        Assert.Equal(expected, InstallerPathHelper.ResolveInstallDirectory(registeredPath, "C:\\Fallback\\NexClip"));
    }

    [Fact]
    public void PayloadEntryPathCannotEscapeDestination()
    {
        Assert.True(PayloadService.TryResolveEntryPath("C:\\Install", "bin\\NexClip.exe", out _));
        Assert.False(PayloadService.TryResolveEntryPath("C:\\Install", "..\\outside.exe", out _));
        Assert.False(PayloadService.TryResolveEntryPath("C:\\Install", "C:\\outside.exe", out _));
    }

    [Fact]
    public void UserDataDirectoryListIncludesLegacyAndConfiguredLocations()
    {
        var configured = Path.Combine(Path.GetTempPath(), "NexClip-custom-data");
        var paths = InstallerPathHelper.GetUserDataDirectories(configured);

        Assert.Contains(paths, path => path.EndsWith(Path.Combine("AppData", "Roaming", "NexClip"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path => path.EndsWith(Path.Combine("AppData", "Local", "SyncClipboard"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, path => string.Equals(path, configured, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DependencyMetadataUsesHttpsAndSilentInstallArguments()
    {
        Assert.Equal(3, DependencyService.Dependencies.Count);
        Assert.All(DependencyService.Dependencies, dependency =>
        {
            Assert.Equal(Uri.UriSchemeHttps, dependency.DownloadUri.Scheme);
            Assert.NotEmpty(dependency.SilentArguments);
            Assert.True(dependency.MaximumDownloadBytes > 0);
        });
    }

    [Fact]
    public void DependencyMetadataProvidesFallbackMirrorsAndManualGuidance()
    {
        Assert.All(DependencyService.Dependencies, dependency =>
        {
            Assert.True(dependency.Sources.Count >= 2, $"{dependency.DisplayName} 缺少备用下载源");
            Assert.All(dependency.Sources, source => Assert.Equal(Uri.UriSchemeHttps, source.Uri.Scheme));
            Assert.Equal(dependency.Sources.Count, dependency.DownloadUris.Distinct().Count());
            Assert.Equal(Uri.UriSchemeHttps, dependency.ManualDownloadPage.Scheme);
            Assert.NotEmpty(dependency.RepairArguments);
            Assert.InRange(
                dependency.ExpectedDownloadBytes,
                1L * 1024 * 1024,
                dependency.MaximumDownloadBytes);
        });
    }

    [Fact]
    public void PrimaryDependencySourcePinsLowercaseSha256()
    {
        Assert.All(DependencyService.Dependencies, dependency =>
        {
            var primary = dependency.Sources[0];
            Assert.True(primary.HasPinnedHash, $"{dependency.DisplayName} 主下载源缺少固定哈希");
            Assert.Equal(primary.Sha256.ToLowerInvariant(), primary.Sha256);
            Assert.All(primary.Sha256, character => Assert.True(Uri.IsHexDigit(character)));
            Assert.Contains(dependency.Sources.Skip(1), source => !source.HasPinnedHash);
        });
    }

    [Fact]
    public void PinnedHashMismatchRejectsDownloadedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "NexClip.HashTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "runtime.exe");

        try
        {
            File.WriteAllText(file, "tampered payload");

            Assert.Throws<InvalidDataException>(() => DownloadVerifier.VerifySha256(
                file,
                new string('a', 64)));
            Assert.Throws<InvalidDataException>(() => DownloadVerifier.VerifySha256(file, "not-a-hash"));

            var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            DownloadVerifier.VerifySha256(file, expected);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task DownloadSwitchesSourceWhenPinnedHashDoesNotMatch()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            "NexClip.HashFallbackTest",
            Guid.NewGuid().ToString("N"),
            "runtime.exe");
        var requestedHosts = new List<string>();
        var handler = new StaticContentHandler("mirror payload", requestedHosts);
        using var client = new HttpClient(handler);

        try
        {
            await DownloadVerifier.DownloadAsync(
                client,
                [
                    new DependencySource(new Uri("https://pinned.example/runtime.exe"), new string('b', 64)),
                    new DependencySource(new Uri("https://mirror.example/runtime.exe"))
                ],
                destination,
                maximumBytes: 1024,
                expectedBytes: 0);

            Assert.Equal("mirror payload", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
            // 哈希不匹配不做重试，立即切换下一个源
            Assert.Equal(["pinned.example", "mirror.example"], requestedHosts);
        }
        finally
        {
            var directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class StaticContentHandler(string body, List<string> requestedHosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestedHosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body)
            });
        }
    }

    [Fact]
    public void WindowsAppRuntimeDependencyRequiresFrameworkAndMainPackage()
    {
        var dependency = DependencyService.Dependencies.Single(
            item => item.Kind == DependencyKind.WindowsAppRuntime);

        Assert.Equal("Microsoft.WindowsAppRuntime.1.8", dependency.RequiredPackageName);
        Assert.Equal("MicrosoftCorporationII.WinAppRuntime.Main.1.8", dependency.RequiredMainPackageName);
        Assert.True(DependencyService.IsSupportedWindowsAppRuntimePackage(
            "MicrosoftCorporationII.WinAppRuntime.Main.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe",
            dependency.RequiredMainPackageName,
            dependency.MinimumVersion));
        Assert.False(DependencyService.IsSupportedWindowsAppRuntimePackage(
            "MicrosoftCorporationII.WinAppRuntime.Main.1.4_4000.1309.2056.0_x64__8wekyb3d8bbwe",
            dependency.RequiredMainPackageName,
            dependency.MinimumVersion));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070666), true, false)]
    [InlineData(unchecked((int)0x80073D06), true, false)]
    [InlineData(unchecked((int)0x80070643), false, false)]
    public void AlreadyInstalledExitCodesCountAsSuccess(int exitCode, bool successful, bool restart)
    {
        Assert.Equal(successful, SetupPolicy.IsSuccessfulInstallerExitCode(exitCode));
        Assert.Equal(restart, SetupPolicy.RequiresRestart(exitCode));
    }

    [Theory]
    [InlineData(0, 1024 * 1024, "")]
    [InlineData(5 * 1024 * 1024, 0, "")]
    [InlineData(10 * 1024 * 1024, 5 * 1024 * 1024, "2s")]
    [InlineData(600 * 1024 * 1024, 5 * 1024 * 1024, "2m00s")]
    public void RemainingTimeFormatsOnlyWhenMeasurable(
        long remainingBytes,
        double bytesPerSecond,
        string expected)
    {
        Assert.Equal(expected, SetupPolicy.FormatRemainingTime(remainingBytes, bytesPerSecond));
    }

    [Fact]
    public async Task DownloadResumesFromPartialFileUsingRangeRequest()
    {
        var payload = "0123456789ABCDEF"u8.ToArray();
        var destination = Path.Combine(
            Path.GetTempPath(),
            "NexClip.ResumeTest",
            Guid.NewGuid().ToString("N"),
            "runtime.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var observedRanges = new List<long?>();
        var handler = new RangeAwareHandler(payload, truncateFirstResponseAt: 6, observedRanges);
        using var client = new HttpClient(handler);

        try
        {
            await DownloadVerifier.DownloadAsync(
                client,
                [new Uri("https://download.example/runtime.exe")],
                destination,
                maximumBytes: 1024,
                expectedBytes: payload.Length);

            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
            Assert.Equal([null, 6L], observedRanges);
        }
        finally
        {
            var directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DownloadFallsBackToSecondaryMirrorWhenPrimaryIsUnavailable()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            "NexClip.MirrorTest",
            Guid.NewGuid().ToString("N"),
            "runtime.exe");
        var requestedHosts = new List<string>();
        var handler = new HostRoutingHandler(requestedHosts);
        using var client = new HttpClient(handler);

        try
        {
            await DownloadVerifier.DownloadAsync(
                client,
                [
                    new Uri("https://primary.example/runtime.exe"),
                    new Uri("https://mirror.example/runtime.exe")
                ],
                destination,
                maximumBytes: 1024,
                expectedBytes: 0);

            Assert.Equal("mirror payload", await File.ReadAllTextAsync(destination));
            Assert.Equal(SetupPolicy.DownloadMaxAttempts, requestedHosts.Count(host => host == "primary.example"));
            Assert.Contains("mirror.example", requestedHosts);
        }
        finally
        {
            var directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DownloadFailureAfterAllMirrorsRemovesPartialFile()
    {
        var destination = Path.Combine(
            Path.GetTempPath(),
            "NexClip.MirrorFailTest",
            Guid.NewGuid().ToString("N"),
            "runtime.exe");
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);

        try
        {
            await Assert.ThrowsAsync<IOException>(() => DownloadVerifier.DownloadAsync(
                client,
                [
                    new Uri("https://primary.example/runtime.exe"),
                    new Uri("https://mirror.example/runtime.exe")
                ],
                destination,
                maximumBytes: 1024,
                expectedBytes: 0));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".partial"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>首个响应被截断以模拟连接中断，后续请求必须携带 Range 头继续下载。</summary>
    private sealed class RangeAwareHandler(
        byte[] payload,
        int truncateFirstResponseAt,
        List<long?> observedRanges) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            var from = request.Headers.Range?.Ranges.SingleOrDefault()?.From;
            observedRanges.Add(from);

            var offset = (int)(from ?? 0);
            var body = payload.Skip(offset).ToArray();
            var response = new HttpResponseMessage(
                from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                RequestMessage = request
            };

            if (requestNumber == 1)
            {
                // 声明完整长度但只回传部分内容，触发“下载不完整”重试路径
                response.Content = new ByteArrayContent(body.Take(truncateFirstResponseAt).ToArray());
                response.Content.Headers.ContentLength = body.Length;
            }
            else
            {
                response.Content = new ByteArrayContent(body);
                response.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class HostRoutingHandler(List<string> requestedHosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            requestedHosts.Add(host);

            if (host == "primary.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                {
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent("mirror payload"u8.ToArray())
            });
        }
    }

    [Theory]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_neutral__8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x86__8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsAppRuntime.1.8_7999.999.9999.0_x64__8wekyb3d8bbwe", false)]
    [InlineData("Microsoft.WindowsAppRuntime.1.7_8000.946.1701.0_x64__8wekyb3d8bbwe", false)]
    public void WindowsAppRuntimePackagePolicyRequiresExpectedNameArchitectureAndVersion(
        string packageFullName,
        bool expected)
    {
        Assert.Equal(expected, DependencyService.IsSupportedWindowsAppRuntimePackage(
            packageFullName,
            "Microsoft.WindowsAppRuntime.1.8",
            Version.Parse("8000.0.0.0")));
    }

    [Fact]
    public void ProgressAnimationMovesForwardWithoutOvershoot()
    {
        var next = SetupPolicy.AnimateTowards(0.25, 0.75, 0.016);

        Assert.InRange(next, 0.25, 0.75);
        Assert.Equal(0.75, SetupPolicy.AnimateTowards(0.75, 0.50, 0.016));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void RetryPolicyClassifiesHttpFailures(HttpStatusCode? statusCode, bool expected)
    {
        var exception = new HttpRequestException("download", null, statusCode);
        Assert.Equal(expected, DownloadVerifier.IsRetryable(exception));
    }

    [Fact]
    public async Task DownloadRetriesIntoPartialFileThenAtomicallyCompletes()
    {
        var destination = Path.Combine(Path.GetTempPath(), "NexClip.DownloadTest", Guid.NewGuid().ToString("N"), "runtime.exe");
        var handler = new SequenceHandler(requestNumber => requestNumber == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://download.example/runtime.exe"),
                Content = new ByteArrayContent("verified payload"u8.ToArray())
            });
        using var client = new HttpClient(handler);

        try
        {
            await DownloadVerifier.DownloadAsync(
                client,
                new Uri("https://download.example/runtime.exe"),
                destination,
                maximumBytes: 1024);

            Assert.Equal("verified payload", await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(destination + ".partial"));
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            var directory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responseFactory(Interlocked.Increment(ref _requestCount));
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
