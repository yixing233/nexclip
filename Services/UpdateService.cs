using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NexClip.Desktop.Services;

public record UpdateCheckResult(
    bool Success,
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleaseUrl,
    string? DownloadUrl,
    string? Sha256 = null,
    long? FileSize = null,
    string? ErrorMessage = null
);

public record UpdateProgressInfo(
    long BytesRead,
    long TotalBytes,
    double ProgressPercentage,
    double SpeedBytesPerSecond,
    string FormattedSpeed,
    string FormattedProgress
);

public class UpdateService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public const string ServerDirectBaseUrl = "https://nexclip.157342.xyz/releases";

    private const string GitHubReleasesApi = "https://api.github.com/repos/yixing233/nexclip/releases";
    private const string DefaultReleasesPage = "https://github.com/yixing233/nexclip/releases";

    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, string updateSource = "github", string? customServerUrl = null)
    {
        bool isDirect = string.Equals(updateSource, "direct", StringComparison.OrdinalIgnoreCase);

        // 如果选择服务器直连加速，优先查询服务端 version.json
        if (isDirect)
        {
            var directResult = await CheckServerDirectAsync(currentVersion, customServerUrl);
            if (directResult.Success)
            {
                return directResult;
            }
            Log.Warn($"服务端直连更新检查失败({directResult.ErrorMessage})，回退尝试 GitHub 源");
        }

        // GitHub 官方源检查
        var ghResult = await CheckGitHubAsync(currentVersion, isDirect);
        if (ghResult.Success)
        {
            return ghResult;
        }

        // 如果 GitHub 源失败且之前未尝试直连，尝试直连源降级
        if (!isDirect)
        {
            Log.Warn($"GitHub 更新检查失败({ghResult.ErrorMessage})，尝试服务端直连源降级");
            var fallbackDirect = await CheckServerDirectAsync(currentVersion, customServerUrl);
            if (fallbackDirect.Success)
            {
                return fallbackDirect;
            }
        }

        return ghResult;
    }

    private async Task<UpdateCheckResult> CheckServerDirectAsync(string currentVersion, string? customServerUrl)
    {
        try
        {
            var baseUrl = ServerDirectBaseUrl;
            if (!string.IsNullOrWhiteSpace(customServerUrl) && Uri.TryCreate(customServerUrl.Trim(), UriKind.Absolute, out var serverUri))
            {
                baseUrl = $"{serverUri.Scheme}://{serverUri.Authority}/releases";
            }

            var url = $"{baseUrl}/version.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NexClip-Windows", "1.0"));

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(currentVersion, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // windows 段优先，缺字段才回落顶层：顶层版本号是两端共用的，
            // 只发布 Android 的版本不应该把桌面端也顶成"有新版本"。
            JsonElement? platform =
                root.TryGetProperty("windows", out var winElem) && winElem.ValueKind == JsonValueKind.Object
                    ? winElem
                    : null;

            var tagName = ReadString(platform, root, "tag_name", "version") ?? "";
            var cleanLatest = tagName.TrimStart('v', 'V').Trim();
            var cleanCurrent = currentVersion.TrimStart('v', 'V').Trim();

            var title = ReadString(platform, root, "name") ?? $"NexClip v{cleanLatest}";
            var body = ReadString(platform, root, "body") ?? "";
            var htmlUrl = ReadString(platform, root, "html_url") ?? DefaultReleasesPage;

            string? downloadUrl = null;
            string? sha256 = null;
            long? fileSize = null;

            if (platform is { } windows)
            {
                downloadUrl = ReadString(windows, "url");
                if (downloadUrl is null && ReadString(windows, "filename") is { } fileName)
                {
                    downloadUrl = $"{baseUrl}/{fileName}";
                }

                sha256 = ReadString(windows, "sha256");

                if (windows.TryGetProperty("size", out var szElem) && szElem.TryGetInt64(out var szVal))
                {
                    fileSize = szVal;
                }
            }
            downloadUrl ??= $"{baseUrl}/NexClip_Setup_v{cleanLatest}_x64.exe";

            bool hasUpdate = CompareVersions(cleanLatest, cleanCurrent) > 0;

            return new UpdateCheckResult(
                Success: true,
                HasUpdate: hasUpdate,
                CurrentVersion: currentVersion,
                LatestVersion: cleanLatest,
                ReleaseTitle: title,
                ReleaseNotes: body,
                ReleaseUrl: htmlUrl,
                DownloadUrl: downloadUrl,
                Sha256: sha256,
                FileSize: fileSize,
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            return Failure(currentVersion, ex.Message);
        }
    }

    private static UpdateCheckResult Failure(string currentVersion, string error) =>
        new(false, false, currentVersion, "", "", "", "", null, null, null, error);

    /// <summary>取非空字符串字段；空串与空白视为缺失，好让平台段里的占位值自动回落。</summary>
    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>先在平台段里按顺序找，再在顶层按同样顺序找。</summary>
    private static string? ReadString(JsonElement? platform, JsonElement root, params string[] names)
    {
        if (platform is { } element)
        {
            foreach (var name in names)
            {
                if (ReadString(element, name) is { } value) return value;
            }
        }

        foreach (var name in names)
        {
            if (ReadString(root, name) is { } value) return value;
        }

        return null;
    }

    /// <summary>
    /// GitHub 通道。先在发布列表里找"最新一个挂了 Windows 安装包的发布"，而不是直接用 /releases/latest：
    /// 后者是两端共用的单一指针，只发 Android 的版本会把它顶走，
    /// 桌面端照它比版本号就会提示一个根本没有 exe 可下的新版本。
    /// </summary>
    private async Task<UpdateCheckResult> CheckGitHubAsync(string currentVersion, bool useDirectDownload)
    {
        try
        {
            var (list, listError) = await ReadGitHubJsonAsync($"{GitHubReleasesApi}?per_page=20");
            if (list is not null)
            {
                using (list)
                {
                    if (list.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var release in list.RootElement.EnumerateArray())
                        {
                            if (IsDraft(release)) continue;

                            var candidate = BuildGitHubResult(release, currentVersion, useDirectDownload, out var hasInstaller);
                            if (hasInstaller) return candidate;
                        }
                    }
                }
            }

            // 列表不可用（限流/网络/返回结构变化）就退回旧路径，行为与改动前一致
            var (latest, latestError) = await ReadGitHubJsonAsync($"{GitHubReleasesApi}/latest");
            if (latest is null)
            {
                return Failure(currentVersion, latestError ?? listError ?? "无法获取发布信息");
            }

            using (latest)
            {
                return BuildGitHubResult(latest.RootElement, currentVersion, useDirectDownload, out _);
            }
        }
        catch (Exception ex)
        {
            return Failure(currentVersion, ex.Message);
        }
    }

    private static async Task<(JsonDocument? Document, string? Error)> ReadGitHubJsonAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NexClip-Windows", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(json), null);
    }

    private static bool IsDraft(JsonElement release) =>
        release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 解析单个 GitHub release。<paramref name="hasInstaller"/> 表示它是否真的挂了 Windows 安装包，
    /// 调用方靠它跳过"只发 Android"的版本。
    /// </summary>
    private static UpdateCheckResult BuildGitHubResult(
        JsonElement release, string currentVersion, bool useDirectDownload, out bool hasInstaller)
    {
        var tagName = ReadString(release, "tag_name") ?? "";
        var cleanLatest = tagName.TrimStart('v', 'V').Trim();
        var cleanCurrent = currentVersion.TrimStart('v', 'V').Trim();

        var title = ReadString(release, "name") ?? "";
        var body = ReadString(release, "body") ?? "";
        var htmlUrl = ReadString(release, "html_url") ?? DefaultReleasesPage;

        string? downloadUrl = null;
        string? assetFileName = null;
        long? fileSize = null;
        string? fallbackUrl = null;
        string? fallbackName = null;
        long? fallbackSize = null;

        if (release.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElem.EnumerateArray())
            {
                var assetName = ReadString(asset, "name");
                var assetUrl = ReadString(asset, "browser_download_url");
                if (assetName is null || assetUrl is null) continue;
                if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var size = asset.TryGetProperty("size", out var szElem) && szElem.TryGetInt64(out var szVal)
                    ? szVal
                    : (long?)null;

                if (assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                    assetName.Contains("Installer", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = assetUrl;
                    assetFileName = assetName;
                    fileSize = size;
                    break;
                }

                fallbackUrl ??= assetUrl;
                fallbackName ??= assetName;
                fallbackSize ??= size;
            }

            if (downloadUrl is null)
            {
                downloadUrl = fallbackUrl;
                assetFileName = fallbackName;
                fileSize = fallbackSize;
            }
        }

        hasInstaller = downloadUrl is not null;

        // 指定了直连加速，就把 GitHub 下载链接换成服务端上的同名文件
        if (useDirectDownload && !string.IsNullOrWhiteSpace(assetFileName))
        {
            downloadUrl = $"{ServerDirectBaseUrl}/{assetFileName}";
        }

        return new UpdateCheckResult(
            Success: true,
            HasUpdate: CompareVersions(cleanLatest, cleanCurrent) > 0,
            CurrentVersion: currentVersion,
            LatestVersion: cleanLatest,
            ReleaseTitle: title,
            ReleaseNotes: body,
            ReleaseUrl: htmlUrl,
            DownloadUrl: downloadUrl,
            Sha256: null,
            FileSize: fileSize,
            ErrorMessage: null
        );
    }

    /// <summary>
    /// 流式分块下载安装包，实时汇报速率与百分比，并执行完整性校验。
    /// </summary>
    public async Task<string> DownloadUpdateAsync(
        string downloadUrl,
        string targetVersion,
        string? expectedSha256,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NexClip_Update");
        Directory.CreateDirectory(tempDir);
        var finalInstallerPath = Path.Combine(tempDir, $"NexClip_Setup_v{targetVersion}_x64.exe");
        var tempDownloadPath = finalInstallerPath + ".download";

        // 1. 如果已存在且哈希校验匹配，无需重复下载
        if (File.Exists(finalInstallerPath))
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || VerifySha256(finalInstallerPath, expectedSha256))
            {
                var len = new FileInfo(finalInstallerPath).Length;
                progress?.Report(new UpdateProgressInfo(len, len, 100.0, 0, "", $"{FormatBytes(len)} / {FormatBytes(len)}"));
                return finalInstallerPath;
            }
            else
            {
                try { File.Delete(finalInstallerPath); } catch { }
            }
        }

        if (File.Exists(tempDownloadPath))
        {
            try { File.Delete(tempDownloadPath); } catch { }
        }

        // 2. 发起流式网络请求
        using var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NexClip-Windows", "1.0"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportBytes = 0;
        var lastReportTime = stopwatch.ElapsedMilliseconds;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (bytesRead <= 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            var now = stopwatch.ElapsedMilliseconds;
            if (now - lastReportTime >= 250 || (totalBytes > 0 && totalRead == totalBytes))
            {
                var timeDiff = (now - lastReportTime) / 1000.0;
                var bytesDiff = totalRead - lastReportBytes;
                var speed = timeDiff > 0 ? (bytesDiff / timeDiff) : 0;
                var pct = totalBytes > 0 ? Math.Min(100.0, (double)totalRead / totalBytes * 100.0) : 0;

                progress?.Report(new UpdateProgressInfo(
                    BytesRead: totalRead,
                    TotalBytes: totalBytes,
                    ProgressPercentage: pct,
                    SpeedBytesPerSecond: speed,
                    FormattedSpeed: FormatSpeed(speed),
                    FormattedProgress: totalBytes > 0 ? $"{FormatBytes(totalRead)} / {FormatBytes(totalBytes)}" : FormatBytes(totalRead)
                ));

                lastReportTime = now;
                lastReportBytes = totalRead;
            }
        }

        await fileStream.FlushAsync(cancellationToken);
        fileStream.Close();

        // 3. 校验 SHA256 (若提供)
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (!VerifySha256(tempDownloadPath, expectedSha256))
            {
                try { File.Delete(tempDownloadPath); } catch { }
                throw new InvalidOperationException("安装包 SHA256 校验失败，文件可能在传输中损坏，请重试。");
            }
        }

        // 4. 重命名为正式安装包路径
        if (File.Exists(finalInstallerPath))
        {
            try { File.Delete(finalInstallerPath); } catch { }
        }
        File.Move(tempDownloadPath, finalInstallerPath);

        return finalInstallerPath;
    }

    public static bool VerifySha256(string filePath, string expectedSha256)
    {
        try
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            var actualHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            return string.Equals(actualHex, expectedSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "未知大小";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{(bytesPerSec / 1024.0):F1} KB/s";
        return $"{(bytesPerSec / (1024.0 * 1024.0)):F2} MB/s";
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("安装包不存在", installerPath);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            // 必须显式指定工作目录：不给的话 ShellExecuteEx 拿到的 lpDirectory 是 NULL，
            // 安装器会继承本进程的当前目录（快捷方式把它设成了安装目录）。
            // 进程的当前目录句柄不带 FILE_SHARE_DELETE，安装器随后就没法给安装目录改名，
            // 覆盖更新会直接报“文件正由另一进程使用”。
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
        };
        System.Diagnostics.Process.Start(psi);

        // 退出当前应用，释放资源以便安装程序无缝覆盖更新
        App.Current.Exit();
    }

    public static int CompareVersions(string v1, string v2)
    {
        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase)) return 0;

        var parts1 = v1.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var parts2 = v2.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        int len = Math.Max(parts1.Length, parts2.Length);
        for (int i = 0; i < len; i++)
        {
            long num1 = 0, num2 = 0;
            bool isNum1 = i < parts1.Length && long.TryParse(parts1[i], out num1);
            bool isNum2 = i < parts2.Length && long.TryParse(parts2[i], out num2);

            if (isNum1 && isNum2)
            {
                if (num1 != num2) return num1.CompareTo(num2);
            }
            else
            {
                var s1 = i < parts1.Length ? parts1[i] : "";
                var s2 = i < parts2.Length ? parts2[i] : "";
                int cmp = string.Compare(s1, s2, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }
}
