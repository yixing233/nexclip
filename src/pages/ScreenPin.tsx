// 贴图窗口(图片 + 可选标注编辑层),label = pin-{id}。
// 编辑态由工具条窗口 pin-tools-{id} 通过 pin:edit 事件切换。
import { useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { pinIdFromLabel, pinImageUrl } from "../ipc/screenshot";
import { AnnotationEditor, DEFAULT_ANNOTATION_CONFIG, type AnnotationConfig, type Tool } from "../components/annotation/editor";
import AnnotationToolbar from "../components/annotation/Toolbar";

export default function ScreenPin() {
  const pinId = pinIdFromLabel(getCurrentWindow().label);
  const [src, setSrc] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [tool, setTool] = useState<Tool>("select");
  const [annConfig, setAnnConfig] = useState<AnnotationConfig>({ ...DEFAULT_ANNOTATION_CONFIG });
  const [editor, setEditor] = useState<AnnotationEditor | null>(null);
  const hostRef = useRef<HTMLDivElement | null>(null);
  const imgRef = useRef<HTMLImageElement | null>(null);
  /// 每次保存后递增,触发 <img> 重新加载(绕过浏览器缓存)
  const [rev, setRev] = useState(0);

  useEffect(() => {
    if (!pinId) return;
    setSrc(pinImageUrl(pinId));
  }, [pinId, rev]);

  // 编辑态开关(由工具条窗口控制)
  useEffect(() => {
    if (!pinId) return;
    const un = listen<{ pinId: string; editing: boolean }>("pin:edit", ({ payload }) => {
      if (payload.pinId !== pinId) return;
      setEditing(payload.editing);
    });
    // 图像调整等外部改动后刷新图片
    const un2 = listen("pin:refresh", () => setRev((n) => n + 1));
    return () => {
      void un.then((f) => f());
      void un2.then((f) => f());
    };
  }, [pinId]);

  // 滚轮缩放(编辑态下不缩放,避免与画布冲突)
  useEffect(() => {
    if (!pinId) return;
    const onWheel = (e: WheelEvent) => {
      if (editing) return;
      e.preventDefault();
      void invoke("pin_set_scale", { pinId, scale: e.deltaY < 0 ? 1.08 : 1 / 1.08 }).catch(() => {});
    };
    window.addEventListener("wheel", onWheel, { passive: false });
    return () => window.removeEventListener("wheel", onWheel);
  }, [pinId, editing]);

  // 快捷键
  useEffect(() => {
    if (!pinId) return;
    const onKey = (e: KeyboardEvent) => {
      if (editing) {
        if (e.key === "Escape") {
          e.preventDefault();
          void exitEdit(false);
        } else if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
          e.preventDefault();
          void exitEdit(true);
        }
        return;
      }
      if (e.key === "Escape") void invoke("pin_close", { pinId });
      else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "c") {
        e.preventDefault();
        void invoke("pin_copy", { pinId }).catch(() => {});
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        void invoke("pin_save", { pinId }).catch(() => {});
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pinId, editing, editor]);

  // 进入编辑:等图片就绪后创建 Konva 画布(尺寸=窗口 CSS 尺寸)
  useEffect(() => {
    if (!editing || !hostRef.current || editor) return;
    const img = imgRef.current;
    const host = hostRef.current;
    if (!img) return;
    const w = host.clientWidth || img.clientWidth;
    const h = host.clientHeight || img.clientHeight;
    if (w === 0 || h === 0) return;
    // AnnotationEditor 需要 canvas 底图(马赛克/模糊/放大镜的像素来源)
    const base = document.createElement("canvas");
    base.width = Math.round(w);
    base.height = Math.round(h);
    const ctx = base.getContext("2d")!;
    ctx.drawImage(img, 0, 0, base.width, base.height);
    const ed = new AnnotationEditor(host, base, w, h, 1);
    ed.setConfig({ ...annConfig });
    ed.setTool("select");
    setEditor(ed);
    setTool("select");
    // 载入历史标注:优先对象 JSON(可继续编辑),退化到 PNG 背景层
    void invoke<[string | null, string | null]>("pin_annotation_get", { pinId })
      .then(([png, json]) => {
        if (json) {
          ed.restore(json);
        } else if (png) {
          return ed.loadAnnotationImage(png);
        }
      })
      .catch((e) => console.warn("[贴图编辑] 加载历史标注失败", e));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editing]);

  // 样式随属性面板变化
  useEffect(() => {
    editor?.setConfig(annConfig);
  }, [annConfig, editor]);

  const exitEdit = async (save: boolean) => {
    if (save && editor) {
      const dataUrl = editor.hasShapes() ? editor.toDataURL(1) : null;
      const json = editor.hasShapes() ? editor.serialize() : null;
      try {
        await invoke("pin_annotation_save", { pinId, annotationPng: dataUrl, annotationJson: json });
        setRev((n) => n + 1);
      } catch (e) {
        console.error("[贴图编辑] 保存失败", e);
      }
    }
    editor?.destroy();
    setEditor(null);
    setEditing(false);
    void invoke("pin_edit_state", { pinId, editing: false }).catch(() => {});
  };

  if (!pinId) return null;

  return (
    <div
      style={{
        width: "100vw", height: "100vh", overflow: "hidden",
        position: "relative", background: "transparent",
        cursor: editing ? "default" : "move",
      }}
      data-tauri-drag-region={editing ? undefined : true}
      onDoubleClick={() => { if (!editing) void invoke("pin_close", { pinId }); }}
      onContextMenu={(e) => e.preventDefault()}
    >
      {src && (
        <img
          ref={imgRef}
          src={`${src}${rev > 0 ? `?r=${rev}` : ""}`}
          draggable={false}
          crossOrigin="anonymous"
          style={{
            width: "100%", height: "100%", objectFit: "fill",
            display: "block", userSelect: "none",
            pointerEvents: editing ? "none" : "none",
          }}
        />
      )}

      {/* 编辑:Konva 标注层覆盖整图 */}
      {editing && (
        <div
          ref={hostRef}
          style={{ position: "absolute", inset: 0, cursor: tool === "select" ? "default" : "crosshair" }}
        />
      )}

      {/* 编辑:标注工具条(贴图底部) */}
      {editing && (
        <div style={{ position: "absolute", left: 8, bottom: 8, zIndex: 20 }} onMouseDown={(e) => e.stopPropagation()}>
          <AnnotationToolbar
            editor={editor}
            tool={tool}
            onToolChange={setTool}
            config={annConfig}
            onConfigChange={(p) => setAnnConfig((prev) => ({ ...prev, ...p }))}
          />
          <div style={{ display: "flex", gap: 6, marginTop: 6 }}>
            <button
              onClick={() => void exitEdit(true)}
              style={editBtn("#2563eb")}
            >
              完成 (Ctrl+Enter)
            </button>
            <button
              onClick={() => void exitEdit(false)}
              style={editBtn("#4b5563")}
            >
              取消 (Esc)
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function editBtn(bg: string): React.CSSProperties {
  return {
    padding: "5px 14px", borderRadius: 8, border: "none", cursor: "pointer",
    fontSize: 13, fontWeight: 600, color: "#fff", background: bg,
    boxShadow: "0 2px 10px rgba(0,0,0,0.35)",
  };
}
