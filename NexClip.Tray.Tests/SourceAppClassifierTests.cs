using NexClip.Tray;

namespace NexClip.Tray.Tests;

/// <summary>
/// WebView2 宿主识别规则。
///
/// 守护的是一个真实发生过的误识别：DSH-X 用 WebView2 承载界面，
/// 用户在里面复制文本后，NexClip 记录的来源是 "Microsoft Edge WebView2" ——
/// 界面上根本没有这个程序，用户看到一脸茫然。
///
/// 根因：WebView2 把浏览器内核(msedgewebview2.exe)与宿主应用(DSH.exe)拆成两个进程，
/// 剪贴板 API 实际由内核进程调用。必须沿父进程链回溯到宿主。
/// </summary>
public sealed class SourceAppClassifierTests
{
    [Theory]
    [InlineData("msedgewebview2")]
    [InlineData("msedgewebview2.exe")]
    [InlineData("MSEDGEWEBVIEW2")]
    [InlineData("  msedgewebview2.exe  ")]
    public void WebView2KernelIsRecognizedRegardlessOfCaseOrExtension(string name)
    {
        Assert.True(SourceAppClassifier.IsWebView2Process(name));
    }

    [Theory]
    [InlineData("DSH")]
    [InlineData("DSH.exe")]
    [InlineData("GameViewer")]
    [InlineData("chrome")]
    [InlineData("msedgewebview")]
    [InlineData("")]
    [InlineData(null)]
    public void NonWebView2ProcessesAreNotMistakenForKernel(string? name)
    {
        Assert.False(SourceAppClassifier.IsWebView2Process(name));
    }

    [Theory]
    [InlineData("DSH")]
    [InlineData("DSH.exe")]
    [InlineData("GameViewer")]
    [InlineData("SearchHost")]
    [InlineData("notepad")]
    public void RealApplicationsCanHostWebView2(string name)
    {
        Assert.True(SourceAppClassifier.CanBeWebView2Host(name));
    }

    [Theory]
    [InlineData("msedgewebview2")]
    // 内核自身不能再当宿主，否则父链回溯会原地打转
    [InlineData("msedgewebview2.exe")]
    // 系统伪进程回溯到它们没有意义
    [InlineData("System")]
    [InlineData("Idle")]
    [InlineData("")]
    [InlineData(null)]
    public void KernelItselfAndSystemPseudosCannotBeHost(string? name)
    {
        Assert.False(SourceAppClassifier.CanBeWebView2Host(name));
    }

    [Fact]
    public void ResolvedHostIsUsedOnlyWhenItDiffersFromOriginal()
    {
        // 回溯成功：换成宿主
        Assert.True(SourceAppClassifier.ShouldUseResolvedHost(18528, 28300));

        // 没回溯到东西（父链一路都是 WebView2）→ 保留原始进程
        Assert.False(SourceAppClassifier.ShouldUseResolvedHost(18528, 18528));

        // 回溯失败 → 保留原始进程
        Assert.False(SourceAppClassifier.ShouldUseResolvedHost(18528, 0));
    }

    [Theory]
    [InlineData("DSH.exe", "DSH")]
    [InlineData("DSH", "DSH")]
    [InlineData("DSH.EXE", "DSH")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NamesAreNormalizedForComparison(string? input, string expected)
    {
        Assert.Equal(expected, SourceAppClassifier.NormalizeName(input));
    }

    [Fact]
    public void RealWorldChainDshXIsResolvedToItsHost()
    {
        // 实测数据：DSH-X 的 msedgewebview2(18528) 父进程是 DSH.exe(28300)
        const uint kernelPid = 18528;
        const uint hostPid = 28300;
        const string kernelName = "msedgewebview2";
        const string hostName = "DSH";

        Assert.True(SourceAppClassifier.IsWebView2Process(kernelName));
        Assert.False(SourceAppClassifier.CanBeWebView2Host(kernelName));
        Assert.True(SourceAppClassifier.CanBeWebView2Host(hostName));
        Assert.True(SourceAppClassifier.ShouldUseResolvedHost(kernelPid, hostPid));
    }

    [Fact]
    public void RealWorldChainGameViewerIsResolvedToItsHost()
    {
        // 实测数据：GameViewer 的 msedgewebview2(7468) 父进程是 GameViewer.exe(41696)
        Assert.True(SourceAppClassifier.IsWebView2Process("msedgewebview2"));
        Assert.True(SourceAppClassifier.CanBeWebView2Host("GameViewer"));
        Assert.True(SourceAppClassifier.ShouldUseResolvedHost(7468, 41696));
    }
}
