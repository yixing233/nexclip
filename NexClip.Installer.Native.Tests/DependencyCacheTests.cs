using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

public sealed class DependencyCacheTests
{
    [Fact]
    public void DownloadCacheIsAStablePathSoRetriesCanResume()
    {
        var cache = DependencyService.DownloadCacheDirectory;

        Assert.True(Path.IsPathFullyQualified(cache));
        Assert.Equal(cache, DependencyService.DownloadCacheDirectory);
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()),
            Path.GetFullPath(cache),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PruneRemovesExpiredEntriesAndKeepsRecentOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NexClip.CacheTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, "vc_redist.x64.exe");
        var recent = Path.Combine(directory, "windowsdesktop-runtime-9-win-x64.exe");

        try
        {
            File.WriteAllText(expired, "stale");
            File.WriteAllText(recent, "fresh");
            File.SetLastWriteTimeUtc(
                expired,
                DateTime.UtcNow - DependencyService.DownloadCacheRetention - TimeSpan.FromHours(1));

            DependencyService.PruneDownloadCache(directory);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(recent));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task VerifiedInstallerInCacheIsReusedWithoutDownloadingAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NexClip.ReuseTest", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "runtime.exe");
        Directory.CreateDirectory(directory);

        try
        {
            var payload = "cached payload"u8.ToArray();
            await File.WriteAllBytesAsync(destination, payload);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
            var handler = new ThrowingHandler();
            using var client = new HttpClient(handler);

            await DownloadVerifier.DownloadAsync(
                client,
                [new DependencySource(new Uri("https://download.example/runtime.exe"), hash)],
                destination,
                maximumBytes: 1024,
                expectedBytes: payload.Length);

            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CorruptedCachedInstallerIsDiscardedAndDownloadedAgain()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NexClip.CorruptCacheTest", Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(directory, "runtime.exe");
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(destination, "corrupted");
            var payload = "fresh payload"u8.ToArray();
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
            var handler = new StaticPayloadHandler(payload);
            using var client = new HttpClient(handler);

            await DownloadVerifier.DownloadAsync(
                client,
                [new DependencySource(new Uri("https://download.example/runtime.exe"), hash)],
                destination,
                maximumBytes: 1024,
                expectedBytes: payload.Length);

            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
            Assert.Equal(1, handler.RequestCount);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PartialFileNamesAreStableAndDistinctPerSource()
    {
        var destination = Path.Combine(Path.GetTempPath(), "runtime.exe");
        var pinned = new DependencySource(new Uri("https://download.example/runtime.exe"), new string('a', 64));
        var evergreen = new DependencySource(new Uri("https://aka.ms/runtime"));

        var pinnedPath = DownloadVerifier.GetPartialPath(destination, pinned);
        var evergreenPath = DownloadVerifier.GetPartialPath(destination, evergreen);

        Assert.EndsWith(".partial", pinnedPath);
        Assert.NotEqual(pinnedPath, evergreenPath);
        Assert.Equal(pinnedPath, DownloadVerifier.GetPartialPath(destination, pinned));
        Assert.Equal(evergreenPath, DownloadVerifier.GetPartialPath(destination, evergreen));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("缓存命中时不应发起网络请求。");
        }
    }

    private sealed class StaticPayloadHandler(byte[] payload) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
        }
    }
}