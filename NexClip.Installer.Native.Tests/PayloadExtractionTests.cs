using System.Diagnostics;
using NexClip.Installer.Native.Services;

namespace NexClip.Installer.Native.Tests;

/// <summary>
/// 解压阶段的进度回调与取消语义。
///
/// 背景:原实现对**每个文件**单独 <c>await Task.Run</c> 并回调一次进度。
/// payload 有 121 个文件,于是解压期间 UI 线程被要求重绘 121 次。
/// 实测解压本身只有 200ms 量级、磁盘 IO 占其中约 45%,
/// 逐文件切线程带来的额外开销约 13%(211ms vs 184ms 纯同步),
/// 真正值得修的是**回调次数**而不是线程切换 —— 所以这里重点验证节流。
///
/// 这些测试依赖构建产物里是否嵌入了 payload:
/// 用 <see cref="PayloadService.HasEmbeddedPayload"/> 判断,没有则跳过(Release 打包构建才有)。
/// </summary>
public sealed class PayloadExtractionTests
{
    private static string CreateScratch() =>
        Path.Combine(Path.GetTempPath(), "NexClip.ExtractTest", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task ProgressIsThrottledInsteadOfFiringPerFile()
    {
        if (!PayloadService.HasEmbeddedPayload()) return; // 未打包构建:无 payload 可解

        var destination = CreateScratch();
        var callbacks = 0;
        var lastProgress = -1.0;
        var monotonic = true;
        try
        {
            await PayloadService.ExtractPayloadAsync(
                destination,
                (progress, _) =>
                {
                    callbacks++;
                    if (progress < lastProgress) monotonic = false;
                    lastProgress = progress;
                },
                CancellationToken.None);

            var files = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;
            Assert.True(files > 0, "应当解出至少一个文件");

            // 关键断言:回调次数必须显著少于文件数。
            // 逐文件回调时 callbacks == files(实测 121);
            // 节流后实测 19 次。留足余量取 60% 作为上限,避免机器快慢导致抖动。
            Assert.True(callbacks < files * 0.6,
                $"进度回调 {callbacks} 次 / {files} 个文件,节流似乎未生效(应显著少于文件数)");

            // 进度必须单调不减:UI 进度条不允许回退
            Assert.True(monotonic, "进度回调出现回退");

            // 且必须到达 100%:节流吞掉最后一次会让进度条停在 99%
            Assert.Equal(1.0, lastProgress, 3);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task ExtractionCompletesQuicklyEnoughForInteractiveInstall()
    {
        if (!PayloadService.HasEmbeddedPayload()) return;

        var destination = CreateScratch();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await PayloadService.ExtractPayloadAsync(destination, (_, _) => { }, CancellationToken.None);
            stopwatch.Stop();

            // 实测 195~210ms(121 文件 / 60MB 展开)。这里给 10 秒的宽松上限,
            // 只用于拦住"退化成逐文件串行慢查询"这类量级问题,不做性能精确断言。
            Assert.True(stopwatch.ElapsedMilliseconds < 10_000,
                $"解压耗时 {stopwatch.ElapsedMilliseconds}ms,异常偏慢");
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task CancellationBeforeStartThrowsAndLeavesNoFiles()
    {
        if (!PayloadService.HasEmbeddedPayload()) return;

        var destination = CreateScratch();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PayloadService.ExtractPayloadAsync(destination, (_, _) => { }, cancellation.Token));

            // 取消后不应留下半成品内容(目录可能已建,但不应有解出的文件)
            var files = Directory.Exists(destination)
                ? Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length
                : 0;
            Assert.Equal(0, files);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [Fact]
    public async Task NullProgressCallbackIsAllowed()
    {
        if (!PayloadService.HasEmbeddedPayload()) return;

        var destination = CreateScratch();
        try
        {
            // 静默安装路径不传回调;节流逻辑里必须容忍 null,不能因此跳过解压
            await PayloadService.ExtractPayloadAsync(destination, null!, CancellationToken.None);
            Assert.True(Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length > 0);
        }
        finally
        {
            Cleanup(destination);
        }
    }
}
