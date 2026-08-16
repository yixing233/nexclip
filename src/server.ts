import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { existsSync } from 'node:fs';
import { loadConfig } from './config.js';
import { openDb } from './db.js';
import { LatencyTracker } from './latency.js';
import { routeClass, checkSession } from './auth.js';
import { SessionStore } from './sessions.js';
import { RateLimiter } from './rate.js';
import { SignalRHub } from './signalr.js';
import { SyncService } from './service.js';
import { handleApi, sendJson, type Ctx } from './controllers.js';
import { serveStatic } from './static.js';

const cfg = loadConfig();
const db = openDb(cfg);
const hub = new SignalRHub();
const sessions = new SessionStore();
const rate = new RateLimiter();
const svc = new SyncService(db, cfg, hub);
const latency = new LatencyTracker();

hub.onConnected = (deviceId, deviceName, platform, version) => svc.registerHubDevice(deviceId, deviceName, platform, version);

// 心跳:45s 刷新在线设备 LastSeenAt(与 .NET HubHeartbeatService 一致)
const heartbeatTimer = setInterval(() => {
  try { svc.heartbeat(); } catch (e) { console.warn('[heartbeat]', e); }
}, 45_000);
heartbeatTimer.unref();

const server = createServer(async (req: IncomingMessage, res: ServerResponse) => {
  const started = performance.now();
  const url = new URL(req.url ?? '/', 'http://localhost');
  const p = url.pathname;

  // CORS(与 .NET 一致:放开)
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, Accept');
  res.setHeader('Access-Control-Allow-Methods', 'GET, PUT, POST, DELETE, OPTIONS');
  if (req.method === 'OPTIONS') {
    res.statusCode = 204;
    res.end();
    return;
  }

  // 鉴权:角色矩阵(open 免认证;user 需任意会话;admin 需管理台会话)
  const cls = routeClass(req.method ?? 'GET', p);
  let actor: ReturnType<typeof checkSession> = null;
  if (cls !== 'open') {
    actor = checkSession(req, sessions);
    if (!actor || (cls === 'admin' && actor.role !== 'admin')) {
      res.statusCode = 401;
      res.setHeader('Content-Type', 'application/json; charset=utf-8');
      res.end(JSON.stringify({ error: 'unauthorized' }));
      return;
    }
  }

  // negotiate(必须走统一鉴权:query access_token 也接受)
  if (p === '/hubs/clipboard/negotiate') {
    const r = hub.negotiate(url);
    sendJson(res, r.status, r.body);
    return;
  }

  // API / 旧协议
  const ctx: Ctx = { cfg, svc, sessions, rate, latency: latency as unknown as Ctx['latency'], url, req, res, actor };
  const handled = await handleApi(ctx).catch((e) => {
    if (e.message === 'body-too-large') {
      res.statusCode = 413;
      res.setHeader('Content-Type', 'application/json; charset=utf-8');
      res.end(JSON.stringify({ error: '请求体过大' }));
    } else {
      console.error('[api]', e);
      res.statusCode = 500;
      res.setHeader('Content-Type', 'application/json; charset=utf-8');
      res.end(JSON.stringify({ error: 'internal' }));
    }
    return true;
  });
  if (!handled) {
    // 静态托管 + SPA 回退
    const served = serveStatic(cfg.webDist, req, res);
    if (!served) {
      res.statusCode = 404;
      res.end();
    }
  }

  // 耗时采样(与 .NET 一致:仅 /api 且状态 < 400)
  const ms = performance.now() - started;
  if (p.startsWith('/api/') && res.statusCode < 400) {
    latency.add(Math.round(ms * 1000));
  }
});

hub.attach(server);

server.listen(cfg.port, () => {
  console.log('[SyncClipboard Node Server] 端口 ' + cfg.port + ', 鉴权: 管理台账密 + 设备令牌(配对)');
  console.log('[SyncClipboard Node Server] 数据库: ' + cfg.databasePath);
  console.log('[SyncClipboard Node Server] 静态托管: ' + (cfg.webDist && existsSync(cfg.webDist) ? cfg.webDist : '(未找到 web/dist)'));
});

// 优雅退出
for (const sig of ['SIGINT', 'SIGTERM'] as const) {
  process.on(sig, () => {
    clearInterval(heartbeatTimer);
    hub.dispose();
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 1500).unref();
  });
}
