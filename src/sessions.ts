import { createHash, randomBytes } from 'node:crypto';
import type { DatabaseSync } from 'node:sqlite';

/** 会话载荷:admin = 管理台;user = 用户网页(绑定用户ID + 设备) */
export interface SessionPayload {
  role: 'admin' | 'user';
  /** 管理台:登录用户名(界面展示) */
  username?: string;
  userId?: string;
  deviceId?: string;
  /** 兑换会话时的设备令牌哈希,用于设备移除/重新配对后使旧会话永久失效。 */
  deviceTokenHash?: string;
}

/** 会话存储:SQLite 持久化 + 令牌只存 SHA-256 哈希。
 *
 * 之前是内存 Map —— 每次部署/重启(pm2 restart)所有网页会话即蒸发,
 * 浏览器下次请求 401 被清凭证,而网页登录本身就是配对,于是频繁要求重新配对。
 * 落库后重启/部署不再掉线;设备行被删除/重配对仍会经 server.ts 的绑定校验作废。
 */
export class SessionStore {
  private readonly db: DatabaseSync;

  constructor(db: DatabaseSync) {
    this.db = db;
  }

  private static hash(token: string): string {
    return createHash('sha256').update(token).digest('hex');
  }

  create(payload: SessionPayload, ttlHours: number): string {
    const token = randomBytes(32).toString('hex');
    const tokenHash = SessionStore.hash(token);
    const expiresAt = Date.now() + ttlHours * 3600_000;
    this.db.prepare(
      `INSERT INTO "Sessions" ("TokenHash","Role","UserId","Username","DeviceId","DeviceTokenHash","ExpiresAt")
       VALUES (?,?,?,?,?,?,?)`
    ).run(tokenHash, payload.role, payload.userId ?? null, payload.username ?? null, payload.deviceId ?? null, payload.deviceTokenHash ?? null, expiresAt);
    // 顺带清理过期会话(创建频率低,全表扫描代价可忽略)
    this.db.prepare('DELETE FROM "Sessions" WHERE "ExpiresAt" < ?').run(Date.now());
    return token;
  }

  /** 校验并返回载荷;过期/不存在返回 null */
  validate(token: string): SessionPayload | null {
    const tokenHash = SessionStore.hash(token);
    const row = this.db.prepare(
      'SELECT "Role","UserId","Username","DeviceId","DeviceTokenHash","ExpiresAt" FROM "Sessions" WHERE "TokenHash" = ?'
    ).get(tokenHash) as
      | { Role: string; UserId: string | null; Username: string | null; DeviceId: string | null; DeviceTokenHash: string | null; ExpiresAt: number }
      | null;
    if (!row) return null;
    if (row.ExpiresAt < Date.now()) {
      this.db.prepare('DELETE FROM "Sessions" WHERE "TokenHash" = ?').run(tokenHash);
      return null;
    }
    return {
      role: row.Role as 'admin' | 'user',
      userId: row.UserId ?? undefined,
      username: row.Username ?? undefined,
      deviceId: row.DeviceId ?? undefined,
      deviceTokenHash: row.DeviceTokenHash ?? undefined,
    };
  }

  revoke(token: string): void {
    this.db.prepare('DELETE FROM "Sessions" WHERE "TokenHash" = ?').run(SessionStore.hash(token));
  }
}
