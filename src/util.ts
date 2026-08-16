import { createHash, randomBytes, randomInt as cryptoRandomInt } from 'node:crypto';

/** 与 .NET Convert.ToHexString(SHA256) 一致:大写十六进制 */
export function sha256Hex(s: string): string {
  return createHash('sha256').update(s, 'utf8').digest('hex').toUpperCase();
}

/** 入库时间格式(与 EF Core SQLite 存储一致):"yyyy-MM-dd HH:mm:ss.fff" (UTC) */
export function dbNow(): string {
  return new Date().toISOString().replace('T', ' ').replace('Z', '');
}

/** API 输出格式(与 .NET DateTime.ToString("O") 一致):"yyyy-MM-ddTHH:mm:ss.fffZ" */
export function toIso(dbValue: string): string {
  return dbValue.replace(' ', 'T').replace('Z', '') + 'Z';
}

export function truncate(s: string, n: number): string {
  return s.length <= n ? s : s.slice(0, n);
}

export function clamp(n: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, n));
}

export function randomHex(bytes: number): string {
  return randomBytes(bytes).toString('hex');
}

export function randomInt(max: number): number {
  return cryptoRandomInt(max);
}
