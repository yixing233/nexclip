import { mkdirSync, existsSync, writeFileSync, unlinkSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';
import type { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';
import type { EntryRow, DeviceRow, ActivityRow, PairingRequestRow, AuditRow } from './db.js';
import { dbNow, toIso, sha256Hex, truncate, clamp, randomHex, randomCode } from './util.js';
import type { SignalRHub } from './signalr.js';

export interface EntryDto {
  id: number; type: string; text: string | null; imageRef: string | null;
  deviceId: string; deviceName: string | null; createdAt: string;
}

/** 配对业务错误(status + 中文提示,与设计文档错误码一致) */
export class PairError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

export class SyncService {
  constructor(
    private readonly db: DatabaseSync,
    private readonly cfg: AppConfig,
    private readonly hub: SignalRHub,
  ) {}

  // ---------- 条目序列化 ----------
  private toDto(e: EntryRow): EntryDto {
    return {
      id: e.Id, type: e.Type, text: e.Text, imageRef: e.ImageRef,
      deviceId: e.DeviceId, deviceName: e.DeviceName, createdAt: toIso(e.CreatedAt),
    };
  }

  private lastInsertId(): number {
    return Number((this.db.prepare('SELECT last_insert_rowid() AS id').get() as { id: number }).id);
  }

  // ---------- 查询 ----------
  getCurrent(): EntryRow | null {
    return this.db.prepare('SELECT * FROM "Entries" ORDER BY "Id" DESC LIMIT 1').get() as unknown as EntryRow | null;
  }

  getById(id: number): EntryRow | null {
    return this.db.prepare('SELECT * FROM "Entries" WHERE "Id" = ?').get(id) as unknown as EntryRow | null;
  }

  getHistory(offset: number, limit: number, userId: string | null = null): { items: EntryDto[]; total: number } {
    limit = clamp(limit, 1, 200);
    offset = Math.max(0, offset);
    if (userId) {
      // 按用户过滤:条目来源设备归属该用户
      const total = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ?').get(userId) as { c: number }).c);
      const rows = this.db.prepare('SELECT e.* FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ? ORDER BY e."Id" DESC LIMIT ? OFFSET ?')
        .all(userId, limit, offset) as unknown as EntryRow[];
      return { items: rows.map(r => this.toDto(r)), total };
    }
    const total = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries"').get() as { c: number }).c);
    const rows = this.db.prepare('SELECT * FROM "Entries" ORDER BY "Id" DESC LIMIT ? OFFSET ?').all(limit, offset) as unknown as EntryRow[];
    return { items: rows.map(r => this.toDto(r)), total };
  }

  listDevices(): Array<Record<string, unknown>> {
    const threshold = new Date(Date.now() - this.cfg.onlineThresholdSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
    const rows = this.db.prepare('SELECT * FROM "Devices" ORDER BY "LastSeenAt" DESC').all() as unknown as DeviceRow[];
    return rows.map(d => ({
      id: d.Id, name: d.Name, platform: d.Platform, ip: d.Ip, version: d.Version,
      online: d.LastSeenAt >= threshold,
      userId: d.UserId ?? null,
      bound: d.UserId != null,
      paired: d.Token != null,
      lastSeenAt: toIso(d.LastSeenAt),
    }));
  }

  // ---------- 文本上传(含去重) ----------
  uploadText(
    text: string, deviceId: string, deviceName: string,
    platform: string | null, version: string | null, ip: string | null,
    broadcast: boolean,
  ): { entry: EntryDto; unchanged: boolean } {
    const hash = sha256Hex(text);
    const current = this.getCurrent();
    if (current && current.ContentHash === hash && current.Type === 'Text') {
      return { entry: this.toDto(current), unchanged: true };
    }
    const now = dbNow();
    this.db.prepare(`INSERT INTO "Entries" ("Type","Text","ImageRef","ContentHash","DeviceId","DeviceName","CreatedAt") VALUES ('Text', ?, NULL, ?, ?, ?, ?)`)
      .run(text, hash, deviceId, deviceName, now);
    const entry = this.getById(this.lastInsertId())!;
    this.touchDevice(deviceId, deviceName, platform, version, ip);
    this.addActivity('push', deviceName, truncate(text, 120), now, deviceId);
    this.trimHistory();
    if (broadcast) this.hub.broadcastUpdated(this.toDto(entry));
    return { entry: this.toDto(entry), unchanged: false };
  }

  /** 定向推送:只通知指定设备(与 .NET 版 send 语义一致) */
  broadcastTo(entry: EntryDto, deviceIds: ReadonlySet<string>): void {
    this.hub.broadcastUpdatedTo(entry, deviceIds);
  }

  // ---------- 图片上传 ----------
  uploadImage(fileName: string, data: Buffer, deviceId: string, deviceName: string, ip: string | null): EntryDto {
    let ext = extname(fileName);
    if (!ext) ext = '.png';
    const dir = new Date().toISOString().slice(0, 10).replace(/-/g, ''); // yyyyMMdd
    const fullDir = join(this.cfg.imageStoragePath, dir);
    mkdirSync(fullDir, { recursive: true });
    const rel = dir + '/' + randomHex(16) + ext.toLowerCase();
    const full = join(this.cfg.imageStoragePath, rel);
    writeFileSync(full, data);
    const now = dbNow();
    this.db.prepare(`INSERT INTO "Entries" ("Type","Text","ImageRef","ContentHash","DeviceId","DeviceName","CreatedAt") VALUES ('Image', ?, ?, ?, ?, ?, ?)`)
      .run(fileName, rel, sha256Hex(rel), deviceId, deviceName, now);
    const entry = this.getById(this.lastInsertId())!;
    this.touchDevice(deviceId, deviceName, null, null, ip);
    this.addActivity('push', deviceName, truncate(fileName, 120), now, deviceId);
    this.trimHistory();
    this.hub.broadcastUpdated(this.toDto(entry));
    return this.toDto(entry);
  }

  // ---------- 删除 ----------
  deleteEntry(id: number): void {
    const e = this.db.prepare('SELECT * FROM "Entries" WHERE "Id" = ?').get(id) as unknown as EntryRow | null;
    if (!e) return;
    if (e.ImageRef) this.tryDeleteImage(e.ImageRef);
    this.db.prepare('DELETE FROM "Entries" WHERE "Id" = ?').run(id);
    this.addActivity('delete', e.DeviceName ?? '?', '删除了 1 条剪贴板记录', dbNow(), e.DeviceId);
    this.trimHistory();
  }

  clearHistory(): void {
    const rows = this.db.prepare('SELECT "ImageRef" FROM "Entries" WHERE "ImageRef" IS NOT NULL').all() as unknown as { ImageRef: string }[];
    for (const r of rows) this.tryDeleteImage(r.ImageRef);
    this.db.prepare('DELETE FROM "Entries"').run();
    this.hub.broadcastCleared();
  }

  /** 超上限删除最旧条目(含图片文件) */
  private trimHistory(): void {
    const total = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries"').get() as { c: number }).c);
    if (total <= this.cfg.maxHistoryCount) return;
    const overflow = this.db.prepare('SELECT * FROM "Entries" ORDER BY "Id" ASC LIMIT ?')
      .all(total - this.cfg.maxHistoryCount) as unknown as EntryRow[];
    for (const e of overflow) if (e.ImageRef) this.tryDeleteImage(e.ImageRef);
    for (const e of overflow) this.db.prepare('DELETE FROM "Entries" WHERE "Id" = ?').run(e.Id);
  }

  private tryDeleteImage(rel: string): void {
    try {
      const root = resolve(this.cfg.imageStoragePath);
      const full = resolve(join(this.cfg.imageStoragePath, rel));
      if (full.toLowerCase().startsWith(root.toLowerCase()) && existsSync(full)) {
        unlinkSync(full);
      }
    } catch { /* 忽略删除失败 */ }
  }

  // ---------- 设备 ----------
  getDevice(id: string): DeviceRow | null {
    return this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
  }

  touchDevice(id: string, name: string, platform: string | null, version: string | null, ip: string | null): void {
    if (!id) return;
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
    const now = dbNow();
    if (!d) {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt") VALUES (?,?,?,?,?,?)')
        .run(id, name || '未知设备', platform ?? 'Unknown', ip, version, now);
    } else {
      this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Ip" = ?, "Version" = ?, "LastSeenAt" = ? WHERE "Id" = ?')
        .run(name || d.Name, platform ?? d.Platform, ip ?? d.Ip, version ?? d.Version, now, id);
    }
  }

  /** hub 连接登记(与 .NET OnConnected 一致:不存在则建 未知设备/Web,每次记 connect 活动)。
   *  携带 platform/version(客户端上报)时更新设备平台信息。 */
  registerHubDevice(
    deviceId: string | null, deviceName: string | null = null,
    platform: string | null = null, version: string | null = null,
  ): void {
    if (!deviceId) return;
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    const now = dbNow();
    const name = deviceName?.trim() || d?.Name || '未知设备';
    const plat = platform?.trim() || d?.Platform || 'Web';
    const ver = version?.trim() || d?.Version || null;
    if (!d) {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt") VALUES (?,?,?,NULL,?,?)')
        .run(deviceId, name, plat, ver, now);
    } else {
      this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Version" = ?, "LastSeenAt" = ? WHERE "Id" = ?')
        .run(name, plat, ver, now, deviceId);
    }
    this.addActivity('connect', name, null, now, deviceId);
  }

  // ---------- 用户(用户ID组)与配对(配对码 + 用户ID + 生成方确认) ----------

  /** 创建用户ID(短随机,唯一) */
  private createUser(now: string): string {
    let id: string;
    do {
      id = randomCode(8);
    } while (this.db.prepare('SELECT 1 FROM "Users" WHERE "Id" = ?').get(id));
    this.db.prepare('INSERT INTO "Users" ("Id","Name","CreatedAt") VALUES (?,?,?)').run(id, id, now);
    return id;
  }

  /** 设备归属:未绑定则自动创建用户ID并绑定 */
  private ensureUserBinding(deviceId: string, now: string): string {
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    if (d?.UserId) return d.UserId;
    const uid = this.createUser(now);
    if (d) {
      this.db.prepare('UPDATE "Devices" SET "UserId" = ? WHERE "Id" = ?').run(uid, deviceId);
    } else {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt","UserId") VALUES (?,?,?,NULL,NULL,?,?)')
        .run(deviceId, '未知设备', 'Unknown', now, uid);
    }
    return uid;
  }

  /** 生成配对码:归属生成方用户ID;未绑定则自动创建用户ID。
   *  返回 userId —— 未绑定场景界面需同时展示 用户ID + 配对码。 */
  generatePairingCode(deviceId: string, deviceName: string | null): { code: string; expiresAt: string; userId: string } {
    const now = dbNow();
    this.touchDevice(deviceId, deviceName || '未知设备', 'Unknown', null, null);
    const userId = this.ensureUserBinding(deviceId, now);
    let code: string;
    do {
      code = randomCode(8);
    } while (this.db.prepare('SELECT 1 FROM "PairingRequests" WHERE "Code" = ?').get(code));
    const expiresAt = new Date(Date.now() + this.cfg.pairingCodeTtlSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
    this.db.prepare('INSERT INTO "PairingRequests" ("Code","GeneratorId","UserId","TargetDeviceId","TargetDeviceName","Status","ExpiresAt","CreatedAt","ConfirmedAt") VALUES (?,?,?,NULL,NULL,\'open\',?,?,NULL)')
      .run(code, deviceId, userId, expiresAt, now);
    this.addAudit('pair_code', '设备 ' + (deviceName || deviceId) + ' 生成配对码(用户 ' + userId + ')', null);
    return { code, expiresAt: toIso(expiresAt), userId };
  }

  /** 作废未用配对码(open 状态) */
  revokePairingRequest(code: string): boolean {
    const r = this.db.prepare('DELETE FROM "PairingRequests" WHERE "Code" = ? AND "Status" = \'open\'').run(code);
    return r.changes > 0;
  }

  /** 发起配对:校验 配对码 + 用户ID 匹配 → 挂起待确认请求 */
  pair(pairingCode: string, userId: string, deviceId: string, deviceName: string): { status: string } {
    const now = dbNow();
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(pairingCode) as unknown as PairingRequestRow | null;
    if (!r || r.Status !== 'open' || r.ExpiresAt < now) {
      throw new PairError(400, '配对码无效或已过期');
    }
    if (r.UserId !== userId) {
      throw new PairError(400, '用户ID与配对码不匹配');
    }
    const existing = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    if (existing?.UserId) {
      throw new PairError(409, '该设备已绑定用户');
    }
    this.db.prepare('UPDATE "PairingRequests" SET "TargetDeviceId" = ?, "TargetDeviceName" = ?, "Status" = \'pending\' WHERE "Code" = ?')
      .run(deviceId, deviceName || '未知设备', pairingCode);
    this.touchDevice(deviceId, deviceName || '未知设备', 'Unknown', null, null);
    this.addAudit('pair_request', '设备 ' + (deviceName || deviceId) + ' 请求配对(用户 ' + userId + ')', null);
    this.hub.broadcastDevicesChanged();
    return { status: 'pending' };
  }

  /** 新设备轮询配对结果 */
  pairStatus(pairingCode: string, deviceId: string): { status: string; userId?: string } {
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(pairingCode) as unknown as PairingRequestRow | null;
    if (!r || r.TargetDeviceId !== deviceId) return { status: 'not-found' };
    const expired = r.ExpiresAt < dbNow();
    const status = expired && (r.Status === 'open' || r.Status === 'pending') ? 'expired' : r.Status;
    return status === 'approved' ? { status, userId: r.UserId } : { status };
  }

  /** 确认/拒绝:生成方(码+生成方设备ID)、同组用户会话、管理端会话 */
  confirmPairing(
    pairingCode: string,
    action: 'approve' | 'reject',
    actor: { kind: 'secret'; generatorId: string } | { kind: 'user'; userId: string } | { kind: 'admin' },
  ): { status: string } {
    const now = dbNow();
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(pairingCode) as unknown as PairingRequestRow | null;
    if (!r || r.Status !== 'pending' || r.ExpiresAt < now) {
      throw new PairError(400, '请求不存在、已处理或已过期');
    }
    const ok =
      actor.kind === 'admin' ||
      (actor.kind === 'user' && actor.userId === r.UserId) ||
      (actor.kind === 'secret' && actor.generatorId === r.GeneratorId);
    if (!ok) throw new PairError(403, '无权确认该配对请求');
    if (action === 'approve') {
      this.db.prepare('UPDATE "PairingRequests" SET "Status" = \'approved\', "ConfirmedAt" = ? WHERE "Code" = ?').run(now, pairingCode);
      if (r.TargetDeviceId) {
        this.db.prepare('UPDATE "Devices" SET "UserId" = ? WHERE "Id" = ?').run(r.UserId, r.TargetDeviceId);
      }
      this.addAudit('pair_approve', '设备 ' + (r.TargetDeviceName || r.TargetDeviceId) + ' 已加入用户 ' + r.UserId, null);
    } else {
      this.db.prepare('UPDATE "PairingRequests" SET "Status" = \'rejected\', "ConfirmedAt" = ? WHERE "Code" = ?').run(now, pairingCode);
      this.addAudit('pair_reject', '拒绝设备 ' + (r.TargetDeviceName || r.TargetDeviceId) + ' 加入用户 ' + r.UserId, null);
    }
    this.hub.broadcastDevicesChanged();
    return { status: action };
  }

  /** 待确认请求列表(管理端:全部;用户会话:本组;生成方:凭 码+设备ID) */
  listPairingRequests(
    scope: { kind: 'admin' } | { kind: 'user'; userId: string } | { kind: 'secret'; generatorId: string; code: string },
  ): Array<Record<string, unknown>> {
    const now = dbNow();
    let rows: PairingRequestRow[];
    if (scope.kind === 'admin') {
      rows = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Status" IN (\'open\',\'pending\') AND "ExpiresAt" >= ? ORDER BY "CreatedAt" DESC').all(now) as unknown as PairingRequestRow[];
    } else if (scope.kind === 'user') {
      rows = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "UserId" = ? AND "Status" IN (\'open\',\'pending\') AND "ExpiresAt" >= ? ORDER BY "CreatedAt" DESC').all(scope.userId, now) as unknown as PairingRequestRow[];
    } else {
      const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ? AND "GeneratorId" = ?').get(scope.code, scope.generatorId) as unknown as PairingRequestRow | null;
      rows = r ? [r] : [];
    }
    return rows.map(r => ({
      code: r.Code, generatorId: r.GeneratorId, userId: r.UserId,
      deviceId: r.TargetDeviceId, deviceName: r.TargetDeviceName,
      status: r.Status, createdAt: toIso(r.CreatedAt),
    }));
  }

  /** 配对确认后:换取用户会话载荷(浏览器/应用) */
  sessionForPair(pairingCode: string, deviceId: string): { role: 'user'; userId: string; deviceId: string } {
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(pairingCode) as unknown as PairingRequestRow | null;
    if (!r || r.Status !== 'approved' || r.TargetDeviceId !== deviceId) {
      throw new PairError(400, '配对未完成或设备不匹配');
    }
    return { role: 'user', userId: r.UserId, deviceId };
  }

  /** 用户ID改名(全局唯一) */
  renameUser(userId: string, newName: string): void {
    const name = newName.trim();
    if (!name || name.length > 32 || !/^[A-Za-z0-9_-]+$/.test(name)) {
      throw new PairError(400, '用户ID仅支持字母数字与 _- ,长度 1-32');
    }
    const dup = this.db.prepare('SELECT "Id" FROM "Users" WHERE "Name" = ? AND "Id" != ?').get(name, userId) as { Id: string } | undefined;
    if (dup) throw new PairError(409, '用户ID已被占用');
    const r = this.db.prepare('UPDATE "Users" SET "Name" = ? WHERE "Id" = ?').run(name, userId);
    if (r.changes === 0) throw new PairError(404, '用户不存在');
  }

  /** 用户列表(管理端):含设备数 */
  listUsers(): Array<Record<string, unknown>> {
    const rows = this.db.prepare('SELECT u."Id", u."Name", u."CreatedAt", (SELECT COUNT(*) FROM "Devices" d WHERE d."UserId" = u."Id") AS deviceCount FROM "Users" u ORDER BY u."CreatedAt" DESC').all() as unknown as Array<Record<string, unknown>>;
    return rows.map(r => ({ id: String(r.Id), name: String(r.Name), createdAt: toIso(String(r.CreatedAt)), deviceCount: Number(r.deviceCount) }));
  }

  /** 删除用户(管理端):解除其设备绑定 */
  deleteUser(userId: string): void {
    const r = this.db.prepare('DELETE FROM "Users" WHERE "Id" = ?').run(userId);
    if (r.changes === 0) throw new PairError(404, '用户不存在');
    this.db.prepare('UPDATE "Devices" SET "UserId" = NULL WHERE "UserId" = ?').run(userId);
  }

  /** 用户ID查询(管理端/本组) */
  getUser(userId: string): Record<string, unknown> | null {
    const r = this.db.prepare('SELECT * FROM "Users" WHERE "Id" = ?').get(userId) as unknown as { Id: string; Name: string; CreatedAt: string } | null;
    if (!r) return null;
    return { id: r.Id, name: r.Name, createdAt: toIso(r.CreatedAt) };
  }

  // ---------- 审计 ----------
  addAudit(action: string, detail: string | null, ip: string | null): void {
    this.db.prepare('INSERT INTO "AuditLog" ("Action","Detail","Ip","CreatedAt") VALUES (?,?,?,?)')
      .run(action, detail, ip, dbNow());
  }

  listAudit(limit: number): Array<Record<string, unknown>> {
    limit = clamp(limit, 1, 500);
    const rows = this.db.prepare('SELECT * FROM "AuditLog" ORDER BY "Id" DESC LIMIT ?').all(limit) as unknown as AuditRow[];
    return rows.map(a => ({ id: a.Id, action: a.Action, detail: a.Detail, ip: a.Ip, createdAt: toIso(a.CreatedAt) }));
  }

  /** 每 45s 心跳:hub 存续期间的设备保持在线 */
  heartbeat(): void {
    const ids = this.hub.onlineDeviceIds();
    if (ids.size === 0) return;
    const now = dbNow();
    const stmt = this.db.prepare('UPDATE "Devices" SET "LastSeenAt" = ? WHERE "Id" = ?');
    for (const id of ids) stmt.run(now, id);
  }

  renameDevice(id: string, name: string): boolean {
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
    if (!d) return false;
    if (name.trim()) {
      this.db.prepare('UPDATE "Devices" SET "Name" = ? WHERE "Id" = ?').run(name.trim(), id);
    }
    this.hub.broadcastDevicesChanged();
    return true;
  }

  removeDevice(id: string): boolean {
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
    if (!d) return false;
    this.db.prepare('DELETE FROM "Devices" WHERE "Id" = ?').run(id);
    this.addActivity('delete', d.Name, '移除了设备', dbNow(), id);
    this.hub.broadcastDevicesChanged();
    return true;
  }

  // ---------- 活动 ----------
  addActivity(action: string, deviceName: string, content: string | null, createdAt: string, deviceId: string | null = null): void {
    this.db.prepare('INSERT INTO "Activities" ("Action","DeviceName","Content","CreatedAt","DeviceId") VALUES (?,?,?,?,?)')
      .run(action, deviceName, content, createdAt, deviceId);
  }

  listActivities(limit: number, userId: string | null = null): Array<Record<string, unknown>> {
    limit = clamp(limit, 1, 200);
    let rows: ActivityRow[];
    if (userId) {
      // 按用户过滤:活动来源设备归属该用户(仅新记录带 DeviceId 可关联)
      rows = this.db.prepare('SELECT a.* FROM "Activities" a JOIN "Devices" d ON d."Id" = a."DeviceId" WHERE d."UserId" = ? ORDER BY a."Id" DESC LIMIT ?')
        .all(userId, limit) as unknown as ActivityRow[];
    } else {
      rows = this.db.prepare('SELECT * FROM "Activities" ORDER BY "Id" DESC LIMIT ?').all(limit) as unknown as ActivityRow[];
    }
    return rows.map(a => ({
      id: a.Id, action: a.Action, deviceName: a.DeviceName, content: a.Content,
      createdAt: toIso(a.CreatedAt),
    }));
  }

  // ---------- 统计 ----------
  stats(latency: { avgMs: number; last12: number[] }): Record<string, unknown> {
    const now = new Date();
    const threshold = new Date(now.getTime() - this.cfg.onlineThresholdSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
    const online = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Devices" WHERE "LastSeenAt" >= ?').get(threshold) as { c: number }).c);
    const total = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Devices"').get() as { c: number }).c);
    const totalUsers = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Users"').get() as { c: number }).c);
    const onlineUsers = Number((this.db.prepare('SELECT COUNT(DISTINCT "UserId") AS c FROM "Devices" WHERE "LastSeenAt" >= ? AND "UserId" IS NOT NULL').get(threshold) as { c: number }).c);
    const totalEntries = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries"').get() as { c: number }).c);

    const dayStart = now.toISOString().slice(0, 10) + ' 00:00:00';
    const yesterdayStart = new Date(now.getTime() - 86400000).toISOString().slice(0, 10) + ' 00:00:00';
    const todayCount = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries" WHERE "CreatedAt" >= ?').get(dayStart) as { c: number }).c);
    const yesterdayCount = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries" WHERE "CreatedAt" >= ? AND "CreatedAt" < ?').get(yesterdayStart, dayStart) as { c: number }).c);
    const syncTrend = yesterdayCount === 0 ? (todayCount > 0 ? 100 : 0) : Math.round(((todayCount - yesterdayCount) * 100) / yesterdayCount);

    // 最近 12 小时分布
    const since = new Date(now.getTime() - 12 * 3600000).toISOString().replace('T', ' ').replace('Z', '');
    const entries = this.db.prepare('SELECT "CreatedAt" FROM "Entries" WHERE "CreatedAt" >= ?').all(since) as unknown as { CreatedAt: string }[];
    const cumBefore = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Entries" WHERE "CreatedAt" < ?').get(since) as { c: number }).c);
    const sync = new Array<number>(12).fill(0);
    const hist = new Array<number>(12).fill(0);
    let cum = cumBefore;
    for (let i = 0; i < 12; i++) {
      const s = new Date(now.getTime() + (i - 12) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      const e = new Date(now.getTime() + (i - 11) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      sync[i] = entries.filter(x => x.CreatedAt >= s && x.CreatedAt < e).length;
      cum += sync[i];
      hist[i] = cum;
    }
    const connects = this.db.prepare('SELECT "CreatedAt", "DeviceId" FROM "Activities" WHERE "Action" = ? AND "CreatedAt" >= ?').all('connect', since) as unknown as { CreatedAt: string; DeviceId: string | null }[];
    const devicesSpark = new Array<number>(12).fill(0);
    const usersSpark = new Array<number>(12).fill(0);
    const devOwner = new Map<string, string>();
    for (const d of this.db.prepare('SELECT "Id", "UserId" FROM "Devices"').all() as unknown as { Id: string; UserId: string | null }[]) {
      if (d.UserId) devOwner.set(d.Id, d.UserId);
    }
    for (let i = 0; i < 12; i++) {
      const s = new Date(now.getTime() + (i - 12) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      const e = new Date(now.getTime() + (i - 11) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      const bucket = connects.filter(x => x.CreatedAt >= s && x.CreatedAt < e);
      devicesSpark[i] = bucket.length;
      usersSpark[i] = new Set(bucket.map(x => x.DeviceId ? devOwner.get(x.DeviceId) : undefined).filter((v): v is string => !!v)).size;
    }

    const uptimeMs = now.getTime() - this.cfg.startedAt.getTime();
    const days = Math.floor(uptimeMs / 86400000);
    const hours = Math.floor(uptimeMs / 3600000) % 24;
    const minutes = Math.floor(uptimeMs / 60000) % 60;
    const uptimeStr = days >= 1
      ? days + ' 天 ' + hours + ' 小时'
      : uptimeMs >= 3600000
        ? hours + ' 小时 ' + minutes + ' 分钟'
        : minutes + ' 分钟';

    return {
      onlineDevices: online,
      totalDevices: total,
      onlineUsers,
      totalUsers,
      todaySyncCount: todayCount,
      syncTrend,
      totalClipboardCount: totalEntries,
      status: 'running',
      uptime: uptimeStr,
      avgLatencyMs: latency.avgMs,
      sparklines: { devices: devicesSpark, users: usersSpark, sync, history: hist, latency: latency.last12 },
    };
  }
}
