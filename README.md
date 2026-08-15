# SyncClipboard Node Server

ASP.NET Core 版服务端的 Node.js(TypeScript) 重写版,API 契约与 SignalR 线协议完全兼容,
Web / WinUI3 桌面端 / Android 三个客户端零改动。

## 运行

```powershell
cd server-node
npm install
npm run build
node dist/server.js
# 或: npm start
```

- 默认端口 5033;配置在 `config.json`(AuthToken / 端口 / 数据库路径 / 历史上限等)。
- 环境变量覆盖:`SC_PORT` `SC_AUTH_TOKEN` `SC_DB_PATH` `SC_IMAGE_PATH` `SC_MAX_HISTORY`
  `SC_MAX_IMAGE_BYTES` `SC_ONLINE_THRESHOLD_SECONDS` `SC_WEB_DIST`。
- 数据库直接复用旧 `server/data/syncclipboard.db`(表结构与 EF Core 生成的完全一致),
  数据零迁移;图片存 `data/images/`。
- 实时推送:实现 ASP.NET Core SignalR JSON 线协议(negotiate + WebSocket),支持
  `ClipboardUpdated` / `ClipboardCleared` 事件与按 deviceId 的定向推送。
- 依赖仅 `ws`;SQLite 用 Node 内置 `node:sqlite`(需 Node >= 22.5,推荐 24+)。

## 与 .NET 版差异

- 传输仅支持 WebSocket(negate 只通告 WebSockets);局域网场景无影响。
- 其余行为(去重、历史裁剪、活动日志、统计、鉴权、静态托管 SPA 回退)与 .NET 版逐项对齐。
