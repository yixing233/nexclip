import type { IncomingMessage, ServerResponse } from 'node:http';
import { resolve, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import type { AppConfig } from './config.js';
import type { SyncService } from './service.js';
import { PairError } from './service.js';
import type { SessionStore } from './sessions.js';
import { parseMultipart } from './multipart.js';
import { clamp, randomHex } from './util.js';
import { extractToken } from './auth.js';

export interface Ctx {
  cfg: AppConfig;
  svc: SyncService;
  sessions: SessionStore;
  latency: { add(us: number): void; avgMs: number; last12: number[] };
  url: URL;
  req: IncomingMessage;
  res: ServerResponse;
}

export function sendJson(res: ServerResponse, status: number, body: unknown): void {
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.end(JSON.stringify(body));
}

export function sendNoContent(res: ServerResponse): void {
  res.statusCode = 204;
  res.end();
}

/** 读取请求体(带大小上限) */
export function readBody(req: IncomingMessage, limitBytes: number): Promise<Buffer> {
  return new Promise((resolvePromise, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    req.on('data', (c: Buffer) => {
      size += c.length;
      if (size > limitBytes) {
        reject(new Error('body-too-large'));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on('end', () => resolvePromise(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

const IP_CT: Record<string, string> = {
  '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif',
  '.webp': 'image/webp', '.bmp': 'image/bmp',
};

/** 主路由:/api/* 与 /SyncClipboard.json */
export async function handleApi(ctx: Ctx): Promise<boolean> {
  const { url, req, res, svc, cfg } = ctx;
  const p = url.pathname;
  const method = req.method ?? 'GET';

  // ============ 管理台账密登录 ============
  if (p === '/api/login' && method === 'POST') {
    const body = await readBody(req, 16 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const username = typeof json.username === 'string' ? json.username.trim() : '';
    const password = typeof json.password === 'string' ? json.password : '';
    if (username === cfg.adminUsername && password === cfg.adminPassword) {
      const token = ctx.sessions.create(cfg.sessionTtlHours);
      sendJson(res, 200, { token, expiresAt: new Date(Date.now() + cfg.sessionTtlHours * 3600_000).toISOString() });
    } else {
      sendJson(res, 401, { error: '用户名或密码错误' });
    }
    return true;
  }
  if (p === '/api/logout' && method === 'POST') {
    const token = extractToken(req);
    if (token) ctx.sessions.revoke(token);
    sendNoContent(res);
    return true;
  }

  // ============ /api/clipboard ============
  if (p === '/api/clipboard' && method === 'GET') {
    const cur = svc.getCurrent();
    if (!cur) { sendNoContent(res); return true; }
    sendJson(res, 200, toEntryDto(svc, cur));
    return true;
  }

  if (p === '/api/clipboard' && method === 'PUT') {
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text || text.length > 500_000) {
      sendJson(res, 400, { error: 'text 不能为空且不超过 500KB' });
      return true;
    }
    const deviceId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : 'web-' + randomHex(4);
    const deviceName = typeof json.deviceName === 'string' && json.deviceName ? json.deviceName : deviceId;
    const platform = typeof json.platform === 'string' ? json.platform : null;
    const version = typeof json.version === 'string' ? json.version : null;
    const { entry, unchanged } = svc.uploadText(text, deviceId, deviceName, platform, version, remoteIp(req), true);
    sendJson(res, 200, { ...entry, unchanged });
    return true;
  }

  if (p === '/api/clipboard/image' && method === 'POST') {
    const body = await readBody(req, cfg.maxImageSizeBytes + 64 * 1024).catch(() => { sendJson(res, 413, { error: '请求体过大' }); return null as unknown as Buffer; });
    if (!body) return true;
    const mp = parseMultipart(req.headers['content-type'], body);
    if (!mp.file || mp.file.data.length === 0) {
      sendJson(res, 400, { error: '缺少图片文件' });
      return true;
    }
    if (mp.file.data.length > cfg.maxImageSizeBytes) {
      sendJson(res, 400, { error: '图片超过大小限制(' + Math.floor(cfg.maxImageSizeBytes / 1024 / 1024) + 'MB)' });
      return true;
    }
    const deviceId = mp.fields.deviceId || 'web-' + randomHex(4);
    const deviceName = mp.fields.deviceName || deviceId;
    const entry = svc.uploadImage(mp.file.filename || 'image', mp.file.data, deviceId, deviceName, remoteIp(req));
    sendJson(res, 200, entry);
    return true;
  }

  if (p === '/api/clipboard/history' && method === 'GET') {
    const offset = Number(url.searchParams.get('offset') ?? 0);
    const limit = Number(url.searchParams.get('limit') ?? 20);
    sendJson(res, 200, svc.getHistory(Number.isFinite(offset) ? offset : 0, Number.isFinite(limit) ? limit : 20));
    return true;
  }

  if (p === '/api/clipboard/history' && method === 'DELETE') {
    svc.clearHistory();
    sendNoContent(res);
    return true;
  }

  if (p === '/api/clipboard/send' && method === 'POST') {
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text) { sendJson(res, 400, { error: 'text 不能为空' }); return true; }
    const deviceId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : 'web-' + randomHex(4);
    const deviceName = typeof json.deviceName === 'string' && json.deviceName ? json.deviceName : deviceId;
    const rawTargets = Array.isArray(json.deviceIds) ? json.deviceIds.filter((x): x is string => typeof x === 'string' && !!x.trim()) : [];
    const targets = [...new Set(rawTargets)];
    // 未指定目标 → 广播全员(旧行为);指定 → 只写库 + 定向通知
    const { entry } = svc.uploadText(text, deviceId, deviceName, 'Web', null, remoteIp(req), targets.length === 0);
    if (targets.length > 0) {
      svc.broadcastTo(entry, new Set(targets));
    }
    sendJson(res, 200, entry);
    return true;
  }

  const mEntry = /^\/api\/clipboard\/(\d+)$/.exec(p);
  if (mEntry) {
    const id = Number(mEntry[1]);
    if (method === 'GET') {
      const e = svc.getById(id);
      if (!e) { res.statusCode = 404; res.end(); return true; }
      sendJson(res, 200, toEntryDto(svc, e));
      return true;
    }
    if (method === 'DELETE') {
      svc.deleteEntry(id);
      sendNoContent(res);
      return true;
    }
  }

  // ============ /api/images/{**path} ============
  const mImg = /^\/api\/images\/(.+)$/.exec(p);
  if (mImg && method === 'GET') {
    const root = resolve(cfg.imageStoragePath);
    const full = resolve(join(root, decodeURIComponent(mImg[1])));
    if (!full.toLowerCase().startsWith(root.toLowerCase()) || !existsSync(full)) {
      res.statusCode = 404; res.end(); return true;
    }
    const ext = full.slice(full.lastIndexOf('.')).toLowerCase();
    res.statusCode = 200;
    res.setHeader('Content-Type', IP_CT[ext] ?? 'application/octet-stream');
    res.end(readFileSync(full));
    return true;
  }

  // ============ /api/devices ============
  if (p === '/api/devices' && method === 'GET') {
    sendJson(res, 200, svc.listDevices());
    return true;
  }
  const mDev = /^\/api\/devices\/(.+)$/.exec(p);
  if (mDev) {
    const id = decodeURIComponent(mDev[1]);
    if (method === 'PUT') {
      const body = await readBody(req, 64 * 1024);
      let name = '';
      try { name = String((JSON.parse(body.toString('utf8')) as { name?: unknown }).name ?? ''); } catch { /* 忽略 */ }
      if (!svc.renameDevice(id, name)) { res.statusCode = 404; res.end(); return true; }
      sendNoContent(res);
      return true;
    }
    if (method === 'DELETE') {
      if (!svc.removeDevice(id)) { res.statusCode = 404; res.end(); return true; }
      sendNoContent(res);
      return true;
    }
  }

  // ============ 设备配对(设计文档 §3.2) ============
  // 生成配对码:免认证;可选 body { deviceId, deviceName } 用于登记生成方设备(自声明)
  if (p === '/api/pairing-codes' && method === 'POST') {
    let deviceId: string | null = null;
    let deviceName: string | null = null;
    const body = await readBody(req, 64 * 1024);
    if (body.length > 0) {
      try {
        const j = JSON.parse(body.toString('utf8')) as { deviceId?: unknown; deviceName?: unknown };
        deviceId = typeof j.deviceId === 'string' && j.deviceId ? j.deviceId.trim() : null;
        deviceName = typeof j.deviceName === 'string' && j.deviceName ? j.deviceName.trim() : null;
      } catch { /* 无 body 或非 JSON:生成方信息可缺省 */ }
    }
    sendJson(res, 200, svc.createPairingCode(deviceId, deviceName));
    return true;
  }
  const mPairRevoke = /^\/api\/pairing-codes\/([^/]+)$/.exec(p);
  if (mPairRevoke && method === 'DELETE') {
    if (!svc.revokePairingCode(decodeURIComponent(mPairRevoke[1]))) {
      res.statusCode = 404; res.end(); return true;
    }
    sendNoContent(res);
    return true;
  }
  if (p === '/api/pair' && method === 'POST') {
    const body = await readBody(req, 64 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const pairingCode = typeof json.pairingCode === 'string' ? json.pairingCode.trim().toUpperCase() : '';
    const deviceId = typeof json.deviceId === 'string' ? json.deviceId.trim() : '';
    const deviceName = typeof json.deviceName === 'string' ? json.deviceName.trim() : '';
    if (!pairingCode || !deviceId) { sendJson(res, 400, { error: 'pairingCode 与 deviceId 不能为空' }); return true; }
    try {
      sendJson(res, 200, svc.pair(pairingCode, deviceId, deviceName));
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // ============ /api/stats /api/activities /api/health ============
  if (p === '/api/stats' && method === 'GET') {
    sendJson(res, 200, svc.stats({ avgMs: ctx.latency.avgMs, last12: ctx.latency.last12 }));
    return true;
  }
  if (p === '/api/activities' && method === 'GET') {
    const limit = Number(url.searchParams.get('limit') ?? 20);
    sendJson(res, 200, svc.listActivities(Number.isFinite(limit) ? limit : 20));
    return true;
  }
  if (p === '/api/health' && method === 'GET') {
    sendJson(res, 200, { status: 'ok', time: new Date().toISOString() });
    return true;
  }

  // ============ 旧协议 /SyncClipboard.json ============
  if (p === '/SyncClipboard.json' && method === 'GET') {
    const cur = svc.getCurrent();
    if (!cur) { sendJson(res, 200, { text: '', deviceId: '', deviceName: '', createdAt: '' }); return true; }
    sendJson(res, 200, { text: cur.Text ?? '', deviceId: cur.DeviceId, deviceName: cur.DeviceName ?? '', createdAt: toIsoStr(cur.CreatedAt) });
    return true;
  }
  if (p === '/SyncClipboard.json' && method === 'PUT') {
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text) { sendJson(res, 400, { error: 'text 不能为空' }); return true; }
    const deviceId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : 'legacy';
    const deviceName = typeof json.deviceName === 'string' && json.deviceName ? json.deviceName : 'Legacy Client';
    svc.uploadText(text, deviceId, deviceName, null, null, remoteIp(req), true);
    sendJson(res, 200, { ok: true });
    return true;
  }

  return false;
}

function toEntryDto(svc: SyncService, e: { Id: number; Type: string; Text: string | null; ImageRef: string | null; DeviceId: string; DeviceName: string | null; CreatedAt: string }) {
  return {
    id: e.Id, type: e.Type, text: e.Text, imageRef: e.ImageRef,
    deviceId: e.DeviceId, deviceName: e.DeviceName, createdAt: toIsoStr(e.CreatedAt),
  };
}

function toIsoStr(dbValue: string): string {
  return dbValue.replace(' ', 'T').replace('Z', '') + 'Z';
}

/**
 * 规范化客户端 IP:
 * - 优先 x-forwarded-for(反向代理场景,取最左的真实客户端)
 * - 去掉 IPv4-mapped 前缀(::ffff:192.168.0.1 → 192.168.0.1)
 * - 本机访问显示为 127.0.0.1(而非 ::1)
 */
function remoteIp(req: IncomingMessage): string | null {
  const fwd = req.headers['x-forwarded-for'];
  let ip: string | null = null;
  if (typeof fwd === 'string' && fwd.trim()) {
    ip = fwd.split(',')[0].trim();
  } else {
    ip = req.socket.remoteAddress ?? null;
  }
  if (!ip) return null;
  // IPv4-mapped IPv6 → 纯 IPv4(::ffff:a.b.c.d)
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(ip);
  if (mapped) return mapped[1];
  // 本机回环统一显示
  if (ip === '::1') return '127.0.0.1';
  return ip;
}
