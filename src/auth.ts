import type { IncomingMessage } from 'node:http';
import type { SessionStore } from './sessions.js';

/** 提取 Bearer header 或 query access_token */
export function extractToken(req: IncomingMessage): string {
  const auth = req.headers.authorization ?? '';
  if (auth.startsWith('Bearer ')) return auth.slice('Bearer '.length).trim();
  const u = new URL(req.url ?? '/', 'http://localhost');
  return u.searchParams.get('access_token') ?? '';
}

/**
 * 需登录的路径:仅管理台接口。
 * 设备同步接口(剪贴板/历史/设备列表/图片/hub/配对/配对码)一律免认证:
 * 设备接入只靠"配对码"完成登记,不再签发/校验设备令牌(设计变更)。
 */
export function needsAuth(method: string, p: string): boolean {
  if (
    p === '/api/stats' || p === '/api/activities' || p === '/api/health' ||
    (p === '/api/devices' && method === 'PUT') ||
    (p === '/api/devices' && method === 'DELETE') ||
    (/^\/api\/devices\//.test(p) && (method === 'PUT' || method === 'DELETE'))
  ) return true;
  return false;
}

/** 管理台会话令牌(账密登录签发) */
export function checkSessionToken(req: IncomingMessage, sessions: SessionStore): boolean {
  const token = extractToken(req);
  return !!token && sessions.validate(token);
}
