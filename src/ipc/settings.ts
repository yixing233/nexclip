// IPC 封装:settings 相关命令(src-tauri/src/commands/mod.rs 的 camelCase Dto)
import { invoke } from "@tauri-apps/api/core";

export interface SettingsDto {
  serverUrl: string;
  isPaired: boolean;
  deviceId: string;
  deviceName: string;
  hotkey: string;
  hotkeySettings: string;
  hotkeyOpenUrl: string;
  hotkeyTopmost: string;
  hotkeyScreenshot: string;
  hotkeyScreenshotScreen: string;
  hotkeyScreenshotWindow: string;
  /** 剪贴板图片贴图 */
  hotkeyPin: string;
  screenshotEnabled: boolean;
  screenshotSaveDir: string;
  themeMode: "light" | "dark" | "system";
  iconLibrary: "lucide" | "tabler" | "phosphor" | "remix";
  monitorEnabled: boolean;
  appFilterEnabled: boolean;
  customFilteredProcesses: string[];
  autoPaste: boolean;
  notifyEnabled: boolean;
  copyDirectEnabled: boolean;
  smartColorEnabled: boolean;
  smartPathEnabled: boolean;
  smartDeepLinkEnabled: boolean;
  smartNetdiskEnabled: boolean;
  smartUrlEnabled: boolean;
  maxHistory: number;
  rememberScrollPosition: boolean;
  richTextEnabled: boolean;
  windowPositionMode: "cursor" | "center";
  retentionDays: number;
  pasteKey: "CtrlV" | "ShiftInsert";
  storageDir: string;
  bootStartEnabled: boolean;
  startMinimized: boolean;
  closeToTray: boolean;
  autoCheckUpdate: boolean;
  updateSource: "github" | "direct";
  resolvedStorageDir: string;
}

/// 设置补丁:只传需要修改的字段
export type SettingsPatch = Partial<Omit<SettingsDto, "isPaired" | "deviceId" | "resolvedStorageDir">>;

export async function settingsGet(): Promise<SettingsDto> {
  return invoke<SettingsDto>("settings_get");
}

export async function settingsSet(patch: SettingsPatch): Promise<void> {
  await invoke("settings_set", { patch });
}

export interface HistoryStats {
  count: number;
  starredCount: number;
  dbSizeBytes: number;
}

export async function historyStats(): Promise<HistoryStats> {
  return invoke<HistoryStats>("history_stats");
}

export async function historyClear(keepStarred = true): Promise<void> {
  await invoke("history_clear", { keepStarred });
}

export async function openInExplorer(path: string): Promise<void> {
  await invoke("open_in_explorer", { path });
}

export interface AppInfo {
  version: string;
  os: string;
}

export async function appInfo(): Promise<AppInfo> {
  return invoke<AppInfo>("app_info");
}

export interface DeviceInfo {
  id?: string;
  name?: string | null;
  platform?: string | null;
  ip?: string | null;
  version?: string | null;
  online?: boolean;
  lastSeenAt?: string | null;
  isCurrent?: boolean;
}

/// 设备列表(本地缓存;服务器实时列表待同步引擎接入)
export async function devicesCached(): Promise<DeviceInfo[]> {
  return invoke<DeviceInfo[]>("devices_cached");
}

export interface UpdateResult {
  success: boolean;
  hasUpdate: boolean;
  currentVersion: string;
  latestVersion: string;
  releaseTitle: string;
  releaseNotes: string;
  releaseUrl: string;
  downloadUrl: string;
  sha256: string | null;
  fileSize: number | null;
  errorMessage: string | null;
}

export async function checkUpdate(): Promise<UpdateResult> {
  return invoke<UpdateResult>("check_update");
}

export function updateCheckState(): Promise<UpdateResult | null> {
  return invoke<UpdateResult | null>("update_check_state");
}

/// 更新下载进度(事件 update-download-progress 载荷)
export interface DownloadProgress {
  downloaded: number;
  total: number | null;
  /** 0-100;total 未知时为 -1 */
  percent: number;
  done: boolean;
  error: string | null;
}

export async function downloadUpdate(url: string, sha256: string | null): Promise<string> {
  return invoke<string>("download_update", { url, sha256 });
}

export async function updateDownloadState(): Promise<DownloadProgress | null> {
  return invoke<DownloadProgress | null>("update_download_state");
}

export function cancelUpdateDownload(): Promise<void> {
  return invoke("cancel_update_download");
}

export async function runUpdateInstaller(path: string): Promise<void> {
  await invoke("run_update_installer", { path });
}

/// 设备"最后在线"文案,与旧版 LastSeenText 一致
export function lastSeenText(online: boolean | undefined, lastSeenAt: string | null | undefined): string {
  if (online) return "在线";
  if (!lastSeenAt) return "离线";
  const diff = Date.now() - new Date(lastSeenAt).getTime();
  if (diff < 60_000) return "刚刚离线";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前离线`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前离线`;
  return `${Math.floor(diff / 86_400_000)} 天前离线`;
}

export interface SyncState {
  status: string;
  isPaired: boolean;
  serverUrl: string;
  deviceName: string;
  deviceId: string;
}

export async function syncGetState(): Promise<SyncState> {
  return invoke<SyncState>("sync_get_state");
}

export interface PairingCode {
  code: string;
  expiresAt: string | null;
  qrPayload: string | null;
}

/// 生成一次性配对码(10 分钟有效)
export async function syncPairCreate(): Promise<PairingCode> {
  return invoke<PairingCode>("sync_pair_create");
}

/// 作废配对码(关闭展示时调用)
export async function syncPairRevoke(code: string): Promise<void> {
  await invoke("sync_pair_revoke", { code });
}

/// 6 位码确认配对(成功后自动重连推送)
export async function syncPairConfirm(code: string): Promise<void> {
  await invoke("sync_pair_confirm", { code });
}

export async function syncTestConnection(): Promise<[boolean, string]> {
  return invoke<[boolean, string]>("sync_test_connection");
}

/// 移除设备(危险操作)
export async function syncRemoveDevice(targetDeviceId: string): Promise<void> {
  await invoke("sync_remove_device", { targetDeviceId });
}

/// 重命名设备
export async function syncRenameDevice(targetDeviceId: string, newName: string): Promise<void> {
  await invoke("sync_rename_device", { targetDeviceId, newName });
}

/// 从服务端拉取设备真值并覆盖本地缓存
export async function syncDevicesRefresh(): Promise<void> {
  await invoke("sync_devices_refresh");
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(0)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${mb.toFixed(1)} MB`;
  return `${(mb / 1024).toFixed(2)} GB`;
}

/// 进程候选(可见窗口的用户进程,排除系统进程)
export interface ProcessEntry {
  name: string;
  stem: string;
  /// 友好显示名(exe 版本信息 FileDescription,退化到文件名)
  display: string;
  /// 应用图标 PNG 本地路径(提取失败为 null)
  icon: string | null;
}

export async function listUserProcesses(): Promise<ProcessEntry[]> {
  return invoke<ProcessEntry[]>("list_user_processes");
}

/// 系统文件夹选择对话框;取消返回 null
export async function pickFolder(): Promise<string | null> {
  return invoke<string | null>("pick_folder");
}
