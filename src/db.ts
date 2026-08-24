import { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';

export interface EntryRow {
  Id: number; Type: string; Text: string | null; ImageRef: string | null;
  ContentHash: string; DeviceId: string; DeviceName: string | null; IsManual?: number | null; CreatedAt: string;
}
export interface DeviceRow {
  Id: string; Name: string; Platform: string; Ip: string | null; Version: string | null;
  LastSeenAt: string; Token: string | null; PairedAt: string | null; UserId: string | null;
  RevokedAt: string | null;
}
export interface UserRow {
  Id: string; Name: string; CreatedAt: string;
}
export interface PairingRequestRow {
  Code: string; GeneratorId: string; UserId: string;
  TargetDeviceId: string | null; TargetDeviceName: string | null;
  TargetTokenHash: string | null;
  Status: string; ExpiresAt: string; CreatedAt: string; ConfirmedAt: string | null;
  SessionIssuedAt: string | null;
}
export interface AuditRow {
  Id: number; Action: string; Detail: string | null; Ip: string | null; CreatedAt: string;
}
export interface ActivityRow {
  Id: number; Action: string; DeviceName: string; Content: string | null; CreatedAt: string; DeviceId: string | null;
}

/** 打开(必要时创建)与 EF Core 生成的 schema 完全一致的 SQLite 库,可直接复用旧数据文件 */
export function openDb(cfg: AppConfig): DatabaseSync {
  const db = new DatabaseSync(cfg.databasePath);
  db.exec('PRAGMA journal_mode = WAL;');
  db.exec(`
CREATE TABLE IF NOT EXISTS "Entries" (
  "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  "Type" TEXT NOT NULL,
  "Text" TEXT NULL,
  "ImageRef" TEXT NULL,
  "ContentHash" TEXT NOT NULL,
  "DeviceId" TEXT NOT NULL,
  "DeviceName" TEXT NULL,
  "IsManual" INTEGER NULL DEFAULT 0,
  "CreatedAt" TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_Entries_CreatedAt" ON "Entries" ("CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_Entries_ContentHash" ON "Entries" ("ContentHash");
CREATE TABLE IF NOT EXISTS "Devices" (
  "Id" TEXT NOT NULL PRIMARY KEY,
  "Name" TEXT NOT NULL,
  "Platform" TEXT NOT NULL,
  "Ip" TEXT NULL,
  "Version" TEXT NULL,
  "LastSeenAt" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "Activities" (
  "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  "Action" TEXT NOT NULL,
  "DeviceName" TEXT NOT NULL,
  "Content" TEXT NULL,
  "CreatedAt" TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_Activities_CreatedAt" ON "Activities" ("CreatedAt");
CREATE TABLE IF NOT EXISTS "Users" (
  "Id" TEXT NOT NULL PRIMARY KEY,
  "Name" TEXT NOT NULL UNIQUE,
  "CreatedAt" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "PairingRequests" (
  "Code" TEXT NOT NULL PRIMARY KEY,
  "GeneratorId" TEXT NOT NULL,
  "UserId" TEXT NOT NULL,
  "TargetDeviceId" TEXT NULL,
  "TargetDeviceName" TEXT NULL,
  "TargetTokenHash" TEXT NULL,
  "Status" TEXT NOT NULL,
  "ExpiresAt" TEXT NOT NULL,
  "CreatedAt" TEXT NOT NULL,
  "ConfirmedAt" TEXT NULL,
  "SessionIssuedAt" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_PairingRequests_UserId" ON "PairingRequests" ("UserId");
CREATE INDEX IF NOT EXISTS "IX_PairingRequests_Status" ON "PairingRequests" ("Status");
CREATE TABLE IF NOT EXISTS "AuditLog" (
  "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  "Action" TEXT NOT NULL,
  "Detail" TEXT NULL,
  "Ip" TEXT NULL,
  "CreatedAt" TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS "Settings" (
  "Key" TEXT NOT NULL PRIMARY KEY,
  "Value" TEXT NOT NULL
);
`);
  // Entries 增量加列: IsManual 标记
  const entryCols = new Set((db.prepare('PRAGMA table_info("Entries")').all() as { name: string }[]).map(c => c.name));
  if (!entryCols.has('IsManual')) db.exec('ALTER TABLE "Entries" ADD COLUMN "IsManual" INTEGER NULL DEFAULT 0');

  // Devices 增量加列(老库兼容):设备专属 Token 哈希 + 配对时间
  const devCols = new Set((db.prepare('PRAGMA table_info("Devices")').all() as { name: string }[]).map(c => c.name));
  if (!devCols.has('Token')) db.exec('ALTER TABLE "Devices" ADD COLUMN "Token" TEXT NULL');
  if (!devCols.has('PairedAt')) db.exec('ALTER TABLE "Devices" ADD COLUMN "PairedAt" TEXT NULL');
  if (!devCols.has('UserId')) db.exec('ALTER TABLE "Devices" ADD COLUMN "UserId" TEXT NULL');
  if (!devCols.has('RevokedAt')) db.exec('ALTER TABLE "Devices" ADD COLUMN "RevokedAt" TEXT NULL');
  const pairCols = new Set((db.prepare('PRAGMA table_info("PairingRequests")').all() as { name: string }[]).map(c => c.name));
  if (!pairCols.has('TargetTokenHash')) db.exec('ALTER TABLE "PairingRequests" ADD COLUMN "TargetTokenHash" TEXT NULL');
  if (!pairCols.has('SessionIssuedAt')) db.exec('ALTER TABLE "PairingRequests" ADD COLUMN "SessionIssuedAt" TEXT NULL');
  const actCols = new Set((db.prepare('PRAGMA table_info("Activities")').all() as { name: string }[]).map(c => c.name));
  if (!actCols.has('DeviceId')) db.exec('ALTER TABLE "Activities" ADD COLUMN "DeviceId" TEXT NULL');
  // 平滑修复历史遗留的 Unknown Web 平台设备
  try {
    db.exec(`
      UPDATE "Devices"
      SET "Platform" = 'Web'
      WHERE ("Platform" = 'Unknown' OR "Platform" IS NULL OR "Platform" = '')
        AND ("Name" LIKE '%Web%' OR "Name" LIKE '%浏览器%' OR "Name" LIKE '%管理页%' OR "Name" LIKE '%控制台%');
    `);
  } catch { /* 忽略 */ }
  // 旧版本已有绑定关系但未签发令牌；客户端通过一次性 legacy claim 平滑迁移。
  return db;
}
