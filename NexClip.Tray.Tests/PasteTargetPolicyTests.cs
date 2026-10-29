using NexClip.Tray;

namespace NexClip.Tray.Tests;

/// <summary>
/// 粘贴落点判定规则。
///
/// 守护三个"错了也不报错"的判定 —— 粘贴注入到错误窗口时用户完全无从察觉：
/// 1. 系统壳窗口不能当粘贴目标（往桌面/输入法候选栏粘贴等于无操作）；
/// 2. 只有标准编辑控件才值得尝试 WM_PASTE（自绘输入框不响应）；
/// 3. 等修饰键超时后必须继续粘贴，不能放弃。
/// </summary>
public sealed class PasteTargetPolicyTests
{
    [Theory]
    [InlineData("SysShadow")]
    [InlineData("Button")]
    [InlineData("MsgrIMEWindowClass")]
    [InlineData("Shell_CharmWindow")]
    [InlineData("DV2ControlHost")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("sysshadow")]      // 类名比较不区分大小写
    public void SystemShellClassesAreRejectedAsPasteTargets(string className)
    {
        Assert.True(PasteTargetPolicy.IsShellWindowClass(className));
    }

    [Theory]
    [InlineData("Notepad")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("Notepad++")]
    [InlineData("ApplicationFrameWindow")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryApplicationWindowsAreNotShellClasses(string? className)
    {
        Assert.False(PasteTargetPolicy.IsShellWindowClass(className));
    }

    [Theory]
    [InlineData("Edit")]                 // Win32 单行编辑框
    [InlineData("RichEdit20W")]
    [InlineData("RichEditD2DPT")]
    [InlineData("RICHEDIT50W")]
    [InlineData("EditBox")]
    public void StandardEditControlsSupportWmPaste(string className)
    {
        Assert.True(PasteTargetPolicy.ShouldTryPasteMessage(className));
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]           // 浏览器自绘输入框
    [InlineData("Internet Explorer_Server")]     // WebOC
    [InlineData("Scintilla")]                    // 编辑器自绘
    [InlineData("Windows.UI.Core.CoreWindow")]   // UWP 自绘
    [InlineData("")]
    [InlineData(null)]
    public void SelfDrawnControlsDoNotGetWmPaste(string? className)
    {
        Assert.False(PasteTargetPolicy.ShouldTryPasteMessage(className));
    }

    [Fact]
    public void ChromeRendererIsNeverTreatedAsEditControl()
    {
        // 浏览器里 Ctrl+V 才可靠；尝试 WM_PASTE 不仅无效，还可能触发意外的行为
        Assert.False(PasteTargetPolicy.ShouldTryPasteMessage("Chrome_WidgetWin_1"));
        Assert.False(PasteTargetPolicy.ShouldTryPasteMessage("Chrome_RenderWidgetHostHWND"));
    }

    [Fact]
    public void PasteContinuesEvenWhenModifierWaitTimesOut()
    {
        // 关键设计取舍：等不到松开也必须继续粘贴。
        // "点了没反应"对用户是明确的失败，而按键叠加只是概率性的小问题。
        Assert.True(PasteTargetPolicy.ShouldPasteAfterWaiting(
            modifiersReleased: false,
            waitedMilliseconds: PasteTargetPolicy.ModifierReleaseTimeoutMilliseconds));

        Assert.True(PasteTargetPolicy.ShouldPasteAfterWaiting(
            modifiersReleased: false,
            waitedMilliseconds: PasteTargetPolicy.ModifierReleaseTimeoutMilliseconds + 50));
    }

    [Fact]
    public void PasteProceedsImmediatelyWhenNoModifierWasDown()
    {
        Assert.True(PasteTargetPolicy.ShouldPasteAfterWaiting(
            modifiersReleased: true,
            waitedMilliseconds: 0));
    }

    [Fact]
    public void ModifierWaitTimesOutBeforeUserPerceivesDelay()
    {
        // 300ms 是上限：再长用户就会觉得"点了没反应"
        Assert.InRange(PasteTargetPolicy.ModifierReleaseTimeoutMilliseconds, 100, 500);
    }
}
