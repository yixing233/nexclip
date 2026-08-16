using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SyncClipboard.Desktop.Models;

namespace SyncClipboard.Desktop.Services;

/// <summary>服务器地址格式非法等本地错误。</summary>
public sealed class ApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public ApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// REST 客户端(设计文档 §6 API 契约)。
/// 统一 Bearer 认证(设备凭证);401 抛 ApiException,由 UI 引导重新配对。
/// </summary>
public sealed class ServerApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static Uri Endpoint(string serverUrl, string path) =>
        new(new Uri(serverUrl.TrimEnd('/')), path);

    private static void ApplyAuth(HttpRequestMessage request, string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static async Task<T> SendAsync<T>(
        HttpRequestMessage request, string token, CancellationToken ct = default)
    {
        ApplyAuth(request, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiException("未授权(401):设备未配对或已被移除,请在设置页重新配对", response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException($"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    /// <summary>GET /api/clipboard:获取当前剪贴板。204(空)返回 null。</summary>
    public async Task<ClipboardEntry?> GetCurrentAsync(string serverUrl, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(serverUrl, "/api/clipboard"));
        ApplyAuth(request, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiException("未授权(401):设备未配对或已被移除,请在设置页重新配对", response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClipboardEntry>(cancellationToken: ct);
    }

    /// <summary>PUT /api/clipboard:上传文本。返回新条目;内容未变化(unchanged)返回 null。</summary>
    public async Task<ClipboardEntry?> PutTextAsync(
        string serverUrl, string token, string text,
        string deviceId, string deviceName,
        string? platform = null, string? version = null, CancellationToken ct = default)
    {
        var payload = new { type = "Text", text, deviceId, deviceName, platform, version };
        using var request = new HttpRequestMessage(HttpMethod.Put, Endpoint(serverUrl, "/api/clipboard"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, token, ct);
        if (json.TryGetProperty("unchanged", out var u) && u.GetBoolean())
        {
            return null;
        }
        // 注意:JsonElement.Deserialize 需显式 Web 默认策略(camelCase),否则 Id 等字段映射失败
        return json.Deserialize<ClipboardEntry>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>POST /api/clipboard/image:上传图片(multipart),返回新条目。</summary>
    public async Task<ClipboardEntry?> UploadImageAsync(
        string serverUrl, string token, byte[] pngBytes,
        string deviceId, string deviceName,
        string? platform = null, string? version = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(pngBytes), "file", "clipboard.png");
        form.Add(new StringContent(deviceId), "deviceId");
        form.Add(new StringContent(deviceName), "deviceName");
        if (!string.IsNullOrWhiteSpace(platform)) form.Add(new StringContent(platform), "platform");
        if (!string.IsNullOrWhiteSpace(version)) form.Add(new StringContent(version), "version");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/clipboard/image"))
        {
            Content = form,
        };
        return await SendAsync<ClipboardEntry>(request, token, ct);
    }

    /// <summary>GET /api/images/{ref}:下载图片字节。404 返回 null。</summary>
    public async Task<byte[]?> DownloadImageAsync(
        string serverUrl, string token, string imageRef, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(serverUrl, "/api/images/" + imageRef.TrimStart('/')));
        ApplyAuth(request, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiException("未授权(401):设备未配对或已被移除,请在设置页重新配对", response.StatusCode);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// POST /api/pairing-codes:生成一次性配对码(一码一设备,10 分钟有效)。
    /// 携带本设备信息:服务端生成码的同时登记/更新生成方设备(设备列表可见)。
    /// </summary>
    public async Task<PairingCodeResult?> CreatePairingCodeAsync(
        string serverUrl, string deviceId, string deviceName, CancellationToken ct = default)
    {
        var payload = new { deviceId, deviceName };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/pairing-codes"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, "", ct);
        return json.Deserialize<PairingCodeResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// DELETE /api/pairing-codes/{code}:作废配对码(204 无内容)。
    /// 关闭展示对话框/底部弹层后调用,码立即失效。
    /// </summary>
    public async Task RevokePairingCodeAsync(
        string serverUrl, string code, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, Endpoint(serverUrl, "/api/pairing-codes/" + Uri.EscapeDataString(code)));
        ApplyAuth(request, "");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// POST /api/pair:用一次性配对码换取本设备专属 Token。
    /// 服务端校验配对码(未过期/未使用)后签发 deviceToken,并登记/更新设备。
    /// </summary>
    public async Task<PairResult?> PairAsync(
        string serverUrl, string pairingCode,
        string deviceId, string deviceName, CancellationToken ct = default)
    {
        var payload = new { pairingCode, deviceId, deviceName };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/pair"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, "", ct);
        return json.Deserialize<PairResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>GET /api/devices:设备列表(含在线状态)。失败返回空列表。</summary>
    public async Task<List<DeviceInfo>> GetDevicesAsync(
        string serverUrl, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(serverUrl, "/api/devices"));
        var json = await SendAsync<JsonElement>(request, token, ct);
        return json.Deserialize<List<DeviceInfo>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
    }

    /// <summary>
    /// 注:设备移除/重命名属管理操作,由网页端(账密会话)执行,桌面端不提供。
    /// </summary>

    /// <summary>连接测试:GET 当前剪贴板,任何 200/204 即通过。</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(string serverUrl, string token, CancellationToken ct = default)
    {
        try
        {
            var entry = await GetCurrentAsync(serverUrl, token, ct);
            return (true, entry is null
                ? "连接成功(服务器当前无剪贴板内容)"
                : $"连接成功,服务器当前条目来自 {entry.DeviceName ?? "未知设备"}");
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (false, "认证失败(401):设备未配对或已被移除,请在设置页重新配对");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败:{ex.Message}");
        }
    }
}
