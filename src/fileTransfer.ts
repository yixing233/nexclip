import { DatabaseSync } from 'node:sqlite';
import { mkdirSync, writeFileSync, statSync, createReadStream, existsSync, unlinkSync } from 'node:fs';
import { join, extname, resolve, relative, isAbsolute } from 'node:path';
import { randomHex, sha256Bytes, dbNow, toIso, truncate } from './util.js';
import type { SignalRHub } from './signalr.js';

/**
 * 网页端文件互传:上传 → 存储 → 推送 → 下载/删除。
 *
 * ## 为什么独立于 SyncService 的 Entries 表
 *
 * 剪贴板条目会被 trimHistory 清理、被各端当作"可写入剪贴板的内容"消费;
 * 文件互传是独立的对话式传输流,有自己的生命周期(网页会话维度),
 * 复用 Entries 会让两边的清理策略互相干扰。
 *
 * ## 存储与隐私
 *
 * - 文件落盘在 fileStoragePath(默认 data/files),按 yyyyMMdd 分目录;
 * - 元数据(FileId/原名/大小/SHA-256/上传者)存 SQLite Files 表;
 * - 下载路径校验与 static.ts 同款:resolve + relative 防目录穿越;
 * - 文件按用户组隔离:userId 为 null(单用户模式)时全量可见。
 */
export interface FileRow {
  Id: number;
  FileId: string;
  FileName: string;
  Size: number;
  ContentHash: string;
  StoragePath: string;
  DeviceId: string;
  DeviceName: string | null;
  UserId: string | null;
  CreatedAt: string;
  TargetIds?: string | null;
  TransferStatus?: string | null;
}

export interface FileDto {
  id: number;
  fileId: string;
  fileName: string;
  size: number;
  contentHash: string;
  deviceId: string;
  deviceName: string | null;
  createdAt: string;
  downloadUrl: string;
  /** 确认制传输状态:pending=等待目标设备确认;accepted/rejected;null=旧式广播 */
  transferStatus?: string | null;
}

export class FileTransferService {
  /** 文件存储根目录(懒加载,管理台可改则沿用 Settings 表) */
  private readonly storageRoot: string;

  constructor(
    private readonly db: DatabaseSync,
    storageRoot: string,
    private readonly hub: SignalRHub,
  ) {
    this.storageRoot = resolve(storageRoot);
    mkdirSync(this.storageRoot, { recursive: true });
    this.ensureSchema();
  }

  private ensureSchema(): void {
    this.db.exec(`
CREATE TABLE IF NOT EXISTS "Files" (
  "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  "FileId" TEXT NOT NULL UNIQUE,
  "FileName" TEXT NOT NULL,
  "Size" INTEGER NOT NULL,
  "ContentHash" TEXT NOT NULL,
  "StoragePath" TEXT NOT NULL,
  "DeviceId" TEXT NOT NULL,
  "DeviceName" TEXT NULL,
  "UserId" TEXT NULL,
  "CreatedAt" TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_Files_CreatedAt" ON "Files" ("CreatedAt");
`);
    // 确认制传输增量加列(老库兼容)
    const fileCols = new Set((this.db.prepare('PRAGMA table_info("Files")').all() as { name: string }[]).map(c => c.name));
    if (!fileCols.has('TargetIds')) this.db.exec('ALTER TABLE "Files" ADD COLUMN "TargetIds" TEXT NULL');
    if (!fileCols.has('TransferStatus')) this.db.exec('ALTER TABLE "Files" ADD COLUMN "TransferStatus" TEXT NULL');
  }

