using Microsoft.AspNetCore.SignalR.Client;
using SyncClipboard.Desktop.Models;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// SignalR 推送客户端:/hubs/clipboard 推送通道(新架构:同步接口免认证)。
/// 回调来自 SignalR 线程,调用方需自行切换到 UI 线程。
/// 首次连接失败会进入后台重连循环(自动重连仅对"已连接后断开"生效)。
/// </summary>
public sealed class PushClient : IAsyncDisposable
{
    private HubConnection? _hub;
    private CancellationTokenSource? _retryCts;
    private Task? _retryTask;
    private readonly object _gate = new();

    /// <summary>收到 ClipboardUpdated(entry)。</summary>
    public event Action<ClipboardEntry>? EntryReceived;

    /// <summary>连接状态变化:connecting/connected/reconnecting/disconnected。</summary>
    public event Action<string>? StateChanged;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>
    /// 连接推送通道。serverUrl 必填;deviceId/deviceName/platform/version 用于服务端设备登记
    /// (node 服务端从 WebSocket URL 参数识别设备并更新平台信息)。
    /// 同步接口免认证,不携带任何令牌。
    /// </summary>
    public async Task ConnectAsync(
        string serverUrl, string? deviceId = null, string? deviceName = null,
        string? platform = null, string? version = null)
    {
        await DisconnectAsync();

        var endpoint = new Uri(new Uri(serverUrl.TrimEnd('/')), "/hubs/clipboard");
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var qs = new List<string> { "deviceId=" + Uri.EscapeDataString(deviceId) };
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                qs.Add("deviceName=" + Uri.EscapeDataString(deviceName));
            }
            if (!string.IsNullOrWhiteSpace(platform))
            {
                qs.Add("platform=" + Uri.EscapeDataString(platform));
            }
            if (!string.IsNullOrWhiteSpace(version))
            {
                qs.Add("version=" + Uri.EscapeDataString(version));
            }
            var ub = new UriBuilder(endpoint) { Query = string.Join("&", qs) };
            endpoint = ub.Uri;
        }
        var builder = new HubConnectionBuilder()
            .WithUrl(endpoint)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
            });
        var hub = builder.Build();
        _hub = hub;

        hub.On<ClipboardEntry>("ClipboardUpdated", entry => EntryReceived?.Invoke(entry));
        hub.Reconnecting += _ => { StateChanged?.Invoke("reconnecting"); return Task.CompletedTask; };
        hub.Reconnected += _ => { StateChanged?.Invoke("connected"); return Task.CompletedTask; };
        hub.Closed += _ => { StateChanged?.Invoke("disconnected"); return Task.CompletedTask; };

        StateChanged?.Invoke("connecting");
        try
        {
            await hub.StartAsync();
            StateChanged?.Invoke("connected");
        }
        catch (Exception ex)
        {
            Log.Error($"SignalR 连接失败:{ex.Message}");
            StateChanged?.Invoke("disconnected");
            // 首次连接失败不会触发自动重连:启动后台重试循环(指数退避,上限 60s)
            StartRetryLoop(hub);
        }
    }

    /// <summary>后台重试:每 2/10/30/60 秒尝试 StartAsync,直到成功或 Stop。</summary>
    private void StartRetryLoop(HubConnection hub)
    {
        lock (_gate)
        {
            _retryCts?.Cancel();
            _retryCts = new CancellationTokenSource();
            var ct = _retryCts.Token;
            _retryTask = Task.Run(async () =>
            {
                var delays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };
                var i = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(delays[i % delays.Length], ct);
                        if (ct.IsCancellationRequested || ReferenceEquals(_hub, hub) == false) return;
                        StateChanged?.Invoke("reconnecting");
                        await hub.StartAsync(ct);
                        StateChanged?.Invoke("connected");
                        return;   // 连上即退出重试循环
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Log.Warn($"SignalR 重连失败(第 {i + 1} 次):{ex.Message}");
                        i++;
                    }
                }
            }, ct);
        }
    }

    private void StopRetryLoop()
    {
        lock (_gate)
        {
            _retryCts?.Cancel();
            _retryCts = null;
            _retryTask = null;
        }
    }

    public async Task DisconnectAsync()
    {
        StopRetryLoop();
        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch { /* 忽略 */ }
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
