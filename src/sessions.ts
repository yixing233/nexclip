/** 管理台账密登录会话:内存态随机令牌 + TTL */
export class SessionStore {
  private readonly tokens = new Map<string, number>(); // token -> expiresAt(ms)

  /** 创建会话,返回 64hex 令牌 */
  create(ttlHours: number): string {
    const token = randomHexBytes(32);
    this.tokens.set(token, Date.now() + ttlHours * 3600_000);
    return token;
  }

  /** 校验会话(存在且未过期) */
  validate(token: string): boolean {
    const exp = this.tokens.get(token);
    if (exp === undefined) return false;
    if (exp < Date.now()) {
      this.tokens.delete(token);
      return false;
    }
    return true;
  }

  /** 注销会话 */
  revoke(token: string): void {
    this.tokens.delete(token);
  }
}

import { randomBytes } from 'node:crypto';
function randomHexBytes(n: number): string {
  return randomBytes(n).toString('hex');
}
