import { readFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export interface AppConfig {
  port: number;
  maxHistoryCount: number;
  databasePath: string;
  imageStoragePath: string;
  maxImageSizeBytes: number;
  onlineThresholdSeconds: number;
  webDist: string | null;
  pairingCodeTtlSeconds: number;
  adminUsername: string;
  adminPassword: string;
  sessionTtlHours: number;
  startedAt: Date;
}

const here = dirname(fileURLToPath(import.meta.url)); // dist/ 或 src/(tsx)
const rootDir = resolve(here, '..');

/** 极简 .env 解析:KEY=VALUE,# 注释,可选引号包裹 */
function loadDotEnv(file: string): Record<string, string> {
  const out: Record<string, string> = {};
  if (!existsSync(file)) return out;
  for (const raw of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim();
    let val = line.slice(eq + 1).trim();
    if (val.startsWith('"') && val.endsWith('"')) val = val.slice(1, -1);
    if (val.startsWith("'") && val.endsWith("'")) val = val.slice(1, -1);
    out[key] = val;
  }
  return out;
}

const dotEnv = loadDotEnv(join(rootDir, '.env'));

/** 服务端版本(package.json,接口返回给前端展示) */
export const SERVER_VERSION: string =
  (JSON.parse(readFileSync(join(rootDir, 'package.json'), 'utf8')) as { version?: string }).version ?? '0.0.0';

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
  let defaultDb = 'data/nexclip.db';
  if (!existsSync(resolve(rootDir, defaultDb)) && existsSync(resolve(rootDir, 'data/syncclipboard.db'))) {
    defaultDb = 'data/syncclipboard.db';
  }
  const cfg: AppConfig = {
    port: num(env('SC_PORT', String(file.port ?? 5033)), 5033),
    maxHistoryCount: num(env('SC_MAX_HISTORY', String(file.maxHistoryCount ?? 1000)), 1000),
    databasePath: env('SC_DB_PATH', String(file.databasePath ?? defaultDb)),
    imageStoragePath: env('SC_IMAGE_PATH', String(file.imageStoragePath ?? 'data/images')),
    maxImageSizeBytes: num(env('SC_MAX_IMAGE_BYTES', String(file.maxImageSizeBytes ?? 10 * 1024 * 1024)), 10 * 1024 * 1024),
    onlineThresholdSeconds: num(env('SC_ONLINE_THRESHOLD_SECONDS', String(file.onlineThresholdSeconds ?? 120)), 120),
    webDist: file.webDist == null ? '../web/dist' : String(file.webDist),
    pairingCodeTtlSeconds: num(env('SC_PAIRING_TTL_SECONDS', String(file.pairingCodeTtlSeconds ?? 600)), 600),
    adminUsername: env('ADMIN_USERNAME', dotEnv.ADMIN_USERNAME ?? 'syncadmin'),
    adminPassword: env('ADMIN_PASSWORD', dotEnv.ADMIN_PASSWORD ?? ''),
    sessionTtlHours: num(env('SESSION_TTL_HOURS', dotEnv.SESSION_TTL_HOURS ?? '24'), 24),
    startedAt: new Date(),
  };
  cfg.databasePath = resolve(rootDir, cfg.databasePath);
  cfg.imageStoragePath = resolve(rootDir, cfg.imageStoragePath);
  cfg.webDist = cfg.webDist ? resolve(rootDir, cfg.webDist) : null;
  mkdirSync(dirname(cfg.databasePath), { recursive: true });
  mkdirSync(cfg.imageStoragePath, { recursive: true });
  return cfg;
}
