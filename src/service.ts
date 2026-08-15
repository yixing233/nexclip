import { mkdirSync, existsSync, writeFileSync, unlinkSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';
import type { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';
import type { EntryRow, DeviceRow, ActivityRow } from './db.js';
import { dbNow, toIso, sha256Hex, truncate, clamp, randomHex } from './util.js';
import type { SignalRHub } from './signalr.js';

export interface EntryDto {
  id: number; type: string; text: string | null; imageRef: string | null;
  deviceId: string; deviceName: string | null; createdAt: string;
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

  getHistory(offset: number, limit: number): { items: EntryDto[]; total: number } {
    limit = clamp(limit, 1, 200);
    offset = Math.max(0, offset);
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
    this.addActivity('push', deviceName, truncate(text, 120), now);
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
    this.addActivity('push', deviceName, truncate(fileName, 120), now);
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
    this.addActivity('delete', e.DeviceName ?? '?', '删除了 1 条剪贴板记录', dbNow());
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

  /** hub 连接登记(与 .NET OnConnected 一致:不存在则建 未知设备/Web,每次记 connect 活动) */
  registerHubDevice(deviceId: string | null, deviceName: string | null = null): void {
    if (!deviceId) return;
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(deviceId) as unknown as DeviceRow | null;
    const now = dbNow();
    const name = deviceName?.trim() || d?.Name || '未知设备';
    if (!d) {
      this.db.prepare('INSERT INTO "Devices" ("Id","Name","Platform","Ip","Version","LastSeenAt") VALUES (?,?,?,NULL,NULL,?)')
        .run(deviceId, name, 'Web', now);
    } else {
      this.db.prepare('UPDATE "Devices" SET "Name" = ?, "LastSeenAt" = ? WHERE "Id" = ?').run(name, now, deviceId);
    }
    this.addActivity('connect', name, null, now);
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
    return true;
  }

  removeDevice(id: string): boolean {
    const d = this.db.prepare('SELECT * FROM "Devices" WHERE "Id" = ?').get(id) as unknown as DeviceRow | null;
    if (!d) return false;
    this.db.prepare('DELETE FROM "Devices" WHERE "Id" = ?').run(id);
    this.addActivity('delete', d.Name, '移除了设备', dbNow());
    return true;
  }

  // ---------- 活动 ----------
  addActivity(action: string, deviceName: string, content: string | null, createdAt: string): void {
    this.db.prepare('INSERT INTO "Activities" ("Action","DeviceName","Content","CreatedAt") VALUES (?,?,?,?)')
      .run(action, deviceName, content, createdAt);
  }

  listActivities(limit: number): Array<Record<string, unknown>> {
    limit = clamp(limit, 1, 200);
    const rows = this.db.prepare('SELECT * FROM "Activities" ORDER BY "Id" DESC LIMIT ?').all(limit) as unknown as ActivityRow[];
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
    const connects = this.db.prepare('SELECT "CreatedAt" FROM "Activities" WHERE "Action" = ? AND "CreatedAt" >= ?').all('connect', since) as unknown as { CreatedAt: string }[];
    const devicesSpark = new Array<number>(12).fill(0);
    for (let i = 0; i < 12; i++) {
      const s = new Date(now.getTime() + (i - 12) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      const e = new Date(now.getTime() + (i - 11) * 3600000).toISOString().replace('T', ' ').replace('Z', '');
      devicesSpark[i] = connects.filter(x => x.CreatedAt >= s && x.CreatedAt < e).length;
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
      todaySyncCount: todayCount,
      syncTrend,
      totalClipboardCount: totalEntries,
      status: 'running',
      uptime: uptimeStr,
      avgLatencyMs: latency.avgMs,
      sparklines: { devices: devicesSpark, sync, history: hist, latency: latency.last12 },
    };
  }
}
