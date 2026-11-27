import type { ChatMessage, TransferFile } from "../ipc/chat";

type TimelineRow =
  | { kind: "message"; key: string; timestamp: number; value: ChatMessage }
  | { kind: "file"; key: string; timestamp: number; value: TransferFile };

export function chatTimeline(messages: ChatMessage[], files: TransferFile[]): TimelineRow[] {
  const rows: TimelineRow[] = messages.map((value) => ({
    kind: "message", key: `t-${value.id}`, timestamp: value.createdAt, value,
  }));
  for (const value of files) {
    const createdAt = value.createdAt ? Date.parse(value.createdAt) : NaN;
    rows.push({
      kind: "file", key: `f-${value.fileId}`,
      timestamp: Number.isFinite(createdAt) ? createdAt : value.receivedAt || 0, value,
    });
  }
  return rows.sort((a, b) => a.timestamp - b.timestamp || a.key.localeCompare(b.key));
}
