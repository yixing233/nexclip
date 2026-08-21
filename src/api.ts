// API 类型与请求封装(与 SyncClipboard Server 契约一致)
// 请求走同源 /api,由服务端静态托管或 Vite 代理转发;Bearer 令牌鉴权

export type ClipType = 'Text' | 'Image';

export interface ClipboardEntry {
  id: number;
  type: ClipType;
  text?: string | null;
  imageRef?: string | null;
  deviceId: string;
  deviceName?: string | null;
  createdAt: string;
}

export interface DeviceInfo {
  id: string;
  name: string;
  platform: string;
  ip?: string | null;
  version?: string | null;
  online: boolean;
  userId?: string | null;
  bound?: boolean;
  paired?: boolean;
  lastSeenAt: string;
}

export interface PairingCode {
  code: string;
  expiresAt: string;
  userId?: string;
  deviceToken?: string;
  qrPayload?: string;
}

export type ActivityAction = 'push' | 'receive' | 'connect' | 'delete';

export interface ActivityLog {
  id: number;
  action: ActivityAction;
  deviceName: string;
  content?: string | null;
  createdAt: string;
}

export interface Stats {
  onlineDevices: number;
  totalDevices: number;
  onlineUsers: number;
  totalUsers: number;
  todaySyncCount: number;
  syncTrend: number; // 较昨日百分比
  totalClipboardCount: number;
  status: 'running';
  uptime: string;
  avgLatencyMs: number;
  version?: string;
  sparklines: {
    devices: number[];
    users: number[];
    sync: number[];
    history: number[];
    latency: number[];
  };
}

export interface HistoryPage {
  items: ClipboardEntry[];
  total: number;
}

export type ThemeMode = 'light' | 'dark' | 'system'

export function getThemeMode(): ThemeMode {
  return (localStorage.getItem('clipsync_theme') as ThemeMode) ?? 'light'
}

export function setThemeMode(m: ThemeMode) {
  localStorage.setItem('clipsync_theme', m)
}

export function getToken(): string | null {
  return localStorage.getItem('clipsync_token');
}

export function getRole(): 'user' | 'admin' | null {
  const r = localStorage.getItem('clipsync_role');
  return r === 'user' || r === 'admin' ? r : null;
}

export function getUserId(): string | null {
  return localStorage.getItem('clipsync_user_id');
}

export function setSession(token: string, role: 'user' | 'admin', userId?: string, username?: string) {
  localStorage.setItem('clipsync_token', token);
  localStorage.setItem('clipsync_role', role);
  if (userId) localStorage.setItem('clipsync_user_id', userId);
  else localStorage.removeItem('clipsync_user_id');
  if (username) localStorage.setItem('clipsync_username', username);
  else localStorage.removeItem('clipsync_username');
}

export function getUsername(): string | null {
  return localStorage.getItem('clipsync_username');
}

export function setToken(t: string | null) {
  if (t) localStorage.setItem('clipsync_token', t);
  else {
    localStorage.removeItem('clipsync_token');
    localStorage.removeItem('clipsync_role');
    localStorage.removeItem('clipsync_user_id');
    localStorage.removeItem('clipsync_username');
  }
}

