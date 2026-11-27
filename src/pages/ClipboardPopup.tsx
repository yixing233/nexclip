import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { Input, Segmented, Tooltip, Empty, Spin, Dropdown, Modal, Image as AntImage, message } from "antd";
import type { MenuProps } from "antd";
import { Icon } from "../icons";
import type { IconName } from "../icons";
import {
  historyQuery, historyDelete, historyToggleStar, imageSrc,
  copyItem, pasteItem, setPinned, historyUpdateText, historyUpdateRemark,
  type HistoryItem,
} from "../ipc/history";
import TransferChat from "./TransferChat";
import { smartDetect, smartExec, type SmartAction } from "../ipc/chat";
import { settingsGet } from "../ipc/settings";
import { subscribeAsync, subscribeEvent } from "../ipc/events";
import { captureBookmark, restoreBookmark, type ScrollBookmark } from "../utils/historyView";

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
  all: <Icon name="grid" size={14} />,
  Text: <Icon name="type" size={14} />,
  Image: <Icon name="image" size={14} />,
  File: <Icon name="file" size={14} />,
  url: <Icon name="link" size={14} />,
  chat: <Icon name="chat" size={14} />,
  starred: <Icon name="star" size={14} />,
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
  const [rememberScroll, setRememberScroll] = useState(false);
  const scrollMemo = useRef<Record<string, ScrollBookmark>>({});
  const queryKey = `${category}|${search}`;
  const queryKeyRef = useRef(queryKey);
  queryKeyRef.current = queryKey;
  const requestVersion = useRef(0);
  const loadedCount = useRef(0);
  const restoringScroll = useRef(false);
  const pendingRestore = useRef<{ key: string; version: number; bookmark: ScrollBookmark } | null>(null);
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

  // 读取"记住滚动位置"开关,并在设置变更时同步
  useEffect(() => {
    let disposed = false;
    const load = () => {
      void settingsGet()
        .then((s) => { if (!disposed) setRememberScroll(s.rememberScrollPosition); })
        .catch(() => {});
    };
    load();
    const stop = subscribeEvent("settings-changed", load);
    return () => { disposed = true; stop(); };
  }, []);

  const lastScrollTop = useRef(0);
  const fetchingMore = useRef(false);

  /// 分页查询:多取 1 条判断 hasMore,真实展示条数 = limit
  const fetchPage = useCallback(
    async (offset: number, append: boolean, limit = PAGE_SIZE, bookmark?: ScrollBookmark) => {
      const key = `${category}|${search}`;
      const version = append ? requestVersion.current : ++requestVersion.current;
      if (category === "chat") return false;
      const rows = await historyQuery({
        search: search || null,
        entryType:
          category === "Text" || category === "Image" || category === "File"
            ? category
            : null,
        starredOnly: category === "starred",
        urlOnly: category === "url",
        limit: limit + 1,
        offset,
      });
      if (queryKeyRef.current !== key || requestVersion.current !== version) return false;
      setHasMore(rows.length > limit);
      const page = rows.slice(0, limit);
      loadedCount.current = offset + page.length;
      if (!append) pendingRestore.current = { key, version, bookmark: bookmark ?? { top: 0, count: limit } };
      setItems((prev) => append
        ? [...prev, ...page.filter((item) => !prev.some((old) => old.id === item.id))] : page);
      return true;
    },
    [search, category],
  );

  useLayoutEffect(() => {
    const pending = pendingRestore.current;
    if (!pending || loading) return;
    pendingRestore.current = null;
    if (pending.key !== queryKeyRef.current || pending.version !== requestVersion.current) return;
    restoreBookmark(listRef.current, pending.bookmark);
    lastScrollTop.current = listRef.current?.scrollTop ?? 0;
    restoringScroll.current = false;
  }, [items, loading]);

  // 首屏 / 搜索 / 分类切换:重置分页并回到顶部
  useEffect(() => {
    let cancelled = false;
    const key = `${category}|${search}`;
    const saved = rememberScroll ? scrollMemo.current[key] : undefined;
    ++requestVersion.current;
    fetchingMore.current = false;
    setLoadingMore(false);
    restoringScroll.current = true;
    setLoading(true);
    setShowBackTop(false);
    lastScrollTop.current = 0;
    if (listRef.current && !rememberScroll) listRef.current.scrollTop = 0;
    const t = window.setTimeout(async () => {
      try {
        const applied = await fetchPage(0, false, Math.max(PAGE_SIZE, saved?.count ?? 0), saved);
        if (!cancelled && applied) {
          setLoading(false);
        }
      } catch (e) {
        if (!cancelled) { restoringScroll.current = false; console.error("历史查询失败", e); }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }, search ? 150 : 0);
    return () => {
      cancelled = true;
      requestVersion.current++;
      window.clearTimeout(t);
    };
  }, [search, category, fetchPage, rememberScroll]);

  /// 滚动接近底部时追加下一页
  const loadMore = useCallback(() => {
    if (restoringScroll.current || loading || fetchingMore.current || !hasMore || loadingMore) return;
    const version = requestVersion.current;
    fetchingMore.current = true;
    setLoadingMore(true);
    void fetchPage(items.length, true)
      .catch(() => {})
      .finally(() => {
        if (requestVersion.current === version) {
          fetchingMore.current = false;
          setLoadingMore(false);
        }
      });
  }, [fetchPage, hasMore, items.length, loadingMore, loading]);

  /// 滚动处理:回到顶部按钮的出现/消失 + 追加加载
  const onScroll = useCallback(
    (e: React.UIEvent<HTMLDivElement>) => {
      const el = e.currentTarget;
      if (category === "chat" || restoringScroll.current) return;
      const st = el.scrollTop;
      const goingUp = st < lastScrollTop.current;

      // 规则:已下滚超过阈值 && 正在向上滚 → 显示;一旦向下滚或回到阈值上方 → 隐藏
      if (st > BACK_TOP_THRESHOLD && goingUp) setShowBackTop(true);
      else if (!goingUp || st <= BACK_TOP_THRESHOLD) setShowBackTop(false);

      // 接近底部 → 追加
      if (el.scrollHeight - st - el.clientHeight < LOAD_MORE_THRESHOLD) {
        loadMore();
      }
      if (rememberScroll) {
        scrollMemo.current[`${category}|${search}`] = captureBookmark(el, loadedCount.current);
      }
      lastScrollTop.current = st;
    },
    [loadMore, rememberScroll, category, search],
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
      if (category === "chat" || e.isComposing || (e.target instanceof HTMLElement
        && !!e.target.closest("input,textarea,[contenteditable=true],.ant-modal,.ant-dropdown,.ant-image-preview,[role=dialog]"))
        || document.querySelector(".ant-image-preview-wrap:not([aria-hidden=true])")) return;
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
  }, [items, selectedId, paste, category]);

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
    return subscribeAsync(async (active) => {
        const { getCurrentWindow } = await import("@tauri-apps/api/window");
        return getCurrentWindow().onFocusChanged(({ payload: focused }) => {
          if (active() && !focused && !pinnedRef.current) {
            void import("@tauri-apps/api/core").then(({ invoke }) => invoke("hide_main"));
          }
        });
    });
  }, []);

  // 后端图钉状态同步(Ctrl+Alt+T 也会切换)
  useEffect(() => {
    return subscribeEvent<boolean>("pinned-changed", (e) => setPinnedState(e.payload));
  }, []);

  // 剪贴板健康告警:被其他程序长期独占 / 捕获卡死 / 恢复
  useEffect(() => {
    const unlisten = [
          subscribeEvent<string>("clipboard-blocked", (e) => {
            void message.warning({ content: e.payload, duration: 6 });
          }),
          subscribeEvent<string>("clipboard-watchdog", (e) => {
            void message.error({ content: e.payload, duration: 8 });
          }),
          subscribeEvent("clipboard-unblocked", () => {
            void message.success({ content: "剪贴板已恢复，监听继续", duration: 3 });
          }),
    ];
    return () => unlisten.forEach((fn) => fn());
  }, []);

  // Refresh the loaded range so search, pinyin and category filtering stay authoritative.
  const refreshCurrent = useCallback(async () => {
    if (category === "chat") return;
    const keep = rememberScroll ? captureBookmark(listRef.current, loadedCount.current) : undefined;
    restoringScroll.current = true;
    fetchingMore.current = false;
    setLoadingMore(false);
    try {
      await fetchPage(0, false, Math.max(PAGE_SIZE, loadedCount.current), keep);
    } catch (error) {
      restoringScroll.current = false;
      throw error;
    }
  }, [category, search, rememberScroll, fetchPage]);

  useEffect(() => {
    return subscribeEvent("clipboard-changed", () => { void refreshCurrent().catch(console.error); });
  }, [refreshCurrent]);

  // 窗口重新呼出时整体重查:补齐弹窗隐藏期间捕获的内容
  useEffect(() => {
    return subscribeEvent("history-refresh", () => { void refreshCurrent().catch(console.error); });
  }, [refreshCurrent]);

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
          <Icon name="gripVertical" size={14} style={{ pointerEvents: "none" }} />
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
          active={pinned}
        >
          {pinned ? <Icon name="pinOff" size={15} /> : <Icon name="pin" size={15} />}
        </ToolbarButton>
        <ToolbarButton title="设置" onClick={openSettings}>
          <Icon name="settings" size={15} />
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
          onChange={(v) => {
            if (rememberScroll && category !== "chat") {
              scrollMemo.current[queryKey] = captureBookmark(listRef.current, loadedCount.current);
            }
            restoringScroll.current = true;
            setCategory(v as CategoryKey);
          }}
        />
      </div>

      {/* 列表:分页加载 + 滚动方向感知的回到顶部按钮 */}
      <div
        ref={listRef}
        onScroll={onScroll}
        // 互传页外层不滚动,去掉 scroll-area(它会用 scrollbar-gutter 两侧预留宽度,
        // 导致互传内容与右侧滚动条被整体往里挤;滚动交给 TransferChat 自己的列表)
        className={category === "chat" ? undefined : "scroll-area"}
        style={{
          flex: 1,
          // 互传页自带内部滚动,外层不能再滚,否则顶部工具栏会跟着消息一起滚走
          overflow: category === "chat" ? "hidden" : "auto",
          // 互传页取消左右内边距,让右侧滚动条紧贴窗口边缘
          padding: category === "chat" ? "2px 0 0" : "0 4px 12px",
          minHeight: 0,
        }}
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
            {/* PreviewGroup:所有图片条目在预览模式下可左右切换、缩放/旋转。
                不加自定义 imageRender —— antd 默认深色遮罩即可。 */}
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
        <Icon name="arrowUp" size={16} />
      </button>
    </div>
  );
}

