// IPC:智能直达 + 互传聊天
import { invoke } from "@tauri-apps/api/core";

export type SmartKind =
  | "url"
  | "github"
  | "netdisk"
  | "color"
  | "local_folder"
  | "local_file";

export interface SmartActionSpec {
  kind: "open_url" | "open_path" | "locate_path" | "open_terminal" | "open_file" | "copy_text";
  arg: string;
}

export interface SmartAction {
  kind: SmartKind;
  title: string;
  subtitle: string;
  primary: SmartActionSpec;
  secondary: SmartActionSpec | null;
  previewColor: string | null;
  hexColor: string | null;
  rgbColor: string | null;
  hslColor: string | null;
  extractionCode: string | null;
  targetPath: string | null;
}

export async function smartExec(action: SmartActionSpec): Promise<void> {
  await invoke("smart_exec", { action });
}

/// 检测文本的智能动作(forToast=false 返回完整元数据;路径存在性由后端判定)
export async function smartDetect(
  text: string,
  forToast: boolean,
): Promise<SmartAction | null> {
  return invoke<SmartAction | null>("smart_detect", { text, forToast });
}

export async function hideToast(): Promise<void> {
  await invoke("hide_toast");
}

export interface ChatMessage {
  id: number;
  text: string;
  // 本端发送(origin=2)或远端手动推送(origin=1)
  origin: number;
  isManual: boolean;
  deviceName: string | null;
  createdAt: number;
}

export async function chatQuery(limit = 200): Promise<ChatMessage[]> {
  return invoke<ChatMessage[]>("chat_query", { limit });
}

export async function syncSendText(
  text: string,
  targetDeviceIds: string[] | null,
): Promise<unknown> {
  return invoke("sync_send_text", { text, targetDeviceIds });
}

export async function syncSendFile(
  filePath: string,
  targetDeviceIds: string[] | null,
): Promise<unknown> {
  return invoke("sync_send_file", { filePath, targetDeviceIds });
}

/// 下载互传文件到用户下载目录,返回保存路径
export async function downloadTransferFile(
  fileId: string,
  fileName: string,
): Promise<string> {
  return invoke<string>("download_transfer_file", { fileId, fileName });
}
