import { useCallback, useEffect, useRef, useState } from "react";
import { Input, Button, Empty, Tooltip, message, Popconfirm, Modal, Dropdown } from "antd";
import type { MenuProps } from "antd";
import { Icon } from "../icons";
import {
  chatQuery, chatClear, syncSendText, syncSendFile, pickImages, pickFiles, saveChatImage,
  openInExplorer, transferFilesQuery, type TransferFile, type ChatMessage,
} from "../ipc/chat";
import { copyItem, pasteItem, historyDelete } from "../ipc/history";
import { devicesCached, formatBytes, type DeviceInfo } from "../ipc/settings";
import { imageSrc } from "../ipc/history";
import { subscribeAsync, subscribeEvent } from "../ipc/events";
import { chatTimeline } from "../utils/chatTimeline";

// File transfers are cached separately from clipboard history.
type FileMsg = TransferFile;

const pad2 = (n: number) => String(n).padStart(2, "0");
/// 时:分:秒
function formatHms(ts: number): string {
  const d = new Date(ts);
  return `${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
}
/// 同一天判定用的日期键
function dayKey(ts: number): string {
  const d = new Date(ts);
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
}
/// 日期分隔文案:今天 / 昨天 / 月日 / 年月日
function formatDayLabel(ts: number): string {
  const d = new Date(ts);
  const now = new Date();
  const dayStart = (x: Date) =>
    new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diffDays = Math.round((dayStart(now) - dayStart(d)) / 86_400_000);
  if (diffDays === 0) return "今天";
  if (diffDays === 1) return "昨天";
  if (d.getFullYear() === now.getFullYear()) {
    return `${d.getMonth() + 1}月${d.getDate()}日`;
  }
  return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日`;
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
  // 是否跟随到底部:用户上滑查看历史后不再强制拉回
  const stickToBottom = useRef(true);
  const sendingRef = useRef(false);
  const refreshVersion = useRef(0);

  const refresh = useCallback(async () => {
    const version = ++refreshVersion.current;
    const list = await chatQuery(200);
    if (version !== refreshVersion.current) return;
    // 防御性排序:按时间正序渲染,最新消息永远在底部
    list.sort((a, b) => a.createdAt - b.createdAt);
    setMessages(list);
  }, []);

  useEffect(() => {
    void refresh().catch(console.error);
    let disposed = false;
    let fileVersion = 0;
    const refreshFiles = () => {
      const version = ++fileVersion;
      void transferFilesQuery().then((files) => {
        if (!disposed && version === fileVersion) setFileMsgs(files);
      }).catch(console.error);
    };
    refreshFiles();
    void devicesCached().then((d) => {
      if (!disposed) setDevices(d.filter((x) => !x.isCurrent && x.online && !!x.id));
    }).catch(console.error);
    // 实时事件:新文本消息 + 文件互传推送/删除
    const stops = [
      subscribeEvent("clipboard-changed", () => { void refresh().catch(console.error); }),
      subscribeEvent("file-transferred", refreshFiles),
      subscribeEvent("file-deleted", refreshFiles),
    ];
    return () => { disposed = true; refreshVersion.current++; stops.forEach((stop) => stop()); };
  }, [refresh]);

  // 进入互传页 / 新消息到达:滚动到最底部
  const scrollToBottom = useCallback(() => {
    const el = listRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, []);
  const maybeScrollToBottom = useCallback(() => {
    if (stickToBottom.current) scrollToBottom();
  }, [scrollToBottom]);
  useEffect(() => {
    maybeScrollToBottom();
    // 图片解码/布局完成后高度会变,多补几次直到稳定
    const timers = [50, 150, 300, 600].map((ms) =>
      window.setTimeout(maybeScrollToBottom, ms),
    );
    return () => timers.forEach((t) => window.clearTimeout(t));
  }, [messages, fileMsgs, maybeScrollToBottom]);

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

  // 清空本机互传记录(不影响对端;清空后不会再用服务端历史补回旧消息)
  const clearAll = async () => {
    try {
      const removed = await chatClear();
      setMessages([]);
      setFileMsgs([]);
      void message.success(removed > 0 ? `已清空 ${removed} 条记录` : "没有可清空的记录");
    } catch (e) {
      void message.error(`清空失败: ${e}`);
    }
  };

  const copyMessage = async (id: number) => {
    try {
      await copyItem(id);
      void message.success("已复制");
    } catch (e) {
      void message.error(`复制失败: ${e}`);
    }
  };

  const pasteMessage = async (id: number) => {
    try {
      await pasteItem(id);
    } catch (e) {
      void message.error(`粘贴失败: ${e}`);
    }
  };

  const saveImage = async (id: number) => {
    try {
      const path = await saveChatImage(id);
      if (path) void message.success(`已保存到 ${path}`);
    } catch (e) {
      void message.error(`保存失败: ${e}`);
    }
  };

  const revealImage = async (path: string) => {
    try {
      await openInExplorer(path);
    } catch (e) {
      void message.error(`打开失败: ${e}`);
    }
  };

  const deleteMessage = (m: ChatMessage) => {
    Modal.confirm({
      title: "删除这条消息?",
      content: "仅从本机互传记录中删除,不影响对方设备。",
      okText: "删除",
      cancelText: "取消",
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await historyDelete(m.id);
          setMessages((prev) => prev.filter((x) => x.id !== m.id));
          void message.success("已删除");
        } catch (e) {
          void message.error(`删除失败: ${e}`);
        }
      },
    });
  };

  // 消息右键菜单:与剪贴板条目统一使用 antd Dropdown + MenuProps,样式完全一致
  const msgMenu = (m: ChatMessage): MenuProps => {
    const isImage = m.entryType === "Image" && !!m.imagePath;
    const items = [
      { key: "copy", icon: <Icon name="copy" size={13} />, label: isImage ? "复制图片" : "复制文本" },
      ...(isImage
        ? ([
            { key: "save", icon: <Icon name="save" size={13} />, label: "另存为…" },
            { key: "reveal", icon: <Icon name="folderOpen" size={13} />, label: "打开所在位置" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      { key: "paste", icon: <Icon name="clipboardPaste" size={13} />, label: "粘贴到当前窗口" },
      { type: "divider" },
      { key: "delete", icon: <Icon name="trash" size={13} />, label: "删除这条记录", danger: true },
    ] as NonNullable<MenuProps["items"]>;
    return {
      items,
      onClick: ({ key, domEvent }) => {
        domEvent.stopPropagation();
        switch (key) {
          case "copy": void copyMessage(m.id); break;
          case "save": void saveImage(m.id); break;
          case "reveal": if (m.imagePath) void revealImage(m.imagePath); break;
          case "paste": void pasteMessage(m.id); break;
          case "delete": deleteMessage(m); break;
        }
      },
    };
  };

  const send = async () => {
    const text = draft.trim();
    if (!text || sendingRef.current) return;
    sendingRef.current = true;
    setSending(true);
    try {
      const targets = selected.size > 0 ? Array.from(selected) : null;
      await syncSendText(text, targets);
      stickToBottom.current = true;
      setDraft("");
      await refresh();
    } catch (e) {
      void message.error(`发送失败: ${e}`);
    } finally {
      sendingRef.current = false;
      setSending(false);
    }
  };

  const sendFiles = async (paths: string[]) => {
    if (paths.length === 0 || sendingRef.current) return;
    sendingRef.current = true;
    setSending(true);
    try {
      const targets = selected.size > 0 ? Array.from(selected) : null;
      for (const p of paths) {
        await syncSendFile(p, targets);
      }
      stickToBottom.current = true;
      scrollToBottom();
      void message.success(`已发送 ${paths.length} 个文件`);
    } catch (e) {
      void message.error(`文件发送失败: ${e}`);
    } finally {
      sendingRef.current = false;
      setSending(false);
    }
  };
  const sendFilesRef = useRef(sendFiles);
  sendFilesRef.current = sendFiles;

  // 选择图片(系统对话框,支持多选)并发送
  const pickAndSendImages = async () => {
    if (sending) return;
    try {
      const paths = await pickImages();
      if (paths.length > 0) await sendFiles(paths);
    } catch (e) {
      void message.error(`选择图片失败: ${e}`);
    }
  };

  // 选择任意文件(二进制互传)
  const pickAndSendFiles = async () => {
    if (sending) return;
    try {
      const paths = await pickFiles();
      if (paths.length > 0) await sendFiles(paths);
    } catch (e) {
      void message.error(`选择文件失败: ${e}`);
    }
  };

  // Tauri 拖拽事件:drop 携带真实文件路径
  useEffect(() => {
    return subscribeAsync(async (active) => {
        const { getCurrentWebview } = await import("@tauri-apps/api/webview");
        return getCurrentWebview().onDragDropEvent((event) => {
          if (!active()) return;
          if (event.payload.type === "over") {
            setDragOver(true);
          } else if (event.payload.type === "leave") {
            setDragOver(false);
          } else if (event.payload.type === "drop") {
            setDragOver(false);
            void sendFilesRef.current(event.payload.paths);
          }
        });
    });
  }, []);

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

  const timeline = chatTimeline(messages, fileMsgs);

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
          <Icon name="fileUpload" size={18} style={{ marginRight: 8 }} />
          松开发送文件给{selected.size > 0 ? `选中的 ${selected.size} 台设备` : "所有设备"}
        </div>
      )}

      {/* 顶部工具栏:左侧清空记录,右侧目标设备胶囊(固定不滚动) */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 6,
          padding: "0 4px 6px",
          flexShrink: 0,
          borderBottom: "1px solid var(--ant-color-border-secondary, rgba(128,128,128,0.14))",
          marginBottom: 4,
        }}
      >
        <Popconfirm
          title="清空互传记录?"
          description="仅清除本机保存的互传消息,不影响对方设备。"
          okText="清空"
          cancelText="取消"
          okButtonProps={{ danger: true }}
          onConfirm={() => void clearAll()}
        >
          <Tooltip title="清空记录" mouseEnterDelay={0.3}>
            <Button
              size="small"
              type="text"
              danger
              aria-label="清空记录"
              icon={<Icon name="trash" size={15} />}
              style={{ paddingInline: 2, height: 24, flexShrink: 0 }}
            />
          </Tooltip>
        </Popconfirm>
        <span
          style={{
            width: 1,
            height: 14,
            background: "var(--ant-color-border-secondary, rgba(128,128,128,0.2))",
            flexShrink: 0,
          }}
        />
        <div
          className="device-chip-bar"
          style={{
            display: "flex",
            alignItems: "center",
            flexWrap: "nowrap",
            gap: 6,
            flex: 1,
            minWidth: 0,
            overflowX: "auto",
            overflowY: "hidden",
            whiteSpace: "nowrap",
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
      </div>

      {/* 消息列表(文本气泡 + 文件卡片,按到达顺序) */}
      <div
        ref={listRef}
        className="scroll-area chat-scroll"
        onScroll={() => {
          const el = listRef.current;
          if (el) {
            stickToBottom.current =
              el.scrollHeight - el.scrollTop - el.clientHeight < 48;
          }
        }}
        style={{ flex: 1, overflow: "auto", padding: "2px 4px 78px", minHeight: 0 }}
      >
        {messages.length === 0 && fileMsgs.length === 0 ? (
          <Empty
            style={{ paddingTop: 60 }}
            description="还没有互传消息"
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        ) : (
          <>
            {timeline.map((row, idx) => {
              const m = row.kind === "message" ? row.value : null;
              const f = row.kind === "file" ? row.value : null;
              const mine = row.value.isFromSelf;
              const showDate =
                idx === 0 || dayKey(timeline[idx - 1].timestamp) !== dayKey(row.timestamp);
              return (
                <div key={row.key} data-chat-id={row.key}>
                  {showDate && (
                    <div style={{ display: "flex", justifyContent: "center", margin: "6px 0 10px" }}>
                      <span
                        style={{
                          fontSize: 10.5,
                          color: "var(--ant-color-text-tertiary, #999)",
                          background: "var(--entry-bg)",
                          padding: "2px 10px",
                          borderRadius: 10,
                        }}
                      >
                        {formatDayLabel(row.timestamp)}
                      </span>
                    </div>
                  )}
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      alignItems: mine ? "flex-end" : "flex-start",
                      marginBottom: 10,
                    }}
                  >
                    {/* 发送方 + 时间:对方消息在气泡左上,自己的在右上 */}
                    <div
                      style={{
                        display: "flex",
                        alignItems: "center",
                        gap: 6,
                        fontSize: 10.5,
                        color: "var(--ant-color-text-tertiary, #999)",
                        marginBottom: 3,
                        marginLeft: mine ? 0 : 2,
                        marginRight: mine ? 2 : 0,
                      }}
                    >
                      {!mine && <span>{row.value.deviceName ?? "远端设备"}</span>}
                      <span style={{ fontVariantNumeric: "tabular-nums" }}>
                        {formatHms(row.timestamp)}
                      </span>
                    </div>
                    {m ? (<Dropdown menu={msgMenu(m)} trigger={["contextMenu"]}>
                    <div
                      style={{
                        maxWidth: "78%",
                        padding: "7px 12px",
                        fontSize: 13,
                        lineHeight: 1.55,
                        whiteSpace: "pre-wrap",
                        wordBreak: "break-word",
                        // 自己的消息:品牌色实底 + 白字(最高对比)
                        // 对方消息:分层灰底 + 常规文字色
                        background: mine
                          ? "var(--ant-color-primary, #2563EB)"
                          : "var(--entry-bg)",
                        color: mine ? "#fff" : "var(--ant-color-text, #333)",
                        // 圆角小尾巴:靠近自己那侧的角收窄
                        borderRadius: mine
                          ? "12px 12px 3px 12px"
                          : "12px 12px 12px 3px",
                        boxShadow: mine
                          ? "none"
                          : "0 1px 2px rgba(0,0,0,0.04)",
                      }}
                    >
                      {m.entryType === "Image" && m.imagePath ? (
                        <img
                          src={imageSrc(m.imagePath)}
                          alt="互传图片"
                          onLoad={maybeScrollToBottom}
                          style={{
                            display: "block",
                            maxWidth: "100%",
                            maxHeight: 220,
                            borderRadius: 8,
                            objectFit: "contain",
                          }}
                        />
                      ) : (
                        m.text
                      )}
                    </div>
                    </Dropdown>) : f && (
                  <div
                    style={{
                      maxWidth: "78%",
                      padding: "9px 12px",
                      // 与文本气泡同一套圆角/底色,视觉统一
                      borderRadius: mine ? "12px 12px 3px 12px" : "12px 12px 12px 3px",
                      background: "var(--entry-bg)",
                      boxShadow: "0 1px 2px rgba(0,0,0,0.04)",
                      display: "flex",
                      alignItems: "center",
                      gap: 10,
                    }}
                  >
                    <Icon name="file" size={26} style={{ flexShrink: 0, color: "var(--ant-color-primary, #2563EB)" }} />
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
                          <Icon name="spinner" size={13} className="spin" />
                        ) : (
                          <Icon name="download" size={13} />
                        )
                      }
                      disabled={downloading.has(f.fileId)}
                      onClick={() => void downloadFile(f)}
                      style={{ flexShrink: 0 }}
                    >
                      保存
                    </Button>
                  </div>)}
                  </div>
                </div>
              );
            })}
          </>
        )}
      </div>

      {/* 输入区 */}
      <div className="chat-composer">
        <Tooltip title="发送文件" mouseEnterDelay={0.3}>
          <Button
            variant="text"
            type="text"
            icon={<Icon name="fileUpload" size={14} />}
            onClick={() => void pickAndSendFiles()}
            disabled={sending}
            style={{ flexShrink: 0, width: 32, height: 32, padding: 0 }}
          />
        </Tooltip>
        <Tooltip title="添加图片(可多选)" mouseEnterDelay={0.3}>
          <Button
            variant="text"
            type="text"
            icon={<Icon name="image" size={14} />}
            onClick={() => void pickAndSendImages()}
            disabled={sending}
            style={{ flexShrink: 0, width: 32, height: 32, padding: 0 }}
          />
        </Tooltip>
        <Input
          variant="borderless"
          placeholder={
            selected.size > 0
              ? `发送给 ${selected.size} 台设备…`
              : "发送给所有设备…"
          }
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onPressEnter={() => void send()}
          disabled={sending}
        />
        <Tooltip title="发送" mouseEnterDelay={0.3}>
          <Button
            type="primary"
            icon={sending ? <Icon name="spinner" size={14} className="spin" /> : <Icon name="send" size={14} />}
            onClick={() => void send()}
            disabled={!draft.trim() || sending}
            style={{ flexShrink: 0, width: 32, height: 32, padding: 0 }}
          />
        </Tooltip>
      </div>
    </div>
  );
}
