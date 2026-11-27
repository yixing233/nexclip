// 贴图工具条窗口(独立窗口,label = pin-tools-{id})。
// 参考 flashot src/pin/PinControls.tsx:40px 宽竖排、32x32 图标按钮、颜色语义。
//
// 窗口实际宽 160px = 左侧 40px 可见竖条 + 右侧 120px 透明区。
// 这样做的原因:WebView 窗口内的内容无法渲染到窗口边界之外,
// antd/portal tooltip 在 40px 窗口里必然被裁剪。加宽透明区后,
// tooltip 自绘在透明区内即可完整显示,视觉上仍是"竖条 + 悬浮气泡"。
import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Icon, type IconName } from "../icons";

const SCALES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3];
/// 可见竖条宽度(与 Rust TOOLS_W 中的左半部分对应)
const RAIL_W = 40;
const RAIL_H = 240;

function pinIdFromToolsLabel(label: string): string | null {
  const m = /^pin-tools-(.+)$/.exec(label);
  return m ? m[1] : null;
}

export default function PinTools() {
  const pinId = pinIdFromToolsLabel(getCurrentWindow().label);
  const [scaleMenu, setScaleMenu] = useState(false);
  const [copied, setCopied] = useState(false);
  const [tip, setTip] = useState<{ text: string; y: number } | null>(null);
  const [editing, setEditing] = useState(false);
  const [adjustOpen, setAdjustOpen] = useState(false);
  const [adj, setAdj] = useState({ grayscale: false, brightness: 0, contrast: 0, saturation: 0 });
  const copiedTimer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(copiedTimer.current), []);

  // 编辑态回调(贴图窗口退出编辑时同步按钮状态)
  useEffect(() => {
    const un = listen<boolean>("pin:edit-state", ({ payload }) => setEditing(payload));
    return () => { void un.then((f) => f()); };
  }, []);

  const toggleEdit = () => {
    const next = !editing;
    setEditing(next);
    // 编辑态下工具条保持可见(否则鼠标一动就找不到"完成"按钮)
    void invoke("pin_edit_state", { pinId, editing: next }).catch(() => {});
  };

  // 缩放菜单展开时窗口变高(菜单在竖条内部展开)
  useEffect(() => {
    void (async () => {
      const win = getCurrentWindow();
      const sf = await win.scaleFactor();
      const height = scaleMenu ? RAIL_H + SCALES.length * 26 + 8 : RAIL_H;
      void win
        .setSize(
          new (await import("@tauri-apps/api/dpi")).PhysicalSize(
            Math.round(160 * sf),
            Math.round(height * sf),
          ),
        )
        .catch(() => {});
    })();
  }, [scaleMenu]);

  if (!pinId) return null;

  const doCopy = async () => {
    try {
      await invoke("pin_copy", { pinId });
      setCopied(true);
      window.clearTimeout(copiedTimer.current);
      copiedTimer.current = window.setTimeout(() => setCopied(false), 1200);
    } catch {
      /* 忽略 */
    }
  };

  return (
    <div
      style={{ width: "100vw", height: "100vh", position: "relative", background: "transparent" }}
      onMouseDown={(e) => e.stopPropagation()}
      onWheel={(e) => e.stopPropagation()}
      onContextMenu={(e) => e.preventDefault()}
    >
      {/* 可见竖条(左上角 40px 宽) */}
      <div
        style={{
          position: "absolute", left: 0, top: 0, width: RAIL_W,
          display: "flex", flexDirection: "column", alignItems: "center",
          gap: 2, padding: "4px 0",
          borderRadius: 10, background: "rgb(30,30,30)",
          border: "1px solid rgba(255,255,255,0.1)",
          boxShadow: "0 4px 24px rgba(0,0,0,0.4)",
          userSelect: "none",
        }}
      >
        <RailButton
          name="penLine" tooltip={editing ? "完成编辑 (Ctrl+Enter)" : "编辑 (E)"} active={editing}
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={toggleEdit}
        />
        <RailButton
          name="sliders" tooltip="图像调整" active={adjustOpen}
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={() => setAdjustOpen((v) => !v)}
        />
        <RailButton
          name="radius" tooltip="缩放" active={scaleMenu}
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={() => setScaleMenu((v) => !v)}
        />
        <RailButton
          name="x" tooltip="关闭 (Esc)" tone="danger"
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={() => void invoke("pin_close", { pinId })}
        />
        <RailButton
          name="save" tooltip="保存为… (Ctrl+S)" tone="primary"
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={() => void invoke("pin_save", { pinId }).catch(() => {})}
        />
        <RailButton
          name={copied ? "check" : "copy"} tooltip={copied ? "已复制" : "复制 (Ctrl+C)"}
          tone="success"
          onTip={(t, y) => setTip(t ? { text: t, y } : null)}
          onClick={() => void doCopy()}
        />

        {/* 缩放菜单:竖条内展开 */}
        {scaleMenu && (
          <div style={{ display: "flex", flexDirection: "column", gap: 2, width: "100%", padding: "2px 3px" }}>
            {SCALES.map((s) => (
              <button
                key={s}
                onClick={() => {
                  void invoke("pin_set_scale_abs", { pinId, percent: Math.round(s * 100) }).catch(() => {});
                  setScaleMenu(false);
                }}
                style={{
                  border: "none", background: "rgba(255,255,255,0.08)", color: "#e5e7eb",
                  fontSize: 10, padding: "4px 0", borderRadius: 4, cursor: "pointer",
                }}
                onMouseEnter={(e) => (e.currentTarget.style.background = "rgba(255,255,255,0.2)")}
                onMouseLeave={(e) => (e.currentTarget.style.background = "rgba(255,255,255,0.08)")}
              >
                {Math.round(s * 100)}%
              </button>
            ))}
          </div>
        )}
      </div>

      {/* 图像调整面板:竖条右侧展开(窗口已加宽到 160px) */}
      {adjustOpen && (
        <div
          style={{
            position: "absolute", left: RAIL_W + 6, top: 0, width: 108,
            background: "rgb(30,30,30)", borderRadius: 8, padding: 8,
            border: "1px solid rgba(255,255,255,0.1)",
            boxShadow: "0 4px 16px rgba(0,0,0,0.45)",
            display: "flex", flexDirection: "column", gap: 6,
            color: "#e5e7eb", fontSize: 11,
          }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <label style={{ display: "flex", alignItems: "center", gap: 4, cursor: "pointer" }}>
            <input
              type="checkbox" checked={adj.grayscale}
              onChange={(e) => setAdj((v) => ({ ...v, grayscale: e.target.checked }))}
            />
            灰度
          </label>
          <AdjSlider label="亮度" value={adj.brightness} onChange={(v) => setAdj((s) => ({ ...s, brightness: v }))} />
          <AdjSlider label="对比" value={adj.contrast} onChange={(v) => setAdj((s) => ({ ...s, contrast: v }))} />
          <AdjSlider label="饱和" value={adj.saturation} onChange={(v) => setAdj((s) => ({ ...s, saturation: v }))} />
          <div style={{ display: "flex", gap: 4 }}>
            <button
              onClick={() => {
                void invoke("pin_apply_adjustments", {
                  pinId,
                  grayscale: adj.grayscale,
                  brightness: adj.brightness,
                  contrast: adj.contrast,
                  saturation: adj.saturation,
                }).then(() => {
                  // 应用后重置面板值(已烘焙进原图)
                  setAdj({ grayscale: false, brightness: 0, contrast: 0, saturation: 0 });
                  setAdjustOpen(false);
                }).catch((e) => console.error("[贴图调整] 应用失败", e));
              }}
              style={panelBtn("#2563eb")}
            >
              应用
            </button>
            <button
              onClick={() => {
                setAdj({ grayscale: false, brightness: 0, contrast: 0, saturation: 0 });
                setAdjustOpen(false);
              }}
              style={panelBtn("#4b5563")}
            >
              取消
            </button>
          </div>
        </div>
      )}

      {/* 自绘 tooltip:渲染到右侧透明区,不受 40px 竖条限制 */}
      {tip && (
        <div
          style={{
            position: "absolute", left: RAIL_W + 6, top: tip.y,
            transform: "translateY(-50%)",
            padding: "4px 10px", borderRadius: 6,
            background: "rgba(38,38,44,0.96)", color: "#f0f0f5",
            fontSize: 12, whiteSpace: "nowrap",
            border: "1px solid rgba(255,255,255,0.12)",
            boxShadow: "0 4px 14px rgba(0,0,0,0.45)",
            pointerEvents: "none", zIndex: 100,
          }}
        >
          {tip.text}
        </div>
      )}
    </div>
  );
}

