import { mkdirSync, existsSync, writeFileSync, unlinkSync, readdirSync, copyFileSync, cpSync } from 'node:fs';
import { join, extname, resolve, dirname, isAbsolute } from 'node:path';
import type { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';
import type { EntryRow, DeviceRow, ActivityRow, PairingRequestRow, AuditRow } from './db.js';
import { dbNow, toIso, sha256Hex, sha256Bytes, truncate, clamp, randomHex, randomCode, randomNumericCode, detectPlatform } from './util.js';
import type { SignalRHub } from './signalr.js';

export interface EntryDto {
  id: number; type: string; text: string | null; imageRef: string | null;
  deviceId: string; deviceName: string | null; isManual: boolean; createdAt: string;
}

/** 配对业务错误(status + 中文提示,与设计文档错误码一致) */
export class PairError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
  }
}

export type PairingRequestActor =
  | { kind: 'admin' }
  | { kind: 'user'; userId: string }
  | { kind: 'device'; deviceId: string };

export class SyncService {
  /** 历史上限:启动时取配置,管理台可改(持久化到 Settings 表) */
  private maxHistoryCount: number;

  constructor(
    private readonly db: DatabaseSync,
    private readonly cfg: AppConfig,
    private readonly hub: SignalRHub,
  ) {
    this.maxHistoryCount = cfg.maxHistoryCount;
    const saved = this.db.prepare('SELECT "Value" FROM "Settings" WHERE "Key" = ?').get('maxHistoryCount') as { Value: string } | undefined;
    if (saved && Number.isFinite(Number(saved.Value)) && Number(saved.Value) >= 100) {
      this.maxHistoryCount = Math.floor(Number(saved.Value));
    }
  }

  /** 管理台:读取/修改历史上限(修改即持久化,重启保留) */
  getMaxHistoryCount(): number {
    return this.maxHistoryCount;
  }

  setMaxHistoryCount(n: number): number {
    this.maxHistoryCount = Math.max(100, Math.min(100_000, Math.floor(n)));
    this.db.prepare('INSERT OR REPLACE INTO "Settings" ("Key","Value") VALUES (\'maxHistoryCount\', ?)')
      .run(String(this.maxHistoryCount));
    this.trimHistory();
    return this.maxHistoryCount;
  }

  /** 管理台:图片存储位置(持久化,重启保留) */
  private _imageStoragePath: string | null = null;

  getImageStoragePath(): string {
    if (this._imageStoragePath === null) {
      const saved = this.db.prepare('SELECT "Value" FROM "Settings" WHERE "Key" = ?').get('imageStoragePath') as { Value: string } | undefined;
      this._imageStoragePath = saved?.Value ?? this.cfg.imageStoragePath;
    }
    return this._imageStoragePath;
  }

  setImageStoragePath(p: string): { path: string; moved: number } {
    const raw = p.trim();
    if (!raw) throw new PairError(400, '路径不能为空');
    const root = resolve(raw);
    const old = resolve(this.getImageStoragePath());
    if (old === root) return { path: root, moved: 0 };
    mkdirSync(root, { recursive: true });
    let moved = 0;
    if (existsSync(old) && old !== root) {
      // 迁移现有图片目录内容(子目录递归,文件复制;目标已存在则跳过)
      for (const entry of readdirSync(old, { withFileTypes: true })) {
        const from = join(old, entry.name);
        const to = join(root, entry.name);
        try {
          if (existsSync(to)) continue;
          if (entry.isDirectory()) cpSync(from, to, { recursive: true });
          else copyFileSync(from, to);
          moved++;
        } catch { /* 单项失败不阻断 */ }
      }
    }
    this.db.prepare('INSERT OR REPLACE INTO "Settings" ("Key","Value") VALUES (?,?)').run('imageStoragePath', root);
    this._imageStoragePath = root;
    return { path: root, moved };
  }

