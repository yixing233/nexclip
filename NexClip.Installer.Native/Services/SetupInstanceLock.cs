namespace NexClip.Installer.Native.Services;

/// <summary>
/// 安装器单实例锁。多个安装器同时运行会争用同一个依赖下载缓存目录，
/// 并让 Windows Installer / MSIX 部署互斥（1618 / 0x80073D00），因此同一时刻只允许一个实例执行安装或卸载。
/// </summary>
internal sealed class SetupInstanceLock : IDisposable
{
    private const string MutexName = @"Global\NexClip.Setup.SingleInstance";

    /// <summary>命名互斥体对同一线程是可重入的，额外用进程内标记拦截重复获取。</summary>
    private static int _heldInProcess;

    private readonly Mutex? _mutex;
    private bool _released;

    private SetupInstanceLock(Mutex? mutex) => _mutex = mutex;

    /// <summary>获取单实例锁；已有实例在运行时返回 null。</summary>
    internal static SetupInstanceLock? TryAcquire()
    {
        if (Interlocked.CompareExchange(ref _heldInProcess, 1, 0) != 0)
        {
            return null;
        }

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                Interlocked.Exchange(ref _heldInProcess, 0);
                return null;
            }

            return new SetupInstanceLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例异常退出时锁会被标记为已放弃，当前进程仍然取得所有权
            return new SetupInstanceLock(mutex);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // 无法创建全局对象时（受限会话/组策略）不阻断安装，退化为仅进程内互斥
            mutex?.Dispose();
            SetupLog.Write($"无法创建安装器单实例锁：{exception.Message}");
            return new SetupInstanceLock(null);
        }
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }

        Interlocked.Exchange(ref _heldInProcess, 0);
    }
}