// 截图覆盖窗:对齐 flashot 交互。
// 冻结屏 + 拖拽选区(移动/8 手柄缩放) + 窗口悬停高亮(点击自动选窗) +
// 放大镜 + 取色器 + 标注 + 图像调整 + 圆角 + 复制/保存/贴图/长截图。
// 每显示器一个窗口(label=overlay-{id}),选区坐标为显示器本地 CSS 像素。
import { useCallback, useEffect, useRef, useState } from "react";
import { Tooltip } from "antd";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import type { CaptureStartPayload, DetectedWindow, FlashPayload, ScrollProgress, ScreenshotRect } from "../ipc/screenshot";
import {
  monitorIdFromLabel,
  screenshotCancel,
  screenshotCopy,
  screenshotOverlayReady,
  screenshotPendingStart,
  screenshotPin,
  screenshotSave,
  scrollCaptureStart,
} from "../ipc/screenshot";
import { AnnotationEditor, DEFAULT_ANNOTATION_CONFIG, type AnnotationConfig, type Tool } from "../components/annotation/editor";
import AnnotationToolbar from "../components/annotation/Toolbar";
import { Icon } from "../icons";

type Phase = "idle" | "crosshair" | "selecting" | "committed" | "scrolling" | "flash";

const MIN_SELECT = 4;
const MAG_SIZE = 132; // 放大镜边长
const MAG_ZOOM = 8;

type DragMode =
  | { kind: "none" }
  | { kind: "new"; startX: number; startY: number }
  | { kind: "move"; startX: number; startY: number; orig: ScreenshotRect }
  | { kind: "resize"; startX: number; startY: number; orig: ScreenshotRect; handle: string };