async function readResponseSafely<T = unknown>(res: Response): Promise<{ data: T | null; text: string; invalidJson: boolean }> {
  // Response body is a one-shot stream. Always consume the original response exactly once.
  const text = await res.text();
  if (!text.trim()) return { data: null, text: '', invalidJson: false };
  try {
    return { data: JSON.parse(text) as T, text, invalidJson: false };
  } catch {
    return { data: null, text, invalidJson: true };
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = { ...(init?.headers as Record<string, string> | undefined) };
  // FormData must keep the browser-generated multipart boundary. Setting a
  // JSON content type here makes image uploads unparsable on the server.
  if (!(init?.body instanceof FormData) && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
  const token = getToken();
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const res = await fetch(path, { ...init, headers });
  const parsed = await readResponseSafely<Record<string, unknown>>(res);
  if (res.status === 401) {
    setToken(null);
    window.dispatchEvent(new Event('clipsync:unauthorized'));
  }
  if (!res.ok) {
    const errMsg = (parsed.data && typeof parsed.data.error === 'string')
      ? parsed.data.error
      : parsed.invalidJson
        ? `服务器返回了无效响应(${res.status})`
        : `请求失败: ${res.status} ${res.statusText}`;
    throw new Error(errMsg);
  }
  if (res.status === 204 || parsed.data === null) return undefined as T;
  return parsed.data as T;
}

// ---------- API 函数 ----------
/** 管理台账密登录:换取会话令牌 */
export async function login(username: string, password: string): Promise<boolean> {
  try {
    const res = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
    if (res.status === 401) return false;
    const parsed = await readResponseSafely<{ token: string; role?: 'admin' | 'user'; username?: string; error?: string }>(res);
    if (!res.ok || !parsed.data?.token) {
      throw new Error(parsed.data?.error || `登录失败(${res.status})`);
    }
    setSession(parsed.data.token, parsed.data.role ?? 'admin', undefined, parsed.data.username);
    return true;
  } catch (e) {
    console.error('login error', e);
    return false;
  }
}

/** 登出:注销会话令牌 */
export async function logout(): Promise<void> {
  const token = getToken();
  if (token) {
    try {
      await fetch('/api/logout', { method: 'POST', headers: { Authorization: 'Bearer ' + token } });
    } catch { /* 忽略 */ }
  }
  setToken(null);
}

/** 当前会话信息(用户名/角色/服务端版本) */
export function getMe(): Promise<{ role: 'admin' | 'user'; username: string | null; userId: string | null; deviceId: string | null; version: string }> {
  return request('/api/me');
}

/** 健康检查(免认证,含版本号) */
export function getHealth(): Promise<{ status: string; version: string; time: string }> {
  return request('/api/health');
}

export function getStats(): Promise<Stats> {
  return request<Stats>('/api/stats');
}

export async function getCurrentClipboard(): Promise<ClipboardEntry | null> {
  try {
    return await request<ClipboardEntry>('/api/clipboard');
  } catch {
    return null;
  }
}

export function getHistory(offset = 0, limit = 20, userId?: string | null, q?: string): Promise<HistoryPage> {
  const u = userId ? '&userId=' + encodeURIComponent(userId) : '';
  const s = q && q.trim() ? '&q=' + encodeURIComponent(q.trim()) : '';
  return request<HistoryPage>('/api/clipboard/history?offset=' + offset + '&limit=' + limit + u + s);
}

export function pushText(text: string, deviceId: string, deviceName: string): Promise<ClipboardEntry> {
  return request<ClipboardEntry>('/api/clipboard', {
    method: 'PUT',
    body: JSON.stringify({ type: 'Text', text, deviceId, deviceName }),
  });
}

export function pushImage(file: File | Blob, deviceId: string, deviceName: string): Promise<ClipboardEntry> {
  const form = new FormData();
  form.append('file', file, file instanceof File ? file.name : 'clipboard.png');
  form.append('deviceId', deviceId);
  form.append('deviceName', deviceName || getDefaultDeviceName());
  form.append('platform', getBrowserPlatform());
  return request<ClipboardEntry>('/api/clipboard/image', { method: 'POST', body: form });
}

export function getDevices(): Promise<DeviceInfo[]> {
  return request<DeviceInfo[]>('/api/devices');
}

export function renameDevice(id: string, name: string): Promise<void> {
  return request('/api/devices/' + encodeURIComponent(id), {
    method: 'PUT',
    body: JSON.stringify({ name }),
  });
}

export function removeDevice(id: string): Promise<void> {
  return request('/api/devices/' + encodeURIComponent(id), { method: 'DELETE' });
}

/** 生成 6 位纯数字配对码与扫码直连 URL */
export function createPairingCode(): Promise<PairingCode & { userId: string; qrPayload?: string }> {
  return request<PairingCode & { userId: string; qrPayload?: string }>('/api/pairing-codes', {
    method: 'POST',
    body: JSON.stringify({ deviceId: deviceId(), deviceName: getDefaultDeviceName(), platform: getBrowserPlatform() }),
  });
}

/** 单向即入配对(方案 1 扫码直连 + 方案 2 纯 6 位数字验证码): 凭码直接准入并登录 */
export async function pairDirect(
  code: string,
  deviceName?: string
): Promise<{ status: string; userId: string; token: string; deviceToken: string }> {
  const res = await fetch('/api/pair', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      code: code.trim(),
      deviceId: deviceId(),
      deviceName: deviceName || getDefaultDeviceName(),
      platform: getBrowserPlatform(),
    }),
  });
  const parsed = await readResponseSafely<{ status?: string; userId?: string; token?: string; deviceToken?: string; error?: string }>(res);
  if (!res.ok || !parsed.data?.token) {
    throw new Error(parsed.data?.error || `配对失败(${res.status})`);
  }
  return parsed.data as { status: string; userId: string; token: string; deviceToken: string };
}

/** 别名 */
export const pair = pairDirect;

/** 作废未使用的配对码 */
export function revokePairingCode(code: string): Promise<void> {
  return request('/api/pairing-codes/' + encodeURIComponent(code), { method: 'DELETE' });
}

/** 用户信息(本组或管理端) */
export function getUser(uid: string): Promise<{ id: string; name: string; createdAt: string }> {
  return request('/api/users/' + encodeURIComponent(uid));
}

