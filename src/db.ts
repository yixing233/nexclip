import { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';

export interface EntryRow {
  Id: number; Type: string; Text: string | null; ImageRef: string | null;
  ContentHash: string; DeviceId: string; DeviceName: string | null; CreatedAt: string;
}
export interface DeviceRow {
  Id: string; Name: string; Platform: string; Ip: string | null; Version: string | null;
  LastSeenAt: string; Token: string | null; PairedAt: string | null; UserId: string | null;
}
export interface UserRow {
  Id: string; Name: string; CreatedAt: string;
}
export interface PairingRequestRow {
  Code: string; GeneratorId: string; UserId: string;
  TargetDeviceId: string | null; TargetDeviceName: string | null;
  Status: string; ExpiresAt: string; CreatedAt: string; ConfirmedAt: string | null;
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
  "Status" TEXT NOT NULL,
  "ExpiresAt" TEXT NOT NULL,
  "CreatedAt" TEXT NOT NULL,
  "ConfirmedAt" TEXT NULL
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
`);
  // Devices 增量加列(老库兼容):设备专属 Token 哈希 + 配对时间
  const devCols = new Set((db.prepare('PRAGMA table_info("Devices")').all() as { name: string }[]).map(c => c.name));
  if (!devCols.has('Token')) db.exec('ALTER TABLE "Devices" ADD COLUMN "Token" TEXT NULL');
  if (!devCols.has('PairedAt')) db.exec('ALTER TABLE "Devices" ADD COLUMN "PairedAt" TEXT NULL');
  if (!devCols.has('UserId')) db.exec('ALTER TABLE "Devices" ADD COLUMN "UserId" TEXT NULL');
  const actCols = new Set((db.prepare('PRAGMA table_info("Activities")').all() as { name: string }[]).map(c => c.name));
  if (!actCols.has('DeviceId')) db.exec('ALTER TABLE "Activities" ADD COLUMN "DeviceId" TEXT NULL');
  // 设备行兼容:Token/PairedAt 列保留但不再签发令牌(设计变更)
  return db;
}
