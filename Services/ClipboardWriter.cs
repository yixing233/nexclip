using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NexClip.Desktop.Services;

/// <summary>
/// 系统剪贴板的唯一写入通道:一条常驻的 STA 线程 + Win32 消息泵。
///
/// 为什么必须离开 UI 线程
/// ----------------------
/// WinRT 的 <c>Clipboard.SetContent</c> / <c>Clipboard.Flush</c> 底层是 OLE 剪贴板,调用
/// **跨进程且没有超时、没有取消**。此前它们全部跑在 WinUI 的 UI 线程上,一旦占用者
/// (远程控制软件、卡住的浏览器、explorer 的 XAML 岛)不回应,UI 线程就被钉死;而 UI 线程
/// 恰恰是本进程唯一的消息泵 —— 此时本进程既不再处理自己的窗口消息,也无法响应别人来取
/// 剪贴板数据(WM_RENDERFORMAT),于是**整机**的复制粘贴跟着一起卡住。
///
/// 本类的三条约束
/// --------------
/// 1. 所有写入串行跑在这条专用 STA 线程上,调用方 (UI 线程) 只 await 一个 Task,并带硬超时。
///    超时只是本次写入失败,UI 线程照常运行,不会跟着占用者一起挂死。
/// 2. 该线程**永久存活并持续泵消息**。它一旦成为剪贴板所有者,就必须随时能响应后续的
///    WM_RENDERFORMAT;线程活着 + 泵消息,别人来粘贴才不会等我们。
/// 3. 写之前先用 OpenClipboard 探针确认剪贴板可打开:被持续独占时立即失败并让用户知道
///    是谁占着,而不是去排队等一个永远等不到的机会。
/// </summary>
internal static class ClipboardWriter
{
    /// <summary>一次写入的内容。图片用内存字节而不是文件路径,理由见 <see cref="Execute"/>。</summary>
    private readonly record struct Payload(
        string? Text = null,
        string? Html = null,
        byte[]? Png = null,
        IReadOnlyList<IStorageItem>? StorageItems = null);

    /// <summary>单次写入的硬超时。图片另给更长预算(编码 + 渲染)。</summary>
    private const int TextTimeoutMs = NexClip.Tray.ClipboardWritePolicy.TextTimeoutMs;
    private const int ImageTimeoutMs = NexClip.Tray.ClipboardWritePolicy.ImageTimeoutMs;

    /// <summary>待处理队列上限:通道卡住时不再堆积,直接让新的写入快速失败。</summary>
    private const int MaxPending = 8;

    private sealed class Work
    {
        public required Payload Payload;
        public readonly TaskCompletionSource<bool> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly object Gate = new();
    private static readonly Queue<Work> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static Thread? _worker;
    /// <summary>最近一次写入使用的图片流。Flush 失败时系统仍是所有者,流必须活着。</summary>
    private static InMemoryRandomAccessStream? _lastImageStream;

    /// <summary>写入文本(可选附带 HTML)。返回 false 表示本次没有写进剪贴板。</summary>
    internal static Task<bool> WriteTextAsync(string text, string? html = null) =>
        SubmitAsync(new Payload(Text: text, Html: html), TextTimeoutMs);

    /// <summary>写入图片(PNG 字节)。</summary>
    internal static Task<bool> WriteImageAsync(byte[] png) =>
        SubmitAsync(new Payload(Png: png), ImageTimeoutMs);

    /// <summary>写入文件列表(CF_HDROP / StorageItems,复制语义)。</summary>
    internal static Task<bool> WriteFilesAsync(IReadOnlyList<IStorageItem> items) =>
        SubmitAsync(new Payload(StorageItems: items), TextTimeoutMs);