/** 修改用户ID(本组或管理端;全局唯一) */
export function renameUser(uid: string, name: string): Promise<void> {
  return request('/api/users/' + encodeURIComponent(uid), {
    method: 'PUT',
    body: JSON.stringify({ name }),
  });
}

/** 用户列表(管理端) */
export function listUsers(): Promise<Array<{ id: string; name: string; createdAt: string; deviceCount: number }>> {
  return request('/api/users');
}

/** 删除用户(管理端) */
export function deleteUser(uid: string): Promise<void> {
  return request('/api/users/' + encodeURIComponent(uid), { method: 'DELETE' });
}

/** 审计日志(管理端) */
export function getAudit(limit = 50): Promise<Array<{ id: number; action: string; detail: string | null; ip: string | null; createdAt: string }>> {
  return request('/api/admin/audit?limit=' + limit);
}

/** 管理台运行设置:历史上限 + 存储位置(服务端持久化,修改立即生效) */
export interface AdminSettings {
  maxHistoryCount: number
  imageStoragePath: string
  databasePath: string
}

export function getAdminSettings(): Promise<AdminSettings> {
  return request<AdminSettings>('/api/admin/settings');
}

export function putAdminSettings(partial: { maxHistoryCount?: number; imageStoragePath?: string }): Promise<AdminSettings & { storageApplied?: { path: string; moved: number } }> {
  return request('/api/admin/settings', { method: 'PUT', body: JSON.stringify(partial) });
}

/** 管理端:浏览服务端目录(仅子目录,用于选择存储位置) */
export function browseStorageDir(path?: string): Promise<{ path: string; parent: string | null; exists: boolean; dirs: string[] }> {
  const q = path ? '?path=' + encodeURIComponent(path) : '';
  return request('/api/admin/storage/browse' + q);
}

export function getActivities(limit = 20, userId?: string | null): Promise<ActivityLog[]> {
  const u = userId ? '&userId=' + encodeURIComponent(userId) : '';
  return request<ActivityLog[]>('/api/activities?limit=' + limit + u);
}

export function deleteEntry(id: number): Promise<void> {
  return request('/api/clipboard/' + id, { method: 'DELETE' });
}

export function clearHistory(): Promise<void> {
  return request('/api/clipboard/history', { method: 'DELETE' });
}

export function sendToDevices(text: string, deviceIds: string[]): Promise<ClipboardEntry> {
  return request<ClipboardEntry>('/api/clipboard/send', {
    method: 'POST',
    body: JSON.stringify({ text, deviceIds, deviceId: deviceId(), deviceName: 'Web 管理页' }),
  });
}

export function imageUrl(ref: string | null | undefined): string {
  if (!ref) return '';
  return '/api/images/' + ref;
}

/** 智能检测当前浏览器所在平台 (精细区分电脑端与手机端) */
export function getBrowserPlatform(): string {
  if (typeof navigator === 'undefined') return 'Web (Browser)';
  const ua = navigator.userAgent.toLowerCase();
  if (ua.includes('android')) return 'Web (Android)';
  if (ua.includes('iphone') || ua.includes('ipod')) return 'Web (iOS)';
  if (ua.includes('ipad')) return 'Web (iPadOS)';
  if (ua.includes('windows nt') || ua.includes('windows')) return 'Web (Windows)';
  if (ua.includes('macintosh') || ua.includes('mac os')) return 'Web (macOS)';
  if (ua.includes('linux')) return 'Web (Linux)';
  return 'Web (Browser)';
}

/** 智能生成默认设备名称 */
export function getDefaultDeviceName(): string {
  if (typeof navigator === 'undefined') return 'Web 控制台';
  const ua = navigator.userAgent.toLowerCase();
  let browser = 'Web 浏览器';
  if (ua.includes('edg/')) browser = 'Edge';
  else if (ua.includes('chrome/')) browser = 'Chrome';
  else if (ua.includes('safari/') && !ua.includes('chrome')) browser = 'Safari';
  else if (ua.includes('firefox/')) browser = 'Firefox';

  if (ua.includes('android')) return `${browser} (Android 手机)`;
  if (ua.includes('iphone')) return `${browser} (iPhone)`;
  if (ua.includes('ipad')) return `${browser} (iPad)`;
  if (ua.includes('windows')) return `${browser} (Windows)`;
  if (ua.includes('macintosh') || ua.includes('mac os')) return `${browser} (Mac)`;
  if (ua.includes('linux')) return `${browser} (Linux)`;
  return `${browser} 端`;
}

/** 本端设备标识(localStorage 持久化) */
export function deviceId(): string {
  let id = localStorage.getItem('clipsync_device_id');
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem('clipsync_device_id', id);
  }
  return id;
}
