import { DatabaseSync } from 'node:sqlite';
import type { AppConfig } from './config.js';

export interface EntryRow {
  Id: number; Type: string; Text: string | null; ImageRef: string | null;
  ContentHash: string; DeviceId: string; DeviceName: string | null; CreatedAt: string;
}
export interface DeviceRow {
  Id: string; Name: string; Platform: string; Ip: string | null; Version: string | null; LastSeenAt: string;
}
export interface ActivityRow {
  Id: number; Action: string; DeviceName: string; Content: string | null; CreatedAt: string;
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
`);
  return db;
}
