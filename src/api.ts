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
  paired?: boolean;
  lastSeenAt: string;
}

export interface PairingCode {
  code: string;
  expiresAt: string;
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
  todaySyncCount: number;
  syncTrend: number; // 较昨日百分比
  totalClipboardCount: number;
  status: 'running';
  uptime: string;
  avgLatencyMs: number;
  sparklines: {
    devices: number[];
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

export function getMaxHistory(): number {
  return Number(localStorage.getItem('clipsync_max_history') ?? '1000')
}

export function setMaxHistory(n: number) {
  localStorage.setItem('clipsync_max_history', String(n))
}

export function getThemeMode(): ThemeMode {
  return (localStorage.getItem('clipsync_theme') as ThemeMode) ?? 'light'
}

export function setThemeMode(m: ThemeMode) {
  localStorage.setItem('clipsync_theme', m)
}

export function getToken(): string | null {
  return localStorage.getItem('clipsync_token');
}

export function setToken(t: string | null) {
  if (t) localStorage.setItem('clipsync_token', t);
  else localStorage.removeItem('clipsync_token');
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(init?.headers as Record<string, string> | undefined),
  };
  const token = getToken();
  if (token) headers['Authorization'] = 'Bearer ' + token;
  const res = await fetch(path, { ...init, headers });
  if (res.status === 401) {
    setToken(null);
    window.dispatchEvent(new Event('clipsync:unauthorized'));
    throw new Error('未授权');
  }
  if (!res.ok) throw new Error('请求失败: ' + res.status + ' ' + res.statusText);
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
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
    if (!res.ok) throw new Error('登录失败(' + res.status + ')');
    const j = (await res.json()) as { token: string };
    setToken(j.token);
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

export function getHistory(offset = 0, limit = 20): Promise<HistoryPage> {
  return request<HistoryPage>('/api/clipboard/history?offset=' + offset + '&limit=' + limit);
}

export function pushText(text: string, deviceId: string, deviceName: string): Promise<ClipboardEntry> {
  return request<ClipboardEntry>('/api/clipboard', {
    method: 'PUT',
    body: JSON.stringify({ type: 'Text', text, deviceId, deviceName }),
  });
}

export function pushImage(file: File, deviceId: string, deviceName: string): Promise<ClipboardEntry> {
  const form = new FormData();
  form.append('file', file);
  form.append('deviceId', deviceId);
  form.append('deviceName', deviceName);
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

/** 生成一次性配对码(免认证;携带本端设备信息,服务端登记生成方设备;10 分钟有效,一码一设备) */
export function createPairingCode(): Promise<PairingCode> {
  return request<PairingCode>('/api/pairing-codes', {
    method: 'POST',
    body: JSON.stringify({ deviceId: deviceId(), deviceName: 'Web 管理页' }),
  });
}

/** 配对:用一次性配对码换取本设备专属 Token(免认证接口) */
export async function pairDevice(pairingCode: string, deviceName: string): Promise<{ deviceId: string; deviceToken: string }> {
  const res = await fetch('/api/pair', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pairingCode, deviceId: deviceId(), deviceName }),
  });
  let err = '';
  try { err = (await res.json()).error ?? '' } catch { /* ignore */ }
  if (!res.ok) throw new Error(err || '配对失败(' + res.status + ')');
  return res.json();
}

/** 作废未使用的配对码 */
export function revokePairingCode(code: string): Promise<void> {
  return request('/api/pairing-codes/' + encodeURIComponent(code), { method: 'DELETE' });
}

export function getActivities(limit = 20): Promise<ActivityLog[]> {
  return request<ActivityLog[]>('/api/activities?limit=' + limit);
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

/** 本端设备标识(localStorage 持久化) */
export function deviceId(): string {
  let id = localStorage.getItem('clipsync_device_id');
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem('clipsync_device_id', id);
  }
  return id;
}
