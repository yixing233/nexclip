namespace NexClipServer;

/// 共享令牌鉴权:所有 /api、/hubs、/SyncClipboard.json 请求需鉴权
/// 支持 Authorization: Bearer <token>、X-Device-Token、X-Auth-Token 标头以及 query 参数 (access_token/deviceToken/token)
public class TokenAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string _token = config["AppSettings:AuthToken"] ?? "change-me";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var needsAuth = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("/SyncClipboard.json", StringComparison.OrdinalIgnoreCase);

        // 开放免密白名单
        if (path.StartsWith("/api/pair", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/pairing-codes", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/stats", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (needsAuth)
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Headers["X-Auth-Token"].ToString();
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Headers["X-Device-Token"].ToString();
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Headers["token"].ToString();
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["access_token"].ToString();
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["deviceToken"].ToString();
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["token"].ToString();

            // 如果服务端配置了非默认 AuthToken，进行比对
            if (!string.IsNullOrEmpty(_token) && _token != "change-me" && !string.Equals(token, _token, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next(ctx);
    }
}
