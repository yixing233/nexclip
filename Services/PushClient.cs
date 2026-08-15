using Microsoft.AspNetCore.SignalR.Client;
using SyncClipboard.Desktop.Models;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// SignalR 推送客户端(设计文档 §6):/hubs/clipboard + access_token 认证 + 自动重连。
/// 回调来自 SignalR 线程,调用方需自行切换到 UI 线程。
/// </summary>
public sealed class PushClient : IAsyncDisposable
{
    private HubConnection? _hub;

    /// <summary>收到 ClipboardUpdated(entry)。</summary>
    public event Action<ClipboardEntry>? EntryReceived;

    /// <summary>连接状态变化:connecting/connected/reconnecting/disconnected。</summary>
    public event Action<string>? StateChanged;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, string token)
    {
        await DisconnectAsync();

        var endpoint = new Uri(new Uri(serverUrl.TrimEnd('/')), "/hubs/clipboard");
        var builder = new HubConnectionBuilder()
            .WithUrl(endpoint, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
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
            // 保留 hub,由自动重连机制继续尝试
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch { /* 忽略 */ }
            _hub = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
