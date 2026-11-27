// IPC 封装:截图相关命令与事件(src-tauri/src/commands/mod.rs 的截图段)
import { invoke } from "@tauri-apps/api/core";

/// 截图选区(显示器本地逻辑坐标,CSS 像素)
export interface ScreenshotRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/// 检测到的窗口矩形(虚拟桌面物理坐标)
export interface DetectedWindow {
  x: number;
  y: number;
  width: number;
  height: number;
  title: string;
  app: string;
}

/// screenshot:start 事件载荷
export interface CaptureStartPayload {
  revision: number;
  monitorId: number;
  /// 冻结帧 URL(自定义协议 nexclip-frame 内存直出)
  frameUrl: string;
  frameWidth: number;
  frameHeight: number;
  monitorX: number;
  monitorY: number;
  monitorWidth: number;
  monitorHeight: number;
  scaleFactor: number;
  windows: DetectedWindow[];
}

/// 快速截图闪烁反馈载荷
export interface FlashPayload {
  monitorId: number;
  /// 显示器本地物理坐标,None=整屏
  rect: [number, number, number, number] | null;
  durationMs: number;
  scaleFactor: number;
}

/// 长截图进度载荷
export interface ScrollProgress {
  rounds: number;
  height: number;
  done: boolean;
  aborted: boolean;
}

export async function screenshotOverlayReady(revision: number, monitorId: number): Promise<void> {
  await invoke("screenshot_overlay_ready", { revision, monitorId });
}

/// 拉取未消费的会话载荷(覆盖首次创建窗口错过 screenshot:start 事件的情况)
export async function screenshotPendingStart(
  monitorId: number,
): Promise<CaptureStartPayload | null> {
  return invoke<CaptureStartPayload | null>("screenshot_pending_start", { monitorId });
}

export async function screenshotCancel(): Promise<void> {
  await invoke("screenshot_cancel");
}

export async function screenshotCopy(
  monitorId: number,
  rect: ScreenshotRect,
  annotationPng: string | null,
  cornerRadius?: number,
): Promise<void> {
  await invoke("screenshot_copy", {
    monitorId,
    rect,
    annotationPng,
    cornerRadius: cornerRadius ?? 0,
  });
}

export async function screenshotSave(
  monitorId: number,
  rect: ScreenshotRect,
  annotationPng: string | null,
  cornerRadius?: number,
): Promise<string | null> {
  return invoke<string | null>("screenshot_save", {
    monitorId,
    rect,
    annotationPng,
    cornerRadius: cornerRadius ?? 0,
  });
}

export async function screenshotPin(
  monitorId: number,
  rect: ScreenshotRect,
  annotationPng: string | null,
  cornerRadius?: number,
): Promise<void> {
  await invoke("screenshot_pin", {
    monitorId,
    rect,
    annotationPng,
    cornerRadius: cornerRadius ?? 0,
  });
}

/// 长截图(选区固定滚动拼接,结果入剪贴板)
export async function scrollCaptureStart(
  monitorId: number,
  rect: ScreenshotRect,
): Promise<void> {
  await invoke("scroll_capture_start", { monitorId, rect, annotationPng: null });
}

/// 活屏快捷截图(无选区)
export async function screenshotQuickScreen(): Promise<void> {
  await invoke("screenshot_quick_screen");
}

/// 活动窗口快捷截图(无选区)
export async function screenshotQuickWindow(): Promise<void> {
  await invoke("screenshot_quick_window");
}

/// 从当前窗口 label 解析 monitorId("overlay-{id}")
export function monitorIdFromLabel(label: string): number | null {
  const m = /^overlay-(\d+)$/.exec(label);
  return m ? Number(m[1]) : null;
}

/// 从当前窗口 label 解析 pinId("pin-{id}")
export function pinIdFromLabel(label: string): string | null {
  const m = /^pin-(.+)$/.exec(label);
  return m ? m[1] : null;
}

/// 贴图图片 URL(自定义协议直出)
export function pinImageUrl(pinId: string): string {
  return `http://nexclip-frame.localhost/p/${pinId}`;
}
