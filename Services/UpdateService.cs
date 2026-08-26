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
    string? ErrorMessage
);

public class UpdateService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
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
                return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, $"HTTP {(int)response.StatusCode}");
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
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, ex.Message);
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
                return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, $"HTTP {(int)response.StatusCode}");
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
                                break;
                            }
                            fallbackExeUrl ??= downloadElem.GetString();
                            assetFileName ??= assetName;
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
                ErrorMessage: null
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, currentVersion, "", "", "", "", null, ex.Message);
        }
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
