using NexClip.Tray;

namespace NexClip.Tray.Tests;

/// <summary>
/// WM_PASTE 的成功判定。
///
/// 守护一个实测复现过的 bug：<c>RichEditD2DPT</c>（Win11 记事本的编辑区）
/// 收到 WM_PASTE 后**确实粘贴成功**，但返回值是 0。
/// 旧实现用"返回值非 0"判断成功，于是误判失败 → 回退注入 Ctrl+V → 内容粘贴两次。
///
/// 实测证据：
///   WM_PASTE 返回 0
///   文本长度 554 → 572（+18，正好是粘贴内容长度）
/// </summary>
public sealed class PasteMessageResultTests
{
    [Fact]
    public void ZeroReturnValueDoesNotMeanPasteFailed()
    {
        // 这是本 bug 的核心：判断必须基于"消息是否送达"，而不是控件返回值。
        // 送达 = SendMessageTimeout 返回非 0；控件返回 0 是合法的（RichEdit 就这样）。
        Assert.True(PasteTargetPolicy.IsPasteMessageDelivered(delivered: true, controlReturnValue: 0));
    }

    [Fact]
    public void TimeoutIsTheOnlyFailureSignal()
    {
        // 只有超时/窗口无响应才算失败
        Assert.False(PasteTargetPolicy.IsPasteMessageDelivered(delivered: false, controlReturnValue: 0));
        Assert.False(PasteTargetPolicy.IsPasteMessageDelivered(delivered: false, controlReturnValue: 1));
    }

    [Fact]
    public void NonZeroControlReturnIsAlsoSuccess()
    {
        // 标准 Edit 控件成功时返回非 0，同样算成功
        Assert.True(PasteTargetPolicy.IsPasteMessageDelivered(delivered: true, controlReturnValue: 1));
        Assert.True(PasteTargetPolicy.IsPasteMessageDelivered(delivered: true, controlReturnValue: 42));
    }

    [Theory]
    [InlineData("RichEditD2DPT")]   // Win11 记事本，实测返回 0 但确实粘贴了
    [InlineData("RichEdit20W")]
    [InlineData("Edit")]
    public void RichEditFamilyIsTreatedAsPasteMessageTarget(string className)
    {
        // 这些控件要走 WM_PASTE 路径；走对之后就不会再叠加 Ctrl+V
        Assert.True(PasteTargetPolicy.ShouldTryPasteMessage(className));
    }
}
