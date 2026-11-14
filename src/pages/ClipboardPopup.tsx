import { useCallback, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { Input, Segmented, Tooltip, Empty, Spin, Dropdown, Modal, Image as AntImage } from "antd";
import type { MenuProps } from "antd";
import {
  Copy, Image as ImageIcon, File as FileIcon, Link, Star, Type,
  Pin, PinOff, Settings, Trash2, ClipboardPaste, Laptop, ArrowUp,
  LayoutGrid, MessagesSquare, GripVertical, ExternalLink, Pencil, PenLine,
  FolderOpen, Palette,
} from "lucide-react";
import {
  historyQuery, historyDelete, historyToggleStar, imageSrc,
  copyItem, pasteItem, setPinned, historyUpdateText, historyUpdateRemark,
  type HistoryItem,
} from "../ipc/history";
import TransferChat from "./TransferChat";
import { smartDetect, smartExec, type SmartAction } from "../ipc/chat";

// 分类与旧版 SelectorBar 一致
const CATEGORIES = [
  { key: "all", label: "全部" },
  { key: "Text", label: "文本" },
  { key: "Image", label: "图片" },
  { key: "File", label: "文件" },
  { key: "url", label: "链接" },
  { key: "chat", label: "互传" },
  { key: "starred", label: "收藏" },
] as const;

type CategoryKey = (typeof CATEGORIES)[number]["key"];

/// 分类图标(窄宽度下 Segmented 收缩为纯图标)
const CATEGORY_ICONS: Record<CategoryKey, ReactNode> = {
  all: <LayoutGrid size={14} />,
  Text: <Type size={14} />,
  Image: <ImageIcon size={14} />,
  File: <FileIcon size={14} />,
  url: <Link size={14} />,
  chat: <MessagesSquare size={14} />,
  starred: <Star size={14} />,
};

/// 图标模式的容器宽度阈值:低于此值放不下 7 个文字标签
const SEG_ICON_MODE_WIDTH = 360;

/// 分页参数:每批条数;查询多取 1 条用于判断是否还有下一页
const PAGE_SIZE = 50;
/// 滚动到距底部多少像素时触发追加加载
const LOAD_MORE_THRESHOLD = 300;
/// 回到顶部按钮的"已下滚"判定阈值
const BACK_TOP_THRESHOLD = 300;

export default function ClipboardPopup() {
  const [items, setItems] = useState<HistoryItem[]>([]);
  const [loading, setLoading] = useState(true);      // 首屏/切换分类
  const [loadingMore, setLoadingMore] = useState(false); // 追加分页
  const [hasMore, setHasMore] = useState(false);
  const [search, setSearch] = useState("");
  const [category, setCategory] = useState<CategoryKey>("all");
  const [pinned, setPinnedState] = useState(false);
  const pinnedRef = useRef(false);
  pinnedRef.current = pinned;
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [showBackTop, setShowBackTop] = useState(false);
  const [segWidth, setSegWidth] = useState(0);
  const segRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  // 测量分类栏宽度 → 窄宽度切换图标模式
  useEffect(() => {
    const el = segRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0;
      setSegWidth(Math.round(w));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const lastScrollTop = useRef(0);
  const fetchingMore = useRef(false);

  /// 分页查询:多取 1 条判断 hasMore,真实展示条数 = limit
  const fetchPage = useCallback(
    async (offset: number, append: boolean) => {
      const rows = await historyQuery({
        search: search || null,
        entryType:
          category === "Text" || category === "Image" || category === "File"
            ? category
            : null,
        starredOnly: category === "starred",
        urlOnly: category === "url",
        limit: PAGE_SIZE + 1,
        offset,
      });
      setHasMore(rows.length > PAGE_SIZE);
      const page = rows.slice(0, PAGE_SIZE);
      setItems((prev) => (append ? [...prev, ...page] : page));
    },
    [search, category],
  );

  // 首屏 / 搜索 / 分类切换:重置分页并回到顶部
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setShowBackTop(false);
    lastScrollTop.current = 0;
    if (listRef.current) listRef.current.scrollTop = 0;
    const t = window.setTimeout(async () => {
      try {
        await fetchPage(0, false);
      } finally {
        if (!cancelled) setLoading(false);
      }
    }, search ? 150 : 0);
    return () => {
      cancelled = true;
      window.clearTimeout(t);
    };
  }, [search, category, fetchPage]);

  /// 滚动接近底部时追加下一页
  const loadMore = useCallback(() => {
    if (fetchingMore.current || !hasMore || loadingMore) return;
    fetchingMore.current = true;
    setLoadingMore(true);
    void fetchPage(items.length, true)
      .catch(() => {})
      .finally(() => {
        fetchingMore.current = false;
        setLoadingMore(false);
      });
  }, [fetchPage, hasMore, items.length, loadingMore]);

  /// 滚动处理:回到顶部按钮的出现/消失 + 追加加载
  const onScroll = useCallback(
    (e: React.UIEvent<HTMLDivElement>) => {
      const el = e.currentTarget;
      const st = el.scrollTop;
      const goingUp = st < lastScrollTop.current;

      // 规则:已下滚超过阈值 && 正在向上滚 → 显示;一旦向下滚或回到阈值上方 → 隐藏
      if (st > BACK_TOP_THRESHOLD && goingUp) setShowBackTop(true);
      else if (!goingUp || st <= BACK_TOP_THRESHOLD) setShowBackTop(false);

      // 接近底部 → 追加
      if (el.scrollHeight - st - el.clientHeight < LOAD_MORE_THRESHOLD) {
        loadMore();
      }
      lastScrollTop.current = st;
    },
    [loadMore],
  );

  const scrollToTop = useCallback(() => {
    listRef.current?.scrollTo({ top: 0, behavior: "smooth" });
  }, []);

  const copy = useCallback(async (item: HistoryItem) => {
    try {
      await copyItem(item.id);
    } catch (e) {
      console.error("复制失败", e);
    }
  }, []);

  // 双击/Enter:粘贴回注到呼出前的目标应用(核心交互)
  const paste = useCallback(async (item: HistoryItem) => {
    try {
      await pasteItem(item.id);
    } catch (e) {
      console.error("粘贴失败", e);
    }
  }, []);

  // 键盘导航:↑↓ 移动选中,Enter 复制
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        setSelectedId((cur) => {
          const idx = items.findIndex((it) => it.id === cur);
          const next =
            e.key === "ArrowDown"
              ? Math.min(items.length - 1, idx < 0 ? 0 : idx + 1)
              : Math.max(0, idx <= 0 ? 0 : idx - 1);
          return items[next]?.id ?? null;
        });
      } else if (e.key === "Enter" && selectedId != null) {
        const item = items.find((it) => it.id === selectedId);
        if (item) void paste(item);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [items, selectedId, paste]);

  // 选中项滚动到可视区
  useEffect(() => {
    if (selectedId == null) return;
    listRef.current
      ?.querySelector(`[data-id="${selectedId}"]`)
      ?.scrollIntoView({ block: "nearest" });
  }, [selectedId]);

  const togglePin = useCallback(async () => {
    const next = !pinned;
    await setPinned(next); // 后端:窗口置顶 + 状态广播
    setPinnedState(next);
  }, [pinned]);

  // 失焦自动隐藏(置顶时保持;与旧版行为一致)
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void (async () => {
      try {
        const { getCurrentWindow } = await import("@tauri-apps/api/window");
        const un = await getCurrentWindow().onFocusChanged(({ payload: focused }) => {
          if (!focused && !pinnedRef.current) {
            void import("@tauri-apps/api/core").then(({ invoke }) => invoke("hide_main"));
          }
        });
        unlisten = un;
      } catch {
        /* 浏览器环境无窗口 API */
      }
    })();
    return () => unlisten?.();
  }, []);

  // 后端图钉状态同步(Ctrl+Alt+T 也会切换)
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void (async () => {
      try {
        const { listen } = await import("@tauri-apps/api/event");
        unlisten = await listen<boolean>("pinned-changed", (e) => setPinnedState(e.payload));
      } catch {
        /* 浏览器环境无事件 API */
      }
    })();
    return () => unlisten?.();
  }, []);

  const openSettings = useCallback(async () => {
    try {
      const { invoke } = await import("@tauri-apps/api/core");
      await invoke("open_settings");
    } catch (e) {
      console.error("打开设置失败", e);
    }
  }, []);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh", position: "relative" }}>
      {/* 单行头部:搜索框 + 置顶/设置按钮;空白处即窗口拖拽区 */}
      <div
        data-tauri-drag-region
        style={{
          display: "flex",
          alignItems: "center",
          gap: 8,
          padding: "10px 12px 0",
          flexShrink: 0,
        }}
      >
        <div
          data-tauri-drag-region
          className="drag-handle"
          title="拖动窗口"
          style={{
            display: "flex",
            alignItems: "center",
            padding: "4px 2px",
            color: "var(--ant-color-text-quaternary, #bbb)",
            cursor: "grab",
            flexShrink: 0,
            userSelect: "none",
          }}
        >
          <GripVertical size={14} style={{ pointerEvents: "none" }} />
        </div>
        <Input
          placeholder="搜索文本、拼音、来源应用…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          allowClear
          style={{ flex: 1 }}
        />
        <ToolbarButton
          title={pinned ? "取消置顶" : "窗口置顶"}
          onClick={togglePin}
        >
          {pinned ? <PinOff size={15} /> : <Pin size={15} />}
        </ToolbarButton>
        <ToolbarButton title="设置" onClick={openSettings}>
          <Settings size={15} />
        </ToolbarButton>
      </div>

      {/* 分类:窄宽度自动收缩为纯图标(悬停显示文字) */}
      <div ref={segRef} style={{ padding: "8px 12px 8px", flexShrink: 0 }}>
        <Segmented
          block
          size="small"
          className={segWidth > 0 && segWidth < SEG_ICON_MODE_WIDTH ? "cat-seg-icons" : undefined}
          options={CATEGORIES.map((c) =>
            segWidth > 0 && segWidth < SEG_ICON_MODE_WIDTH
              ? {
                  value: c.key,
                  label: (
                    <Tooltip title={c.label} mouseEnterDelay={0.3}>
                      <span style={{ display: "inline-flex" }}>
                        {CATEGORY_ICONS[c.key]}
                      </span>
                    </Tooltip>
                  ),
                }
              : { value: c.key, label: c.label },
          )}
          value={category}
          onChange={(v) => setCategory(v as CategoryKey)}
        />
      </div>

      {/* 列表:分页加载 + 滚动方向感知的回到顶部按钮 */}
      <div
        ref={listRef}
        onScroll={onScroll}
        className="scroll-area"
        style={{ flex: 1, overflow: "auto", padding: "0 12px 12px" }}
      >
        {category === "chat" ? (
          <TransferChat />
        ) : loading ? (
          // 首屏骨架:6 张占位卡片
          Array.from({ length: 6 }, (_, i) => (
            <div
              key={i}
              className="entry-card"
              style={{ height: 64, marginBottom: 6, opacity: 0.5 }}
            >
              <div
                style={{
                  height: 10,
                  width: "40%",
                  borderRadius: 4,
                  background: "rgba(128,128,128,0.25)",
                  marginBottom: 10,
                }}
              />
              <div
                style={{
                  height: 14,
                  width: "80%",
                  borderRadius: 4,
                  background: "rgba(128,128,128,0.18)",
                }}
              />
            </div>
          ))
        ) : items.length === 0 ? (
          <Empty
            style={{ paddingTop: 80 }}
            description="暂无剪贴板内容"
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        ) : (
          <>
            {/* PreviewGroup:所有图片条目在预览模式下可左右切换 */}
            <AntImage.PreviewGroup>
            {items.map((item) => (
              <EntryCard
                key={item.id}
                item={item}
                selected={selectedId === item.id}
                onSelect={() => setSelectedId(item.id)}
                onCopy={() => void copy(item)}
                onPaste={() => void paste(item)}
                onEdited={(text) => {
                  setItems((prev) =>
                    prev.map((it) => (it.id === item.id ? { ...it, text } : it)),
                  );
                  void historyUpdateText(item.id, text);
                }}
                onRemark={(remark) => {
                  setItems((prev) =>
                    prev.map((it) => (it.id === item.id ? { ...it, remark } : it)),
                  );
                  void historyUpdateRemark(item.id, remark);
                }}
                onToggleStar={() =>
                  void historyToggleStar(item.id, !item.starred).then(() => {
                    setItems((prev) =>
                      prev.map((it) =>
                        it.id === item.id ? { ...it, starred: !it.starred } : it,
                      ),
                    );
                  })
                }
                onDelete={() =>
                  void historyDelete(item.id).then(() =>
                    setItems((prev) => prev.filter((it) => it.id !== item.id)),
                  )
                }
              />
            ))}
            </AntImage.PreviewGroup>
            {loadingMore && (
              <div style={{ textAlign: "center", padding: "10px 0" }}>
                <Spin size="small" />
              </div>
            )}
            {!hasMore && items.length > PAGE_SIZE / 2 && (
              <div
                style={{
                  textAlign: "center",
                  padding: "8px 0 2px",
                  fontSize: 11,
                  color: "var(--ant-color-text-quaternary, #bbb)",
                }}
              >
                已加载全部 {items.length} 条
              </div>
            )}
          </>
        )}
      </div>

      {/* 回到顶部:仅"下滚后向上滚"时淡入,再向下滚即淡出 */}
      <button
        className={`back-top ${showBackTop ? "show" : ""}`}
        onClick={scrollToTop}
        tabIndex={showBackTop ? 0 : -1}
        aria-hidden={!showBackTop}
      >
        <ArrowUp size={16} />
      </button>
    </div>
  );
}

