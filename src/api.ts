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

export function getRole(): 'user' | 'admin' | null {
  const r = localStorage.getItem('clipsync_role');
  return r === 'user' || r === 'admin' ? r : null;
}

export function getUserId(): string | null {
  return localStorage.getItem('clipsync_user_id');
}

export function setSession(token: string, role: 'user' | 'admin', userId?: string) {
  localStorage.setItem('clipsync_token', token);
  localStorage.setItem('clipsync_role', role);
  if (userId) localStorage.setItem('clipsync_user_id', userId);
  else localStorage.removeItem('clipsync_user_id');
}

export function setToken(t: string | null) {
  if (t) localStorage.setItem('clipsync_token', t);
  else {
    localStorage.removeItem('clipsync_token');
    localStorage.removeItem('clipsync_role');
    localStorage.removeItem('clipsync_user_id');
  }
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
    const j = (await res.json()) as { token: string; role?: 'admin' | 'user' };
    setSession(j.token, j.role ?? 'admin');
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

export function getHistory(offset = 0, limit = 20, userId?: string | null): Promise<HistoryPage> {
  const u = userId ? '&userId=' + encodeURIComponent(userId) : '';
  return request<HistoryPage>('/api/clipboard/history?offset=' + offset + '&limit=' + limit + u);
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

/** 生成一次性配对码(免认证;携带本端设备信息;未绑定设备自动创建用户ID,返回 userId) */
export function createPairingCode(): Promise<PairingCode & { userId: string }> {
  return request<PairingCode & { userId: string }>('/api/pairing-codes', {
    method: 'POST',
    body: JSON.stringify({ deviceId: deviceId(), deviceName: 'Web 管理页' }),
  });
}

/** 发起配对(免认证):配对码 + 用户ID → 挂起待确认;返回 { status: 'pending' } */
export async function pairDevice(pairingCode: string, userId: string, deviceName: string): Promise<{ status: string }> {
  const res = await fetch('/api/pair', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pairingCode, userId, deviceId: deviceId(), deviceName }),
  });
  let err = '';
  try { err = (await res.json()).error ?? '' } catch { /* ignore */ }
  if (!res.ok) throw new Error(err || '配对失败(' + res.status + ')');
  return res.json();
}

/** 轮询配对结果(免认证):pending / approved / rejected / expired */
export async function pairStatus(pairingCode: string): Promise<{ status: string; userId?: string }> {
  const res = await fetch('/api/pair/status?code=' + encodeURIComponent(pairingCode) + '&deviceId=' + encodeURIComponent(deviceId()));
  if (!res.ok) throw new Error('状态查询失败(' + res.status + ')');
  return res.json();
}

/** 配对确认后换取用户网页会话 */
export async function createPairSession(pairingCode: string): Promise<{ token: string; role: 'user'; userId: string }> {
  const res = await fetch('/api/session/pair', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code: pairingCode, deviceId: deviceId() }),
  });
  let err = '';
  try { err = (await res.json()).error ?? '' } catch { /* ignore */ }
  if (!res.ok) throw new Error(err || '会话建立失败(' + res.status + ')');
  return res.json();
}

/** 作废未使用的配对码 */
export function revokePairingCode(code: string): Promise<void> {
  return request('/api/pairing-codes/' + encodeURIComponent(code), { method: 'DELETE' });
}

/** 待确认配对请求列表(会话) */
export function listPairingRequests(): Promise<Array<{ code: string; userId: string; deviceId: string | null; deviceName: string | null; status: string; createdAt: string }>> {
  return request('/api/pairing-requests');
}

/** 确认/拒绝配对请求(生成方会话) */
export function confirmPairingRequest(code: string, action: 'approve' | 'reject'): Promise<void> {
  return request('/api/pairing-requests/confirm', {
    method: 'POST',
    body: JSON.stringify({ code, action }),
  });
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

/** 本端设备标识(localStorage 持久化) */
export function deviceId(): string {
  let id = localStorage.getItem('clipsync_device_id');
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem('clipsync_device_id', id);
  }
  return id;
}
