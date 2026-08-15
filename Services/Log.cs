namespace SyncClipboard.Desktop.Services;

/// <summary>极简文件日志:写 %LOCALAPPDATA%/SyncClipboard/logs/app-{yyyyMMdd}.log。</summary>
public static class Log
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncClipboard", "logs");

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Warn(string message)
    {
        Write("WARN", message, null);
    }

    public static void Debug(string message)
    {
        Write("DEBUG", message, null);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}" +
                       (ex is null ? "" : $"{Environment.NewLine}{ex}");
            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch
        {
            // 日志失败不致命
        }
    }
}