function ToolbarButton({
  title, onClick, children,
}: {
  title: string;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <Tooltip title={title} mouseEnterDelay={0.3}>
      <button
        onClick={onClick}
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          width: 28,
          height: 28,
          border: "none",
          borderRadius: 6,
          background: "transparent",
          color: "var(--ant-color-text-secondary, #666)",
          cursor: "pointer",
          flexShrink: 0,
        }}
        onMouseEnter={(e) =>
          (e.currentTarget.style.background = "rgba(128,128,128,0.15)")
        }
        onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
      >
        {children}
      </button>
    </Tooltip>
  );
}

function EntryCard({
  item, selected, onSelect, onCopy, onPaste, onToggleStar, onDelete, onEdited, onRemark,
}: {
  item: HistoryItem;
  selected: boolean;
  onSelect: () => void;
  onCopy: () => void;
  onPaste: () => void;
  onToggleStar: () => void;
  onDelete: () => void;
  onEdited: (text: string) => void;
  onRemark: (remark: string | null) => void;
}) {
  const [hover, setHover] = useState(false);
  const [smart, setSmart] = useState<SmartAction | null>(null);
  const [editing, setEditing] = useState(false);
  const [editText, setEditText] = useState(item.text ?? "");
  const [remarking, setRemarking] = useState(false);
  const [remarkText, setRemarkText] = useState(item.remark ?? "");

  // 菜单打开时检测智能动作(完整元数据,不受开关门控)
  const detectSmart = useCallback(() => {
    if (item.entryType !== "Text" || !item.text) {
      setSmart(null);
      return;
    }
    void smartDetect(item.text, false).then(setSmart).catch(() => setSmart(null));
  }, [item.entryType, item.text]);

  const runSmart = useCallback(async (spec: SmartAction["primary"]) => {
    try {
      await smartExec(spec);
    } catch (e) {
      console.error("智能操作失败", e);
    }
  }, []);

  const menu: MenuProps = {
    items: [
      { key: "copy", icon: <Copy size={13} />, label: "复制" },
      { key: "paste", icon: <ClipboardPaste size={13} />, label: "粘贴到目标" },
      // 智能动作(按内容动态注入)
      ...(smart
        ? ([
            { type: "divider" },
            {
              key: "smart-primary",
              icon: <ExternalLink size={13} />,
              label: smart.primary.kind === "copy_text"
                ? smart.primary.arg.slice(0, 20)
                : smart.title,
            },
            ...(smart.secondary
              ? [{
                  key: "smart-secondary",
                  icon: <Copy size={13} />,
                  label: "复制" + (smart.secondary.arg.slice(0, 16)),
                }]
              : []),
          ] as NonNullable<MenuProps["items"]>)
        : []),
      ...(item.entryType === "Image" && item.imagePath
        ? ([
            { type: "divider" },
            { key: "reveal-image", icon: <FolderOpen size={13} />, label: "在资源管理器中显示" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      ...(item.entryType === "File" && item.files.length > 0
        ? ([
            { type: "divider" },
            { key: "reveal-file", icon: <FolderOpen size={13} />, label: "打开所在文件夹" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      { type: "divider" },
      {
        key: "star",
        icon: <Star size={13} fill={item.starred ? "#f59e0b" : "none"} color={item.starred ? "#f59e0b" : "currentColor"} />,
        label: item.starred ? "取消收藏" : "收藏",
      },
      ...(item.entryType === "Text"
        ? ([
            { key: "edit", icon: <PenLine size={13} />, label: "编辑文本…" },
            { key: "remark", icon: <Pencil size={13} />, label: "备注…" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      { type: "divider" },
      { key: "delete", icon: <Trash2 size={13} />, label: "删除", danger: true },
    ] as NonNullable<MenuProps["items"]>,
    onClick: ({ key, domEvent }) => {
      domEvent.stopPropagation();
      switch (key) {
        case "copy": onCopy(); break;
        case "paste": onPaste(); break;
        case "star": onToggleStar(); break;
        case "delete": onDelete(); break;
        case "edit": setEditText(item.text ?? ""); setEditing(true); break;
        case "remark": setRemarkText(item.remark ?? ""); setRemarking(true); break;
        case "reveal-image":
          void smartExec({ kind: "locate_path", arg: item.imagePath ?? "" });
          break;
        case "reveal-file": {
          const fp = item.files[0]?.path ?? "";
          void smartExec({ kind: "open_path", arg: fp });
          break;
        }
        case "smart-primary":
          if (smart) void runSmart(smart.primary);
          break;
        case "smart-secondary":
          if (smart?.secondary) void runSmart(smart.secondary);
          break;
      }
    },
  };

  const showBorder = hover || selected;
  // 始终有微弱细边框;选中品牌蓝、hover 加深、静息极淡
  const borderColor = selected
    ? "var(--ant-color-primary, #2563EB)"
    : hover
      ? "rgba(128,128,128,0.35)"
      : "rgba(128,128,128,0.16)";

  return (
    <>
      <Dropdown menu={menu} trigger={["contextMenu"]} onOpenChange={(o) => { if (o) detectSmart(); }}>
      <div
        className="entry-card"
        data-id={item.id}
        onClick={onSelect}
        onDoubleClick={onPaste}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        style={{
          padding: "8px 10px",
          marginBottom: 6,
          borderRadius: 8,
          // 底色随主题:CSS 变量在 index.css 按 color-scheme 定义(深色用提亮层,不发脏)
          background: selected
            ? "var(--entry-selected-bg, rgba(37,99,235,0.08))"
            : hover
              ? "var(--entry-hover-bg, rgba(128,128,128,0.10))"
              : "var(--entry-bg, rgba(128,128,128,0.06))",
          border: showBorder ? `1px solid ${borderColor}` : "1px solid transparent",
          cursor: "pointer",
          overflow: "hidden",
          transition: "background 0.12s, border-color 0.12s",
        }}
      >
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 6,
            fontSize: 11,
            color: "var(--ant-color-text-tertiary, #999)",
            marginBottom: 4,
          }}
        >
          <SourceIcon item={item} />
          <span>{item.sourceAppName ?? item.deviceName ?? "本机"}</span>
          <span style={{ marginLeft: "auto" }}>{formatTime(item.createdAt)}</span>
          {item.starred && <Star size={12} fill="currentColor" color="#f59e0b" />}
        </div>
        <EntryBody item={item} />
      </div>
      </Dropdown>

      {/* 编辑文本弹窗 */}
      <Modal
        title="编辑文本"
        open={editing}
        okText="保存"
        cancelText="取消"
        onOk={async () => {
          try {
            await historyUpdateText(item.id, editText);
            onEdited(editText);
          } catch (e) {
            console.error(e);
          }
          setEditing(false);
        }}
        onCancel={() => setEditing(false)}
        width={420}
      >
        <Input.TextArea
          rows={5}
          value={editText}
          onChange={(e) => setEditText(e.target.value)}
        />
      </Modal>

      {/* 备注弹窗 */}
      <Modal
        title="备注"
        open={remarking}
        okText="保存"
        cancelText="取消"
        onOk={async () => {
          await onRemark(remarkText.trim() || null);
          setRemarking(false);
        }}
        onCancel={() => setRemarking(false)}
        width={380}
      >
        <Input
          value={remarkText}
          onChange={(e) => setRemarkText(e.target.value)}
          placeholder="备注(留空清除)"
        />
      </Modal>
    </>
  );
}

// 来源应用图标:优先用旧版缓存的本地图标,缺失/加载失败用通用设备图标
function SourceIcon({ item }: { item: HistoryItem }) {
  const [failed, setFailed] = useState(false);
  if (item.sourceAppIcon && !failed) {
    return (
      <img
        src={imageSrc(item.sourceAppIcon)}
        alt=""
        width={12}
        height={12}
        style={{ borderRadius: 3, flexShrink: 0 }}
        onError={() => setFailed(true)}
      />
    );
  }
  return <Laptop size={12} />;
}

function EntryBody({ item }: { item: HistoryItem }) {
  if (item.entryType === "Image") {
    return item.imagePath ? (
      <AntImage
        src={imageSrc(item.imagePath)}
        alt="剪贴板图片"
        loading="lazy"
        // 预览:antd Image 内建(缩放/旋转/1:1/多图切换);
        // 点击预览不要触发卡片的选中/双击粘贴
        onClick={(e) => e.stopPropagation()}
        onDoubleClick={(e) => e.stopPropagation()}
        width="100%"
        style={{
          maxWidth: "100%",
          maxHeight: 120,
          borderRadius: 6,
          objectFit: "cover",
          objectPosition: "left top",
          display: "block",
        }}
        preview={{
          // 预览遮罩下也支持双击关闭外的操作;遮罩层模糊
          mask: <span style={{ fontSize: 11 }}>点击预览</span>,
        }}
      />
    ) : (
      <span style={{ fontSize: 13, color: "var(--ant-color-text-tertiary, #999)" }}>[图片(远端,待下载)]</span>
    );
  }
  if (item.entryType === "File") {
    return (
      <div style={{ display: "flex", flexWrap: "wrap", gap: 4 }}>
        {item.files.map((f) => (
          <span
            key={f.path}
            style={{
              fontSize: 12,
              padding: "2px 8px",
              borderRadius: 999,
              background: "rgba(37,99,235,0.1)",
            }}
          >
            <FileIcon size={11} style={{ verticalAlign: "-1px", marginRight: 3 }} />
            {f.name}
            {f.isDirectory ? "" : ` · ${formatSize(f.sizeBytes)}`}
          </span>
        ))}
      </div>
    );
  }
  const text = item.text ?? "";
  const isUrl = /^https?:\/\//.test(text);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
      {isUrl && <Link size={13} style={{ flexShrink: 0 }} />}
      <Tooltip title={text} placement="top" mouseEnterDelay={0.4}>
        <span
          style={{
            fontSize: 13,
            lineHeight: 1.4,
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
          }}
        >
          {text}
        </span>
      </Tooltip>
    </div>
  );
}

function formatTime(ms: number): string {
  const d = new Date(ms);
  const now = new Date();
  const sameDay = d.toDateString() === now.toDateString();
  if (sameDay) {
    return d.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" });
  }
  return d.toLocaleDateString("zh-CN", { month: "numeric", day: "numeric" });
}

function formatSize(bytes: number): string {
  if (bytes <= 0) return "0 B";
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${trim(kb)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${trim(mb)} MB`;
  return `${trim(mb / 1024)} GB`;
}

const trim = (v: number) => v.toFixed(v < 10 ? 1 : 0).replace(/\.0$/, "");