  /**
   * 上传文件。
   *
   * @param targets 指定接收设备的 deviceId 集合;提供时只定向通知这些设备,缺省全员广播
   * @returns 文件 DTO;size 超限抛 RangeError 由控制器转 413
   */
  upload(fileName: string, data: Buffer, deviceId: string, deviceName: string | null, userId: string | null, targets?: ReadonlySet<string>): FileDto {
    const safeName = sanitizeFileName(fileName || 'file');
    const hash = sha256Bytes(data);
    const dir = new Date().toISOString().slice(0, 10).replace(/-/g, '');
    const fullDir = join(this.storageRoot, dir);
    mkdirSync(fullDir, { recursive: true });
    const ext = extname(safeName).toLowerCase();
    const fileId = randomHex(16);
    const storageRel = dir + '/' + fileId + ext;
    writeFileSync(join(this.storageRoot, storageRel), data);

    const now = dbNow();
    this.db.prepare(
      `INSERT INTO "Files" ("FileId","FileName","Size","ContentHash","StoragePath","DeviceId","DeviceName","UserId","CreatedAt")
       VALUES (?,?,?,?,?,?,?,?,?)`
    ).run(fileId, safeName, data.length, hash, storageRel, deviceId, deviceName, userId, now);

    const dto = this.toDto(this.getByFileId(fileId)!);
    if (targets && targets.size > 0) {
      // 确认制:标记待确认,只向目标设备广播 FileTransferRequested(发送方不收自己的回声)
      this.db.prepare('UPDATE "Files" SET "TargetIds" = ?, "TransferStatus" = ? WHERE "FileId" = ?')
        .run([...targets].join(','), 'pending', fileId);
      dto.transferStatus = 'pending';
      this.hub.broadcastFileTransferRequested(dto, targets);
    } else {
      // 与 broadcastUpdated 的用户组隔离语义一致:userId 为 null 时全量广播
      this.hub.broadcastFileTransferred(dto, userId);
    }
    return dto;
  }

  /**
   * 目标设备响应确认制传输。
   *
   * @param accept true=接受(标记 accepted);false=拒绝(文件立即删除,即时互传不留垃圾)
   * @returns false = fileId 不存在/非 pending/设备不在目标列表/用户组越权
   */
  respond(fileId: string, deviceId: string, accept: boolean, userId: string | null): boolean {
    const row = this.getByFileId(fileId);
    if (!row || row.TransferStatus !== 'pending') return false;
    if (userId && row.UserId && row.UserId !== userId) return false;
    const targetIds = (row.TargetIds ?? '').split(',').filter(Boolean);
    if (!targetIds.some(id => id.toLowerCase() === deviceId.toLowerCase())) return false;

    if (accept) {
      this.db.prepare('UPDATE "Files" SET "TransferStatus" = ? WHERE "FileId" = ?').run('accepted', fileId);
      this.hub.broadcastFileResponded(fileId, 'accepted', row.DeviceId);
      return true;
    }
    // 拒绝:立即删除文件行与磁盘内容,只给发送方一条"已拒绝"
    this.db.prepare('DELETE FROM "Files" WHERE "FileId" = ?').run(fileId);
    this.withSafePath(row.StoragePath, (full) => {
      if (existsSync(full)) runCatching(() => unlinkSync(full));
      return null;
    });
    this.hub.broadcastFileResponded(fileId, 'rejected', row.DeviceId);
    return true;
  }

  /** 清扫超时未确认的 pending 文件(即时互传不悬挂垃圾),逐个广播 FileDeleted 让发送方显示"已过期" */
  sweepExpiredPending(ttlSeconds: number): void {
    const cutoff = Date.now() - Math.max(5, ttlSeconds) * 1000;
    const rows = this.db.prepare(
      `SELECT "FileId","StoragePath","UserId","CreatedAt" FROM "Files" WHERE "TransferStatus" = 'pending'`
    ).all() as unknown as FileRow[];
    for (const row of rows) {
      if (Date.parse(toIso(row.CreatedAt)) >= cutoff) continue;
      this.db.prepare('DELETE FROM "Files" WHERE "FileId" = ?').run(row.FileId);
      this.withSafePath(row.StoragePath, (full) => {
        if (existsSync(full)) runCatching(() => unlinkSync(full));
        return null;
      });
      this.hub.broadcastFileDeleted(row.FileId, row.UserId);
    }
  }

