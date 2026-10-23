namespace NexClip.Tray;

/// <summary>
/// 剪贴板写入的重试/超时策略(纯计算,无 IO、无 WinRT 依赖)。
///
/// 单独抽出来的原因:这是"复制莫名不生效"与"界面卡顿"之间的分界线 ——
/// 重试必须足够宽以吸收剪贴板争用脉冲,又必须严格受预算约束,绝不能无限等待。
/// 这类数值策略值得被单元测试固定住,而不是散落在带跨进程调用的方法里靠肉眼检查。
/// </summary>
public static class ClipboardWritePolicy
{
    /// <summary>最大尝试次数(含首次)。</summary>
    public const int MaxAttempts = 8;

    /// <summary>退避基数(毫秒)。</summary>
    public const int BaseBackoffMs = 10;

    /// <summary>单次退避上限(毫秒),避免指数增长后单步过长。</summary>
    public const int MaxBackoffMs = 120;

    /// <summary>文本写入的调用方超时(毫秒)。</summary>
    public const int TextTimeoutMs = 1500;

    /// <summary>图片写入的调用方超时(毫秒):编码与渲染更重。</summary>
    public const int ImageTimeoutMs = 4000;

    /// <summary>探针重试预算占超时的比例上限。</summary>
    public const int ProbeBudgetRatio = 3;

    /// <summary>探针重试的绝对上限(毫秒)。</summary>
    public const int MaxProbeBudgetMs = 400;

    /// <summary>
    /// 第 <paramref name="attempt"/> 次(从 0 开始)失败后的退避时长。
    /// 指数增长但在 <see cref="MaxBackoffMs"/> 处封顶,保证最坏总退避可控。
    /// </summary>
    public static int BackoffFor(int attempt)
    {
        if (attempt < 0) return BaseBackoffMs;
        // 移位量限制在 4,避免 long 溢出与荒谬的大数
        var shift = Math.Min(attempt, 4);
        var value = BaseBackoffMs * (1 << shift);
        return Math.Min(value, MaxBackoffMs);
    }

    /// <summary>全部重试耗尽时的总退避时间(毫秒),用于验证它不会突破调用方超时。</summary>
    public static int TotalBackoffMs()
    {
        var total = 0;
        for (var i = 0; i < MaxAttempts - 1; i++) total += BackoffFor(i);
        return total;
    }

    /// <summary>探针重试预算:取超时的 1/3,并封顶。</summary>
    public static int ProbeBudgetFor(int timeoutMs) =>
        Math.Clamp(timeoutMs / ProbeBudgetRatio, 0, MaxProbeBudgetMs);

    /// <summary>
    /// 该异常是否属于"剪贴板正被别的程序打开"这类可重试争用。
    /// 0x800401D0 = CLIPBRD_E_CANT_OPEN。
    /// </summary>
    public static bool IsContention(int hresult) => hresult == unchecked((int)0x800401D0);

    /// <summary>剪贴板占用情况的描述文本。</summary>
    /// <param name="lockHolder">GetOpenClipboardWindow 解析出的持锁进程名;无人持锁时为 null。</param>
    /// <param name="contentOwner">GetClipboardOwner 解析出的内容所有者进程名;未知时为 null。</param>
    public static string DescribeBlocker(string? lockHolder, string? contentOwner)
    {
        // 有持锁者:结论明确,直接指名(这是用户唯一能采取行动的信息)
        if (!string.IsNullOrEmpty(lockHolder)) return $"{lockHolder} 正在占用系统剪贴板";

        // 没有持锁者不等于"没人占用":用 OpenClipboard(NULL) 持锁的进程查不到(见
        // SourceAppDetector.TryGetClipboardLockHolderProcessName 的盲区说明)。
        // 所以这里只描述现象,并把内容来源作为线索列出,不断言它是元凶。
        if (!string.IsNullOrEmpty(contentOwner))
        {
            return $"剪贴板正被其他程序短暂占用(当前内容来自 {contentOwner}，不一定是它占用)";
        }
        return "剪贴板正被其他程序短暂占用";
    }

    /// <summary>
    /// 重试总预算是否仍在调用方超时之内。
    /// 这是一条硬约束:一旦退避超过超时,调用方会先放弃,重试就变成纯粹的空转。
    /// </summary>
    public static bool IsWithinBudget(int timeoutMs) => TotalBackoffMs() < timeoutMs;
}