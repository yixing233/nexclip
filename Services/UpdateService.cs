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
                return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, null, null, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" :
                          (root.TryGetProperty("version", out var verElem) ? verElem.GetString() ?? "" : "");
            var cleanLatest = tagName.TrimStart('v', 'V').Trim();
            var cleanCurrent = currentVersion.TrimStart('v', 'V').Trim();

            var title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : $"NexClip v{cleanLatest}";
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? "https://github.com/yixing233/nexclip/releases" : "https://github.com/yixing233/nexclip/releases";

            string? downloadUrl = null;
            string? sha256 = null;
            long? fileSize = null;

            if (root.TryGetProperty("windows", out var winElem) && winElem.ValueKind == JsonValueKind.Object)
            {
                if (winElem.TryGetProperty("url", out var winUrlElem))
                {
                    downloadUrl = winUrlElem.GetString();
                }
                else if (winElem.TryGetProperty("filename", out var fnElem))
                {
                    downloadUrl = $"{baseUrl}/{fnElem.GetString()}";
                }

                if (winElem.TryGetProperty("sha256", out var shaValElem))
                {
                    sha256 = shaValElem.GetString();
                }

                if (winElem.TryGetProperty("size", out var szElem) && szElem.TryGetInt64(out var szVal))
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
            return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, null, null, ex.Message);
        }
    }

    private async Task<UpdateCheckResult> CheckGitHubAsync(string currentVersion, bool useDirectDownload)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yixing233/nexclip/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NexClip-Windows", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, null, null, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            var cleanLatest = tagName.TrimStart('v', 'V').Trim();
            var cleanCurrent = currentVersion.TrimStart('v', 'V').Trim();

            var title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? "https://github.com/yixing233/nexclip/releases" : "https://github.com/yixing233/nexclip/releases";

            string? downloadUrl = null;
            string? assetFileName = null;
            string? fallbackExeUrl = null;
            long? fileSize = null;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var assetNameElem) &&
                        asset.TryGetProperty("browser_download_url", out var downloadElem))
                    {
                        var assetName = assetNameElem.GetString() ?? "";
                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                                assetName.Contains("Installer", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = downloadElem.GetString();
                                assetFileName = assetName;
                                if (asset.TryGetProperty("size", out var szElem) && szElem.TryGetInt64(out var szVal))
                                {
                                    fileSize = szVal;
                                }
                                break;
                            }
                            fallbackExeUrl ??= downloadElem.GetString();
                            assetFileName ??= assetName;
                            if (asset.TryGetProperty("size", out var fbSzElem) && fbSzElem.TryGetInt64(out var fbSzVal))
                            {
                                fileSize = fbSzVal;
                            }
                        }
                    }
                }
                downloadUrl ??= fallbackExeUrl;
            }

            // 如果指定了直连加速，将 GitHub 下载链接重定向到服务端直连
            if (useDirectDownload && !string.IsNullOrWhiteSpace(assetFileName))
            {
                downloadUrl = $"{ServerDirectBaseUrl}/{assetFileName}";
            }

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
                Sha256: null,
                FileSize: fileSize,
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, null, null, ex.Message);
        }
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