  /** 按用户组列出文件(新→旧)。limit 上限 200,防止一次拉爆。 */
  list(userId: string | null, limit = 50, offset = 0): { items: FileDto[]; total: number } {
    const cappedLimit = Math.max(1, Math.min(200, Math.floor(limit)));
    const rows = userId
      ? this.db.prepare('SELECT * FROM "Files" WHERE "UserId" = ? ORDER BY "Id" DESC LIMIT ? OFFSET ?')
          .all(userId, cappedLimit, Math.max(0, Math.floor(offset))) as unknown as FileRow[]
      : this.db.prepare('SELECT * FROM "Files" ORDER BY "Id" DESC LIMIT ? OFFSET ?')
          .all(cappedLimit, Math.max(0, Math.floor(offset))) as unknown as FileRow[];
    const totalRow = userId
      ? this.db.prepare('SELECT COUNT(*) AS c FROM "Files" WHERE "UserId" = ?').get(userId) as { c: number }
      : this.db.prepare('SELECT COUNT(*) AS c FROM "Files"').get() as { c: number };
    return { items: rows.map(r => this.toDto(r)), total: totalRow.c };
  }

  /** 取文件元数据(鉴权后才能拿 downloadUrl) */
  getByFileId(fileId: string): FileRow | null {
    return this.db.prepare('SELECT * FROM "Files" WHERE "FileId" = ?').get(fileId) as unknown as FileRow | null;
  }

  /**
   * 打开文件流。返回 null 表示记录存在但文件已丢失(磁盘清理/被手动删除)。
   * 路径校验:StoragePath 是服务端自己生成的,但仍然走 relative 校验防库被手改。
   */
  openStream(row: FileRow): { stream: ReturnType<typeof createReadStream>; size: number } | null {
    return this.withSafePath(row.StoragePath, (full) => {
      if (!existsSync(full)) return null;
      return { stream: createReadStream(full), size: statSync(full).size };
    });
  }

  /**
   * StoragePath 安全打开:resolve(root, rel) 后用 relative 校验仍在存储根内。
   *
   * 之前写成 resolve(row.StoragePath) —— 那是相对进程 CWD 解析,
   * 与 root 拼接后的结果永远对不上,导致所有下载都 410。
   * (static.ts 的同款校验是对的,这里对齐它。)
   */
  private withSafePath<T>(relPath: string, fn: (full: string) => T | null): T | null {
    const full = resolve(this.storageRoot, relPath);
    const rel = relative(this.storageRoot, full);
    if (rel.startsWith('..') || isAbsolute(rel)) return null;
    return fn(full);
  }

  delete(fileId: string, userId: string | null): boolean {
    const row = this.getByFileId(fileId);
    if (!row) return false;
    if (userId && row.UserId && row.UserId !== userId) return false; // 越权删除
    this.withSafePath(row.StoragePath, (full) => {
      if (existsSync(full)) runCatching(() => unlinkSync(full));
      return null;
    });
    this.db.prepare('DELETE FROM "Files" WHERE "FileId" = ?').run(fileId);
    this.hub.broadcastFileDeleted(fileId, row.UserId);
    return true;
  }

  /** 磁盘占用统计(管理台展示用) */
  stats(): { count: number; totalBytes: number } {
    const row = this.db.prepare('SELECT COUNT(*) AS c, COALESCE(SUM("Size"),0) AS s FROM "Files"').get() as { c: number; s: number };
    return { count: row.c, totalBytes: row.s };
  }

  private toDto(r: FileRow): FileDto {
    return {
      id: r.Id,
      fileId: r.FileId,
      fileName: r.FileName,
      size: r.Size,
      contentHash: r.ContentHash,
      deviceId: r.DeviceId,
      deviceName: r.DeviceName,
      createdAt: toIso(r.CreatedAt),
      downloadUrl: '/api/files/' + r.FileId + '/download',
      transferStatus: r.TransferStatus ?? null,
    };
  }
}

/** 清洗文件名:去路径分隔符与控制字符,截断到 200 字符(含扩展名) */
function sanitizeFileName(name: string): string {
  const base = name.replace(/[\/\\:*?"<>|\x00-\x1f]/g, '_').trim();
  return truncate(base === '' ? 'file' : base, 200);
}

/** runCatching 风格包装,node:fs 删除失败不抛(下次 stats 会暴露) */
function runCatching(fn: () => void): void {
  try { fn(); } catch { /* 保守吞掉:文件已在磁盘上丢失是常态 */ }
}
