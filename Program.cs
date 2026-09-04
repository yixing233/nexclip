using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NexClipServer;

var builder = WebApplication.CreateBuilder(args);

// ---------- 配置 ----------
var options = builder.Configuration.GetSection("AppSettings").Get<AppOptions>() ?? new AppOptions();
var dbPath = Path.GetFullPath(options.DatabasePath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LatencyTracker>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddScoped<ClipboardService>();
builder.Services.AddHostedService<HubHeartbeatService>();

// 自托管/局域网场景放开 CORS(Vite dev 5173 跨端口调试)
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// ---------- 中间件顺序:CORS → 耗时采样 → 鉴权 → 静态 → 路由 ----------
app.UseCors();
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();
    if (ctx.Response.StatusCode < 400 && ctx.Request.Path.Value?.StartsWith("/api/") == true)
        app.Services.GetRequiredService<LatencyTracker>().Add((long)sw.Elapsed.TotalMicroseconds);
});
app.UseMiddleware<TokenAuthMiddleware>();

// 初始化数据库(简单起见用 EnsureCreated,无迁移)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    // EnsureCreated 不做迁移:新增实体属性不会给已存在的库加列,否则老库启动后查询即报 no such column。
    // 这里补一段幂等的原生 SQL,与 Node 版 db.ts 的增量补列行为对齐(列名/类型必须同为 Html / TEXT NULL)。
    var conn = db.Database.GetDbConnection();
    await conn.OpenAsync();
    bool hasHtml;
    await using (var probe = conn.CreateCommand())
    {
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Entries') WHERE \"name\" = 'Html'";
        hasHtml = Convert.ToInt64(await probe.ExecuteScalarAsync()) > 0;
    }
    if (!hasHtml)
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Entries\" ADD COLUMN \"Html\" TEXT NULL");
        app.Logger.LogInformation("数据库补列: Entries.Html");
    }
}

// 静态托管 Web 前端产物(优先 wwwroot,其次 ../web/dist),非 API 路径回退 index.html(SPA)
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var webDist = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "web", "dist"));
string? staticRoot = null;
if (Directory.Exists(wwwroot)) staticRoot = wwwroot;
else if (Directory.Exists(webDist)) staticRoot = webDist;

if (staticRoot is not null)
{
    app.Logger.LogInformation("静态托管: {Root}", staticRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot) });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot) });
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot) });
}

app.MapControllers();
app.MapHub<ClipboardHub>("/hubs/clipboard");

app.Logger.LogInformation("NexClip Server 启动, 端口 {Port}, 令牌已配置: {HasToken}", "5033", !string.IsNullOrEmpty(options.AuthToken) && options.AuthToken != "change-me");
app.Run();
