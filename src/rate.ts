/** 简易滑动窗口限速器(内存态,按 key 计数) */
export class RateLimiter {
  private readonly hits = new Map<string, number[]>();

  /** key 在 windowMs 内是否超过 limit 次;通过则记录一次 */
  allow(key: string, limit: number, windowMs: number): boolean {
    const now = Date.now();
    const arr = (this.hits.get(key) ?? []).filter(t => now - t < windowMs);
    if (arr.length >= limit) {
      this.hits.set(key, arr);
      return false;
    }
    arr.push(now);
    this.hits.set(key, arr);
    return true;
  }

  /** 失败计数(如登录):达到 limit 次则锁定 windowMs */
  fail(key: string, limit: number, windowMs: number): boolean {
    return this.allow(key, limit, windowMs);
  }

  /** 成功后清零 */
  reset(key: string): void {
    this.hits.delete(key);
  }
}