/// flashot 色语义
const TONE_COLOR = {  default: "rgba(255,255,255,0.78)",
  danger: "#f87171",
  primary: "#60a5fa",
  success: "#4ade80",
} as const;

function RailButton({
  name, tooltip, onClick, tone = "default", active, onTip,
}: {
  name: IconName;
  tooltip: string;
  onClick: () => void;
  tone?: keyof typeof TONE_COLOR;
  active?: boolean;
  onTip: (text: string | null, y: number) => void;
}) {
  return (
    <button
      onClick={(e) => { e.stopPropagation(); onClick(); }}
      onMouseEnter={(e) => {
        if (!active) e.currentTarget.style.background = "rgba(255,255,255,0.1)";
        const r = e.currentTarget.getBoundingClientRect();
        onTip(tooltip, r.top + r.height / 2);
      }}
      onMouseLeave={(e) => {
        if (!active) e.currentTarget.style.background = "transparent";
        onTip(null, 0);
      }}
      style={{
        width: 32, height: 32, borderRadius: 6, border: "none", cursor: "pointer",
        background: active ? "rgba(255,255,255,0.16)" : "transparent",
        color: TONE_COLOR[tone],
        display: "flex", alignItems: "center", justifyContent: "center", padding: 0,
        flexShrink: 0,
      }}
    >
      <Icon name={name} size={16} />
    </button>
  );
}

/// 调整滑条(-100..100)
function AdjSlider({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      <span style={{ display: "flex", justifyContent: "space-between" }}>
        <span>{label}</span>
        <span style={{ fontVariantNumeric: "tabular-nums", opacity: 0.75 }}>{value}</span>
      </span>
      <input
        type="range" min={-100} max={100} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        style={{ width: "100%" }}
      />
    </label>
  );
}

function panelBtn(bg: string): React.CSSProperties {
  return {
    flex: 1, padding: "3px 0", border: "none", borderRadius: 5, cursor: "pointer",
    background: bg, color: "#fff", fontSize: 11,
  };
}
