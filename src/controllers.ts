import type { IncomingMessage, ServerResponse } from 'node:http';
import { resolve, join } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';
import type { AppConfig } from './config.js';
import { SERVER_VERSION } from './config.js';
import type { SyncService } from './service.js';
import { PairError } from './service.js';
import type { SessionStore, SessionPayload } from './sessions.js';
import { RateLimiter } from './rate.js';
import { parseMultipart } from './multipart.js';
import { clamp, randomHex, detectPlatform, extractClientIpv4, sha256Hex } from './util.js';
import { extractToken, extractDeviceId, extractDeviceToken } from './auth.js';

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
  /** Bearer 用户会话关联设备已被移除或重新配对。 */
  invalidDeviceSession: boolean;
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

function sendApiError(res: ServerResponse, status: number, error: string): void {
  sendJson(res, status, { error });
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
  const requestDeviceId = extractDeviceId(req);
  const requestDeviceToken = extractDeviceToken(req);
  const deviceActor = requestDeviceId && requestDeviceToken
    ? svc.authenticateDevice(requestDeviceId, requestDeviceToken)
    : null;
  const rejectDeviceCredential = (): boolean => {
    const status = svc.deviceCredentialStatus(requestDeviceId, requestDeviceToken);
    if (status === 'revoked') {
      sendApiError(res, 410, '设备已被移除,请重新配对');
    } else if (status === 'missing') {
      sendApiError(res, 401, '缺少设备凭证,请先完成配对');
    } else {
      sendApiError(res, 401, '设备凭证无效,请重新配对');
    }
    return false;
  };
  const requireDeviceOrSession = (): boolean => {
    if (ctx.actor || deviceActor) return true;
    if (ctx.invalidDeviceSession) {
      sendApiError(res, 410, '网页会话关联的设备已被移除或重新配对,请重新登录');
      return false;
    }
    return rejectDeviceCredential();
  };
  const rejectDeviceOrSessionCredential = (): boolean => {
    if (ctx.invalidDeviceSession) {
      sendApiError(res, 410, '网页会话关联的设备已被移除或重新配对,请重新登录');
      return false;
    }
    return rejectDeviceCredential();
  };
  const requireDeviceOnly = (): boolean => {
    if (deviceActor) return true;
    return rejectDeviceCredential();
  };

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
      const token = ctx.sessions.create({ role: 'admin', username }, cfg.sessionTtlHours);
      svc.addAudit('login_ok', '管理台登录: ' + username, ip);
      sendJson(res, 200, { token, role: 'admin', username, expiresAt: new Date(Date.now() + cfg.sessionTtlHours * 3600_000).toISOString() });
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

  // 当前会话信息(侧栏展示用户名/角色)
  if (p === '/api/me' && method === 'GET') {
    if (!ctx.actor) { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    sendJson(res, 200, {
      role: ctx.actor.role,
      username: ctx.actor.username ?? null,
      userId: ctx.actor.userId ?? null,
      deviceId: ctx.actor.deviceId ?? null,
      version: SERVER_VERSION,
    });
    return true;
  }

  // ============ /api/clipboard ============
  if (p === '/api/clipboard' && method === 'GET') {
    if (!requireDeviceOrSession()) return true;
    const maxAgeRaw = url.searchParams.get('maxAgeSeconds');
    const maxAgeSeconds = maxAgeRaw ? Number(maxAgeRaw) : null;
    const cur = svc.getCurrent();
    if (!cur) { sendNoContent(res); return true; }
    if (maxAgeSeconds && maxAgeSeconds > 0) {
      const ageSec = (Date.now() - new Date(cur.CreatedAt).getTime()) / 1000;
      if (ageSec > maxAgeSeconds) {
        sendNoContent(res);
        return true;
      }
    }
    sendJson(res, 200, toEntryDto(svc, cur));
    return true;
  }

  if (p === '/api/clipboard' && method === 'PUT') {
    if (!requireDeviceOrSession()) return true;
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text || text.length > 500_000) {
      sendJson(res, 400, { error: 'text 不能为空且不超过 500KB' });
      return true;
    }
    const requestedId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : 'web-' + randomHex(4);
    const deviceId = deviceActor?.Id ?? ctx.actor?.deviceId ?? requestedId;
    if (deviceActor && requestedId !== deviceActor.Id) { sendApiError(res, 403, '上传设备身份与凭证不匹配'); return true; }
    const deviceName = typeof json.deviceName === 'string' && json.deviceName ? json.deviceName : deviceId;
    const platform = typeof json.platform === 'string' ? json.platform : null;
    const version = typeof json.version === 'string' ? json.version : null;
    const isManual = Boolean(json.isManual);
    const { entry, unchanged } = svc.uploadText(text, deviceId, deviceName, platform, version, remoteIp(req), true, isManual);
    sendJson(res, 200, { ...entry, unchanged });
    return true;
  }

  if (p === '/api/clipboard/image' && method === 'POST') {
    if (!requireDeviceOrSession()) return true;
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
    const requestedId = mp.fields.deviceId || 'web-' + randomHex(4);
    const deviceId = deviceActor?.Id ?? ctx.actor?.deviceId ?? requestedId;
    if (deviceActor && requestedId !== deviceActor.Id) { sendApiError(res, 403, '上传设备身份与凭证不匹配'); return true; }
    const deviceName = mp.fields.deviceName || deviceId;
    const explicitPlat = mp.fields.platform?.trim() || null;
    const platform = detectPlatform(explicitPlat, req.headers['user-agent'], deviceName);
    const version = mp.fields.version?.trim() || null;
    const isManual = mp.fields.isManual === 'true' || mp.fields.isManual === '1';
    const entry = svc.uploadImage(mp.file.filename || 'image', mp.file.data, deviceId, deviceName, remoteIp(req), platform, version, isManual);
    sendJson(res, 200, entry);
    return true;
  }

  if (p === '/api/clipboard/history' && method === 'GET') {
    if (!requireDeviceOrSession()) return true;
    const offset = Number(url.searchParams.get('offset') ?? 0);
    const limit = Number(url.searchParams.get('limit') ?? 20);
    const requestedUserId = url.searchParams.get('userId')?.trim() || null;
    const userId = ctx.actor?.role === 'admin'
      ? requestedUserId
      : ctx.actor?.role === 'user'
        ? ctx.actor.userId ?? null
        : deviceActor?.UserId ?? null;
    const q = url.searchParams.get('q');
    sendJson(res, 200, svc.getHistory(Number.isFinite(offset) ? offset : 0, Number.isFinite(limit) ? limit : 20, userId, q));
    return true;
  }

  if (p === '/api/clipboard/history' && method === 'DELETE') {
    svc.clearHistory();
    sendNoContent(res);
    return true;
  }

  if (p === '/api/clipboard/send' && method === 'POST') {
    if (!requireDeviceOrSession()) return true;
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text) { sendJson(res, 400, { error: 'text 不能为空' }); return true; }
    const requestedId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : 'web-' + randomHex(4);
    const deviceId = deviceActor?.Id ?? ctx.actor?.deviceId ?? requestedId;
    if (deviceActor && requestedId !== deviceActor.Id) { sendApiError(res, 403, '上传设备身份与凭证不匹配'); return true; }
    const deviceName = typeof json.deviceName === 'string' && json.deviceName ? json.deviceName : (deviceActor?.Name ?? deviceId);
    const rawTargets = Array.isArray(json.deviceIds) ? json.deviceIds.filter((x): x is string => typeof x === 'string' && !!x.trim()) : [];
    const targets = [...new Set(rawTargets)];
    // 未指定目标 → 广播全员(旧行为);指定 → 只写库 + 定向通知
    const { entry } = svc.uploadText(text, deviceId, deviceName, deviceActor?.Platform ?? 'Web', null, remoteIp(req), targets.length === 0, true);
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
      if (!requireDeviceOrSession()) return true;
      const e = svc.getById(id);
      if (!e) { res.statusCode = 404; res.end(); return true; }
      sendJson(res, 200, toEntryDto(svc, e));
      return true;
    }
    if (method === 'DELETE') {
      // 用户会话只能删除自己组的条目;管理端任意
      if (ctx.actor?.role === 'user') {
        const e = svc.getById(id);
        const dev = e ? svc.getDevice(e.DeviceId) : null;
        if (!dev || dev.UserId !== ctx.actor.userId) {
          sendJson(res, 403, { error: '无权删除该条目' });
          return true;
        }
      }
      svc.deleteEntry(id);
      sendNoContent(res);
      return true;
    }
  }

  // ============ /api/images/{**path} ============
  const mImg = /^\/api\/images\/(.+)$/.exec(p);
  if (mImg && method === 'GET') {
    const root = resolve(svc.getImageStoragePath());
    const full = resolve(join(root, decodeURIComponent(mImg[1])));
    if (!full.toLowerCase().startsWith(root.toLowerCase()) || !existsSync(full)) {
      res.statusCode = 404; res.end(); return true;
    }
    const ext = full.slice(full.lastIndexOf('.')).toLowerCase();
    res.statusCode = 200;
    res.setHeader('Content-Type', IP_CT[ext] ?? 'application/octet-stream');
    res.setHeader('Cache-Control', 'public, max-age=31536000, immutable');
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.end(readFileSync(full));
    return true;
  }

  // ============ /api/devices ============
  if (p === '/api/devices' && method === 'GET') {
    const requestedDevId = extractDeviceId(req) || url.searchParams.get('deviceId')?.trim();
    let userId: string | null = null;
    if (ctx.actor?.role === 'user') {
      userId = ctx.actor.userId ?? null;
    } else if (deviceActor?.UserId) {
      userId = deviceActor.UserId;
    } else if (requestedDevId) {
      const dev = svc.getDevice(requestedDevId);
      userId = dev?.UserId ?? null;
    }
    sendJson(res, 200, svc.listDevices(ctx.actor?.role === 'admin' ? null : userId));
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

  // ============ 用户与配对(配对码 + 用户ID + 生成方确认) ============

  // 生成配对码:免认证 + IP 限速;body { deviceId, deviceName }
  // 未绑定设备生成时自动创建用户ID,响应同时返回 userId(界面需展示 用户ID+配对码)
  if (p === '/api/pairing-codes' && method === 'POST') {
    if (ctx.invalidDeviceSession && !deviceActor) { rejectDeviceOrSessionCredential(); return true; }
    if (!ctx.rate.allow('paircode:' + remoteIp(req), 30, 60_000)) {
      sendJson(res, 429, { error: '操作过于频繁,请稍后再试' });
      return true;
    }
    let deviceId = '';
    let deviceName: string | null = null;
    let explicitPlat: string | null = null;
    const body = await readBody(req, 64 * 1024);
    if (body.length > 0) {
      try {
        const j = JSON.parse(body.toString('utf8')) as { deviceId?: unknown; deviceName?: unknown; platform?: unknown };
        deviceId = typeof j.deviceId === 'string' ? j.deviceId.trim().slice(0, 64) : '';
        deviceName = typeof j.deviceName === 'string' ? j.deviceName.trim().slice(0, 128) : null;
        explicitPlat = typeof j.platform === 'string' ? j.platform.trim().slice(0, 32) : null;
      } catch { /* 忽略 */ }
    }
    if (!deviceId) { sendJson(res, 400, { error: 'deviceId 不能为空' }); return true; }
    const suppliedId = requestDeviceId || deviceId;
    if (suppliedId !== deviceId) { sendApiError(res, 403, '设备身份不匹配'); return true; }
    const trustedUserId = ctx.actor?.role === 'user' ? ctx.actor.userId ?? null : null;
    const platform = detectPlatform(explicitPlat, req.headers['user-agent'], deviceName);
    try {
      const r = svc.generatePairingCode(deviceId, deviceName, remoteIp(req), requestDeviceToken, trustedUserId, platform);
      const proto = req.headers['x-forwarded-proto'] || 'https';
      const host = req.headers['x-forwarded-host'] || req.headers.host || 'localhost';
      const origin = `${proto}://${host}`;
      const qrPayload = `${origin}/index?pairCode=${r.code}`;
      sendJson(res, 200, { ...r, qrPayload });
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 单向即入配对(方案 1 扫码直连 + 方案 2 纯 6 位数字验证码): 免认证 + IP 限速
  if ((p === '/api/pair' || p === '/api/pair/direct') && method === 'POST') {
    if (!ctx.rate.allow('pair:' + remoteIp(req), 15, 60_000)) {
      sendJson(res, 429, { error: '尝试过于频繁,请稍后再试' });
      return true;
    }
    const body = await readBody(req, 64 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    // 兼容 code 或 pairingCode 字段名
    const code = (typeof json.code === 'string' ? json.code : typeof json.pairingCode === 'string' ? json.pairingCode : '').trim().toUpperCase();
    const deviceId = typeof json.deviceId === 'string' ? json.deviceId.trim().slice(0, 64) : '';
    const deviceName = typeof json.deviceName === 'string' ? json.deviceName.trim().slice(0, 128) : '';
    const explicitPlat = typeof json.platform === 'string' ? json.platform.trim().slice(0, 32) : null;
    const platform = detectPlatform(explicitPlat, req.headers['user-agent'], deviceName);
    const version = typeof json.version === 'string' ? json.version.trim().slice(0, 64) : null;
    if (!code || !deviceId) { sendJson(res, 400, { error: '配对码与设备ID不能为空' }); return true; }
    try {
      const r = svc.pair(code, deviceId, deviceName, remoteIp(req), platform, version);
      const sessionToken = ctx.sessions.create({
        role: 'user',
        userId: r.userId,
        username: r.userId,
        deviceId,
        deviceTokenHash: sha256Hex(r.deviceToken),
      }, 24 * 30);
      sendJson(res, 200, {
        status: 'approved',
        userId: r.userId,
        token: sessionToken,
        deviceToken: r.deviceToken,
      });
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // 作废未完成配对码(open 状态): 免认证/设备/用户认证
  const mPairRevoke = /^\/api\/pairing-codes\/([^/]+)$/.exec(p);
  if (mPairRevoke && method === 'DELETE') {
    let actor: { kind: 'admin' } | { kind: 'user'; userId: string } | { kind: 'device'; deviceId: string } | null = null;
    if (ctx.actor?.role === 'admin') actor = { kind: 'admin' };
    else if (ctx.actor?.role === 'user' && ctx.actor.userId) actor = { kind: 'user', userId: ctx.actor.userId };
    else if (deviceActor) actor = { kind: 'device', deviceId: deviceActor.Id };
    else { rejectDeviceOrSessionCredential(); return true; }
    try {
      const ok = svc.revokePairingRequest(decodeURIComponent(mPairRevoke[1]).trim().toUpperCase(), actor);
      if (!ok) { sendApiError(res, 404, '配对码不存在、已处理或已过期'); return true; }
      sendNoContent(res);
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

  // 管理台运行设置:历史上限(读取/修改,修改即持久化并立即生效)
  if (p === '/api/admin/settings' && method === 'GET') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    sendJson(res, 200, {
      maxHistoryCount: svc.getMaxHistoryCount(),
      imageStoragePath: svc.getImageStoragePath(),
      databasePath: cfg.databasePath,
    });
    return true;
  }
  if (p === '/api/admin/settings' && method === 'PUT') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    const body = await readBody(req, 16 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    // 历史上限(可选)
    if (json.maxHistoryCount !== undefined) {
      const n = Number(json.maxHistoryCount);
      if (!Number.isFinite(n) || n < 100 || n > 100_000) {
        sendJson(res, 400, { error: '历史上限需在 100 - 100000 之间' });
        return true;
      }
      const applied = svc.setMaxHistoryCount(n);
      svc.addAudit('settings_update', '历史上限 → ' + applied, remoteIp(req));
    }
    // 图片存储位置(可选;迁移现有文件)
    let storageApplied = null;
    if (typeof json.imageStoragePath === 'string') {
      try {
        const r = svc.setImageStoragePath(json.imageStoragePath);
        storageApplied = r;
        svc.addAudit('settings_update', '图片存储位置 → ' + r.path + (r.moved > 0 ? ' (迁移 ' + r.moved + ' 项)' : ''), remoteIp(req));
      } catch (e) {
        if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
        throw e;
      }
    }
    sendJson(res, 200, {
      maxHistoryCount: svc.getMaxHistoryCount(),
      imageStoragePath: svc.getImageStoragePath(),
      ...(storageApplied ? { storageApplied } : {}),
    });
    return true;
  }

  // 目录浏览(管理端,用于选择存储位置)
  if (p === '/api/admin/storage/browse' && method === 'GET') {
    if (ctx.actor?.role !== 'admin') { sendJson(res, 401, { error: 'unauthorized' }); return true; }
    const path = url.searchParams.get('path');
    try {
      sendJson(res, 200, svc.browseDirectory(path));
    } catch (e) {
      if (e instanceof PairError) { sendJson(res, e.status, { error: e.message }); return true; }
      throw e;
    }
    return true;
  }

  // ============ /api/stats /api/activities /api/health ============
  if (p === '/api/stats' && method === 'GET') {
    sendJson(res, 200, { ...svc.stats({ avgMs: ctx.latency.avgMs, last12: ctx.latency.last12 }), version: SERVER_VERSION });
    return true;
  }
  if (p === '/api/activities' && method === 'GET') {
    const limit = Number(url.searchParams.get('limit') ?? 20);
    const userId = url.searchParams.get('userId')?.trim() || null;
    sendJson(res, 200, svc.listActivities(Number.isFinite(limit) ? limit : 20, userId));
    return true;
  }
  if (p === '/api/health' && method === 'GET') {
    sendJson(res, 200, { status: 'ok', version: SERVER_VERSION, time: new Date().toISOString() });
    return true;
  }

  // ============ 旧协议 /SyncClipboard.json ============
  if (p === '/SyncClipboard.json' && method === 'GET') {
    if (!requireDeviceOnly()) return true;
    const cur = svc.getCurrent();
    if (!cur) { sendJson(res, 200, { text: '', deviceId: '', deviceName: '', createdAt: '' }); return true; }
    sendJson(res, 200, { text: cur.Text ?? '', deviceId: cur.DeviceId, deviceName: cur.DeviceName ?? '', createdAt: toIsoStr(cur.CreatedAt) });
    return true;
  }
  if (p === '/SyncClipboard.json' && method === 'PUT') {
    if (!requireDeviceOnly()) return true;
    const body = await readBody(req, 1024 * 1024);
    let json: Record<string, unknown>;
    try { json = JSON.parse(body.toString('utf8')); } catch { sendJson(res, 400, { error: '无效的 JSON' }); return true; }
    const text = typeof json.text === 'string' ? json.text.trim() : '';
    if (!text) { sendJson(res, 400, { error: 'text 不能为空' }); return true; }
    const deviceId = typeof json.deviceId === 'string' && json.deviceId ? json.deviceId : deviceActor!.Id;
    if (deviceId !== deviceActor!.Id) { sendApiError(res, 403, '上传设备身份与凭证不匹配'); return true; }
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

function remoteIp(req: IncomingMessage): string | null {
  return extractClientIpv4(req);
}