  /** 管理台:目录浏览(仅列出子目录,用于选择存储位置) */
  browseDirectory(p: string | null): { path: string; parent: string | null; exists: boolean; dirs: string[] } {
    const root = resolve(p && p.trim() ? p.trim() : this.getImageStoragePath());
    if (!isAbsolute(root)) throw new PairError(400, '仅支持绝对路径');
    let dirs: string[] = [];
    let exists = false;
    try {
      exists = existsSync(root);
      if (exists) {
        dirs = readdirSync(root, { withFileTypes: true })
          .filter(e => e.isDirectory())
          .map(e => e.name)
          .sort();
      }
    } catch { /* 无权限等:返回空 */ }
    const parent = dirname(root);
    return { path: root, parent: parent === root ? null : parent, exists, dirs };
  }

  // ---------- 条目序列化 ----------
  private toDto(e: EntryRow): EntryDto {
    return {
      id: e.Id, type: e.Type, text: e.Text, imageRef: e.ImageRef,
      deviceId: e.DeviceId, deviceName: e.DeviceName, isManual: Boolean(e.IsManual), createdAt: toIso(e.CreatedAt),
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

  getHistory(offset: number, limit: number, userId: string | null = null, q: string | null = null): { items: EntryDto[]; total: number } {
    limit = clamp(limit, 1, 200);
    offset = Math.max(0, offset);
    // 文本搜索:LIKE 转义 %/_
    const like = q && q.trim() ? '%' + q.trim().replace(/[\\%_]/g, ch => '\\' + ch) + '%' : null;
    if (userId) {
      // 按用户过滤:条目来源设备归属该用户
      const total = Number((this.db.prepare(
        like
          ? 'SELECT COUNT(*) AS c FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ? AND e."Text" LIKE ? ESCAPE \'\\\''
          : 'SELECT COUNT(*) AS c FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ?',
      ).get(...(like ? [userId, like] : [userId])) as { c: number }).c);
      const rows = this.db.prepare(
        like
          ? 'SELECT e.* FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ? AND e."Text" LIKE ? ESCAPE \'\\\' ORDER BY e."Id" DESC LIMIT ? OFFSET ?'
          : 'SELECT e.* FROM "Entries" e JOIN "Devices" d ON d."Id" = e."DeviceId" WHERE d."UserId" = ? ORDER BY e."Id" DESC LIMIT ? OFFSET ?',
      ).all(...(like ? [userId, like, limit, offset] : [userId, limit, offset])) as unknown as EntryRow[];
      return { items: rows.map(r => this.toDto(r)), total };
    }
    const total = Number((this.db.prepare(
      like ? 'SELECT COUNT(*) AS c FROM "Entries" WHERE "Text" LIKE ? ESCAPE \'\\\'' : 'SELECT COUNT(*) AS c FROM "Entries"',
    ).get(...(like ? [like] : [])) as { c: number }).c);
    const rows = this.db.prepare(
      like
        ? 'SELECT * FROM "Entries" WHERE "Text" LIKE ? ESCAPE \'\\\' ORDER BY "Id" DESC LIMIT ? OFFSET ?'
        : 'SELECT * FROM "Entries" ORDER BY "Id" DESC LIMIT ? OFFSET ?',
    ).all(...(like ? [like, limit, offset] : [limit, offset])) as unknown as EntryRow[];
    return { items: rows.map(r => this.toDto(r)), total };
  }

  listDevices(userId: string | null = null): Array<Record<string, unknown>> {
    const threshold = new Date(Date.now() - this.cfg.onlineThresholdSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
    const rows = (userId
      ? this.db.prepare('SELECT * FROM "Devices" WHERE "RevokedAt" IS NULL AND "UserId" = ? ORDER BY "LastSeenAt" DESC').all(userId)
      : this.db.prepare('SELECT * FROM "Devices" WHERE "RevokedAt" IS NULL AND "UserId" IS NOT NULL ORDER BY "LastSeenAt" DESC').all()) as unknown as DeviceRow[];
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
    isManual: boolean = false,
  ): { entry: EntryDto; unchanged: boolean } {
    const hash = sha256Hex(text);
    const current = this.getCurrent();
    if (current && current.ContentHash === hash && current.Type === 'Text') {
      return { entry: this.toDto(current), unchanged: true };
    }
    const now = dbNow();
    this.db.prepare(`INSERT INTO "Entries" ("Type","Text","ImageRef","ContentHash","DeviceId","DeviceName","IsManual","CreatedAt") VALUES ('Text', ?, NULL, ?, ?, ?, ?, ?)`)
      .run(text, hash, deviceId, deviceName, isManual ? 1 : 0, now);
    const entry = this.getById(this.lastInsertId())!;
    this.touchDevice(deviceId, deviceName, platform, version, ip);
    this.addActivity(isManual ? 'transfer' : 'push', deviceName, truncate(text, 120), now, deviceId);
    this.trimHistory();
    if (broadcast) this.hub.broadcastUpdated(this.toDto(entry));
    return { entry: this.toDto(entry), unchanged: false };
  }

  /** 定向推送:只通知指定设备(与 .NET 版 send 语义一致) */
  broadcastTo(entry: EntryDto, deviceIds: ReadonlySet<string>): void {
    this.hub.broadcastUpdatedTo(entry, deviceIds);
  }

  // ---------- 图片上传 ----------
  uploadImage(fileName: string, data: Buffer, deviceId: string, deviceName: string, ip: string | null, platform: string | null = null, version: string | null = null, isManual: boolean = false): EntryDto {
    const hash = sha256Bytes(data);
    const current = this.getCurrent();
    if (current && current.Type === 'Image' && current.ContentHash === hash) {
      this.touchDevice(deviceId, deviceName, platform, version, ip);
      return this.toDto(current);
    }
    let ext = extname(fileName);
    if (!ext) ext = '.png';
    const dir = new Date().toISOString().slice(0, 10).replace(/-/g, ''); // yyyyMMdd
    const fullDir = join(this.getImageStoragePath(), dir);
    mkdirSync(fullDir, { recursive: true });
    const rel = dir + '/' + randomHex(16) + ext.toLowerCase();
    const full = join(this.getImageStoragePath(), rel);
    writeFileSync(full, data);
    const now = dbNow();
    this.db.prepare(`INSERT INTO "Entries" ("Type","Text","ImageRef","ContentHash","DeviceId","DeviceName","IsManual","CreatedAt") VALUES ('Image', ?, ?, ?, ?, ?, ?, ?)`)
      .run(fileName, rel, hash, deviceId, deviceName, isManual ? 1 : 0, now);
    const entry = this.getById(this.lastInsertId())!;
    this.touchDevice(deviceId, deviceName, platform, version, ip);
    this.addActivity(isManual ? 'transfer' : 'push', deviceName, truncate(fileName, 120), now, deviceId);
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
    if (total <= this.maxHistoryCount) return;
    const overflow = this.db.prepare('SELECT * FROM "Entries" ORDER BY "Id" ASC LIMIT ?')
      .all(total - this.maxHistoryCount) as unknown as EntryRow[];
    for (const e of overflow) if (e.ImageRef) this.tryDeleteImage(e.ImageRef);
    for (const e of overflow) this.db.prepare('DELETE FROM "Entries" WHERE "Id" = ?').run(e.Id);
  }

  private tryDeleteImage(rel: string): void {
    try {
      const root = resolve(this.getImageStoragePath());
      const full = resolve(join(this.getImageStoragePath(), rel));
      if (full.toLowerCase().startsWith(root.toLowerCase()) && existsSync(full)) {
        unlinkSync(full);
      }
    } catch { /* 忽略删除失败 */ }
  }

  // ---------- 设备 ----------
  getDevice(id: string): DeviceRow | null {
    return this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
  }

  /** 生成设备凭证;只将哈希写入数据库,明文只返回给刚完成配对的客户端。 */
  private issueDeviceToken(): { token: string; hash: string } {
    const token = randomHex(32);
    return { token, hash: sha256Hex(token) };
  }

  /** 验证设备凭证。旧版本无 Token 的设备不能访问同步接口,必须重新配对。 */
  authenticateDevice(deviceId: string, token: string): DeviceRow | null {
    if (!deviceId || !token) return null;
    const d = this.getDevice(deviceId);
    if (!d || !d.UserId || d.RevokedAt || !d.Token) return null;
    return d.Token === sha256Hex(token) ? d : null;
  }

  /** 设备凭证失败原因,供 REST 返回可操作的状态码。 */
  deviceCredentialStatus(deviceId: string, token: string): 'ok' | 'missing' | 'revoked' | 'invalid' {
    if (!deviceId || !token) return 'missing';
    const d = this.getDevice(deviceId);
    if (d?.RevokedAt) return 'revoked';
    if (!d || !d.UserId || !d.Token) return 'invalid';
    return d.Token === sha256Hex(token) ? 'ok' : 'invalid';
  }

  /**
   * 设备初始化/旧版迁移：
   * - 空服务器允许第一台设备建立用户组；
   * - 已绑定但 Token 为空的旧设备可一次性领取新凭证；
   * - 被移除设备保留 tombstone，不能通过重连或初始化复活。
   */
  initializeDevice(
    deviceId: string, deviceName: string, platform: string | null,
    version: string | null, ip: string | null, currentToken: string,
  ): { status: 'active' | 'created' | 'migrated'; userId: string; deviceToken?: string } {
    if (!deviceId) throw new PairError(400, 'deviceId 不能为空');
    const now = dbNow();
    const existing = this.getDevice(deviceId);
    if (existing?.RevokedAt) throw new PairError(410, '设备已被移除,请使用其他已配对设备重新批准接入');
    if (existing?.UserId && existing.Token) {
      if (!currentToken || existing.Token !== sha256Hex(currentToken)) {
        throw new PairError(401, '设备凭证无效,请重新配对');
      }
      this.touchDevice(deviceId, deviceName || existing.Name, platform, version, ip);
      return { status: 'active', userId: existing.UserId };
    }
    if (existing?.UserId && !existing.Token) {
      const issued = this.issueDeviceToken();
      this.db.prepare('UPDATE "Devices" SET "Token" = ?, "PairedAt" = ?, "Name" = ?, "Platform" = ?, "Ip" = COALESCE(?, "Ip"), "Version" = COALESCE(?, "Version"), "LastSeenAt" = ? WHERE "Id" = ?')
        .run(issued.hash, now, deviceName || existing.Name, platform ?? existing.Platform, ip, version, now, deviceId);
      return { status: 'migrated', userId: existing.UserId, deviceToken: issued.token };
    }
    const activeCount = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Devices" WHERE "RevokedAt" IS NULL AND "UserId" IS NOT NULL').get() as { c: number }).c);
    if (activeCount > 0) throw new PairError(409, '服务器已有设备组,请使用配对码加入');
    const userId = this.createUser(now);
    const issued = this.issueDeviceToken();
    if (existing) {
      this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Ip" = ?, "Version" = ?, "LastSeenAt" = ?, "Token" = ?, "PairedAt" = ?, "UserId" = ?, "RevokedAt" = NULL WHERE "Id" = ?')
        .run(deviceName || existing.Name, platform ?? existing.Platform, ip, version, now, issued.hash, now, userId, deviceId);
    } else {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt","Token","PairedAt","UserId","RevokedAt") VALUES (?,?,?,?,?,?,?,?,?,NULL)')
        .run(deviceId, deviceName || '未知设备', platform ?? 'Unknown', ip, version, now, issued.hash, now, userId);
    }
    return { status: 'created', userId, deviceToken: issued.token };
  }

  /** 生成方设备必须属于该用户组,返回设备行供控制器进行授权判断。 */
  deviceForUser(deviceId: string, userId: string): DeviceRow | null {
    const d = this.getDevice(deviceId);
    return d?.UserId === userId && !d.RevokedAt ? d : null;
  }

  touchDevice(id: string, name: string, platform: string | null, version: string | null, ip: string | null): void {
    if (!id) return;
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
    if (!d) return; // 已删除或未登记设备不静默插表，防止删除后被自动复活
    const now = dbNow();
    this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Ip" = COALESCE(?, "Ip"), "Version" = COALESCE(?, "Version"), "LastSeenAt" = ? WHERE "Id" = ?')
      .run(name || d.Name, platform ?? d.Platform, ip, version, now, id);
  }

  /** hub 连接登记：仅对已登记设备更新心跳与信息，避免未配对/已删除设备静默建档 */
  registerHubDevice(
    deviceId: string | null, deviceName: string | null = null,
    platform: string | null = null, version: string | null = null,
    ip: string | null = null,
  ): void {
    if (!deviceId) return;
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    if (!d) return; // 已删除或未登记设备不静默插表
    const now = dbNow();
    const name = deviceName?.trim() || d.Name || '未知设备';
    const plat = platform?.trim() || d.Platform || 'Unknown';
    const ver = version?.trim() || d.Version || null;
    this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Ip" = COALESCE(?, "Ip"), "Version" = COALESCE(?, "Version"), "LastSeenAt" = ? WHERE "Id" = ?')
      .run(name, plat, ip, ver, now, deviceId);
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
  private ensureUserBinding(deviceId: string, now: string, name: string = '未知设备', platform: string = 'Unknown', ip: string | null = null): string {
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    if (d?.UserId) return d.UserId;
    if (!d) throw new PairError(404, '设备尚未登记,请先通过配对流程建立设备记录');
    const uid = this.createUser(now);
    this.db.prepare('UPDATE "Devices" SET "UserId" = ?, "RevokedAt" = NULL WHERE "Id" = ?').run(uid, deviceId);
    return uid;
  }

  /** 生成配对码:归属生成方用户ID;未绑定则自动创建用户ID。
   *  返回 userId —— 未绑定场景界面需同时展示 用户ID + 配对码。 */
  generatePairingCode(
    deviceId: string, deviceName: string | null, ip: string | null = null,
    currentToken = '', trustedUserId: string | null = null, platform: string | null = null,
  ): { code: string; expiresAt: string; userId: string; deviceToken?: string } {
    const now = dbNow();
    const plat = detectPlatform(platform, null, deviceName);
    const existing = this.getDevice(deviceId);
    if (!existing) {
      if (trustedUserId) {
        const user = this.getUser(trustedUserId);
        if (!user) throw new PairError(401, '网页会话对应的用户不存在,请重新配对登录');
        let code: string;
        do { code = randomNumericCode(6); } while (this.db.prepare('SELECT 1 FROM "PairingRequests" WHERE "Code" = ?').get(code));
        const expiresAt = new Date(Date.now() + this.cfg.pairingCodeTtlSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
        this.db.prepare('INSERT INTO "PairingRequests" ("Code","GeneratorId","UserId","TargetDeviceId","TargetDeviceName","TargetTokenHash","Status","ExpiresAt","CreatedAt","ConfirmedAt") VALUES (?,?,?,NULL,NULL,NULL,\'open\',?,?,NULL)')
          .run(code, deviceId, trustedUserId, expiresAt, now);
        this.addAudit('pair_code', '网页会话生成配对码(用户 ' + trustedUserId + ')', ip);
        return { code, expiresAt: toIso(expiresAt), userId: trustedUserId };
      }
      const activeCount = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Devices" WHERE "RevokedAt" IS NULL AND "UserId" IS NOT NULL').get() as { c: number }).c);
      if (activeCount > 0) throw new PairError(404, '设备未登记,请使用现有设备生成的配对码加入');
      const first = this.initializeDevice(deviceId, deviceName || '未知设备', plat, null, ip, '');
      const result = this.generatePairingCode(deviceId, deviceName, ip, first.deviceToken ?? '', trustedUserId, plat);
      return first.deviceToken ? { ...result, deviceToken: first.deviceToken } : result;
    }
    if (existing.RevokedAt) {
      const activeCount = Number((this.db.prepare('SELECT COUNT(*) AS c FROM "Devices" WHERE "RevokedAt" IS NULL AND "UserId" IS NOT NULL').get() as { c: number }).c);
      if (activeCount > 0) {
        throw new PairError(403, '该设备已被解绑，请输入其他已连接设备的 6 位配对码重新加入');
      }
      // 服务器已无其他活跃设备，允许被移除设备重新初始化
      const userId = this.createUser(now);
      const issued = this.issueDeviceToken();
      this.db.prepare('UPDATE "Devices" SET "Name" = ?, "Platform" = ?, "Ip" = ?, "Version" = ?, "LastSeenAt" = ?, "Token" = ?, "PairedAt" = ?, "UserId" = ?, "RevokedAt" = NULL WHERE "Id" = ?')
        .run(deviceName || existing.Name, plat, ip, null, now, issued.hash, now, userId, deviceId);
      const result = this.generatePairingCode(deviceId, deviceName, ip, issued.token, null, plat);
      return { ...result, deviceToken: issued.token };
    }
    if (trustedUserId && existing.UserId !== trustedUserId) {
      throw new PairError(403, '当前网页会话无权使用该设备生成配对码');
    }
    let issuedToken: string | undefined;
    if (existing.Token) {
      const trustedSession = trustedUserId != null && existing.UserId === trustedUserId;
      if (!trustedSession && (!currentToken || existing.Token !== sha256Hex(currentToken))) throw new PairError(401, '设备凭证无效,请重新配对');
    } else {
      const issued = this.issueDeviceToken();
      issuedToken = issued.token;
      this.db.prepare('UPDATE "Devices" SET "Token" = ?, "PairedAt" = ?, "LastSeenAt" = ? WHERE "Id" = ?')
        .run(issued.hash, now, now, deviceId);
    }
    this.touchDevice(deviceId, deviceName || '未知设备', plat, null, ip);
    const userId = this.ensureUserBinding(deviceId, now, deviceName || existing.Name, plat, ip);
    let code: string;
    do {
      code = randomNumericCode(6);
    } while (this.db.prepare('SELECT 1 FROM "PairingRequests" WHERE "Code" = ?').get(code));
    const expiresAt = new Date(Date.now() + this.cfg.pairingCodeTtlSeconds * 1000).toISOString().replace('T', ' ').replace('Z', '');
    this.db.prepare('INSERT INTO "PairingRequests" ("Code","GeneratorId","UserId","TargetDeviceId","TargetDeviceName","Status","ExpiresAt","CreatedAt","ConfirmedAt") VALUES (?,?,?,NULL,NULL,\'open\',?,?,NULL)')
      .run(code, deviceId, userId, expiresAt, now);
    this.addAudit('pair_code', '设备 ' + (deviceName || deviceId) + ' 生成配对码(用户 ' + userId + ')', ip);
    return { code, expiresAt: toIso(expiresAt), userId, ...(issuedToken ? { deviceToken: issuedToken } : {}) };
  }

  /**
   * 单向即入配对(方案 1 扫码直连 + 方案 2 纯 6 位数字验证码):
   * 凭有效 6 位码直接准入并关联设备, 无需手动输入用户ID, 无需原设备人工二次确认。
   */
  pair(
    code: string,
    deviceId: string,
    deviceName: string,
    ip: string | null = null,
    platform: string | null = null,
    version: string | null = null,
  ): { status: string; userId: string; deviceToken: string } {
    const now = dbNow();
    const plat = detectPlatform(platform, null, deviceName);
    const normCode = code.trim().toUpperCase();
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(normCode) as unknown as PairingRequestRow | null;
    if (!r || !['open', 'pending'].includes(r.Status) || r.ExpiresAt < now) {
      throw new PairError(400, '配对验证码无效或已过期');
    }
    if (r.GeneratorId === deviceId) {
      throw new PairError(400, '不能与本机自身配对');
    }
    const userId = r.UserId;
    const issued = this.issueDeviceToken();
    const existing = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    // 将配对请求置为 approved 并关联目标设备
    this.db.prepare('UPDATE "PairingRequests" SET "TargetDeviceId" = ?, "TargetDeviceName" = ?, "TargetTokenHash" = ?, "Status" = \'approved\', "ConfirmedAt" = ? WHERE "Code" = ?')
      .run(deviceId, deviceName || existing?.Name || '未知设备', issued.hash, now, normCode);

    if (existing) {
      this.db.prepare('UPDATE "Devices" SET "UserId" = ?, "Platform" = ?, "Token" = ?, "PairedAt" = ?, "RevokedAt" = NULL, "LastSeenAt" = ?, "Version" = COALESCE(?, "Version"), "Ip" = COALESCE(?, "Ip") WHERE "Id" = ?')
        .run(userId, plat, issued.hash, now, now, version, ip, deviceId);
    } else {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt","Token","PairedAt","UserId","RevokedAt") VALUES (?,?,?,?,?,?,?,?,?,NULL)')
        .run(deviceId, deviceName || '未知设备', plat, ip, version, now, issued.hash, now, userId);
    }
    this.addAudit('pair_direct', '设备 ' + (deviceName || deviceId) + ' 通过 6 位数字码/扫码直连加入用户 ' + userId, ip);
    this.hub.broadcastDevicesChanged();
    return { status: 'approved', userId, deviceToken: issued.token };
  }

  /** 作废未完成配对码(open 或 pending 状态) */
  revokePairingRequest(code: string, actor: PairingRequestActor): boolean {
    const request = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(code) as unknown as PairingRequestRow | null;
    if (!request || !['open', 'pending'].includes(request.Status)) return false;
    const allowed =
      actor.kind === 'admin' ||
      (actor.kind === 'user' && actor.userId === request.UserId) ||
      (actor.kind === 'device' && actor.deviceId === request.GeneratorId);
    if (!allowed) throw new PairError(403, '无权作废该配对码');
    const r = this.db.prepare('DELETE FROM "PairingRequests" WHERE "Code" = ? AND ("Status" = \'open\' OR "Status" = \'pending\')').run(code);
    return r.changes > 0;
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
  sessionForPair(pairingCode: string, deviceId: string): { role: 'user'; userId: string; deviceId: string; deviceTokenHash: string } {
    const r = this.db.prepare('SELECT * FROM "PairingRequests" WHERE "Code" = ?').get(pairingCode) as unknown as PairingRequestRow | null;
    if (!r || r.Status !== 'approved' || r.TargetDeviceId !== deviceId) {
      throw new PairError(400, '配对未完成或设备不匹配');
    }
    const device = this.getDevice(deviceId);
    if (!device || device.RevokedAt) {
      throw new PairError(410, '配对设备已被移除,无法建立会话');
    }
    if (!device.UserId || device.UserId !== r.UserId || !device.Token || !r.TargetTokenHash || device.Token !== r.TargetTokenHash) {
      throw new PairError(409, '配对设备状态已变化,请重新配对');
    }
    const issued = this.db.prepare('UPDATE "PairingRequests" SET "SessionIssuedAt" = ? WHERE "Code" = ? AND "SessionIssuedAt" IS NULL').run(dbNow(), pairingCode);
    if (issued.changes === 0) {
      throw new PairError(409, '该配对请求已兑换过会话');
    }
    return { role: 'user', userId: r.UserId, deviceId, deviceTokenHash: device.Token };
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
    this.db.prepare('UPDATE "Devices" SET "UserId" = NULL, "Token" = NULL, "PairedAt" = NULL, "RevokedAt" = ? WHERE "UserId" = ?').run(dbNow(), userId);
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
    const now = dbNow();
    this.db.prepare('UPDATE "Devices" SET "Token" = NULL, "UserId" = NULL, "PairedAt" = NULL, "RevokedAt" = ? WHERE "Id" = ?').run(now, id);
    this.db.prepare('UPDATE "PairingRequests" SET "Status" = \'rejected\', "ConfirmedAt" = ? WHERE ("GeneratorId" = ? OR "TargetDeviceId" = ?) AND "Status" IN (\'open\',\'pending\')').run(now, id, id);
    this.addActivity('delete', d.Name, '移除了设备', dbNow(), id);
    this.hub.disconnectDevice(id);
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
