import { randomBytes } from 'node:crypto';

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

/** 会话存储:内存态随机令牌 + TTL,携带角色载荷 */
export class SessionStore {
  private readonly tokens = new Map<string, { payload: SessionPayload; expiresAt: number }>();

  create(payload: SessionPayload, ttlHours: number): string {
    const token = randomBytes(32).toString('hex');
    this.tokens.set(token, { payload, expiresAt: Date.now() + ttlHours * 3600_000 });
    return token;
  }

  /** 校验并返回载荷;过期/不存在返回 null */
  validate(token: string): SessionPayload | null {
    const s = this.tokens.get(token);
    if (!s) return null;
    if (s.expiresAt < Date.now()) {
      this.tokens.delete(token);
      return null;
    }
    return s.payload;
  }

  revoke(token: string): void {
    this.tokens.delete(token);
  }
}
