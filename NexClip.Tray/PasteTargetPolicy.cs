namespace NexClip.Tray;

/// <summary>
/// 粘贴落点判定的纯逻辑，便于单元测试。
///
/// <para>背景：双击粘贴时有三个"容易写错、错了也不报错"的判定，
/// 完整实现依赖 Win32 句柄（无法在单元测试里构造），
/// 但判定规则本身是纯函数，抽到这里单独守护：</para>
/// <list type="number">
/// <item>某个窗口类名能否作为粘贴目标（排除系统壳类）；</item>
/// <item>某个控件是否值得尝试 WM_PASTE（只有标准编辑控件响应它）；</item>
/// <item>修饰键等待的超时策略。</item>
/// </list>
/// </summary>
public static class PasteTargetPolicy
{
    /// <summary>Windows 系统壳类名：这些是桌面/任务栏/输入法的一部分，不是用户正在编辑的窗口。</summary>
    private static readonly string[] ShellWindowClasses =
    [
        "SysShadow",
        "Button",
        "MsgrIMEWindowClass",
        "Shell_CharmWindow",
        "DV2ControlHost",
        "Progman",      // 桌面
        "WorkerW",      // 桌面壁纸层
    ];

    /// <summary>
    /// 类名是否属于系统壳窗口（不可作为粘贴目标）。
    /// 参考 CopyQ 的 isAltTabWindow 排除列表。
    /// </summary>
    public static bool IsShellWindowClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;   // 拿不到类名时不武断拒绝
        }

        foreach (var known in ShellWindowClasses)
        {
            if (string.Equals(className, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 该控件类名是否值得尝试 WM_PASTE。只有标准 Win32 编辑控件会响应它；
    /// 浏览器/Electron 的自绘输入框一律不试（避免浪费跨进程 SendMessage）。
    ///
    /// <para>刻意保守：未知类的返回值语义不可靠，"试一下"反而可能造成重复粘贴，
    /// 因此只对确定支持的 <c>Edit</c> / <c>RichEdit</c> 系列返回 true。</para>
    /// </summary>
    public static bool ShouldTryPasteMessage(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        // 浏览器的自绘输入框：明确不响应 WM_PASTE
        if (className.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Edit / RichEdit / RICHEDIT 系列是 WM_PASTE 的标准实现者
        return className.StartsWith("Edit", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// WM_PASTE 是否粘贴成功。
    ///
    /// <para><b>不要看控件返回值</b>：实测 Win11 记事本的 <c>RichEditD2DPT</c>
    /// 接受 WM_PASTE 后确实完成了粘贴，但返回 0。若以"返回值非 0"判定成功，
    /// 会把成功误判为失败并再注入一次 Ctrl+V —— 用户看到内容重复两份。</para>
    ///
    /// <para>正确判据是**消息是否送达**：<paramref name="delivered"/> 来自
    /// SendMessageTimeout 的返回值（非 0 = 控件处理了消息，即使它自己返回 0）。
    /// 参数 <paramref name="controlReturnValue"/> 保留只为记录与调试，不参与判断。</para>
    /// </summary>
    public static bool IsPasteMessageDelivered(bool delivered, long controlReturnValue) => delivered;

    /// <summary>注入粘贴键前等待修饰键释放的上限（毫秒）。</summary>
    public const int ModifierReleaseTimeoutMilliseconds = 300;

    /// <summary>
    /// 等待修饰键释放的策略：轮询到上限就放弃等待并继续粘贴。
    /// 返回 true 表示"应当继续粘贴"—— 无论是否等到松开，粘贴都必须继续，
    /// 因为"点了没反应"比"按键可能叠加"更糟。
    /// </summary>
    public static bool ShouldPasteAfterWaiting(bool modifiersReleased, long waitedMilliseconds) =>
        modifiersReleased || waitedMilliseconds >= ModifierReleaseTimeoutMilliseconds;
}