export default function ScreenOverlay() {
  const [payload, setPayload] = useState<CaptureStartPayload | null>(null);
  const [phase, setPhase] = useState<Phase>("idle");
  const [sel, setSel] = useState<ScreenshotRect | null>(null);
  const [hover, setHover] = useState<{ x: number; y: number } | null>(null);
  const [hoverWindow, setHoverWindow] = useState<ScreenshotRect | null>(null);
  /// 悬停窗口信息(应用名/标题,用于标签显示)
  const [hoverWindowInfo, setHoverWindowInfo] = useState<DetectedWindow | null>(null);
  /// 悬停查询节流时间戳
  const lastProbeRef = useRef(0);
  const [tool, setTool] = useState<Tool | null>(null); // null=未开启标注
  const [annEditor, setAnnEditor] = useState<AnnotationEditor | null>(null);
  const [annConfig, setAnnConfig] = useState<AnnotationConfig>({ ...DEFAULT_ANNOTATION_CONFIG });
  const [cornerRadius, setCornerRadius] = useState(0);
  const [sidePanel, setSidePanel] = useState<"none" | "radius">("none"); // 右侧条侧面板
  const [scrollState, setScrollState] = useState<ScrollProgress | null>(null);
  const [flashRect, setFlashRect] = useState<ScreenshotRect | null>(null);
  const [pickedColor, setPickedColor] = useState<string | null>(null);
  const [picking, setPicking] = useState(false); // 取色模式:下一次点击屏幕取色
  const [imgError, setImgError] = useState<string | null>(null);
  /// 像素缓存是否就绪(懒加载完成;标注/放大镜/取色依赖)
  const [canvasReady, setCanvasReady] = useState(false);
  const [shotHotkey, setShotHotkey] = useState("F1"); // 截图快捷键(文案显示用)

  // 挂载时拉取一次设置(长图 HUD / 兜底文案显示实际热键,不硬编码 F1)
  useEffect(() => {
    void import("../ipc/settings").then(({ settingsGet }) =>
      settingsGet().then((s) => { if (s.hotkeyScreenshot) setShotHotkey(s.hotkeyScreenshot); }),
    ).catch(() => {});
  }, []);

  const dragRef = useRef<DragMode>({ kind: "none" });
  const frameImgRef = useRef<HTMLImageElement | null>(null);
  const frameCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const frameDataRef = useRef<ImageData | null>(null);
  const magCanvasRef = useRef<HTMLCanvasElement | null>(null);
  const annHostRef = useRef<HTMLDivElement | null>(null);
  const monitorId = monitorIdFromLabel(getCurrentWindow().label);
  const logicalSize = payload
    ? { w: payload.monitorWidth / payload.scaleFactor, h: payload.monitorHeight / payload.scaleFactor }
    : { w: 0, h: 0 };

  /** 应用启动载荷(事件与 pending 拉取共用) */
  const startSession = useCallback((p: CaptureStartPayload) => {
    setPhase("crosshair");
    setSel(null);
    setTool(null);
    setPickedColor(null);
    setImgError(null);
    setCanvasReady(false);
    setCornerRadius(0);
    setAnnConfig({ ...DEFAULT_ANNOTATION_CONFIG });
    // 兜底:图片/像素缓存万一卡住,1.2s 后无条件回报就绪(否则后端静默取消 → 表现为快捷键失效)
    const readySent = { v: false };
    const sendReady = () => {
      if (readySent.v) return;
      readySent.v = true;
      void screenshotOverlayReady(p.revision, p.monitorId);
    };
    const guard = window.setTimeout(() => {
      if (!frameImgRef.current) setImgError("冻结帧加载较慢…");
      sendReady();
    }, 1200);

    const img = new Image();
    img.crossOrigin = "anonymous"; // CORS 模式加载,保证 canvas getImageData 不被污染
    img.onload = () => {
      frameImgRef.current = img;
      setPayload(p);
      window.clearTimeout(guard);
      // 关键:图片就绪立即回报 → 覆盖窗马上显示。
      // canvas 像素缓存(4K 约 300-800ms)改为懒加载,不阻塞首屏。
      sendReady();
      // 空闲时后台准备像素缓存(放大镜/取色/标注底图用)
      const idle = (window as unknown as { requestIdleCallback?: (cb: () => void, o?: { timeout: number }) => void }).requestIdleCallback;
      const prepare = () => {
        if (frameCanvasRef.current) return;
        const c = document.createElement("canvas");
        c.width = p.frameWidth;
        c.height = p.frameHeight;
        const ctx = c.getContext("2d", { willReadFrequently: true })!;
        ctx.drawImage(img, 0, 0);
        frameCanvasRef.current = c;
        try {
          frameDataRef.current = ctx.getImageData(0, 0, p.frameWidth, p.frameHeight);
        } catch (e) {
          console.warn("冻结帧像素缓存失败(canvas 污染)", e);
          frameDataRef.current = null;
        }
        setCanvasReady(true);
      };
      if (idle) idle(prepare, { timeout: 800 });
      else window.setTimeout(prepare, 50);
    };
    img.onerror = () => {
      // 加载失败也回报(避免后端死等),并显示错误提示而不是黑屏
      setImgError("冻结帧加载失败");
      window.clearTimeout(guard);
      sendReady();
    };
    img.src = p.frameUrl;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // 握手监听 + 首次创建窗口补偿拉取
  useEffect(() => {
    const un = listen<CaptureStartPayload>("screenshot:start", ({ payload: p }) => startSession(p));
    // 窗口矩形/UI 元素后台探测完成,异步合并进 payload
    const unWin = listen<[number, DetectedWindow[]]>("screenshot:windows", ({ payload: [, wins] }) => {
      setPayload((prev) => (prev ? { ...prev, windows: wins } : prev));
    });
    if (monitorId !== null) {
      void screenshotPendingStart(monitorId).then((p) => { if (p) startSession(p); });
    }
    return () => {
      void un.then((f) => f());
      void unWin.then((f) => f());
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // 会话结束清理
  useEffect(() => {
    const un = listen<number>("screenshot:end", () => {
      setPayload(null);
      setPhase("idle");
      setSel(null);
      setTool(null);
      setScrollState(null);
      setHoverWindow(null);
    });
    return () => { void un.then((f) => f()); };
  }, []);

  // 快速截图闪烁反馈(无会话,独立显示);rect 物理坐标 → 逻辑坐标
  useEffect(() => {
    const un = listen<FlashPayload>("screenshot:flash", ({ payload: p }) => {
      if (p.monitorId !== monitorId) return;
      const s = p.scaleFactor || 1;
      setPhase("flash");
      setFlashRect(
        p.rect
          ? { x: p.rect[0] / s, y: p.rect[1] / s, width: p.rect[2] / s, height: p.rect[3] / s }
          : null,
      );
      window.setTimeout(() => {
        setPhase("idle");
        setFlashRect(null);
      }, p.durationMs);
    });
    return () => { void un.then((f) => f()); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [monitorId]);

  // 长截图进度
  useEffect(() => {
    const un = listen<ScrollProgress>("scroll:progress", ({ payload: p }) => {
      setScrollState(p);
      if (p.done) {
        setScrollState({ ...p });
        window.setTimeout(() => setPhase("idle"), 800);
      }
    });
    return () => { void un.then((f) => f()); };
  }, []);

  // ---- 鼠标:选区新建/移动/缩放 ----

  const rectFromDrag = (a: { x: number; y: number }, b: { x: number; y: number }): ScreenshotRect => ({
    x: Math.min(a.x, b.x),
    y: Math.min(a.y, b.y),
    width: Math.abs(a.x - b.x),
    height: Math.abs(a.y - b.y),
  });

  const onMouseDown = (e: React.MouseEvent) => {
    if (phase === "flash" || phase === "scrolling") return;
    if (e.button !== 0) return;
    const p = { x: e.clientX, y: e.clientY };
    // 取色模式:点击屏幕任意处取色并退出取色(优先级最高,否则被标注工具守卫拦截)
    if (picking) {
      const hex = pixelHexAt(p.x, p.y);
      if (hex) setPickedColor(hex);
      setPicking(false);
      e.stopPropagation();
      return;
    }
    if (tool) return; // 标注模式下 Konva 处理
    if (phase === "crosshair") {
      dragRef.current = { kind: "new", startX: p.x, startY: p.y };
      setSel({ x: p.x, y: p.y, width: 0, height: 0 });
      setPhase("selecting");
      return;
    }
    if (phase === "committed") {
      // 点击面板/操作条以外区域:自动收起二级面板,然后触发重新选区
      if (sidePanel !== "none") setSidePanel("none");
      // 标注画布区域内的事件由 Konva 处理,不能触发"重新选区"
      if (annHostRef.current?.contains(e.target as Node)) return;
      // 空白处按下 → 重新开始选择;边框带/手柄由各自元素处理(冒泡 stopPropagation)
      dragRef.current = { kind: "new", startX: p.x, startY: p.y };
      setSel({ x: p.x, y: p.y, width: 0, height: 0 });
      setPhase("selecting");
    }
  };

  const onMouseMove = (e: React.MouseEvent) => {
    const p = { x: e.clientX, y: e.clientY };
    setHover(p);
    // 放大镜重绘
    drawMagnifier(p);

    // 悬停高亮:实时查询窗口(WindowFromPoint,O(1)),节流 60ms
    if (payload && (phase === "crosshair" || phase === "selecting")) {
      const now = performance.now();
      if (now - lastProbeRef.current > 60) {
        lastProbeRef.current = now;
        const scale = payload.scaleFactor;
        const vx = Math.round(payload.monitorX + p.x * scale);
        const vy = Math.round(payload.monitorY + p.y * scale);
        void invoke<DetectedWindow | null>("window_at_point", { x: vx, y: vy })
          .then((w) => {
            setHoverWindow(
              w
                ? {
                    x: (w.x - payload.monitorX) / scale,
                    y: (w.y - payload.monitorY) / scale,
                    width: w.width / scale,
                    height: w.height / scale,
                  }
                : null,
            );
            setHoverWindowInfo(w);
          })
          .catch(() => {});
      }
    } else {
      setHoverWindow(null);
      setHoverWindowInfo(null);
    }

    const drag = dragRef.current;
    if (drag.kind === "new") {
      setSel(rectFromDrag({ x: drag.startX, y: drag.startY }, p));
    } else if (drag.kind === "move") {
      const dx = p.x - drag.startX;
      const dy = p.y - drag.startY;
      setSel(clampRect({
        x: drag.orig.x + dx,
        y: drag.orig.y + dy,
        width: drag.orig.width,
        height: drag.orig.height,
      }, logicalSize));
    } else if (drag.kind === "resize") {
      setSel(resizeRect(drag, p, logicalSize));
    }
  };

  const onMouseUp = () => {
    const drag = dragRef.current;
    if (drag.kind === "none") return;
    dragRef.current = { kind: "none" };
    if (phase === "selecting" && sel) {
      if (sel.width < MIN_SELECT || sel.height < MIN_SELECT) {
        setSel(null);
        setPhase("crosshair");
        return;
      }
      setPhase("committed");
    }
  };

  // 点击自动选窗(十字态,悬停窗口存在且点击位置在窗内)
  const onClickForWindow = useCallback((e: React.MouseEvent) => {
    if (phase !== "crosshair" || tool) return;
    const w = hoverWindow;
    if (!w || e.button !== 0) return;
    e.stopPropagation();
    setSel(w);
    setPhase("committed");
  }, [phase, tool, hoverWindow]);

  // ---- 键盘 ----
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (phase === "flash" || phase === "scrolling") return;
      if (e.key === "Escape") {
        e.preventDefault();
        // 取色模式下 Esc 先退出取色,不取消会话
        if (picking) {
          setPicking(false);
          return;
        }
        if (phase === "committed") {
          setPhase("crosshair");
          setSel(null);
          setTool(null);
          annEditor?.destroy();
          setAnnEditor(null);
        } else {
          void screenshotCancel();
        }
      } else if (e.key === "Enter" && phase === "committed" && sel) {
        e.preventDefault();
        void doCopy();
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "c" && phase === "committed" && sel) {
        e.preventDefault();
        void doCopy();
      } else if ((e.key === "Delete" || e.key === "Backspace") && tool === "select" && annEditor) {
        annEditor.deleteSelected();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, sel, annEditor, tool, picking]);

  // ---- 放大镜 ----
  const drawMagnifier = (p: { x: number; y: number }) => {
    const canvas = magCanvasRef.current;
    const frame = frameImgRef.current;
    const scale = payload?.scaleFactor ?? 1;
    if (!canvas || !frame || phase === "idle") return;
    const ctx = canvas.getContext("2d")!;
    const srcSize = MAG_SIZE / MAG_ZOOM;
    const sx = p.x * scale - srcSize / 2;
    const sy = p.y * scale - srcSize / 2;
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, MAG_SIZE, MAG_SIZE);
    ctx.drawImage(frame, sx, sy, srcSize, srcSize, 0, 0, MAG_SIZE, MAG_SIZE);
  };

  /** 光标处像素颜色(hex) */
  const pixelHexAt = (x: number, y: number): string | null => {
    const data = frameDataRef.current;
    const scale = payload?.scaleFactor ?? 1;
    if (!data) return null;
    const px = Math.min(data.width - 1, Math.max(0, Math.round(x * scale)));
    const py = Math.min(data.height - 1, Math.max(0, Math.round(y * scale)));
    const i = (py * data.width + px) * 4;
    const hex = (v: number) => v.toString(16).padStart(2, "0").toUpperCase();
    return `#${hex(data.data[i])}${hex(data.data[i + 1])}${hex(data.data[i + 2])}`;
  };

  // ---- 兜底 Esc(仅异常态:idle 无 payload 时正常交互层不存在) ----
  // 正常 Esc 语义由下方主监听处理;此处防止状态异常时全屏窗吞输入无路可退。
  useEffect(() => {
    if (phase !== "idle") return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        void screenshotCancel();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  // ---- 标注初始化 ----
  // flashot 设计:选区确认后标注工具条默认展开,编辑器常驻(tool=select 默认),
  // 选区内直接可画;移动选区走边框带,resize 走手柄。
  const selRef = useRef<ScreenshotRect | null>(null);
  useEffect(() => { selRef.current = sel; }, [sel]);

  const annConfigRef = useRef(annConfig);
  useEffect(() => { annConfigRef.current = annConfig; }, [annConfig]);

  useEffect(() => {
    if (phase !== "committed") return;
    if (annHostRef.current && !annEditor) {
      const frame = frameCanvasRef.current;
      const sel0 = selRef.current;
      const scale = payload?.scaleFactor ?? 1;
      // 像素缓存懒加载中 → 等 canvasReady 变化后重跑本 effect
      if (!frame || !sel0) return;
      // 裁剪选区物理底图(马赛克/模糊/放大镜的像素来源)
      const cc = document.createElement("canvas");
      cc.width = Math.max(1, Math.round(sel0.width * scale));
      cc.height = Math.max(1, Math.round(sel0.height * scale));
      const cctx = cc.getContext("2d")!;
      cctx.drawImage(
        frame,
        Math.round(sel0.x * scale), Math.round(sel0.y * scale), cc.width, cc.height,
        0, 0, cc.width, cc.height,
      );
      const ed = new AnnotationEditor(annHostRef.current, cc, sel0.width, sel0.height, scale);
      ed.setConfig({ ...annConfigRef.current });
      ed.setTool("select");
      setAnnEditor(ed);
      setTool("select");
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, canvasReady]);

  // 标注样式随属性面板变化实时应用
  useEffect(() => {
    annEditor?.setConfig(annConfig);
  }, [annConfig, annEditor]);

  // 离开 committed(重选/滚动/取消)时销毁编辑器,防止容器 DOM 卸载后残留旧实例
  useEffect(() => {
    if (phase !== "committed" && annEditor) {
      annEditor.destroy();
      setAnnEditor(null);
      setTool(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  const exportAnnotation = (): string | null => {
    if (!annEditor || !annEditor.hasShapes() || !sel || !payload) return null;
    return annEditor.toDataURL(payload.scaleFactor);
  };

  const finish = () => {
    annEditor?.destroy();
    setAnnEditor(null);
    setTool(null);
  };

  const doCopy = async () => {
    if (!sel || monitorId === null) return;
    try {
      await screenshotCopy(monitorId, sel, exportAnnotation(), cornerRadius);
    } finally {
      finish();
    }
  };

  const doSave = async () => {
    if (!sel || monitorId === null) return;
    try {
      await screenshotSave(monitorId, sel, exportAnnotation(), cornerRadius);
    } finally {
      finish();
    }
  };

  const doPin = async () => {
    if (!sel || monitorId === null) return;
    try {
      console.info("[贴图] 调用 screenshot_pin", { monitorId, sel, cornerRadius });
      await screenshotPin(monitorId, sel, exportAnnotation(), cornerRadius);
      console.info("[贴图] 调用成功");
    } catch (e) {
      // 关键:不再静默 —— 失败时在屏幕上提示并打日志,避免"点了没反应"无从排查
      console.error("[贴图] 失败", e);
      setImgError(`贴图失败: ${String(e)}`);
    } finally {
      finish();
    }
  };

  const doScroll = async () => {
    if (!sel || monitorId === null || !payload) return;
    setPhase("scrolling");
    setScrollState({ rounds: 0, height: Math.round(sel.height * payload.scaleFactor), done: false, aborted: false });
    try {
      await scrollCaptureStart(monitorId, sel);
    } catch (e) {
      setPhase("committed");
      console.error("长截图启动失败", e);
    }
  };

  const startPicking = () => {
    setPickedColor(null);
    setPicking(true);
  };

  if (phase === "idle" || (!payload && phase !== "flash")) {
    // 兜底交互层:绝不能渲染空内容 —— 全屏置顶窗若无任何交互层,
    // 会吞掉整个系统的鼠标输入(表现为"系统卡死")。点击/Esc 均可取消。
    return (
      <div
        style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.35)", cursor: "pointer" }}
        onClick={() => void screenshotCancel()}
        onContextMenu={(e) => { e.preventDefault(); void screenshotCancel(); }}
      >
        <div style={{
          position: "absolute", top: "45%", left: "50%", transform: "translate(-50%,-50%)",
          background: "rgba(24,26,32,0.95)", color: "#e5e7eb", fontSize: 14,
          padding: "12px 24px", borderRadius: 10, textAlign: "center",
        }}>
          截图初始化中…<br />
          <span style={{ fontSize: 12, color: "#9ca3af" }}>点击任意处或按 Esc 取消;再次按 {shotHotkey} 也可强制取消</span>
        </div>
      </div>
    );
  }

  const committed = phase === "committed" && sel && sel.width >= MIN_SELECT;

  return (
    <div
      style={{
        position: "fixed", inset: 0, overflow: "hidden",
        cursor: picking ? "crosshair" : phase === "committed" && !tool ? "default" : phase === "flash" ? "default" : "crosshair",
        background: "transparent",
      }}
      onMouseDown={onMouseDown}
      onMouseMove={onMouseMove}
      onMouseUp={onMouseUp}
      onClick={onClickForWindow}
      onContextMenu={(e) => {
        e.preventDefault();
        if (phase !== "flash" && phase !== "scrolling") void screenshotCancel();
      }}
    >
      {/* 冻结屏(滚动 HUD 模式隐藏,露出真实桌面) */}
      {payload && phase !== "scrolling" && (
        <img
          src={payload.frameUrl}
          draggable={false}
          style={{ position: "absolute", inset: 0, width: "100%", height: "100%", objectFit: "fill", pointerEvents: "none", userSelect: "none" }}
        />
      )}
      {imgError && (
        <div style={{
          position: "absolute", top: "40%", left: "50%", transform: "translate(-50%,-50%)",
          background: "rgba(153,27,27,0.9)", color: "#fff", padding: "10px 20px",
          borderRadius: 8, fontSize: 14, pointerEvents: "none",
        }}>
          {imgError} — 请重启应用后重试(查看日志 nexclip.log)
        </div>
      )}

      {/* 快速截图闪烁边框 */}
      {phase === "flash" && (
        <div style={{ position: "absolute", inset: 0, pointerEvents: "none" }}>
          <div style={{
            position: "absolute",
            left: flashRect?.x ?? 0, top: flashRect?.y ?? 0,
            width: flashRect?.width ?? "100%", height: flashRect?.height ?? "100%",
            border: "3px solid #2563eb", background: "rgba(37,99,235,0.12)",
            boxShadow: "0 0 0 10000px rgba(0,0,0,0.08)",
          }} />
        </div>
      )}

      {/* 长截图 HUD(点击穿透已由后端设置) */}
      {phase === "scrolling" && (
        <div style={{
          position: "absolute", top: 12, left: "50%", transform: "translateX(-50%)",
          background: "rgba(30,32,38,0.95)", color: "#e5e7eb", fontSize: 13,
          padding: "8px 16px", borderRadius: 10, boxShadow: "0 4px 16px rgba(0,0,0,0.4)",
          pointerEvents: "none", display: "flex", gap: 12, alignItems: "center",
        }}>
          <span>📜 长截图中… {scrollState?.height ?? 0}px</span>
          <span style={{ color: "#9ca3af" }}>再次按 {shotHotkey} 结束并复制</span>
          {scrollState?.done && (
            <span style={{ color: scrollState.aborted ? "#f87171" : "#4ade80" }}>
              {scrollState.aborted ? "已中止" : "完成 ✓"}
            </span>
          )}
        </div>
      )}

      {/* 悬停高亮:UI 元素(细框) 或整窗(粗框) */}
      {hoverWindow && payload && phase !== "flash" && phase !== "scrolling" && !tool && (
        <div style={{
          position: "absolute", left: hoverWindow.x, top: hoverWindow.y,
          width: hoverWindow.width, height: hoverWindow.height,
          border: "2px solid #60a5fa",
          pointerEvents: "none",
        }}>
          {hoverWindowInfo && (
            <div style={{
              position: "absolute", top: -24, left: 0, background: "rgba(37,99,235,0.9)",
              color: "#fff", fontSize: 12, padding: "2px 8px", borderRadius: 4, whiteSpace: "nowrap",
            }}>
              {hoverWindowInfo.app || "未知应用"}
              {hoverWindowInfo.title ? ` — ${hoverWindowInfo.title.slice(0, 40)}` : ""}
            </div>
          )}
        </div>
      )}

      {/* 区外变暗 */}
      {sel && (phase === "selecting" || phase === "committed") && (
        <DimMask rect={sel} width={logicalSize.w} height={logicalSize.h} radius={cornerRadius} />
      )}

      {/* 选区边框 + 8 手柄 + 边框带拖动热区(内部让位给标注画布) */}
      {committed && (
        <>
          <div style={{
            position: "absolute", left: sel!.x, top: sel!.y, width: sel!.width, height: sel!.height,
            border: "1.5px solid #2563eb", pointerEvents: "none",
            // 圆角与预览保持一致
            borderRadius: Math.min(cornerRadius, sel!.width / 2, sel!.height / 2),
          }} />
          {/* 边框带:四条 8px 热区贴边框内侧,拖动移动选区 */}
          {([
            { key: "n", left: sel!.x + 10, top: sel!.y, width: sel!.width - 20, height: 8 },
            { key: "s", left: sel!.x + 10, top: sel!.y + sel!.height - 8, width: sel!.width - 20, height: 8 },
            { key: "w", left: sel!.x, top: sel!.y + 10, width: 8, height: sel!.height - 20 },
            { key: "e", left: sel!.x + sel!.width - 8, top: sel!.y + 10, width: 8, height: sel!.height - 20 },
          ]).map((b) => (
            <div
              key={b.key}
              style={{ position: "absolute", left: b.left, top: b.top, width: b.width, height: b.height, cursor: "move", zIndex: 16 }}
              onMouseDown={(e) => {
                if (e.button !== 0 || !sel) return;
                e.stopPropagation();
                dragRef.current = { kind: "move", startX: e.clientX, startY: e.clientY, orig: sel };
              }}
            />
          ))}
          {HANDLES.map((h) => (
            <div
              key={h.key}
              style={{
                position: "absolute",
                left: handlePos(sel, h.key).x - 5, top: handlePos(sel, h.key).y - 5,
                width: 10, height: 10, background: "#fff", border: "1.5px solid #2563eb",
                borderRadius: 2, cursor: h.cursor, zIndex: 17,
              }}
              onMouseDown={(e) => {
                if (e.button !== 0 || !sel) return;
                e.stopPropagation();
                dragRef.current = { kind: "resize", startX: e.clientX, startY: e.clientY, orig: sel, handle: h.key };
              }}
            />
          ))}
        </>
      )}

      {/* 尺寸徽章(选区左上角,常驻;拖选时显示在选区上方) */}
      {sel && (phase === "selecting" || phase === "committed") && sel.width > 0 && (
        <div style={{
          position: "absolute",
          left: sel.x,
          top: phase === "selecting" ? Math.max(0, sel.y - 28) : sel.y - 1,
          transform: phase === "selecting" ? undefined : "translateY(-100%)",
          background: "rgba(37,99,235,0.92)", color: "#fff", fontSize: 12,
          fontFamily: "ui-monospace, monospace",
          padding: "2px 8px", borderRadius: 5, pointerEvents: "none",
          whiteSpace: "nowrap", zIndex: 25,
        }}>
          {Math.round(sel.width * (payload?.scaleFactor ?? 1))} × {Math.round(sel.height * (payload?.scaleFactor ?? 1))}
        </div>
      )}

      {/* 取色模式/取色结果提示 */}
      {(picking || pickedColor) && committed && (
        <div style={{
          position: "absolute", left: sel!.x, top: sel!.y - 30,
          display: "flex", alignItems: "center", gap: 6,
          background: "rgba(24,26,32,0.95)", color: "#e5e7eb", fontSize: 12,
          padding: "3px 10px", borderRadius: 6, pointerEvents: "none", zIndex: 25,
        }}>
          {picking ? "点击屏幕任意位置取色" : (
            <>
              <span style={{ display: "inline-block", width: 12, height: 12, borderRadius: 3, background: pickedColor!, border: "1px solid #6b7280" }} />
              {pickedColor}
            </>
          )}
        </div>
      )}

      {/* 放大镜(十字/拖选态,悬停窗口时不显示) */}
      {(phase === "crosshair" || phase === "selecting" || picking) && hover && !hoverWindow && (
        <Magnifier
          canvasRef={magCanvasRef}
          x={hover.x}
          y={hover.y}
          boundW={logicalSize.w}
          hex={pixelHexAt(hover.x, hover.y)}
        />
      )}

      {/* 标注画布(选区内,确认后常驻) */}
      {committed && (
        <div
          ref={annHostRef}
          style={{ position: "absolute", left: sel!.x, top: sel!.y, width: sel!.width, height: sel!.height, cursor: tool === "select" ? "default" : "crosshair" }}
        />
      )}

      {/* 标注工具条(选区下方,确认后默认展开;属性面板随工具) */}
      {committed && (
        <div style={{ position: "absolute", left: sel!.x, top: sel!.y + sel!.height + 8, zIndex: 20 }}>
          <AnnotationToolbar
            editor={annEditor}
            tool={tool ?? "select"}
            onToolChange={setTool}
            config={annConfig}
            onConfigChange={(p) => setAnnConfig((prev) => ({ ...prev, ...p }))}
          />
        </div>
      )}

      {/* 操作条(选区右侧竖排,flashot 顺序:圆角/调整/取色 ┃ 复制/贴图/保存/长图 ┃ 关闭) */}
      {committed && (
        <ActionBar
          x={sel!.x + sel!.width + 8}
          y={sel!.y}
          flipLeft={sel!.x + sel!.width + 56 > logicalSize.w}
          panel={sidePanel}
          onPanel={(p) => setSidePanel((cur) => (cur === p ? "none" : p))}
          cornerRadius={cornerRadius}
          onCornerRadiusChange={setCornerRadius}
          onCopy={() => void doCopy()}
          onPin={() => void doPin()}
          onSave={() => void doSave()}
          onScroll={() => void doScroll()}
          onPick={startPicking}
          picking={picking}
          onCancel={() => void screenshotCancel()}
        />
      )}
    </div>
  );
}

/** 选区右侧竖排操作条:圆角/取色 ┃ 复制/贴图/保存/长图 ┃ 关闭(侧面板弹在左侧) */
function ActionBar(props: {
  x: number; y: number; flipLeft: boolean;
  panel: "none" | "radius";
  onPanel: (p: "none" | "radius") => void;
  cornerRadius: number;
  onCornerRadiusChange: (r: number) => void;
  onCopy: () => void; onPin: () => void; onSave: () => void; onScroll: () => void;
  onPick: () => void; picking: boolean; onCancel: () => void;
}) {
  const { x, y, flipLeft, panel } = props;
  return (
    // 二级卡片出现在操作条右侧(flipLeft 时翻到左侧)
    <div
      style={{
        position: "absolute", left: flipLeft ? x - 50 : x, top: y, zIndex: 30,
        display: "flex", flexDirection: flipLeft ? "row-reverse" : "row",
        alignItems: "flex-start", gap: 6,
      }}
      onMouseDown={(e) => e.stopPropagation()} // 面板/按钮点击不冒泡(滑条不触发重选)
    >
      <div
        style={{
          display: "flex", flexDirection: "column", gap: 4,
          width: "fit-content",
          background: "rgba(30,30,30,0.85)", borderRadius: 10, padding: 5,
          border: "1px solid rgba(255,255,255,0.1)",
          boxShadow: "0 4px 24px rgba(0,0,0,0.4)", userSelect: "none",
        }}
        onContextMenu={(e) => e.preventDefault()}
      >
        <ActionIcon name="radius" tooltip="圆角" active={panel === "radius"} onClick={() => props.onPanel("radius")} />
        <ActionIcon name="pipette" tooltip="取色" active={props.picking} onClick={props.onPick} />
        <ActionIcon name="scrollShot" tooltip="长截图(滚动拼接,结果自动复制)" onClick={props.onScroll} />
        <DividerH />
        <ActionIcon name="copy" tooltip="复制 (Enter / Ctrl+C)" iconColor="#60a5fa" onClick={props.onCopy} />
        <ActionIcon name="pin" tooltip="贴图置顶" onClick={props.onPin} />
        <ActionIcon name="save" tooltip="另存为 (Ctrl+S)" iconColor="#4ade80" onClick={props.onSave} />
        <DividerH />
        <ActionIcon name="x" tooltip="取消 (Esc)" iconColor="#f87171" onClick={props.onCancel} />
      </div>
      {panel === "radius" && (
        <RadiusPanel value={props.cornerRadius} onChange={props.onCornerRadiusChange} />
      )}
    </div>
  );
}

const DividerH = () => <div style={{ height: 1, background: "rgba(255,255,255,0.15)", margin: "2px 4px" }} />;

function RadiusPanel({ value, onChange }: { value: number; onChange: (r: number) => void }) {
  return (
    <div style={sidePanelStyle}>
      <span style={sideRowStyle as React.CSSProperties}>圆角</span>
      <input
        type="range" min={0} max={64} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        style={{ width: 110 }}
      />
      <span style={{ width: 30, textAlign: "right", fontVariantNumeric: "tabular-nums" }}>{value}px</span>
    </div>
  );
}

const sidePanelStyle: React.CSSProperties = {
  display: "flex", alignItems: "center", gap: 10, padding: "8px 12px",
  borderRadius: 10, background: "rgba(30,30,30,0.92)",
  border: "1px solid rgba(255,255,255,0.1)",
  color: "#e5e7eb", fontSize: 12, boxShadow: "0 4px 16px rgba(0,0,0,0.4)",
  flexWrap: "wrap", width: "max-content", maxWidth: 260,
};
const sideRowStyle: React.CSSProperties = { display: "flex", alignItems: "center", gap: 6 };

function ActionIcon({
  name, tooltip, onClick, iconColor, active,
}: {
  name: Parameters<typeof Icon>[0]["name"];
  tooltip: string; onClick: () => void; iconColor?: string; active?: boolean;
}) {
  return (
    <Tooltip title={tooltip} placement="right" mouseEnterDelay={0.15}>
      <button
        onClick={(e) => { e.stopPropagation(); onClick(); }}
        onMouseDown={(e) => e.stopPropagation()}
        style={{
          width: 32, height: 32, borderRadius: 7, border: "none", cursor: "pointer",
          // flashot 风格:仅激活态有填充;彩色操作按钮只给图标着色,无背景填充
          background: active ? "#2563eb" : "transparent",
          color: active ? "#fff" : iconColor ?? "rgba(255,255,255,0.78)",
          display: "flex", alignItems: "center", justifyContent: "center", padding: 0,
        }}
        onMouseEnter={(e) => { if (!active) e.currentTarget.style.background = "rgba(255,255,255,0.1)"; }}
        onMouseLeave={(e) => { if (!active) e.currentTarget.style.background = "transparent"; }}
      >
        <Icon name={name} size={16} />
      </button>
    </Tooltip>
  );
}

/// 区外变暗遮罩:单个 path + fill-rule=evenodd 挖出圆角选区。
/// 圆角外的四个小角也在此 path 内(即"全屏减去圆角矩形"),因此角部同样变暗。
function DimMask({
  rect, width, height, radius = 0,
}: {
  rect: ScreenshotRect;
  width: number;
  height: number;
  /** 圆角半径(CSS 像素);0 = 直角 */
  radius?: number;
}) {
  const r = Math.max(0, Math.min(radius, rect.width / 2, rect.height / 2));
  const { x, y, width: w, height: h } = rect;
  const rr = r;
  // 圆角矩形子路径(顺时针)
  const rounded = [
    `M ${x + rr} ${y}`,
    `H ${x + w - rr}`,
    `A ${rr} ${rr} 0 0 1 ${x + w} ${y + rr}`,
    `V ${y + h - rr}`,
    `A ${rr} ${rr} 0 0 1 ${x + w - rr} ${y + h}`,
    `H ${x + rr}`,
    `A ${rr} ${rr} 0 0 1 ${x} ${y + h - rr}`,
    `V ${y + rr}`,
    `A ${rr} ${rr} 0 0 1 ${x + rr} ${y}`,
    "Z",
  ].join(" ");
  // 外框(逆时针)+ 内圆角矩形(顺时针)→ evenodd 得到"外框减内框"
  const outer = `M 0 0 H ${width} V ${height} H 0 Z`;
  return (
    <svg
      width={width}
      height={height}
      style={{ position: "absolute", left: 0, top: 0, pointerEvents: "none" }}
    >
      <path
        d={`${outer} ${rounded}`}
        fill="rgba(0,0,0,0.45)"
        fillRule="evenodd"
      />
    </svg>
  );
}

/** 放大镜 + 取色提示 */
function Magnifier({
  canvasRef, x, y, boundW, hex,
}: {
  canvasRef: React.MutableRefObject<HTMLCanvasElement | null>;
  x: number; y: number; boundW: number; hex: string | null;
}) {
  const size = MAG_SIZE + 26;
  const offset = 28;
  // 靠右时翻转到左侧
  const flip = x + size + offset > boundW;
  const left = flip ? x - size - offset : x + offset;
  return (
    <div
      style={{
        position: "absolute", left, top: y - size / 2, pointerEvents: "none",
        background: "rgba(30,32,38,0.95)", borderRadius: 8, padding: 6,
        boxShadow: "0 4px 16px rgba(0,0,0,0.4)", zIndex: 30,
      }}
    >
      <canvas
        ref={canvasRef}
        width={MAG_SIZE}
        height={MAG_SIZE}
        style={{ display: "block", borderRadius: 4, border: "1px solid #4b5563" }}
      />
      <div style={{ color: "#e5e7eb", fontSize: 12, marginTop: 4, textAlign: "center" }}>
        {hex ?? "—"}
        {hex && (
          <span style={{ display: "inline-block", width: 12, height: 12, borderRadius: 3, background: hex, marginLeft: 6, verticalAlign: "middle", border: "1px solid #6b7280" }} />
        )}
      </div>
    </div>
  );
}

// ---- 手柄几何 ----

type HandleKey = "nw" | "n" | "ne" | "e" | "se" | "s" | "sw" | "w";

const HANDLES: { key: HandleKey; cursor: string }[] = [
  { key: "nw", cursor: "nwse-resize" },
  { key: "n", cursor: "ns-resize" },
  { key: "ne", cursor: "nesw-resize" },
  { key: "e", cursor: "ew-resize" },
  { key: "se", cursor: "nwse-resize" },
  { key: "s", cursor: "ns-resize" },
  { key: "sw", cursor: "nesw-resize" },
  { key: "w", cursor: "ew-resize" },
];

function handlePos(r: ScreenshotRect, key: HandleKey): { x: number; y: number } {
  const cx = r.x + r.width / 2;
  const cy = r.y + r.height / 2;
  const right = r.x + r.width;
  const bottom = r.y + r.height;
  switch (key) {
    case "nw": return { x: r.x, y: r.y };
    case "n": return { x: cx, y: r.y };
    case "ne": return { x: right, y: r.y };
    case "e": return { x: right, y: cy };
    case "se": return { x: right, y: bottom };
    case "s": return { x: cx, y: bottom };
    case "sw": return { x: r.x, y: bottom };
    case "w": return { x: r.x, y: cy };
  }
}

function clampRect(r: ScreenshotRect, bound: { w: number; h: number }): ScreenshotRect {
  const maxW = Math.max(MIN_SELECT, bound.w);
  const maxH = Math.max(MIN_SELECT, bound.h);
  return {
    x: Math.min(Math.max(0, r.x), maxW - MIN_SELECT),
    y: Math.min(Math.max(0, r.y), maxH - MIN_SELECT),
    width: Math.min(r.width, maxW),
    height: Math.min(r.height, maxH),
  };
}

/** 8 向缩放:按拖动增量调整矩形(以原矩形为基准) */
function resizeRect(
  drag: { startX: number; startY: number; orig: ScreenshotRect; handle: string },
  p: { x: number; y: number },
  bound: { w: number; h: number },
): ScreenshotRect {
  const dx = p.x - drag.startX;
  const dy = p.y - drag.startY;
  const o = drag.orig;
  let x1 = o.x, y1 = o.y, x2 = o.x + o.width, y2 = o.y + o.height;
  const h = drag.handle;
  if (h.includes("w")) x1 = o.x + dx;
  if (h.includes("e")) x2 = o.x + o.width + dx;
  if (h.includes("n")) y1 = o.y + dy;
  if (h.includes("s")) y2 = o.y + o.height + dy;
  // 防翻转:边最小 4px
  if (x2 - x1 < MIN_SELECT) {
    if (h.includes("w")) x1 = x2 - MIN_SELECT; else x2 = x1 + MIN_SELECT;
  }
  if (y2 - y1 < MIN_SELECT) {
    if (h.includes("n")) y1 = y2 - MIN_SELECT; else y2 = y1 + MIN_SELECT;
  }
  const r = { x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
  // clamp 到屏幕内
  const cx = Math.min(Math.max(0, r.x), bound.w - MIN_SELECT);
  const cy = Math.min(Math.max(0, r.y), bound.h - MIN_SELECT);
  const shiftX = r.x - cx;
  const shiftY = r.y - cy;
  return {
    x: cx, y: cy,
    width: Math.min(r.width + shiftX, bound.w - cx),
    height: Math.min(r.height + shiftY, bound.h - cy),
  };
}
