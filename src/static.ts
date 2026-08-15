import type { IncomingMessage, ServerResponse } from 'node:http';
import { existsSync, createReadStream, statSync } from 'node:fs';
import { resolve, join, extname, relative, isAbsolute } from 'node:path';

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
  '.map': 'application/json',
  '.txt': 'text/plain; charset=utf-8',
};

/** 静态托管 web/dist,非 API 路径回退 index.html(SPA),与 .NET 版行为一致 */
export function serveStatic(root: string | null, req: IncomingMessage, res: ServerResponse): boolean {
  if (!root || !existsSync(root)) return false;
  const url = new URL(req.url ?? '/', 'http://localhost');
  let pathname = decodeURIComponent(url.pathname);
  if (pathname === '/') pathname = '/index.html';
  const rootResolved = resolve(root);
  const full = resolve(join(rootResolved, pathname));
  const rel = relative(rootResolved, full);
  if (rel.startsWith('..') || isAbsolute(rel)) {
    res.statusCode = 403;
    res.end();
    return true;
  }
  if (!existsSync(full) || statSync(full).isDirectory()) {
    // SPA 回退
    const idx = join(rootResolved, 'index.html');
    if (!existsSync(idx)) {
      res.statusCode = 404;
      res.end();
      return true;
    }
    res.statusCode = 200;
    res.setHeader('Content-Type', 'text/html; charset=utf-8');
    createReadStream(idx).pipe(res);
    return true;
  }
  res.statusCode = 200;
  res.setHeader('Content-Type', MIME[extname(full).toLowerCase()] ?? 'application/octet-stream');
  createReadStream(full).pipe(res);
  return true;
}
