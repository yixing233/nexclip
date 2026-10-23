using System.Collections.Concurrent;
using System.Text;

namespace NexClip.Desktop.Services;

/// <summary>
/// 极简文件日志:写 %LOCALAPPDATA%/NexClip/logs/app-{yyyyMMdd}.log。
///
/// 三条纪律(桌面端是开机自启的常驻托盘程序,不设限会无限增长):
/// 1. 按天分卷 + 保留 <see cref="RetentionDays"/> 天 + 单文件上限 <see cref="MaxFileBytes"/>,
///    超限即滚动到 app-{date}.1.log,避免单日刷屏把磁盘写满(实测曾有单日 11.4MB)。
/// 2. 后台线程单消费者写入:原先每次日志都走 File.AppendAllText(开文件→写→关),
///    单次约 260us,而切换/重连类事件往往是成串触发,会直接拖慢 UI。
/// 3. Debug 级别在 Release 构建里整体关闭:这些日志是开发期排查用的,
///    正式包里它们既占空间,又可能带出用户内容(URL、剪贴板片段等)。
/// </summary>
public static class Log
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexClip", "logs");

    /// <summary>日志保留天数。</summary>
    private const int RetentionDays = 7;

    /// <summary>单个日志文件的大小上限(超过后滚动到 .1/.2 后缀)。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>单个日志行(含异常堆栈)的字符上限,防止一条超长异常把文件撑爆。</summary>
    private const int MaxLineChars = 8 * 1024;

    /// <summary>
    /// 是否输出 Debug 级别。Release 构建默认关闭,可通过环境变量 NEXCLIP_LOG_DEBUG=1 强制打开。
    /// 用运行时开关而非 #if:便于让用户临时取证,也不必为 Release 单独维护一份代码。
    /// </summary>
    private static readonly bool DebugEnabled = ComputeDebugEnabled();

    private static bool ComputeDebugEnabled()
    {
#if DEBUG
        return true;
#else
        // 正式包默认不打 Debug;排障时设 NEXCLIP_LOG_DEBUG=1 再启动即可临时打开。
        var flag = Environment.GetEnvironmentVariable("NEXCLIP_LOG_DEBUG");
        return !string.IsNullOrEmpty(flag) && flag is not ("0" or "false" or "False");
#endif
    }

    /// <summary>写入队列:调用方只入队,不碰磁盘。</summary>
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 8192);

    private static readonly object FileLock = new();
    private static readonly Thread Writer;
    private static volatile bool _shutdown;

    static Log()
    {
        Writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "NexClip.LogWriter",
        };
        Writer.Start();
        CleanupOldLogs();
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Debug(string message)
    {
        if (!DebugEnabled) return;
        Write("DEBUG", message, null);
    }

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_shutdown) return;
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {Truncate(message)}";
            if (ex is not null)
            {
                line += Environment.NewLine + Truncate(ex.ToString());
            }
            // 队列满(理论上只在写盘卡死时发生)直接丢弃本条,绝不阻塞调用方线程。
            Queue.TryAdd(line + Environment.NewLine);
        }
        catch
        {
            // 日志失败不致命
        }
    }

    /// <summary>截断过长的单条日志(异常堆栈可能很长),保留尾部更利于定位。</summary>
    private static string Truncate(string value)
    {
        if (value.Length <= MaxLineChars) return value;
        return value[..(MaxLineChars / 2)] + " …[中略]… " + value[^(MaxLineChars / 2)..];
    }

    private static void WriteLoop()
    {
        var buffer = new StringBuilder();
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                // 批量消费:把当前已排队的行攒到一起再落盘,减少开关文件次数
                buffer.Clear();
                buffer.Append(line);
                var budget = 64;
                while (budget-- > 0 && Queue.TryTake(out var more))
                {
                    buffer.Append(more);
                }
                AppendToFile(buffer.ToString());
            }
            catch
            {
                // 单次写盘失败不影响后续日志
            }
        }
    }

    private static void AppendToFile(string content)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = CurrentFilePath();
                if (File.Exists(path) && new FileInfo(path).Length + content.Length > MaxFileBytes)
                {
                    path = NextRollingPath(path);
                }
                File.AppendAllText(path, content, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不致命
            }
        }
    }

    private static string CurrentFilePath() =>
        Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>当前文件已达上限:滚动到 app-{date}.N.log,直到找到未满的那个。</summary>
    private static string NextRollingPath(string basePath)
    {
        var stem = Path.Combine(Dir, Path.GetFileNameWithoutExtension(basePath));
        for (var i = 1; i < 100; i++)
        {
            var candidate = $"{stem}.{i}.log";
            if (!File.Exists(candidate)) return candidate;
            try
            {
                if (new FileInfo(candidate).Length < MaxFileBytes) return candidate;
            }
            catch
            {
                return candidate;
            }
        }
        return $"{stem}.99.log";
    }

    /// <summary>清理超过保留期的日志(含滚动产生的 .N.log)。构造时执行一次。</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(Dir, "app-*.log"))
            {
                try
                {
                    var match = ParseLogDate(Path.GetFileName(file));
                    if (match is { } date && date < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件删不掉(被占用)就跳过
                }
            }
        }
        catch
        {
            // 清理失败不致命
        }
    }

    /// <summary>从 app-yyyyMMdd.log / app-yyyyMMdd.N.log 解析出日期。</summary>
    private static DateTime? ParseLogDate(string fileName)
    {
        // 去掉 "app-" 前缀与 ".log" 后缀,再取第一个 '.' 之前的部分作为日期
        if (!fileName.StartsWith("app-", StringComparison.Ordinal)) return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 12) return null;
        var datePart = stem.AsSpan(4, Math.Min(8, stem.Length - 4));
        return DateTime.TryParseExact(datePart.ToString(), "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>退出前把队列里剩余的日志刷盘。</summary>
    public static void Flush()
    {
        try
        {
            for (var i = 0; i < 200 && Queue.Count > 0; i++)
            {
                Thread.Sleep(10);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>停止写入线程(应用退出时调用)。</summary>
    public static void Shutdown()
    {
        _shutdown = true;
        try { Queue.CompleteAdding(); } catch { /* 已停止 */ }
        try { Writer.Join(TimeSpan.FromSeconds(2)); } catch { /* 忽略 */ }
    }
}
