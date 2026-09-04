using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Security.Cryptography.X509Certificates;

namespace NexClip.Installer.Native.Services;

internal static class DownloadVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static Task DownloadAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long maximumBytes,
        Action<double, string>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(client, [new DependencySource(uri)], destination, maximumBytes, 0, onProgress, cancellationToken);

    internal static Task DownloadAsync(
        HttpClient client,
        IReadOnlyList<Uri> uris,
        string destination,
        long maximumBytes,
        long expectedBytes,
        Action<double, string>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            client,
            uris.Select(uri => new DependencySource(uri)).ToArray(),
            destination,
            maximumBytes,
            expectedBytes,
            onProgress,
            cancellationToken);

    /// <summary>
    /// 依次尝试多个下载源；单个源内部支持断点续传与指数退避重试，
    /// 带固定哈希的源在落盘后立即强校验，校验失败自动切换下一个源。
    /// </summary>
    internal static async Task DownloadAsync(
        HttpClient client,
        IReadOnlyList<DependencySource> sources,
        string destination,
        long maximumBytes,
        long expectedBytes,
        Action<double, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("缺少依赖组件下载地址。", nameof(sources));
        }

        foreach (var source in sources)
        {
            if (!source.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("依赖组件下载地址必须使用 HTTPS。");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (TryReuseVerifiedDownload(destination, sources, onProgress))
        {
            return;
        }

        Exception? lastError = null;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var mirrorLabel = index > 0 ? $"备用源{index + 1} · " : string.Empty;
            // 不同下载源可能指向不同发布版本，各源使用独立续传文件，避免跨源拼接出损坏的安装包
            var partialPath = GetPartialPath(destination, source);
            if (!source.HasPinnedHash)
            {
                // evergreen 源的内容会随微软发布滚动变化，跨进程续传可能拼出损坏文件，先丢弃历史残片
                TryDelete(partialPath);
            }

            for (var attempt = 1; attempt <= SetupPolicy.DownloadMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadAttemptAsync(
                        client,
                        source.Uri,
                        partialPath,
                        maximumBytes,
                        expectedBytes,
                        mirrorLabel,
                        onProgress,
                        cancellationToken).ConfigureAwait(false);

                    if (source.HasPinnedHash)
                    {
                        onProgress?.Invoke(1.0, "正在校验 SHA-256 完整性...");
                        VerifySha256(partialPath, source.Sha256);
                    }

                    File.Move(partialPath, destination, true);
                    CleanupOtherPartials(destination, sources, source);
                    SetupLog.Write($"{Path.GetFileName(destination)} 已从 {source.Uri.Host} 下载完成。");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 用户主动取消：保留残片，下次重试可直接续传
                    throw;
                }
                catch (Exception exception) when (IsRetryable(exception, cancellationToken))
                {
                    lastError = exception;
                    if (attempt < SetupPolicy.DownloadMaxAttempts)
                    {
                        var delay = SetupPolicy.GetRetryDelay(attempt);
                        onProgress?.Invoke(
                            0,
                            $"网络中断，{delay.TotalSeconds:F0} 秒后续传重试 ({attempt}/{SetupPolicy.DownloadMaxAttempts})");
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // 当前源已用尽重试：保留残片以便用户重试时续传，仅提示切换备用源
                    SetupLog.Write($"下载源 {source.Uri.Host} 不可用：{exception.Message}");
                    if (index < sources.Count - 1)
                    {
                        onProgress?.Invoke(0, "当前下载源不可用，正在切换备用源...");
                    }

                    break;
                }
                catch (InvalidDataException exception)
                {
                    // 哈希/大小等内容校验失败：残片不可信，丢弃后改用其他源
                    lastError = exception;
                    TryDelete(partialPath);
                    if (index == sources.Count - 1)
                    {
                        throw;
                    }

                    SetupLog.Write($"下载源 {source.Uri.Host} 完整性校验失败：{exception.Message}");
                    onProgress?.Invoke(0, "完整性校验未通过，正在切换备用源...");
                    break;
                }
                catch (Exception)
                {
                    TryDelete(partialPath);
                    throw;
                }
            }
        }

        throw new IOException(
            $"依赖组件下载失败（已尝试 {sources.Count} 个下载源）：{lastError?.Message ?? "未知网络错误"}",
            lastError);
    }

    /// <summary>
    /// 每个下载源使用独立的续传文件名，防止上一次失败留下的其他源残片被错误续传。
    /// 令牌取自固定哈希或地址摘要，保证跨进程稳定，重启安装器后仍能续传。
    /// </summary>
    internal static string GetPartialPath(string destination, DependencySource source)
    {
        var token = source.HasPinnedHash
            ? source.Sha256[..8]
            : Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(source.Uri.AbsoluteUri)))[..8];
        return $"{destination}.{token}.partial";
    }

    /// <summary>下载成功后清理其他源留下的残片，避免缓存目录堆积无用文件。</summary>
    private static void CleanupOtherPartials(
        string destination,
        IReadOnlyList<DependencySource> sources,
        DependencySource completedSource)
    {
        foreach (var source in sources)
        {
            if (!ReferenceEquals(source, completedSource))
            {
                TryDelete(GetPartialPath(destination, source));
            }
        }
    }

    /// <summary>
    /// 复用上一次安装尝试已下载并校验通过的安装包（同一临时目录内重试时避免重复下载数百 MB）。
    /// 仅在存在固定哈希且完全匹配时复用；无哈希的 evergreen 源始终重新下载。
    /// </summary>
    private static bool TryReuseVerifiedDownload(
        string destination,
        IReadOnlyList<DependencySource> sources,
        Action<double, string>? onProgress)
    {
        if (!File.Exists(destination))
        {
            return false;
        }

        foreach (var source in sources)
        {
            if (!source.HasPinnedHash)
            {
                continue;
            }

            try
            {
                onProgress?.Invoke(0, "正在校验已下载的安装包...");
                VerifySha256(destination, source.Sha256);
                onProgress?.Invoke(1.0, "已复用本地校验通过的安装包");
                return true;
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
                return false;
            }
        }

        TryDelete(destination);
        return false;
    }

    internal static bool IsRetryable(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is HttpRequestException http)
        {
            return http.StatusCode is null || SetupPolicy.IsTransientDownloadStatus(http.StatusCode);
        }

        return exception is IOException or SocketException or TimeoutException;
    }

    private static async Task DownloadAttemptAsync(
        HttpClient client,
        Uri uri,
        string destination,
        long maximumBytes,
        long expectedBytes,
        string mirrorLabel,
        Action<double, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var resumeOffset = TryGetResumeOffset(destination, maximumBytes);
        onProgress?.Invoke(
            0,
            resumeOffset > 0
                ? $"{mirrorLabel}正在续传 (已完成 {FormatMegabytes(resumeOffset)}M)..."
                : $"{mirrorLabel}正在连接下载服务...");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(SetupPolicy.ConnectTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"连接下载服务超时（{SetupPolicy.ConnectTimeout.TotalSeconds:F0} 秒）。");
        }

        using (response)
        {
            if (resumeOffset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // 服务端不支持断点续传：丢弃残片并整包重下
                resumeOffset = 0;
                TryDelete(destination);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    throw new IOException("续传范围失效，已重置下载进度。");
                }
            }

            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
            {
                throw new InvalidDataException("依赖组件下载被重定向到非 HTTPS 地址。");
            }

            var totalBytes = ResolveTotalBytes(response, resumeOffset, expectedBytes);
            if (totalBytes is > 0 && totalBytes.Value > maximumBytes)
            {
                throw new InvalidDataException("依赖组件超过允许的最大下载大小。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(
                destination,
                resumeOffset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            var written = resumeOffset;
            var lastSampleBytes = resumeOffset;
            long lastSampleMilliseconds = 0;
            double smoothedBytesPerSecond = 0;
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                int read;
                using (var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    idleTimeout.CancelAfter(SetupPolicy.DownloadIdleTimeout);
                    try
                    {
                        read = await source.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"下载连接停滞超过 {SetupPolicy.DownloadIdleTimeout.TotalSeconds:F0} 秒。");
                    }
                }

                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > maximumBytes)
                {
                    throw new InvalidDataException("依赖组件超过允许的最大下载大小。");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                if (elapsedMilliseconds - lastSampleMilliseconds < 120)
                {
                    continue;
                }

                var elapsedSeconds = Math.Max(0.001, (elapsedMilliseconds - lastSampleMilliseconds) / 1000.0);
                var instantSpeed = (written - lastSampleBytes) / elapsedSeconds;
                smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                    ? instantSpeed
                    : smoothedBytesPerSecond * 0.65 + instantSpeed * 0.35;
                lastSampleBytes = written;
                lastSampleMilliseconds = elapsedMilliseconds;
                ReportProgress(written, totalBytes, smoothedBytesPerSecond, mirrorLabel, onProgress);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (written == 0)
            {
                throw new InvalidDataException("依赖组件下载结果为空。");
            }
            if (totalBytes is > 0 && written != totalBytes.Value)
            {
                throw new IOException($"依赖组件下载不完整，预期 {totalBytes.Value} 字节，实际 {written} 字节。");
            }

            onProgress?.Invoke(1.0, $"{FormatMegabytes(written)}M/{FormatMegabytes(written)}M · 下载完成");
        }
    }

    private static long? ResolveTotalBytes(HttpResponseMessage response, long resumeOffset, long expectedBytes)
    {
        var rangeTotal = response.Content.Headers.ContentRange?.Length;
        if (rangeTotal is > 0)
        {
            return rangeTotal;
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0)
        {
            return resumeOffset + contentLength.Value;
        }

        return expectedBytes > 0 ? expectedBytes : null;
    }

    private static long TryGetResumeOffset(string partialPath, long maximumBytes)
    {
        try
        {
            if (!File.Exists(partialPath))
            {
                return 0;
            }

            var length = new FileInfo(partialPath).Length;
            if (length <= 0 || length >= maximumBytes)
            {
                TryDelete(partialPath);
                return 0;
            }

            return length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void ReportProgress(
        long currentBytes,
        long? totalBytes,
        double speedBytesPerSecond,
        string mirrorLabel,
        Action<double, string>? onProgress)
    {
        if (onProgress is null)
        {
            return;
        }

        var progress = totalBytes is > 0
            ? Math.Clamp((double)currentBytes / totalBytes.Value, 0.0, 1.0)
            : 0.0;
        var totalText = totalBytes is > 0 ? $"/{FormatMegabytes(totalBytes.Value)}M" : string.Empty;
        var remainingText = totalBytes is > 0
            ? SetupPolicy.FormatRemainingTime(totalBytes.Value - currentBytes, speedBytesPerSecond)
            : string.Empty;
        var suffix = string.IsNullOrEmpty(remainingText) ? string.Empty : $" · {remainingText}";
        onProgress(
            progress,
            $"{mirrorLabel}{FormatMegabytes(currentBytes)}M{totalText} · {FormatSpeed(speedBytesPerSecond)}{suffix}");
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):F1}";

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/s"
        : $"{bytesPerSecond / 1024.0:F0} KB/s";

    /// <summary>校验固定发布版本的 SHA-256，防止镜像投毒或传输损坏。</summary>
    internal static void VerifySha256(string path, string expectedHash)
    {
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("依赖组件缺少有效的 SHA-256 校验值。");
        }

        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        var expected = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} 的 SHA-256 校验失败，文件可能已损坏或被篡改。");
        }
    }

    internal static void VerifyTrustedMicrosoftSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData(fileInfoPointer);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (result != 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} 的 Authenticode 签名不受信任 (0x{result:X8})。");
            }

#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!certificate.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} 不是由 Microsoft Corporation 签名的组件。");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal WinTrustFileInfo(string path)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        private uint StructureSize;
        [MarshalAs(UnmanagedType.LPWStr)] private string FilePath;
        private IntPtr FileHandle;
        private IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal WinTrustData(IntPtr fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UIChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000080;
            UIContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        private uint StructureSize;
        private IntPtr PolicyCallbackData;
        private IntPtr SipClientData;
        private uint UIChoice;
        private uint RevocationChecks;
        private uint UnionChoice;
        private IntPtr FileInfo;
        private uint StateAction;
        private IntPtr StateData;
        private IntPtr UrlReference;
        private uint ProviderFlags;
        private uint UIContext;
        private IntPtr SignatureSettings;
    }
}