function ToolbarButton({
  title, onClick, children, active = false,
}: {
  title: string;
  onClick: () => void;
  children: ReactNode;
  /// 激活态(如已置顶):品牌色填充 + 反色图标
  active?: boolean;
}) {
  const baseBg = active ? "var(--ant-color-primary, #2563EB)" : "transparent";
  const baseColor = active
    ? "var(--ant-color-white, #fff)"
    : "var(--ant-color-text-secondary, #666)";
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
          background: baseBg,
          color: baseColor,
          cursor: "pointer",
          flexShrink: 0,
          transition: "background 0.15s, color 0.15s",
        }}
        onMouseEnter={(e) =>
          (e.currentTarget.style.background = active
            ? "var(--ant-color-primary-hover, #1d4ed8)"
            : "rgba(128,128,128,0.15)")
        }
        onMouseLeave={(e) => (e.currentTarget.style.background = baseBg)}
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
      { key: "copy", icon: <Icon name="copy" size={13} />, label: "复制" },
      { key: "paste", icon: <Icon name="clipboardPaste" size={13} />, label: "粘贴到目标" },
      // 智能动作(按内容动态注入)
      ...(smart
        ? ([
            { type: "divider" },
            {
              key: "smart-primary",
              icon: <Icon name="externalLink" size={13} />,
              label: smart.primary.kind === "copy_text"
                ? smart.primary.arg.slice(0, 20)
                : smart.title,
            },
            ...(smart.secondary
              ? [{
                  key: "smart-secondary",
                  icon: <Icon name="copy" size={13} />,
                  label: "复制" + (smart.secondary.arg.slice(0, 16)),
                }]
              : []),
          ] as NonNullable<MenuProps["items"]>)
        : []),
      ...(item.entryType === "Image" && item.imagePath
        ? ([
            { type: "divider" },
            { key: "reveal-image", icon: <Icon name="folderOpen" size={13} />, label: "在资源管理器中显示" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      ...(item.entryType === "File" && item.files.length > 0
        ? ([
            { type: "divider" },
            { key: "reveal-file", icon: <Icon name="folderOpen" size={13} />, label: "打开所在文件夹" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      { type: "divider" },
      {
        key: "star",
        icon: <Icon name="star" size={13} fill={item.starred ? "#f59e0b" : "none"} color={item.starred ? "#f59e0b" : "currentColor"} />,
        label: item.starred ? "取消收藏" : "收藏",
      },
      ...(item.entryType === "Text"
        ? ([
            { key: "edit", icon: <Icon name="penLine" size={13} />, label: "编辑文本…" },
            { key: "remark", icon: <Icon name="pencil" size={13} />, label: "备注…" },
          ] as NonNullable<MenuProps["items"]>)
        : []),
      { type: "divider" },
      { key: "delete", icon: <Icon name="trash" size={13} />, label: "删除", danger: true },
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
          // 底色统一浅灰;hover/选中用边框区分,不变整卡底色
          background: "var(--entry-bg, rgba(128,128,128,0.06))",
          border: `1px solid ${
            selected
              ? "var(--ant-color-primary, #2563EB)"
              : hover
                ? "rgba(128,128,128,0.4)"
                : "rgba(128,128,128,0.16)"
          }`,
          // 选中加粗为视觉 2px(inset 阴影叠加,不引起布局抖动)
          boxShadow: selected
            ? "inset 0 0 0 1px var(--ant-color-primary, #2563EB)"
            : undefined,
          cursor: "pointer",
          overflow: "hidden",
          transition: "border-color 0.12s, box-shadow 0.12s",
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
          {/* 来源标签优先级:来源应用名 → (仅远端)设备名 → 本机。
              本机捕获的条目绝不能回落到 deviceName —— 那是本机机器名,
              显示出来会被误读成「其他设备同步过来的」。 */}
          <span>
            {item.sourceAppName ??
              (item.origin === 1 ? (item.deviceName ?? "远端设备") : "本机")}
          </span>
          {item.oversized && (
            <span
              title="图片原始体积超过 10MB，仅本地保存，不参与同步"
              style={{
                padding: "0 5px",
                borderRadius: 4,
                fontSize: 10,
                lineHeight: "15px",
                color: "#b45309",
                background: "rgba(245,158,11,0.14)",
                flexShrink: 0,
              }}
            >
              仅本地 · 超 10MB
            </span>
          )}
          <span style={{ marginLeft: "auto" }}>{formatTime(item.createdAt)}</span>
          {item.starred && <Icon name="star" size={12} fill="currentColor" color="#f59e0b" />}
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
/// 设备平台 → 图标语义名。服务端 platform 值形如 Android / Windows / Web / iOS。
function platformIconName(platform: string | null): IconName {
  const p = (platform ?? "").toLowerCase();
  if (p.includes("android") || p.includes("ios") || p.includes("phone") || p.includes("mobile")) {
    return "smartphone";
  }
  if (p.includes("web") || p.includes("browser") || p.includes("linux") || p.includes("mac")) {
    // 网页端与其它类 Unix 归到「网页/通用」图标;Windows 走默认笔记本
    return p.includes("web") || p.includes("browser") ? "globe" : "laptop";
  }
  return "laptop";
}

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
  // 没有来源应用图标(远端条目):按设备平台区分手机/电脑/网页
  return <Icon name={platformIconName(item.devicePlatform)} size={12} />;
}

function EntryBody({ item }: { item: HistoryItem }) {
  if (item.entryType === "Image") {
    if (!item.imagePath && item.oversized) {
      return (
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 6,
            padding: "10px 8px",
            borderRadius: 6,
            background: "rgba(128,128,128,0.06)",
            border: "1px dashed rgba(128,128,128,0.28)",
            fontSize: 12,
            color: "var(--ant-color-text-tertiary, #999)",
          }}
        >
          <Icon name="image" size={14} />
          图片超过 10MB，无法生成预览
        </div>
      );
    }
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
          // 缩略图按原始比例整体缩小(contain):不裁剪、不变形,
          // 超宽/超长图只缩不放,配合 maxHeight 在卡片内自适应
          maxWidth: "100%",
          maxHeight: 120,
          borderRadius: 6,
          objectFit: "contain",
          objectPosition: "center",
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
    // 脏数据兜底:历史库里可能残留 file_paths 为空的旧条目
    if (item.files.length === 0) {
      return (
        <span style={{ fontSize: 13, color: "var(--ant-color-text-tertiary, #999)" }}>
          [文件记录已失效]
        </span>
      );
    }
    // 与 WinUI 版一致:整条只渲染一张卡片,首个文件名 + 「等 N 项」,
    // 完整路径清单放 Tooltip —— 不再平铺多个 chip(平铺会把文件名挤断)。
    const first = item.files[0];
    const hasRest = item.files.length > 1;
    const tooltipLines = item.files.slice(0, 10).map((f) => f.path);
    const tooltip =
      item.files.length > 10
        ? `${tooltipLines.join("\n")}\n… 其余 ${item.files.length - 10} 项`
        : tooltipLines.join("\n");
    const sizeText =
      !first.isDirectory && first.sizeBytes > 0 ? formatSize(first.sizeBytes) : "";

    return (
      <Tooltip title={<span style={{ whiteSpace: "pre-line" }}>{tooltip}</span>} mouseEnterDelay={0.4}>
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 8,
            maxWidth: "100%",
            padding: "8px 12px",
            borderRadius: 8,
            border: "1px solid var(--ant-color-border-secondary, rgba(128,128,128,0.18))",
            background: "var(--entry-bg)",
          }}
        >
          <Icon
            name={first.isDirectory ? "folderOpen" : "file"}
            size={16}
            style={{ flexShrink: 0, color: "var(--ant-color-primary, #2563EB)" }}
          />
          {/* 主文件名:单行 + 省略号,不换行也不硬裁切 */}
          <span
            style={{
              fontSize: 14,
              fontWeight: 600,
              whiteSpace: "nowrap",
              overflow: "hidden",
              textOverflow: "ellipsis",
              minWidth: 0,
            }}
          >
            {first.name || first.path}
          </span>
          {/* 多个文件时提示总数 */}
          {hasRest && (
            <span
              style={{
                fontSize: 12,
                flexShrink: 0,
                color: "var(--ant-color-text-tertiary, #999)",
              }}
            >
              等 {item.files.length} 项
            </span>
          )}
          {/* 大小(取不到时不显示,避免误报 0 B) */}
          {sizeText && (
            <span
              style={{
                fontSize: 12,
                flexShrink: 0,
                color: "var(--ant-color-text-tertiary, #999)",
              }}
            >
              {sizeText}
            </span>
          )}
        </div>
      </Tooltip>
    );
  }
  const text = item.text ?? "";
  const isUrl = /^https?:\/\//.test(text);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
      {isUrl && <Icon name="link" size={13} style={{ flexShrink: 0 }} />}
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
  // 脏数据兜底:历史库可能存在 file_paths 为空的旧条目,字段缺失会变成
  // undefined/NaN,直接算会输出 "NaN GB"。
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${trim(kb)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${trim(mb)} MB`;
  return `${trim(mb / 1024)} GB`;
}

const trim = (v: number) => v.toFixed(v < 10 ? 1 : 0).replace(/\.0$/, "");
