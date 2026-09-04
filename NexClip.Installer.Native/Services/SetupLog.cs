using System.Text;

namespace NexClip.Installer.Native.Services;

/// <summary>
/// 安装器诊断日志。独立于依赖清单加载，确保清单解析失败等早期错误也能落盘取证。
/// </summary>
internal static class SetupLog
{
    /// <summary>并行检测与下载会同时写日志，加锁避免共享冲突导致日志行丢失。</summary>
    private static readonly object WriteGate = new();

    internal static string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "NexClip-Setup", "logs");

    internal static string FilePath { get; } = Path.Combine(Directory, "dependency-setup.log");

    internal static void Write(string message)
    {
        try
        {
            lock (WriteGate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}