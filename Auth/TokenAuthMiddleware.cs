namespace SyncClipboardServer;

/// 共享令牌鉴权:所有 /api、/hubs、/SyncClipboard.json 请求需 Authorization: Bearer <token>
/// SignalR 另支持 query access_token(标准做法)
public class TokenAuthMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string _token = config["AppSettings:AuthToken"] ?? "change-me";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var needsAuth = path.StartsWith("/api/") || path.StartsWith("/hubs/") || path.StartsWith("/SyncClipboard.json");
        if (needsAuth)
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ") ? auth["Bearer ".Length..].Trim() : null;
            if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["access_token"].ToString();
            if (string.IsNullOrEmpty(_token) || token != _token)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next(ctx);
    }
}
