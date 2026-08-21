import { createHash, randomBytes, randomInt as cryptoRandomInt } from 'node:crypto';

/** 与 .NET Convert.ToHexString(SHA256) 一致:大写十六进制 */
export function sha256Hex(s: string): string {
  return createHash('sha256').update(s, 'utf8').digest('hex').toUpperCase();
}

export function sha256Bytes(b: Buffer): string {
  return createHash('sha256').update(b).digest('hex').toUpperCase();
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

/** 短随机 ID/配对码字符集:大写字母+数字,去掉易混淆的 0/O/1/I */
export const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

/** 生成 n 位短随机码(用户ID) */
export function randomCode(n: number): string {
  let s = '';
  for (let i = 0; i < n; i++) s += CODE_ALPHABET[randomInt(CODE_ALPHABET.length)];
  return s;
}

/** 生成 n 位纯数字验证码 (如 6 位数字配对码 839201) */
export function randomNumericCode(n: number = 6): string {
  let s = '';
  for (let i = 0; i < n; i++) s += String(randomInt(10));
  return s;
}

/** 智能推断设备平台 (显式声明 > User-Agent 智能分析 > 设备名称特征)
 *  精细区分: Web (Windows) / Web (macOS) / Web (Linux) / Web (Android) / Web (iOS) / Android / Windows 等
 */
export function detectPlatform(explicitPlatform?: string | null, ua?: string | null, devName?: string | null): string {
  if (explicitPlatform && explicitPlatform.trim() && explicitPlatform.trim().toLowerCase() !== 'unknown') {
    return explicitPlatform.trim();
  }
  const name = (devName || '').toLowerCase();
  const agent = (ua || '').toLowerCase();

  // 1. 移动端识别
  if (agent.includes('android') || name.includes('android')) {
    if (agent.includes('mozilla') || agent.includes('chrome') || agent.includes('safari') || name.includes('web') || name.includes('浏览器')) {
      return 'Web (Android)';
    }
    return 'Android';
  }
  if (agent.includes('iphone') || agent.includes('ipod') || name.includes('iphone') || name.includes('ios')) {
    return 'Web (iOS)';
  }
  if (agent.includes('ipad') || name.includes('ipad')) {
    return 'Web (iPadOS)';
  }

  // 2. 桌面端识别
  if (agent.includes('windows') || name.includes('windows')) {
    if (agent.includes('mozilla') || agent.includes('chrome') || agent.includes('safari') || agent.includes('edg') || name.includes('web') || name.includes('浏览器') || name.includes('控制台')) {
      return 'Web (Windows)';
    }
    return 'Windows';
  }
  if (agent.includes('macintosh') || agent.includes('mac os') || name.includes('mac')) {
    if (agent.includes('mozilla') || agent.includes('chrome') || agent.includes('safari') || name.includes('web') || name.includes('浏览器')) {
      return 'Web (macOS)';
    }
    return 'macOS';
  }
  if (agent.includes('linux') || name.includes('linux')) {
    if (agent.includes('mozilla') || agent.includes('chrome') || agent.includes('safari') || name.includes('web') || name.includes('浏览器')) {
      return 'Web (Linux)';
    }
    return 'Linux';
  }

  // 3. 通用 Web 特征
  if (name.includes('web') || name.includes('网页') || name.includes('浏览器') || name.includes('控制台') || agent.includes('mozilla') || agent.includes('chrome') || agent.includes('safari')) {
    return 'Web';
  }

  return 'Unknown';
}

/** 净化为合法的纯 IPv4 字符串 */
export function cleanIpv4(ip: string | null | undefined): string | null {
  if (!ip || typeof ip !== 'string') return null;
  const s = ip.trim();
  if (s === '::1' || s === 'localhost') return '127.0.0.1';
  // IPv4-mapped IPv6: ::ffff:192.168.1.1
  const mapped = /^::ffff:(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})$/i.exec(s);
  if (mapped) return mapped[1];
  // 标准 IPv4 (支持附带端口号形式: 192.168.1.1:8080)
  const standard = /^(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(?::\d+)?$/.exec(s);
  if (standard) return standard[1];
  return null;
}

/** 规范化提取客户端真实 IPv4 地址 (优先 X-Real-IP > X-Forwarded-For > Socket) */
export function extractClientIpv4(req: { headers: Record<string, string | string[] | undefined>; socket?: { remoteAddress?: string } }): string | null {
  // 1. 优先提取反向代理 X-Real-IP
  const realIp = req.headers['x-real-ip'];
  if (typeof realIp === 'string' && realIp.trim()) {
    const ipv4 = cleanIpv4(realIp);
    if (ipv4) return ipv4;
  }

  // 2. 遍历 X-Forwarded-For 代理链,优先寻找第一个有效 IPv4
  const fwd = req.headers['x-forwarded-for'];
  if (typeof fwd === 'string' && fwd.trim()) {
    const parts = fwd.split(',').map((p) => p.trim());
    for (const p of parts) {
      const ipv4 = cleanIpv4(p);
      if (ipv4) return ipv4;
    }
  }

  // 3. 底层 Socket 地址
  const sock = req.socket?.remoteAddress;
  if (sock) {
    const ipv4 = cleanIpv4(sock);
    if (ipv4) return ipv4;
  }

  // 4. 兜底本机
  return '127.0.0.1';
}
