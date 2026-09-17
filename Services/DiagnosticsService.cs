using System.Diagnostics;
using System.Text;
using NexClip.Desktop.ViewModels;

namespace NexClip.Desktop.Services;

/// <summary>
/// 诊断报告:汇总剪贴板监听状态、最近日志、剪贴板所有者与进程线程摘要。
/// 报告只包含本机运行状态,不包含剪贴板正文或历史内容。
/// </summary>
public static class DiagnosticsService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexClip", "logs");

    public static async Task<string> ExportAsync(string path, AppServices services)
    {
        var report = BuildReport(services);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));
        return report;
    }

    public static string BuildReport(AppServices services)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NexClip 诊断报告");
        sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"应用版本: {GetVersion()}");
        sb.AppendLine();

        AppendClipboard(sb, services);
        AppendConnection(sb, services);
        AppendProcess(sb, services);
        AppendRecentLogs(sb);
        return sb.ToString();
    }

    private static void AppendClipboard(StringBuilder sb, AppServices services)
    {
        sb.AppendLine("== 剪贴板监听 ==");
        var monitor = services.Engine?.Monitor;
        if (monitor is null)
        {
            sb.AppendLine("监听器: 未启动");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"监听开关: {services.Settings.MonitorEnabled}");
        sb.AppendLine($"监听状态: {monitor.CaptureStage}");
        sb.AppendLine($"捕获中: {monitor.IsCapturing}");
        sb.AppendLine($"当前捕获耗时: {monitor.CurrentCaptureElapsedMs:F0} ms");
        sb.AppendLine($"最后成功: {FormatUtc(monitor.LastCaptureSuccessUtc)}");
        sb.AppendLine($"最后失败: {FormatUtc(monitor.LastCaptureFailureUtc)}");
        sb.AppendLine($"失败次数: {monitor.CaptureFailureCount}");
        sb.AppendLine($"看门狗恢复次数: {monitor.WatchdogRecoveryCount}");
        sb.AppendLine($"剪贴板所有者: {SourceAppDetector.TryGetClipboardOwnerProcessName() ?? "未知"} (PID {GetClipboardOwnerPid()})");
        sb.AppendLine($"剪贴板可打开: {NativeMethods.TryProbeClipboard()}");
        if (monitor.IsClipboardBlocked)
        {
            sb.AppendLine($"剪贴板熔断: 已触发(累计 {monitor.ClipboardBlockedCount} 次)");
        }
        sb.AppendLine();
    }

    private static void AppendConnection(StringBuilder sb, AppServices services)
    {
        var s = services.Settings;
        sb.AppendLine("== 同步连接 ==");
        sb.AppendLine($"服务器: {MaskUrl(s.ServerUrl)}");
        sb.AppendLine($"已配对: {s.IsPaired}");
        sb.AppendLine($"设备 ID: {Shorten(s.DeviceId)}");
        sb.AppendLine($"设备名称: {s.DeviceName}");
        sb.AppendLine($"连接状态: {services.Engine?.State.ToString() ?? "未创建"}");
        sb.AppendLine();
    }

    private static void AppendProcess(StringBuilder sb, AppServices services)
    {
        sb.AppendLine("== 进程与线程 ==");
        try
        {
            using var process = Process.GetCurrentProcess();
            sb.AppendLine($"PID: {process.Id}");
            sb.AppendLine($"工作集: {process.WorkingSet64 / 1024 / 1024} MB");
            sb.AppendLine($"线程数: {process.Threads.Count}");
            sb.AppendLine($"启动时间: {process.StartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("主要线程:");

            var threads = process.Threads
                .Cast<ProcessThread>()
                .OrderByDescending(t => t.TotalProcessorTime)
                .Take(12);
            foreach (var thread in threads)
            {
                string state;
                try { state = thread.ThreadState.ToString(); }
                catch { state = "unknown"; }
                sb.AppendLine($"  #{thread.Id} {state} CPU={thread.TotalProcessorTime.TotalMilliseconds:F0}ms");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取进程信息失败: {ex.Message}");
        }
        sb.AppendLine();
    }

    private static void AppendRecentLogs(StringBuilder sb)
    {
        sb.AppendLine("== 最近日志 ==");
        try
        {
            var file = Directory.Exists(LogDir)
                ? Directory.GetFiles(LogDir, "app-*.log").OrderByDescending(f => f).FirstOrDefault()
                : null;
            if (file is null)
            {
                sb.AppendLine("没有可用日志");
                return;
            }

            sb.AppendLine($"日志文件: {file}");
            var lines = File.ReadLines(file).TakeLast(200);
            foreach (var line in lines) sb.AppendLine(line);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取日志失败: {ex.Message}");
        }
    }

    private static string GetVersion() =>
        (System.Attribute.GetCustomAttribute(typeof(App).Assembly,
            typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? "unknown";

    private static uint GetClipboardOwnerPid()
    {
        try { return SourceAppDetector.CaptureOwnerHandle()?.Pid ?? 0; }
        catch { return 0; }
    }

    private static string FormatUtc(DateTime? value) =>
        value is { } dt ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") : "从未";

    private static string Shorten(string value) =>
        string.IsNullOrEmpty(value) ? "(空)" : value.Length <= 12 ? value : value[..12] + "…";

    private static string MaskUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(空)";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}" + (uri.IsDefaultPort ? "" : $":{uri.Port}")
            : "(无效地址)";
    }
}
