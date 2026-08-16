import type { IncomingMessage, ServerResponse } from 'node:http';
import { resolve, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import type { AppConfig } from './config.js';
import type { SyncService } from './service.js';
import { PairError } from './service.js';
import type { SessionStore, SessionPayload } from './sessions.js';
import { RateLimiter } from './rate.js';
import { parseMultipart } from './multipart.js';
import { clamp, randomHex } from './util.js';
import { extractToken } from './auth.js';

export interface Ctx {
  cfg: AppConfig;
  svc: SyncService;
  sessions: SessionStore;
  rate: RateLimiter;
  latency: { add(us: number): void; avgMs: number; last12: number[] };
  url: URL;
  req: IncomingMessage;
  res: ServerResponse;
  /** 会话载荷(admin/user),未登录为 null */
  actor: SessionPayload | null;
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
    const ip = remoteIp(req);
    if (!ctx.rate.allow('login:' + username + ':' + ip, 5, 600_000)) {
      svc.addAudit('login_locked', '用户 ' + username + ' 触发锁定(IP ' + ip + ')', ip);
      sendJson(res, 429, { error: '尝试过于频繁,已锁定 10 分钟' });
      return true;
    }
    if (username === cfg.adminUsername && password === cfg.adminPassword) {
      ctx.rate.reset('login:' + username + ':' + ip);
      const token = ctx.sessions.create({ role: 'admin' }, cfg.sessionTtlHours);
      svc.addAudit('login_ok', '管理台登录: ' + username, ip);
      sendJson(res, 200, { token, role: 'admin', expiresAt: new Date(Date.now() + cfg.sessionTtlHours * 3600_000).toISOString() });
    } else {
      svc.addAudit('login_fail', '登录失败: ' + username + ' (IP ' + ip + ')', ip);
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
    const userId = url.searchParams.get('userId')?.trim() || null;
    sendJson(res, 200, svc.getHistory(Number.isFinite(offset) ? offset : 0, Number.isFinite(limit) ? limit : 20, userId));
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
    // 用户会话只能操作自己组内的设备;管理端任意
    if (ctx.actor?.role === 'user') {
      const dev = svc.getDevice(id);
      if (!dev || dev.UserId !== ctx.actor.userId) {
        sendJson(res, 403, { error: '无权操作该设备' });
        return true;
      }
    }
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

  // ============ 用户与配对(配对码 + 用户ID + 生成方确认) ============

  // 生成配对码:免认证 + IP 限速;body { deviceId, deviceName }
  // 未绑定设备生成时自动创建用户ID,响应同时返回 userId(界面需展示 用户ID+配对码)
  if (p === '/api/pairing-codes' && method === 'POST') {
    if (!ctx.rate.allow('paircode:' + remoteIp(req), 30, 60_000)) {
      sendJson(res, 429, { error: '操作过于频繁,请稍后再试' });
      return true;
    }
    let deviceId = '';
    let deviceName: string | null = null;
    const body = await readBody(req, 64 * 1024);
    if (body.length > 0) {
      try {
        const j = JSON.parse(body.toString('utf8')) as { deviceId?: unknown; deviceName?: unknown };
        deviceId = typeof j.deviceId === 'string' ? j.deviceId.trim().slice(0, 64) : '';
        deviceName = typeof j.deviceName === 'string' ? j.deviceName.trim().slice(0, 128) : null;
      } catch { /* 忽略 */ }
    }
    if (!deviceId) { sendJson(res, 400, { error: 'deviceId 不能为空' }); return true; }
    const r = svc.generatePairingCode(deviceId, deviceName);
    svc.addAudit('pair_code', '设备 ' + (deviceName || deviceId) + ' 生成配对码', remoteIp(req));
    sendJson(res, 200, r);
    return true;
  }

  // 作废未用配对码(open 状态):免认证
  const mPairRevoke = /^\/api\/pairing-codes\/([^/]+)$/.exec(p);
  if (mPairRevoke && method === 'DELETE') {
    const r = svc.revokePairingRequest(decodeURIComponent(mPairRevoke[1]));
    if (!r) { res.statusCode = 404; res.end(); return true; }
    sendNoContent(res);
    return true;
  }

  // 发起配对:校验 配对码 + 用户ID → 挂起待确认;IP 限速防枚举
  if (p === '/api/pair' && method === 'POST') {
    if (!ctx.rate.allow('pair:' + remoteIp(req), 10, 60_000)) {
      sendJson(res, 429, { error: '尝试过于频繁,请稍后再试' });
      return true;
    }
    const body = await readBody(req, 64 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const pairingCode = typeof json.pairingCode === 'string' ? json.pairingCode.trim().toUpperCase() : '';
    const userId = typeof json.userId === 'string' ? json.userId.trim() : '';
    const deviceId = typeof json.deviceId === 'string' ? json.deviceId.trim().slice(0, 64) : '';
    const deviceName = typeof json.deviceName === 'string' ? json.deviceName.trim().slice(0, 128) : '';
    if (!pairingCode || !userId || !deviceId) { sendJson(res, 400, { error: 'pairingCode、userId、deviceId 不能为空' }); return true; }
    try {
      const r = svc.pair(pairingCode, userId, deviceId, deviceName);
      svc.addAudit('pair_request', '设备 ' + (deviceName || deviceId) + ' 请求配对(用户 ' + userId + ')', remoteIp(req));
      sendJson(res, 200, r);
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 新设备轮询配对结果:免认证(凭 码+自身deviceId)
  if (p === '/api/pair/status' && method === 'GET') {
    const pairingCode = (url.searchParams.get('code') ?? '').trim().toUpperCase();
    const deviceId = url.searchParams.get('deviceId') ?? '';
    if (!pairingCode || !deviceId) { sendJson(res, 400, { error: 'code 与 deviceId 不能为空' }); return true; }
    sendJson(res, 200, svc.pairStatus(pairingCode, deviceId));
    return true;
  }

  // 配对确认/拒绝:管理端会话 | 同组用户会话 | 生成方(码+生成方设备ID)
  if (p === '/api/pairing-requests/confirm' && method === 'POST') {
    const body = await readBody(req, 64 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const pairingCode = typeof json.code === 'string' ? json.code.trim().toUpperCase() : '';
    const action = json.action === 'reject' ? 'reject' : json.action === 'approve' ? 'approve' : null;
    if (!pairingCode || !action) { sendJson(res, 400, { error: 'code 与 action 不能为空' }); return true; }
    let actor:
      | { kind: 'admin' }
      | { kind: 'user'; userId: string }
      | { kind: 'secret'; generatorId: string } = { kind: 'secret', generatorId: '' };
    if (ctx.actor?.role === 'admin') actor = { kind: 'admin' };
    else if (ctx.actor?.role === 'user' && ctx.actor.userId) actor = { kind: 'user', userId: ctx.actor.userId };
    else {
      const generatorId = typeof json.generatorId === 'string' ? json.generatorId.trim().slice(0, 64) : '';
      if (!generatorId) { sendJson(res, 403, { error: '无权确认该配对请求' }); return true; }
      actor = { kind: 'secret', generatorId };
    }
    try {
      const r = svc.confirmPairing(pairingCode, action, actor);
      svc.addAudit(action === 'approve' ? 'pair_approve' : 'pair_reject', '配对码 ' + pairingCode, remoteIp(req));
      sendJson(res, 200, r);
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 待确认请求列表:管理端全部 / 用户会话本组 / 生成方(码+生成方设备ID)
  if (p === '/api/pairing-requests' && method === 'GET') {
    let scope: { kind: 'admin' } | { kind: 'user'; userId: string } | { kind: 'secret'; generatorId: string; code: string };
    if (ctx.actor?.role === 'admin') scope = { kind: 'admin' };
    else if (ctx.actor?.role === 'user' && ctx.actor.userId) scope = { kind: 'user', userId: ctx.actor.userId };
    else {
      const code = (url.searchParams.get('code') ?? '').trim().toUpperCase();
      const generatorId = url.searchParams.get('generatorId') ?? '';
      if (!code || !generatorId) { sendJson(res, 403, { error: '无权查看' }); return true; }
      scope = { kind: 'secret', generatorId, code };
    }
    sendJson(res, 200, svc.listPairingRequests(scope));
    return true;
  }

  // 配对确认后换取用户网页会话
  if (p === '/api/session/pair' && method === 'POST') {
    const body = await readBody(req, 64 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const pairingCode = typeof json.code === 'string' ? json.code.trim().toUpperCase() : '';
    const deviceId = typeof json.deviceId === 'string' ? json.deviceId.trim().slice(0, 64) : '';
    if (!pairingCode || !deviceId) { sendJson(res, 400, { error: 'code 与 deviceId 不能为空' }); return true; }
    try {
      const payload = svc.sessionForPair(pairingCode, deviceId);
      const token = ctx.sessions.create(payload, cfg.sessionTtlHours);
      sendJson(res, 200, { token, role: 'user', userId: payload.userId, expiresAt: new Date(Date.now() + cfg.sessionTtlHours * 3600_000).toISOString() });
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 用户ID:查询(本组/管理端)、改名(本组/管理端,唯一)、列表(管理端)、删除(管理端)
  const mUser = /^\/api\/users\/([^/]+)$/.exec(p);
  if (p === '/api/users' && method === 'GET') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    sendJson(res, 200, svc.listUsers());
    return true;
  }
  if (mUser && method === 'GET') {
    const uid = decodeURIComponent(mUser[1]);
    if (ctx.actor?.role !== 'admin' && !(ctx.actor?.role === 'user' && ctx.actor.userId === uid)) {
      sendJson(res, 401, { error: 'unauthorized' }); return true;
    }
    const u = svc.getUser(uid);
    if (!u) { res.statusCode = 404; res.end(); return true; }
    sendJson(res, 200, u);
    return true;
  }
  if (mUser && method === 'PUT') {
    const uid = decodeURIComponent(mUser[1]);
    if (ctx.actor?.role !== 'admin' && !(ctx.actor?.role === 'user' && ctx.actor.userId === uid)) {
      sendJson(res, 401, { error: 'unauthorized' }); return true;
    }
    const body = await readBody(req, 16 * 1024);
    let name = '';
    try { name = String((JSON.parse(body.toString('utf8')) as { name?: unknown }).name ?? ''); } catch { /* 忽略 */ }
    try {
      svc.renameUser(uid, name);
      svc.addAudit('user_rename', uid + ' → ' + name.trim(), remoteIp(req));
      sendNoContent(res);
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }
  if (mUser && method === 'DELETE') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    try {
      svc.deleteUser(decodeURIComponent(mUser[1]));
      svc.addAudit('user_delete', decodeURIComponent(mUser[1]), remoteIp(req));
      sendNoContent(res);
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 审计日志(管理端)
  if (p === '/api/admin/audit' && method === 'GET') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    const limit = Number(url.searchParams.get('limit') ?? 50);
    sendJson(res, 200, svc.listAudit(Number.isFinite(limit) ? limit : 50));
    return true;
  }

  // ============ /api/stats /api/activities /api/health ============
  if (p === '/api/stats' && method === 'GET') {
    sendJson(res, 200, svc.stats({ avgMs: ctx.latency.avgMs, last12: ctx.latency.last12 }));
    return true;
  }
  if (p === '/api/activities' && method === 'GET') {
    const limit = Number(url.searchParams.get('limit') ?? 20);
    const userId = url.searchParams.get('userId')?.trim() || null;
    sendJson(res, 200, svc.listActivities(Number.isFinite(limit) ? limit : 20, userId));
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
