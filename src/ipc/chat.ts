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
  entryType: "Text" | "Image";
  imagePath: string | null;
  // 本端发送(origin=2)或远端手动推送(origin=1)
  origin: number;
  isManual: boolean;
  deviceName: string | null;
  createdAt: number;
  /// 是否本机发出(后端按 origin + deviceId 判定,前端不再自行推断)
  isFromSelf: boolean;
}

export async function chatQuery(limit = 200): Promise<ChatMessage[]> {
  return invoke<ChatMessage[]>("chat_query", { limit });
}

/// 清空本机保存的互传记录(互传/手动推送条目),返回删除条数
export async function chatClear(): Promise<number> {
  return invoke<number>("chat_clear");
}

/// 图片消息另存为(系统对话框),返回保存路径;取消返回 null
export async function saveChatImage(id: number): Promise<string | null> {
  return invoke<string | null>("save_image_as", { id });
}

/// 在资源管理器中打开/定位路径
export async function openInExplorer(path: string): Promise<void> {
  await invoke("open_in_explorer", { path });
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

export interface TransferFile {
  fileId: string;
  fileName: string;
  size: number;
  deviceId: string;
  deviceName: string | null;
  createdAt: string | null;
  downloadUrl: string;
  receivedAt: number;
  isFromSelf: boolean;
}

export function transferFilesQuery(): Promise<TransferFile[]> {
  return invoke<TransferFile[]>("transfer_files_query");
}

/// 下载互传文件到用户下载目录,返回保存路径
export async function downloadTransferFile(
  fileId: string,
  fileName: string,
): Promise<string> {
  return invoke<string>("download_transfer_file", { fileId, fileName });
}

/// 选择图片文件(系统对话框,支持多选);用户取消返回空数组
export async function pickImages(): Promise<string[]> {
  return invoke<string[]>("pick_images");
}

/// 选择任意文件(二进制互传);用户取消返回空数组
export async function pickFiles(): Promise<string[]> {
  return invoke<string[]>("pick_files");
}
