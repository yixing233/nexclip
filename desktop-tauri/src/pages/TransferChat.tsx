import { useCallback, useEffect, useRef, useState } from "react";
import { Input, Button, Tag, Empty, Tooltip, message } from "antd";
import { Send, Loader2, File as FileIcon, Download, FileUp } from "lucide-react";
import {
  chatQuery, syncSendText, syncSendFile, type ChatMessage,
} from "../ipc/chat";
import { devicesCached, formatBytes, type DeviceInfo } from "../ipc/settings";

/// 文件消息(FileTransferred 推送;文件不进剪贴板历史,前端内存展示与旧版一致)
interface FileMsg {
  fileId: string;
  fileName: string;
  size: number;
  deviceName: string | null;
  downloadUrl: string;
}

export default function TransferChat() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [fileMsgs, setFileMsgs] = useState<FileMsg[]>([]);
  const [devices, setDevices] = useState<DeviceInfo[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const [downloading, setDownloading] = useState<Set<string>>(new Set());
  const [savedPath, setSavedPath] = useState<Record<string, string>>({});
  const listRef = useRef<HTMLDivElement>(null);
  const bottomRef = useRef<HTMLDivElement>(null);

  const refresh = useCallback(async () => {
    setMessages(await chatQuery(200));
  }, []);

  useEffect(() => {
    void refresh();
    void devicesCached().then((d) => {
      setDevices(d.filter((x) => !x.isCurrent && x.online && !!x.id));
    });
    // 实时事件:新文本消息 + 文件互传推送/删除
    let unlisten: (() => void) | undefined;
    void (async () => {
      try {
        const { listen } = await import("@tauri-apps/api/event");
        const un1 = await listen("clipboard-changed", () => {
          void refresh();
        });
        const un2 = await listen<FileMsg>("file-transferred", (e) => {
          setFileMsgs((prev) => [...prev, e.payload]);
        });
        const un3 = await listen<string>("file-deleted", (fileId) => {
          setFileMsgs((prev) => prev.filter((f) => f.fileId !== fileId.payload));
        });
        unlisten = () => {
          un1();
          un2();
          un3();
        };
      } catch {
        /* 浏览器环境 */
      }
    })();
    return () => unlisten?.();
  }, [refresh]);

  // 新消息滚动到底
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ block: "end" });
  }, [messages.length, fileMsgs.length]);

  const toggleDevice = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const send = async () => {
    const text = draft.trim();
    if (!text || sending) return;
    setSending(true);
    try {
      const targets = selected.size > 0 ? Array.from(selected) : null;
      await syncSendText(text, targets);
      setDraft("");
      await refresh();
    } catch (e) {
      console.error("发送失败", e);
    } finally {
      setSending(false);
    }
  };

  const sendFiles = async (paths: string[]) => {
    if (paths.length === 0) return;
    setSending(true);
    try {
      const targets = selected.size > 0 ? Array.from(selected) : null;
      for (const p of paths) {
        await syncSendFile(p, targets);
      }
      void message.success(`已发送 ${paths.length} 个文件`);
    } catch (e) {
      void message.error(`文件发送失败: ${e}`);
    } finally {
      setSending(false);
    }
  };

  // Tauri 拖拽事件:drop 携带真实文件路径
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void (async () => {
      try {
        const { getCurrentWebview } = await import("@tauri-apps/api/webview");
        const un = await getCurrentWebview().onDragDropEvent((event) => {
          if (event.payload.type === "over") {
            setDragOver(true);
          } else if (event.payload.type === "leave") {
            setDragOver(false);
          } else if (event.payload.type === "drop") {
            setDragOver(false);
            void sendFiles(event.payload.paths);
          }
        });
        unlisten = un;
      } catch (e) {
        console.error("拖拽监听失败", e);
      }
    })();
    return () => unlisten?.();
  });

  const downloadFile = async (f: FileMsg) => {
    setDownloading((prev) => new Set(prev).add(f.fileId));
    try {
      const { invoke } = await import("@tauri-apps/api/core");
      const path = await invoke<string>("download_transfer_file", {
        fileId: f.fileId,
        fileName: f.fileName,
      });
      setSavedPath((prev) => ({ ...prev, [f.fileId]: path }));
      void message.success(`已保存到 ${path}`);
    } catch (e) {
      void message.error(`下载失败: ${e}`);
    } finally {
      setDownloading((prev) => {
        const next = new Set(prev);
        next.delete(f.fileId);
        return next;
      });
    }
  };

  return (
    <div
      style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0, position: "relative" }}
      onDragOver={(e) => e.preventDefault()}
    >
      {/* 拖拽遮罩 */}
      {dragOver && (
        <div
          style={{
            position: "absolute",
            inset: 0,
            zIndex: 20,
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            background: "rgba(37,99,235,0.08)",
            border: "2px dashed var(--ant-color-primary, #2563EB)",
            borderRadius: 10,
            fontSize: 14,
            color: "var(--ant-color-primary, #2563EB)",
            pointerEvents: "none",
          }}
        >
          <FileUp size={18} style={{ marginRight: 8 }} />
          松开发送文件给{selected.size > 0 ? `选中的 ${selected.size} 台设备` : "所有设备"}
        </div>
      )}

      {/* 目标设备胶囊(多选;不选 = 广播全部在线设备) */}
      <div
        className="device-chip-bar"
        style={{
          display: "flex",
          flexWrap: "nowrap",
          gap: 6,
          padding: "0 2px 8px",
          overflowX: "auto",
          overflowY: "hidden",
        }}
      >
        {devices.length === 0 ? (
          <span style={{ fontSize: 11, color: "var(--ant-color-text-tertiary, #999)" }}>
            暂无在线设备;发送将广播给所有设备
          </span>
        ) : (
          devices.map((d) => {
            const active = selected.has(d.id!);
            return (
              <span
                key={d.id!}
                className={`device-chip${active ? " active" : ""}`}
                onClick={() => toggleDevice(d.id!)}
              >
                <span
                  className="device-dot"
                  style={{
                    background: d.online ? "#4ade80" : "rgba(128,128,128,0.45)",
                  }}
                />
                {d.name ?? (d.id ?? "").slice(0, 8)}
              </span>
            );
          })
        )}
      </div>

      {/* 消息列表(文本气泡 + 文件卡片,按到达顺序) */}
      <div ref={listRef} className="scroll-area" style={{ flex: 1, overflow: "auto", padding: "0 4px", minHeight: 0 }}>
        {messages.length === 0 && fileMsgs.length === 0 ? (
          <Empty
            style={{ paddingTop: 60 }}
            description="还没有互传消息"
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        ) : (
          <>
            {messages.map((m) => {
              const mine = m.origin === 2;
              return (
                <div
                  key={`t-${m.id}`}
                  style={{
                    display: "flex",
                    justifyContent: mine ? "flex-end" : "flex-start",
                    marginBottom: 8,
                  }}
                >
                  <Tooltip
                    title={`${m.deviceName ?? "未知设备"} · ${new Date(m.createdAt).toLocaleTimeString("zh-CN")}`}
                    mouseEnterDelay={0.4}
                  >
                    <div
                      style={{
                        maxWidth: "78%",
                        padding: "7px 11px",
                        borderRadius: 10,
                        fontSize: 13,
                        lineHeight: 1.5,
                        whiteSpace: "pre-wrap",
                        wordBreak: "break-word",
                        background: mine
                          ? "var(--ant-color-primary-bg, rgba(37,99,235,0.12))"
                          : "rgba(128,128,128,0.12)",
                        color: "var(--ant-color-text, #333)",
                      }}
                    >
                      {m.text}
                    </div>
                  </Tooltip>
                </div>
              );
            })}
            {fileMsgs.map((f) => {
              // 文件卡片:上传方的文件永远显示在右侧(自己发的);此处简化统一左侧
              void f.deviceName;
              return (
                <div key={`f-${f.fileId}`} style={{ display: "flex", justifyContent: "flex-start", marginBottom: 8 }}>
                  <div
                    style={{
                      maxWidth: "78%",
                      padding: "8px 12px",
                      borderRadius: 10,
                      background: "rgba(128,128,128,0.12)",
                      display: "flex",
                      alignItems: "center",
                      gap: 10,
                    }}
                  >
                    <FileIcon size={26} style={{ flexShrink: 0, color: "var(--ant-color-primary, #2563EB)" }} />
                    <div style={{ minWidth: 0 }}>
                      <div style={{ fontSize: 13, fontWeight: 600, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                        {f.fileName}
                      </div>
                      <div style={{ fontSize: 11, color: "var(--ant-color-text-tertiary, #888)" }}>
                        {formatBytes(f.size)} · {f.deviceName ?? "未知设备"}
                      </div>
                      {savedPath[f.fileId] && (
                        <div style={{ fontSize: 10.5, color: "#22c55e", marginTop: 2, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                          已保存: {savedPath[f.fileId]}
                        </div>
                      )}
                    </div>
                    <Button
                      size="small"
                      icon={
                        downloading.has(f.fileId) ? (
                          <Loader2 size={13} className="spin" />
                        ) : (
                          <Download size={13} />
                        )
                      }
                      disabled={downloading.has(f.fileId)}
                      onClick={() => void downloadFile(f)}
                      style={{ flexShrink: 0 }}
                    >
                      保存
                    </Button>
                  </div>
                </div>
              );
            })}
          </>
        )}
        <div ref={bottomRef} />
      </div>

      {/* 输入区 */}
      <div style={{ display: "flex", gap: 8, padding: "8px 2px 0", flexShrink: 0 }}>
        <Input
          placeholder={
            selected.size > 0
              ? `发送给 ${selected.size} 台设备…`
              : "发送给所有设备(广播)…"
          }
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onPressEnter={() => void send()}
          disabled={sending}
        />
        <Tooltip title="发送" mouseEnterDelay={0.3}>
          <Button
            type="primary"
            icon={sending ? <Loader2 size={14} className="spin" /> : <Send size={14} />}
            onClick={() => void send()}
            disabled={!draft.trim() || sending}
            style={{ flexShrink: 0, width: 32, height: 32, padding: 0 }}
          />
        </Tooltip>
      </div>
    </div>
  );
}