    private static async Task<bool> SubmitAsync(Payload payload, int timeoutMs)
    {
        // 探针必须在任何 OLE 调用之前:它只做 OpenClipboard,被独占时立即返回
        // ERROR_ACCESS_DENIED 而不阻塞,是"现在能不能安全写"的唯一低成本判据。
        //
        // 探针失败时不要立刻放弃:此时剪贴板往往只是被某个监听程序"碰了一下"
        // (实测本机 msrdc / GameViewer / Snipaste / 微信 之间会互相打断),
        // 几十毫秒后就能打开。给一小段带退避的重试,总预算远小于调用方超时。
        if (!await WaitForProbeAsync(timeoutMs))
        {
            // 探针失败 = 有人 OpenClipboard 后没关,所以这里报的是持锁者而不是内容所有者
            var holder = SourceAppDetector.TryGetClipboardLockHolderProcessName();
            Log.Warn($"写剪贴板跳过:剪贴板持续被占用(持锁者={holder ?? "未知"})");
            LastBlockedBy = holder;
            return false;
        }

        var work = new Work { Payload = payload };
        lock (Gate)
        {
            if (!EnsureWorkerLocked()) return false;
            if (Pending.Count >= MaxPending)
            {
                Log.Warn($"写剪贴板跳过:写入队列积压 {Pending.Count} 笔");
                return false;
            }
            Pending.Enqueue(work);
            Signal.Set();
        }

        var finished = await Task.WhenAny(work.Done.Task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (finished != work.Done.Task)
        {
            // 超时:调用方不再等待。写入线程可能仍卡在跨进程调用里,后续写入会排在它后面,
            // 由队列上限兜底,避免无限堆积。
            LastBlockedBy = SourceAppDetector.TryGetClipboardLockHolderProcessName();
            Log.Warn($"写剪贴板超时({timeoutMs}ms 未返回),本次放弃;持锁者={LastBlockedBy ?? "未知"}");
            return false;
        }

        var ok = await work.Done.Task.ConfigureAwait(true);
        if (ok) LastBlockedBy = null;
        else if (LastBlockedBy is null) LastBlockedBy = SourceAppDetector.TryGetClipboardLockHolderProcessName();
        return ok;
    }

    /// <summary>最近一次因剪贴板被占用而失败的占用者进程名(供 UI 提示用)。</summary>
    internal static string? LastBlockedBy { get; private set; }

    /// <summary>探针重试预算:占整个超时预算的一小部分,保证真正写不动时仍能快速返回。</summary>
    private static async Task<bool> WaitForProbeAsync(int timeoutMs)
    {
        var budget = Policy.ProbeBudgetFor(timeoutMs);
        var deadline = Environment.TickCount64 + budget;
        var delay = 5;
        while (true)
        {
            if (NativeMethods.TryProbeClipboard()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            // 用 Task.Delay 而不是 Thread.Sleep:调用方常是 UI 线程,绝不能被这里拖住
            await Task.Delay(delay).ConfigureAwait(true);
            delay = Math.Min(delay * 2, 60);
        }
    }

    private static bool EnsureWorkerLocked()
    {
        if (_worker is { IsAlive: true }) return true;
        try
        {
            var thread = new Thread(RunLoop)
            {
                // 该线程可能卡在跨进程调用上,必须后台化,否则会拖住进程退出
                IsBackground = true,
                Name = "NexClip.ClipboardWriter",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _worker = thread;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("创建剪贴板写入线程失败", ex);
            return false;
        }
    }

    /// <summary>写入线程主循环:取出待写内容 → 写入 → 泵消息 → 回到等待。</summary>
    private static void RunLoop()
    {
        try
        {
            // STA 线程必须先初始化公寓,否则 Clipboard.SetContent 会以 RPC_E_WRONG_THREAD 失败
            NativeMethods.EnsureWinRtInitialized();
            // 也必须有消息队列:延迟渲染的数据是经窗口消息回取到本线程的
            NativeMethods.EnsureMessageQueue();
            while (true)
            {
                // 先处理积压工作(可能与消息交替到达)
                while (true)
                {
                    Work? work;
                    lock (Gate)
                    {
                        if (Pending.Count == 0) break;
                        work = Pending.Dequeue();
                    }
                    ExecuteWork(work);
                }

                // 把这一轮到达的窗口消息全部处理掉:Flush 之后的延迟渲染、
                // 别人发来的跨线程回调都靠这里落地。
                NativeMethods.PumpMessages();

                // 有工作、有消息,或每 200ms 醒来一次:空闲时几乎零 CPU,
                // 而 200ms 的固定上限保证即使漏掉一次唤醒也能恢复。
                NativeMethods.WaitForWorkOrMessage(Signal.SafeWaitHandle.DangerousGetHandle(), 200);
            }
        }
        catch (Exception ex)
        {
            Log.Error("剪贴板写入线程异常退出", ex);
            // 清空队列,避免所有等待者永久挂起
            lock (Gate)
            {
                while (Pending.Count > 0) Pending.Dequeue().Done.TrySetResult(false);
                _worker = null;
            }
        }
    }

    // 重试策略集中在 ClipboardWritePolicy 里(纯计算、有单元测试):
    // 争用是极短脉冲而不是长占 —— 每次写入都会唤醒系统里所有剪贴板监听程序
    // (Snipaste / 微信 / 远程控制软件…),它们紧接着抢着 OpenClipboard 读一遍,
    // 于是"刚写完 → 下一次写"很容易撞上。实测约 15% 的写入会收到 CLIPBRD_E_CANT_OPEN。
    // 退避总预算远小于调用方超时,且花在专用线程上,不占用 UI。

    /// <summary>
    /// 在写入线程上重试。与旧实现的关键区别:这段退避跑在**专用线程**上,
    /// 调用方(UI 线程)只等待一个带超时的 Task,因此重试不会变成界面卡顿。
    /// </summary>
    private static void ExecuteWork(Work work)
    {
        var ok = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                ok = Execute(work.Payload);
                break;
            }
            catch (Exception ex) when (attempt < Policy.MaxAttempts - 1 && Policy.IsContention(ex.HResult))
            {
                // 争用类异常(CLIPBRD_E_CANT_OPEN)的 Message 往往是空串,日志要带上 HRESULT。
                var backoff = Policy.BackoffFor(attempt);
                Log.Debug($"写剪贴板争用,{backoff}ms 后重试(第 {attempt + 1} 次): 0x{ex.HResult:X8}");
                Thread.Sleep(backoff);
            }
            catch (Exception ex)
            {
                // CLIPBRD_E_CANT_OPEN 的直接原因是"有人锁着剪贴板",因此记持锁者。
                // 持锁窗口通常只存在几毫秒,这时可能已经查不到 —— 退而记录内容所有者作为线索,
                // 但日志措辞要区分开,不能让读者以为内容所有者就是锁持有者。
                LastBlockedBy = Policy.IsContention(ex.HResult)
                    ? SourceAppDetector.TryGetClipboardLockHolderProcessName()
                    : null;
                var hint = LastBlockedBy is not null
                    ? $"，剪贴板被 {LastBlockedBy} 占用"
                    : Policy.IsContention(ex.HResult)
                        ? $"，锁已释放(当时内容来自 {SourceAppDetector.TryGetClipboardOwnerProcessName() ?? "未知"})"
                        : "";
                Log.Warn($"写剪贴板失败(尝试 {attempt + 1} 次): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}{hint}");
                break;
            }
        }
        work.Done.TrySetResult(ok);
    }

    /// <summary>重试策略(独立可测,见 NexClip.Tray.ClipboardWritePolicy)。</summary>
    private static class Policy
    {
        public const int MaxAttempts = NexClip.Tray.ClipboardWritePolicy.MaxAttempts;
        public static int BackoffFor(int attempt) => NexClip.Tray.ClipboardWritePolicy.BackoffFor(attempt);
        public static int ProbeBudgetFor(int timeoutMs) => NexClip.Tray.ClipboardWritePolicy.ProbeBudgetFor(timeoutMs);
        public static bool IsContention(int hresult) => NexClip.Tray.ClipboardWritePolicy.IsContention(hresult);
    }

    private static bool Execute(Payload payload)
    {
        InMemoryRandomAccessStream? incomingStream = null;
        var package = new DataPackage();
        // 标记本程序写入,供回环抑制的快速路径识别(Flush 后该属性可能丢失,故仅作辅助)
        try { package.Properties[ImageCodec.SelfOriginProperty] = "1"; } catch { }

        if (payload.StorageItems is { Count: > 0 } items)
        {
            // readOnly: true = 复制语义;传 false 会被资源管理器当成剪切,粘贴后原文件消失
            package.SetStorageItems(items, true);
        }
        else if (payload.Png is { LongLength: > 0 } png)
        {
            // 内存流而不是文件:Flush 需要立即渲染,文件流会把磁盘 IO 拖进这条关键路径;
            // 而且本进程一旦不再响应,文件型延迟渲染会让别的程序粘贴时一直等我们。
            var stream = new InMemoryRandomAccessStream();
            // 注意:不能用 using 包住 AsStreamForWrite —— 它释放时会把底层 WinRT 流一起关闭,
            // 交出去的位图引用随即失效(实测表现为 ObjectDisposedException 0x80000013)。
            // 这里显式 Flush 而不 Dispose,让流保持可读。
            var net = stream.AsStreamForWrite();
            net.Write(png, 0, png.Length);
            net.Flush();
            stream.Seek(0);
            incomingStream = stream;
            // 必须保留强引用:RandomAccessStreamReference 内部只持弱关系,
            // 一旦本方法返回、流被回收,剪贴板里的位图就变成悬空引用,别人粘贴时读到空数据。
            // 直到下一次写入(或在 Flush 成功之后)才可以释放上一份。
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        }
        else if (payload.Text is not null)
        {
            package.SetText(payload.Text);
            if (!string.IsNullOrWhiteSpace(payload.Html))
            {
                try
                {
                    package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Html));
                }
                catch (Exception ex)
                {
                    // HTML 写入失败只损失富文本,纯文本不受影响
                    Log.Warn($"写入剪贴板 HTML 失败,仅保留纯文本: {ex.Message}");
                }
            }
        }
        else
        {
            return false;
        }

        Clipboard.SetContent(package);

        // Flush 的收益远大于代价:内容固化后本进程不再是"必须活着才能粘贴"的所有者,
        // 本进程后续无论怎么卡,都不会再拖累全机剪贴板。
        // Flush 与 SetContent 是两次独立的剪贴板写入,后者同样可能撞上 CLIPBRD_E_CANT_OPEN
        // (实测本机约 4% 的写入会在这里失败,原因通常是别的监听程序恰好来读)。
        // 失败不影响本次内容已经生效,只是"本进程退出后是否留存"会打折扣,所以只记录、不重试:
        // 重试 Flush 需要在已持有内容的情况下再抢一次剪贴板,收益低而打扰别人的概率高。
        try
        {
            Clipboard.Flush();
            // Flush 成功后内容已固化到系统,不再需要本进程持有流;
            // 失败时保持引用,因为此时系统仍要回调本进程取值。
            _lastImageStream = null;
        }
        catch (Exception ex)
        {
            if (payload.Png is not null) _lastImageStream = incomingStream;
            Log.Debug($"剪贴板 Flush 失败(内容已写入,仅影响退出后留存): {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
        return true;
    }
}
