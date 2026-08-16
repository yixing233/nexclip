import type { IncomingMessage } from 'node:http';
import type { SessionStore } from './sessions.js';

/** 提取 Bearer header 或 query access_token */
export function extractToken(req: IncomingMessage): string {
  const auth = req.headers.authorization ?? '';
  if (auth.startsWith('Bearer ')) return auth.slice('Bearer '.length).trim();
  const u = new URL(req.url ?? '/', 'http://localhost');
  return u.searchParams.get('access_token') ?? '';
}

/** 路由鉴权类别:
 *  open  = 免认证(设备同步、配对流程、hub、登录/登出、健康检查)
 *  user  = 需会话(用户网页或管理台)
 *  admin = 仅管理台会话
 */
export function routeClass(method: string, p: string): 'open' | 'user' | 'admin' {
  // 管理台专属
  if (
    p === '/api/stats' ||
    p === '/api/users' ||
    p.startsWith('/api/admin/') ||
    p === '/api/clipboard/send' ||
    (p === '/api/clipboard/history' && method === 'DELETE') ||
    (/^\/api\/users\/[^/]+$/.test(p) && method === 'DELETE')
  ) return 'admin';
  // 用户/管理会话皆可(控制器内再按归属校验:自己的用户ID/组内设备/组内条目)
  if (
    p === '/api/me' ||
    p === '/api/activities' ||
    /^\/api\/users\//.test(p) ||
    (/^\/api\/devices\//.test(p) && (method === 'PUT' || method === 'DELETE')) ||
    (p === '/api/pairing-requests' && method === 'GET') ||
    (/^\/api\/clipboard\/\d+$/.test(p) && method === 'DELETE')
  ) return 'user';
  return 'open';
}

/** 校验会话令牌,返回载荷(admin/user);无效返回 null */
export function checkSession(req: IncomingMessage, sessions: SessionStore) {
  const token = extractToken(req);
  if (!token) return null;
  return sessions.validate(token);
}
