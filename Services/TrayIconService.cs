namespace NexClip.Desktop.Services;

using Microsoft.UI.Dispatching;
using NexClip.Tray;

/// <summary>
/// 托盘图标服务: 桥接至 NexClip.Tray.TrayManager (基于 Windows Forms NotifyIcon 保证 100% 稳定挂载系统通知区)。
/// 启动后延迟 1.5s/5s/12s 各强制重挂一次: 覆盖 Explorer/Shell 未就绪导致首次 NIM_ADD 失败 -> 托盘图标"看不到"的场景(自愈)。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public enum TrayState { Disconnected, Connected, Uploading, Downloading, Error }

    private readonly TrayManager _trayManager;
    private readonly List<DispatcherQueueTimer> _retryTimers = new();

    public TrayIconService(
        Action onActivate,
        Action onShow,
        Action onCheckUpdate,
        Action onSettings,
        Action onRestart,
        Action onExit)
    {
        _trayManager = new TrayManager(
            onActivate,
            onShow,
            onCheckUpdate,
            onSettings,
            onRestart,
            onExit,
            AppContext.BaseDirectory,
            Log.Info);
    }

    public void Initialize()
    {
        SetState(TrayState.Disconnected);
        SetTheme(Lucide.IsDarkTheme);
        ScheduleReAttach(1.5);
        ScheduleReAttach(5);
        ScheduleReAttach(12);
    }

    public void SetTheme(bool isDark)
    {
        _trayManager.SetTheme(isDark);
    }

    private void ScheduleReAttach(double seconds)
    {
        try
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            if (dq is null) return;
            var timer = dq.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _retryTimers.Remove(timer);
                Log.Debug($"托盘启动后自愈重挂({seconds}s)");
                _trayManager.EnsureVisible();
            };
            _retryTimers.Add(timer);
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("托盘自愈重挂调度失败", ex);
        }
    }

    public void SetState(TrayState state)
    {
        _trayManager.SetState((TrayManager.TrayState)state);
    }

    public void Notify(string title, string text)
    {
        _trayManager.Notify(title, text);
    }

    public void Dispose()
    {
        _trayManager.Dispose();
    }
}
