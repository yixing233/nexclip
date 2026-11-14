# NexClip 推送通道协议(原生 WebSocket)

> v2.0(Tauri 客户端)起,桌面端以**原生 WebSocket** 替代 SignalR 连接
> `/hubs/clipboard`。服务端(node 分支)需按下文改造;改造完成前,旧 WinUI
> 客户端(SignalR)与新 Tauri 客户端(原生 WS)可并存于不同端点。

## 1. 端点与鉴权

- 端点:`GET {server}/hubs/clipboard`(HTTP 升级为 WebSocket)
- **鉴权走请求头,绝不拼进 URL**(URL 会进 nginx access log / 浏览器历史 /
  Referer / 抓包记录,而这里传的是长期有效的设备令牌):

| Header | 说明 |
|---|---|
| `X-Device-Id` | 设备 ID(配对时登记) |
| `X-Device-Token` | 设备令牌(服务端 `/api/pair` 签发) |

- URL query 仅携带**非敏感**的登记信息(服务端据此更新设备平台信息):

```
ws(s)://{host}/hubs/clipboard?deviceId={id}&deviceName={name}&platform={platform}&version={version}
```

- 鉴权失败:服务端在升级阶段返回 **401**(凭证缺失/无效)或 **410**(设备被移除)
  或 **403**;客户端把这三类视为**永久错误,停止重连**(与旧版 DeviceAuthRetryPolicy
  行为一致),等用户重新配对后再下发新连接指令。

## 2. 消息帧

单向为主:服务器 → 客户端 推送事件;客户端 → 服务器 仅 ping 保活。

### 2.1 服务器 → 客户端(事件帧)

```json
{ "event": "ClipboardUpdated", "data": { ...ClipboardEntry } }
{ "event": "FileTransferred", "data": { ...FileTransferInfo } }
{ "event": "FileDeleted", "data": "{fileId}" }
{ "event": "DevicesChanged", "data": null }
```

- `ClipboardEntry`:`{ id, starred, remark, type: "Text"|"Image", text, html,
  imageRef, deviceId, deviceName, isManual, createdAt }`(camelCase,与
  `/api/clipboard` 系列一致)
- `FileTransferInfo`:`{ fileId, fileName, size, contentHash, deviceId,
  deviceName, createdAt, downloadUrl }`
- 未知 `event` 客户端静默忽略(向前兼容)。

### 2.2 客户端 → 服务器(保活)

```json
{ "event": "ping" }
```

客户端同时使用 WebSocket 协议层 Ping/Pong 帧(10s 间隔);应用层 `ping` 帧
作为不支持协议层 ping 的中间件的兜底。服务端建议 30s 无活动即断开。

## 3. 重连策略(与旧版 PushClient 一致)

| 场景 | 行为 |
|---|---|
| 升级返回 401/403/410 | **永久停止**,等待用户重新配对 |
| 其它断开/网络错误 | 指数退避:**0s(首连)→ 2s → 10s → 30s → 60s → 固定 60s** |
| 设置变更(服务器地址/令牌) | 立即以新参数重建连接 |

## 4. 服务端改造要点(server 分支)

1. 在现有 SignalR hub 旁新增原生 WS 升级处理(或替换),升级阶段校验
   `X-Device-Id` / `X-Device-Token`(支持两种头,保持与 REST 一致)。
2. 原有广播点(CloudPut/Files/Devices)在向 SignalR 组广播的同时,向同组
   原生 WS 连接广播上文同构的 JSON 帧。
3. 忽略客户端 `ping` 应用层帧(可回 `{"event":"pong"}`),协议层 Ping 用
   ws 库的自动 Pong。
4. `POST /hubs/clipboard/negotiate`(SignalR 协商端点)对原生 WS 客户端
   **不再需要**;如保留,原生客户端不会调用。
