import type { IncomingMessage } from 'node:http';
import type { AppConfig } from './config.js';

/** 与 .NET TokenAuthMiddleware 一致:/api、/hubs、/SyncClipboard.json 需 Bearer 或 query access_token */
export function needsAuth(p: string): boolean {
  return p.startsWith('/api/') || p.startsWith('/hubs/') || p.startsWith('/SyncClipboard.json');
}

export function checkAuth(req: IncomingMessage, cfg: AppConfig): boolean {
  if (!cfg.authToken) return true;
  const auth = req.headers.authorization ?? '';
  let token = auth.startsWith('Bearer ') ? auth.slice('Bearer '.length).trim() : '';
  if (!token) {
    const u = new URL(req.url ?? '/', 'http://localhost');
    token = u.searchParams.get('access_token') ?? '';
  }
  return token === cfg.authToken;
}
