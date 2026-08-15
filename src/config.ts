import { readFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export interface AppConfig {
  port: number;
  authToken: string;
  maxHistoryCount: number;
  databasePath: string;
  imageStoragePath: string;
  maxImageSizeBytes: number;
  onlineThresholdSeconds: number;
  webDist: string | null;
  startedAt: Date;
}

const here = dirname(fileURLToPath(import.meta.url)); // dist/ 或 src/(tsx)
const rootDir = resolve(here, '..');

export function loadConfig(): AppConfig {
  const cfgPath = join(rootDir, 'config.json');
  let file: Record<string, unknown> = {};
  if (existsSync(cfgPath)) {
    file = JSON.parse(readFileSync(cfgPath, 'utf8')) as Record<string, unknown>;
  }
  const num = (v: unknown, d: number) => {
    const n = Number(v);
    return Number.isFinite(n) && n > 0 ? n : d;
  };
  const env = (k: string, d: string) => process.env[k] ?? d;
  const cfg: AppConfig = {
    port: num(env('SC_PORT', String(file.port ?? 5033)), 5033),
    authToken: env('SC_AUTH_TOKEN', String(file.authToken ?? 'change-me')),
    maxHistoryCount: num(env('SC_MAX_HISTORY', String(file.maxHistoryCount ?? 1000)), 1000),
    databasePath: env('SC_DB_PATH', String(file.databasePath ?? 'data/syncclipboard.db')),
    imageStoragePath: env('SC_IMAGE_PATH', String(file.imageStoragePath ?? 'data/images')),
    maxImageSizeBytes: num(env('SC_MAX_IMAGE_BYTES', String(file.maxImageSizeBytes ?? 10 * 1024 * 1024)), 10 * 1024 * 1024),
    onlineThresholdSeconds: num(env('SC_ONLINE_THRESHOLD_SECONDS', String(file.onlineThresholdSeconds ?? 120)), 120),
    webDist: file.webDist == null ? '../web/dist' : String(file.webDist),
    startedAt: new Date(),
  };
  cfg.databasePath = resolve(rootDir, cfg.databasePath);
  cfg.imageStoragePath = resolve(rootDir, cfg.imageStoragePath);
  cfg.webDist = cfg.webDist ? resolve(rootDir, cfg.webDist) : null;
  mkdirSync(dirname(cfg.databasePath), { recursive: true });
  mkdirSync(cfg.imageStoragePath, { recursive: true });
  return cfg;
}
