using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using NexClip.Desktop.Models;

namespace NexClip.Desktop.Services;

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
/// 设备同步请求使用 X-Device-Id/X-Device-Token;401/410 抛 ApiException,由 UI 引导重新配对。
/// </summary>
public sealed class ServerApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static Uri Endpoint(string serverUrl, string path) =>
        new(new Uri(serverUrl.TrimEnd('/')), path);

    private static void ApplyDeviceAuth(HttpRequestMessage request, string deviceId, string token)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
            request.Headers.TryAddWithoutValidation("X-Device-Token", token);
        }
    }

    /// <summary>把 .NET/网络异常转换为用户可理解的中文原因；原始异常仍由调用方写入日志。</summary>
    public static string DescribeException(Exception ex, string fallback = "请求失败")
    {
        while (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
        {
            ex = aggregate.InnerExceptions[0];
        }

        return ex switch
        {
            ApiException api => api.Message,
            UriFormatException => "服务器地址格式不正确，请填写包含协议和端口的有效地址，例如 http://127.0.0.1:5033。",
            TaskCanceledException => "服务器响应超时，请确认服务器正在运行，并检查当前网络连接。",
            HttpRequestException { InnerException: SocketException socket } => DescribeSocketError(socket.SocketErrorCode),
            HttpRequestException http when http.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || http.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                => "与服务器建立安全连接失败，请检查协议（http/https）和服务器证书。",
            HttpRequestException => "无法连接到服务器，请检查服务器地址、端口、网络和防火墙设置。",
            JsonException => "服务器返回的数据格式无效，请确认服务端版本与客户端兼容。",
            IOException => "本地文件读写失败，请检查储存目录权限和磁盘空间。",
            UnauthorizedAccessException => "没有权限完成此操作，请检查文件夹权限或使用其他储存位置。",
            _ => fallback,
        };
    }

    private static string DescribeSocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "无法连接到服务器：目标端口未开放或服务器尚未启动。",
        SocketError.HostNotFound => "无法找到服务器主机，请检查服务器地址和 DNS 设置。",
        SocketError.TimedOut => "连接服务器超时，请检查服务器是否在线以及网络/防火墙设置。",
        _ => "无法连接到服务器，请检查服务器地址、端口、网络和防火墙设置。",
    };

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await ReadErrorDetailAsync(response, ct);
        throw new ApiException(detail, response.StatusCode);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return "服务器拒绝请求：设备未配对、配对已失效或设备已被移除，请重新完成配对。";
        }

        string? body = null;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { }
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    return $"服务器拒绝请求：{error.GetString()}";
                }
            }
            catch (JsonException) { }
        }

        var statusMessage = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "请求参数无效",
            HttpStatusCode.Forbidden => "没有权限执行此操作",
            HttpStatusCode.NotFound => "服务器接口不存在，请确认服务端版本",
            HttpStatusCode.Conflict => "请求与服务器当前状态冲突",
            (HttpStatusCode)410 => "设备已被移除，请重新完成配对",
            HttpStatusCode.RequestEntityTooLarge => "请求内容超过服务器允许的大小",
            (HttpStatusCode)429 => "请求过于频繁，请稍后重试",
            >= HttpStatusCode.InternalServerError => "服务器内部发生错误",
            _ => $"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
        };
        return $"服务器请求失败：{statusMessage}。";
    }

    private static async Task<T> SendAsync<T>(
        HttpRequestMessage request, string deviceId, string token, CancellationToken ct = default)
    {
        ApplyDeviceAuth(request, deviceId, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static async Task SendNoContentAsync(HttpRequestMessage request, string deviceId, string token, CancellationToken ct = default)
    {
        ApplyDeviceAuth(request, deviceId, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>GET /api/clipboard:获取当前剪贴板。204(空)返回 null。</summary>
    public async Task<ClipboardEntry?> GetCurrentAsync(string serverUrl, string deviceId, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(serverUrl, "/api/clipboard"));
        ApplyDeviceAuth(request, deviceId, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClipboardEntry>(cancellationToken: ct);
    }

    /// <summary>PUT /api/clipboard:上传文本。返回新条目;内容未变化(unchanged)返回 null。</summary>
    public async Task<ClipboardEntry?> PutTextAsync(
        string serverUrl, string token, string text,
        string deviceId, string deviceName,
        string? platform = null, string? version = null, bool isManual = false, CancellationToken ct = default)
    {
        var payload = new { type = "Text", text, deviceId, deviceName, platform, version, isManual };
        using var request = new HttpRequestMessage(HttpMethod.Put, Endpoint(serverUrl, "/api/clipboard"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, deviceId, token, ct);
        if (json.TryGetProperty("unchanged", out var u) && u.GetBoolean())
        {
            return null;
        }
        // 注意:JsonElement.Deserialize 需显式 Web 默认策略(camelCase),否则 Id 等字段映射失败
        return json.Deserialize<ClipboardEntry>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>POST /api/clipboard/send: 发送文本给指定的一批目标设备。</summary>
    public async Task<ClipboardEntry?> SendToDevicesAsync(
        string serverUrl, string token, string text, string deviceId, string deviceName, string[] targetDeviceIds, CancellationToken ct = default)
    {
        var payload = new
        {
            text,
            deviceId,
            deviceName,
            deviceIds = targetDeviceIds,
            isManual = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/clipboard/send"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, deviceId, token, ct);
        return json.Deserialize<ClipboardEntry>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>POST /api/clipboard/image:上传图片(multipart),返回新条目。</summary>
    public async Task<ClipboardEntry?> UploadImageAsync(
        string serverUrl, string token, byte[] pngBytes,
        string deviceId, string deviceName,
        string? platform = null, string? version = null, bool isManual = false, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(pngBytes), "file", "clipboard.png");
        form.Add(new StringContent(deviceId), "deviceId");
        form.Add(new StringContent(deviceName), "deviceName");
        if (isManual) form.Add(new StringContent("true"), "isManual");
        if (!string.IsNullOrWhiteSpace(platform)) form.Add(new StringContent(platform), "platform");
        if (!string.IsNullOrWhiteSpace(version)) form.Add(new StringContent(version), "version");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/clipboard/image"))
        {
            Content = form,
        };
        return await SendAsync<ClipboardEntry>(request, deviceId, token, ct);
    }

    /// <summary>GET /api/images/{ref}:下载图片字节。404 返回 null。</summary>
    public async Task<byte[]?> DownloadImageAsync(
        string serverUrl, string deviceId, string token, string imageRef, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(serverUrl, "/api/images/" + imageRef.TrimStart('/')));
        ApplyDeviceAuth(request, deviceId, token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// POST /api/pairing-codes:生成一次性配对码(一码一设备,10 分钟有效)。
    /// 携带本设备信息:服务端生成码的同时登记/更新生成方设备(设备列表可见)。
    /// </summary>
    public async Task<PairingCodeResult?> CreatePairingCodeAsync(
        string serverUrl, string deviceId, string deviceName, string deviceToken = "", CancellationToken ct = default)
    {
        var payload = new { deviceId, deviceName };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/pairing-codes"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, deviceId, deviceToken, ct);
        return json.Deserialize<PairingCodeResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>
    /// DELETE /api/pairing-codes/{code}:作废配对码(204 无内容)。
    /// 关闭展示对话框/底部弹层后调用,码立即失效。
    /// </summary>
    public async Task RevokePairingCodeAsync(
        string serverUrl, string code, string deviceId = "", string deviceToken = "", CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Delete, Endpoint(serverUrl, "/api/pairing-codes/" + Uri.EscapeDataString(code)));
            ApplyDeviceAuth(request, deviceId, deviceToken);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            await EnsureSuccessAsync(response, ct);
        }
        catch (Exception ex)
        {
            Log.Debug($"作废配对码静默处理: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /api/pair: 6 位纯数字验证码 / 扫码单向即入配对 (无需二次确认)。
    /// </summary>
    public async Task<PairResult> PairAsync(
        string serverUrl, string code,
        string deviceId, string deviceName, string platform = "Windows", CancellationToken ct = default)
    {
        var payload = new { code = code.Trim(), deviceId, deviceName, platform };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(serverUrl, "/api/pair"))
        {
            Content = JsonContent.Create(payload),
        };
        var json = await SendAsync<JsonElement>(request, "", "", ct);
        return new PairResult
        {
            DeviceId = deviceId,
            DeviceToken = json.TryGetProperty("deviceToken", out var token) ? token.GetString() ?? "" : "",
            Status = json.TryGetProperty("status", out var status) ? status.GetString() ?? "approved" : "approved",
            UserId = json.TryGetProperty("userId", out var uid) ? uid.GetString() ?? "" : "",
        };
    }

    /// <summary>别名</summary>
    public Task<PairResult> PairDirectAsync(
        string serverUrl, string code,
        string deviceId, string deviceName, string platform = "Windows", CancellationToken ct = default)
        => PairAsync(serverUrl, code, deviceId, deviceName, platform, ct);

    /// <summary>GET /api/devices:设备列表(含在线状态)。失败返回空列表。</summary>
    public async Task<List<DeviceInfo>> GetDevicesAsync(
        string serverUrl, string deviceId, string token, CancellationToken ct = default)
    {
        try
        {
            var url = Endpoint(serverUrl, $"/api/devices?deviceId={Uri.EscapeDataString(deviceId)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var json = await SendAsync<JsonElement>(request, deviceId, token, ct);
            return json.Deserialize<List<DeviceInfo>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == (HttpStatusCode)410)
        {
            return new List<DeviceInfo>();
        }
    }

    /// <summary>DELETE /api/devices/{id}: 移除/注销指定设备。</summary>
    public async Task RemoveDeviceAsync(
        string serverUrl, string targetDeviceId, string actorDeviceId, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint(serverUrl, $"/api/devices/{Uri.EscapeDataString(targetDeviceId)}"));
        await SendNoContentAsync(request, actorDeviceId, token, ct);
    }

    /// <summary>PUT /api/devices/{id}: 修改指定设备名称。</summary>
    public async Task RenameDeviceAsync(
        string serverUrl, string targetDeviceId, string newName, string actorDeviceId, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Endpoint(serverUrl, $"/api/devices/{Uri.EscapeDataString(targetDeviceId)}"))
        {
            Content = JsonContent.Create(new { name = newName })
        };
        await SendNoContentAsync(request, actorDeviceId, token, ct);
    }

    /// <summary>连接测试：检测服务器地址有效性、可达性与网络延迟。</summary>
    public async Task<(bool Ok, string Message)> TestConnectionAsync(
        string serverUrl, string deviceId = "", string token = "", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return (false, "请先输入服务器地址。");
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var healthUrl = Endpoint(serverUrl, "/api/health");
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                return (true, $"连接成功！服务器响应正常（延迟 {sw.ElapsedMilliseconds}ms）");
            }

            // 若 /api/health 返回其他状态 (如老版本或自定义前缀)，只要收到 HTTP 响应即证明地址有效且服务在线
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.NoContent)
            {
                return (true, $"连接成功！服务器已响应（延迟 {sw.ElapsedMilliseconds}ms）");
            }

            return (false, $"服务器返回异常状态码：{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, DescribeException(ex, "无法连接到服务器，请检查服务器地址、端口或网络。"));
        }
    }
}
