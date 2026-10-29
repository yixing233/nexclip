namespace NexClip.Tray;

/// <summary>
/// 来源应用归类的纯逻辑，便于单元测试。
///
/// <para><b>为什么单独抽出来</b>：WebView2 宿主识别是"容易写错、错了也不报错"的典型逻辑，
/// 但完整实现依赖 Win32 句柄与进程查询，无法在纯单元测试里构造。
/// 这里只保留**判定规则**（某个进程名是不是 WebView2、能否作为宿主），
/// 真正的进程遍历留在桌面端的 SourceAppDetector 里。</para>
/// </summary>
public static class SourceAppClassifier
{
    /// <summary>WebView2 浏览器内核进程名（不带 .exe）。</summary>
    public const string WebView2ProcessName = "msedgewebview2";

    /// <summary>
    /// 是否是 WebView2 内核进程。这类进程绝不能被当作"来源应用"展示给用户 ——
    /// 用户界面上根本没有叫 "Microsoft Edge WebView2" 的程序。
    /// </summary>
    public static bool IsWebView2Process(string? processName) =>
        string.Equals(NormalizeName(processName), WebView2ProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该进程能否作为 WebView2 的宿主（即真实来源应用）。
    /// 除内核自身外，两种系统级空壳也要排除：
    /// <c>System</c> / <c>Idle</c> 是内核伪进程，回溯到它们没有意义。
    /// </summary>
    public static bool CanBeWebView2Host(string? processName)
    {
        var normalized = NormalizeName(processName);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        if (IsWebView2Process(normalized))
        {
            return false;
        }

        return !string.Equals(normalized, "System", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断给定的父进程回溯结果是否需要采用。
    /// <paramref name="originalPid"/> 等于 <paramref name="hostPid"/> 表示没有回溯到任何东西；
    /// 0 表示回溯失败。两种情况都应保留原始进程。
    /// </summary>
    public static bool ShouldUseResolvedHost(uint originalPid, uint hostPid) =>
        hostPid != 0 && hostPid != originalPid;

    /// <summary>去掉 <c>.exe</c> 后缀并去空白，便于与 Process.ProcessName 比较。</summary>
    public static string NormalizeName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
