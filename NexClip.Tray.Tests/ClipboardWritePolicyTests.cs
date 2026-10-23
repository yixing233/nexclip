using System;
using NexClip.Tray;

namespace NexClip.Tray.Tests;

/// <summary>
/// 剪贴板写入重试策略的回归测试。
///
/// 背景:WinUI 应用原先在 UI 线程上同步 SetContent + Flush 并 Thread.Sleep 重试,
/// 一旦剪贴板被别的进程(远程控制软件、其它剪贴板管理器)占住,UI 线程被钉死,
/// 进而拖死全机的复制粘贴。现在的做法是"专用 STA 线程 + 有界重试 + 调用方硬超时",
/// 于是策略数值本身就成了关键不变量:
///
/// - 退避必须能吸收争用脉冲(本机实测约 15% 的写入会撞上 CLIPBRD_E_CANT_OPEN);
/// - 退避总时长必须严格小于调用方超时,否则调用方先放弃,重试变成纯空转;
/// - 探针预算必须只占超时的一小部分,保证"真写不动"时仍能快速返回。
/// </summary>
public class ClipboardWritePolicyTests
{
    [Fact]
    public void Backoff_IsMonotonic_AndCapped()
    {
        var previous = 0;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var backoff = ClipboardWritePolicy.BackoffFor(attempt);
            Assert.True(backoff >= previous, $"第 {attempt} 次退避({backoff}ms)不应小于前一次({previous}ms)");
            Assert.True(backoff <= ClipboardWritePolicy.MaxBackoffMs,
                $"第 {attempt} 次退避({backoff}ms)不得超过上限 {ClipboardWritePolicy.MaxBackoffMs}ms");
            previous = backoff;
        }
    }

    [Fact]
    public void TotalBackoff_StaysBelowCallerTimeout()
    {
        // 这是最关键的一条:重试预算必须被调用方超时严格覆盖。
        Assert.True(ClipboardWritePolicy.IsWithinBudget(ClipboardWritePolicy.TextTimeoutMs),
            $"文本写入:总退避 {ClipboardWritePolicy.TotalBackoffMs()}ms 必须小于超时 {ClipboardWritePolicy.TextTimeoutMs}ms");
        Assert.True(ClipboardWritePolicy.IsWithinBudget(ClipboardWritePolicy.ImageTimeoutMs),
            $"图片写入:总退避 {ClipboardWritePolicy.TotalBackoffMs()}ms 必须小于超时 {ClipboardWritePolicy.ImageTimeoutMs}ms");
    }

    [Fact]
    public void Backoff_IsLongEnoughToAbsorbContentionBurst()
    {
        // 实测本机持锁方(GameViewer / msrdc)的占用是毫秒级脉冲。
        // 总退避至少要有 100ms 量级,否则等于没有重试。
        Assert.True(ClipboardWritePolicy.TotalBackoffMs() >= 100,
            $"总退避 {ClipboardWritePolicy.TotalBackoffMs()}ms 太短,无法吸收剪贴板争用脉冲");
    }

    [Fact]
    public void ProbeBudget_IsSmallFractionOfTimeout()
    {
        foreach (var timeout in new[] { ClipboardWritePolicy.TextTimeoutMs, ClipboardWritePolicy.ImageTimeoutMs })
        {
            var budget = ClipboardWritePolicy.ProbeBudgetFor(timeout);
            Assert.True(budget >= 0);
            Assert.True(budget <= timeout / 2,
                $"探针预算 {budget}ms 不应超过超时 {timeout}ms 的一半,否则真正写入没有时间可用");
            Assert.True(budget <= ClipboardWritePolicy.MaxProbeBudgetMs);
        }
    }

    [Fact]
    public void IsContention_MatchesClipbrdCantOpen()
    {
        // 0x800401D0 = CLIPBRD_E_CANT_OPEN:剪贴板被别的程序打开,是唯一值得重试的情形。
        Assert.True(ClipboardWritePolicy.IsContention(unchecked((int)0x800401D0)));

        // 其它错误(权限、参数、OLE 未初始化)不应被当成争用无限重试
        Assert.False(ClipboardWritePolicy.IsContention(unchecked((int)0x80070005))); // E_ACCESSDENIED
        Assert.False(ClipboardWritePolicy.IsContention(0));                          // S_OK
        Assert.False(ClipboardWritePolicy.IsContention(unchecked((int)0x8001010E))); // RPC_E_WRONG_THREAD
    }
}

/// <summary>
/// "剪贴板被谁占着"的措辞测试。
///
/// 守护的是一个真实的排查陷阱:GetClipboardOwner 返回的是**内容所有者**,
/// 而 OpenClipboard/SetContent 失败的直接原因是**持锁者**(GetOpenClipboardWindow)。
/// 两者经常不是同一个进程 —— 实测本机内容所有者长期是 GameViewer(它放了内容),
/// 而无人操作时根本没有持锁者。若把内容所有者当"占用者"上报,
/// 每一次偶发争用都会被错误归因,排查方向直接跑偏。
/// </summary>
public class ClipboardBlockerWordingTests
{
    [Fact]
    public void LockHolder_TakesPrecedenceAndIsStatedAsOccupying()
    {
        var text = ClipboardWritePolicy.DescribeBlocker("Ditto", "GameViewer");
        Assert.Contains("Ditto", text);
        Assert.Contains("占用", text);
        // 明确指名的情况下不应再啰嗦内容来源,那是次要信息
        Assert.DoesNotContain("GameViewer", text);
    }

    [Fact]
    public void WithoutLockHolder_ContentOwnerIsNeverBlamed()
    {
        var text = ClipboardWritePolicy.DescribeBlocker(null, "GameViewer");

        // 必须提到它,但只能作为线索。这是一个真实踩过的坑:
        // 本机数据所有者长期是 GameViewer(UU远程),而它多数时候根本没在持锁,
        // 早期把数据所有者当"占用者"上报,导致所有偶发争用都被错误归因。
        Assert.Contains("GameViewer", text);
        Assert.Contains("内容来自", text);
        Assert.DoesNotContain("GameViewer 正在占用", text);
        // 明确标注不确定,避免用户照此去怪罪一个无关的程序
        Assert.Contains("不一定", text);
    }

    [Fact]
    public void NullLockHolder_IsNotTreatedAsNobodyHolding()
    {
        // OpenClipboard(NULL) 的持锁者在 GetOpenClipboardWindow 里查不到(实测盲区)。
        // 因此 null 只能表示"未知",文案不能说成"没有人占用"。
        var text = ClipboardWritePolicy.DescribeBlocker(null, null);
        Assert.Contains("占用", text);
        Assert.DoesNotContain("无人", text);
        Assert.DoesNotContain("可以自由", text);
    }

    [Fact]
    public void UnknownBoth_StillGivesActionableText()
    {
        var text = ClipboardWritePolicy.DescribeBlocker(null, null);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("剪贴板", text);
    }

    [Fact]
    public void EmptyStringsAreTreatedAsUnknown()
    {
        // 进程名解析失败时会传空串,不能因此输出 " 正在占用系统剪贴板" 这种残缺文案
        var text = ClipboardWritePolicy.DescribeBlocker("", "");
        Assert.DoesNotContain("正在占用系统剪贴板", text.TrimStart());
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}