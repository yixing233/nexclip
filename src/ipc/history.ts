// IPC 封装:与 src-tauri/src/commands/mod.rs 的 Dto 字段一一对应(camelCase)。
import { invoke, convertFileSrc } from "@tauri-apps/api/core";

export interface ClipboardFileInfo {
  path: string;
  name: string;
  isDirectory: boolean;
  sizeBytes: number;
}

export interface HistoryItem {
  id: number;
  serverId: number | null;
  entryType: "Text" | "Image" | "File";
  text: string | null;
  html: string | null;
  imagePath: string | null;
  imageRef: string | null;
  files: ClipboardFileInfo[];
  deviceId: string;
  deviceName: string | null;
  sourceAppName: string | null;
  sourceAppPath: string | null;
  sourceAppIcon: string | null;
  createdAt: number;
  origin: number;
  starred: boolean;
  remark: string | null;
  isManual: boolean;
}

export async function historyQuery(params: {
  search?: string | null;
  entryType?: string | null;
  starredOnly?: boolean;
  urlOnly?: boolean;
  limit?: number;
  offset?: number;
}): Promise<HistoryItem[]> {
  return invoke<HistoryItem[]>("history_query", {
    search: params.search ?? null,
    entryType: params.entryType ?? null,
    starredOnly: params.starredOnly ?? false,
    urlOnly: params.urlOnly ?? false,
    limit: params.limit ?? 500,
    offset: params.offset ?? 0,
  });
}

export async function historyCount(): Promise<number> {
  return invoke<number>("history_count");
}

export async function historyDelete(id: number): Promise<void> {
  await invoke("history_delete", { id });
}

export async function historyToggleStar(id: number, starred: boolean): Promise<void> {
  await invoke("history_toggle_star", { id, starred });
}

/// 编辑文本条目内容(仅本地;拼音检索自动重算)
export async function historyUpdateText(id: number, text: string): Promise<void> {
  await invoke("history_update_text", { id, text });
}

/// 更新备注(null = 清除)
export async function historyUpdateRemark(
  id: number,
  remark: string | null,
): Promise<void> {
  await invoke("history_update_remark", { id, remark });
}

/// 复制条目到剪贴板(Rust 写入线程;支持文本/HTML/文件/图片)
export async function copyItem(id: number): Promise<void> {
  await invoke("copy_item", { id });
}

/// 粘贴到目标应用(写剪贴板 + 隐藏弹窗 + SendInput Ctrl+V)
export async function pasteItem(id: number): Promise<void> {
  await invoke("paste_item", { id });
}

/// 图钉开关(后端同步置顶状态,失焦隐藏依赖它)
export async function setPinned(pinned: boolean): Promise<void> {
  await invoke("set_pinned", { pinned });
}

/// 本地图片缓存文件的 WebView 资源 URL(asset protocol)
export function imageSrc(imagePath: string): string {
  return convertFileSrc(imagePath);
}
