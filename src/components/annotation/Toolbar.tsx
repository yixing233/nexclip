// 标注工具条:严格对齐 flashot src/annotation/Toolbar.tsx。
// 13 工具(无 select 按钮,再点当前工具回选择态)+ 撤销/重做;
// 属性面板(PropertyPanel)随工具/选中图形显示在工具条下方:
// 色板(flashot 10 色) + 笔宽(1-20) + 工具专属模式。
import { useEffect, useRef, useState } from "react";
import { Tooltip } from "antd";
import { Icon, type IconName } from "../../icons";
import { AnnotationEditor, PRESET_COLORS, DEFAULT_CONFIG, type AnnotationConfig, type Tool } from "./editor";
import { enumLocalFonts, staticFontList, SYSTEM_FONT_VALUE, type FontEntry } from "./fonts";

/// flashot TOOLS 顺序与图标(图标为语义名,四库 MAP 提供)
const TOOLS: { id: Tool; icon: IconName | null; label: string }[] = [
  { id: "draw", icon: "draw", label: "画笔" },
  { id: "line", icon: "line", label: "直线" },
  { id: "arrow", icon: "arrowUpRight", label: "箭头" },
  { id: "rect", icon: "square", label: "矩形" },
  { id: "ellipse", icon: "circle", label: "椭圆" },
  { id: "text", icon: "type", label: "文字" },
  { id: "blur", icon: "blur", label: "模糊" },
  { id: "highlight", icon: "highlight", label: "高亮" },
  { id: "spotlight", icon: "spotlight", label: "聚光灯" },
  { id: "marker", icon: null, label: "标号" }, // 数字徽章图标
  { id: "magnifier", icon: "search" as IconName, label: "放大镜" },
  { id: "eraser", icon: "eraser", label: "橡皮" },
  { id: "measure", icon: "measure", label: "测量" },
];

interface Props {
  editor: AnnotationEditor | null;
  tool: Tool;
  onToolChange: (t: Tool) => void;
  config: AnnotationConfig;
  onConfigChange: (patch: Partial<AnnotationConfig>) => void;
}

export default function AnnotationToolbar({
  editor, tool, onToolChange, config, onConfigChange,
}: Props) {
  const [, force] = useState(0);
  const repaint = () => force((n) => n + 1);

  useEffect(() => {
    editor?.setTool(tool);
  }, [editor, tool]);

  // flashot handleToolClick:再点当前工具回 select
  const handleToolClick = (t: Tool) => {
    if (t === tool) {
      onToolChange("select");
      return;
    }
    onToolChange(t);
  };

  // flashot:橡皮也有属性(擦除模式);仅 select 无面板
  const showPanel = tool !== "select";

  return (
    <div
      style={{ display: "flex", flexDirection: "column", gap: 6, alignItems: "flex-start" }}
      onMouseDown={(e) => e.stopPropagation()} // 工具条点击不冒泡到选区逻辑
    >
      {/* flashot:属性面板在工具条下方 */}
      <div style={barStyle} onContextMenu={(e) => e.preventDefault()}>
        {TOOLS.map((t) => (
          <ToolButton
            key={t.id}
            icon={t.icon}
            markerNumber={t.id === "marker" ? 1 : undefined}
            label={t.label}
            active={tool === t.id}
            onClick={() => handleToolClick(t.id)}
          />
        ))}
        <Divider />
        <IconBtn name="undo" title="撤销 (Ctrl+Z)" disabled={!editor?.canUndo()} onClick={() => editor?.undo()} />
        <IconBtn name="redo" title="重做 (Ctrl+Shift+Z)" disabled={!editor?.canRedo()} onClick={() => editor?.redo()} />
      </div>
      {showPanel && (
        <PropertyPanel tool={tool} config={config} onChange={(p) => { onConfigChange(p); repaint(); }} />
      )}
    </div>
  );
}

// ---- 属性面板(flashot PropertyPanel 对齐) ----

function PropertyPanel({
  tool, config, onChange,
}: {
  tool: Tool;
  config: AnnotationConfig;
  onChange: (patch: Partial<AnnotationConfig>) => void;
}) {
  return (
    <div style={panelStyle} onContextMenu={(e) => e.preventDefault()}>
      {/* 色板:flashot 10 色(橡皮/模糊无颜色语义) */}
      {tool !== "eraser" && tool !== "blur" && (
        <>
          <div style={groupStyle}>
            {PRESET_COLORS.map((c) => (
              <button
                key={c}
                title={c}
                onClick={() => onChange({ color: c })}
                style={{
                  width: 16, height: 16, borderRadius: "50%", flexShrink: 0,
                  border: config.color === c ? "2px solid #fff" : "1px solid #4b5563",
                  background: c, cursor: "pointer", padding: 0,
                }}
              />
            ))}
          </div>
          <DividerV />
        </>
      )}
      {/* 笔宽 1-20(橡皮=擦除范围;模糊无粗细) */}
      {tool !== "blur" && (
        <div style={groupStyle}>
          <span style={labelStyle}>{tool === "eraser" ? "擦除范围" : "粗细"}</span>
          <input
            type="range" min={1} max={20} value={config.strokeWidth}
            onChange={(e) => onChange({ strokeWidth: Number(e.target.value) })}
            style={{ width: 90 }}
          />
          <span style={valueStyle}>{config.strokeWidth}</span>
        </div>
      )}

      {tool === "line" && (
        <>
          <DividerV />
          <Segment
            label="线形"
            options={[{ v: "straight", label: "直线" }, { v: "wavy", label: "波形" }]}
            value={config.lineShape}
            onChange={(v) => onChange({ lineShape: v as AnnotationConfig["lineShape"] })}
          />
          <Segment
            label="线型"
            options={[{ v: "solid", label: "实线" }, { v: "dotted", label: "点线" }, { v: "dashed", label: "虚线" }]}
            value={config.lineStyle}
            onChange={(v) => onChange({ lineStyle: v as AnnotationConfig["lineStyle"] })}
          />
        </>
      )}
      {tool === "arrow" && (
        <>
          <DividerV />
          <Segment
            label="箭头"
            options={[{ v: "v-shape", label: "细箭头" }, { v: "filled-triangle", label: "实心箭头" }]}
            value={config.arrowStyle}
            onChange={(v) => onChange({ arrowStyle: v as AnnotationConfig["arrowStyle"] })}
          />
        </>
      )}
      {(tool === "rect" || tool === "ellipse") && (
        <>
          <DividerV />
          <Segment
            label="填充"
            options={[{ v: "hollow", label: "空心" }, { v: "solid", label: "实心" }]}
            value={config.fill}
            onChange={(v) => onChange({ fill: v as AnnotationConfig["fill"] })}
          />
        </>
      )}
      {tool === "text" && (
        <>
          <DividerV />
          <div style={groupStyle}>
            <span style={labelStyle}>字体</span>
            <FontSelect value={config.fontFamily} onChange={(v) => onChange({ fontFamily: v })} />
          </div>
          <div style={groupStyle}>
            <span style={labelStyle}>字号</span>
            <input
              type="range" min={1} max={72} value={config.fontSize}
              onChange={(e) => onChange({ fontSize: Number(e.target.value) })}
              style={{ width: 90 }}
            />
            <span style={valueStyle}>{config.fontSize}</span>
          </div>
        </>
      )}
      {tool === "blur" && (
        <>
          <DividerV />
          <Segment
            label="模式"
            options={[{ v: "mosaic", label: "马赛克" }, { v: "gaussian", label: "高斯" }]}
            value={config.blurMode}
            onChange={(v) => onChange({ blurMode: v as AnnotationConfig["blurMode"] })}
          />
          <div style={groupStyle}>
            <span style={labelStyle}>强度</span>
            <input
              type="range" min={4} max={30} value={config.blurIntensity}
              onChange={(e) => onChange({ blurIntensity: Number(e.target.value) })}
              style={{ width: 80 }}
            />
            <span style={valueStyle}>{config.blurIntensity}</span>
          </div>
        </>
      )}
      {tool === "highlight" && (
        <>
          <DividerV />
          <Segment
            label="方式"
            options={[{ v: "freehand", label: "手绘" }, { v: "straight", label: "直线" }]}
            value={config.highlightMode}
            onChange={(v) => onChange({ highlightMode: v as AnnotationConfig["highlightMode"] })}
          />
        </>
      )}
      {tool === "spotlight" && (
        <>
          <DividerV />
          <Segment
            label="形状"
            options={[{ v: "rect", label: "矩形" }, { v: "circle", label: "圆形" }]}
            value={config.spotlightShape}
            onChange={(v) => onChange({ spotlightShape: v as AnnotationConfig["spotlightShape"] })}
          />
        </>
      )}
      {tool === "eraser" && (
        <>
          <DividerV />
          <Segment
            label="模式"
            options={[{ v: "pixel", label: "像素擦除" }, { v: "stroke", label: "整笔擦除" }]}
            value={config.eraserMode}
            onChange={(v) => onChange({ eraserMode: v as AnnotationConfig["eraserMode"] })}
          />
        </>
      )}
      {tool === "measure" && (
        <>
          <DividerV />
          <Segment
            label="方式"
            options={[{ v: "free", label: "自由" }, { v: "axis", label: "水平/垂直" }]}
            value={config.measureMode}
            onChange={(v) => onChange({ measureMode: v as AnnotationConfig["measureMode"] })}
          />
        </>
      )}
    </div>
  );
}

function Segment({
  label, options, value, onChange,
}: {
  label: string;
  options: { v: string; label: string }[];
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <div style={groupStyle}>
      <span style={labelStyle}>{label}</span>
      {options.map((o) => (
        <button
          key={o.v}
          onClick={() => onChange(o.v)}
          style={{
            border: "none", borderRadius: 6, color: "#fff", fontSize: 12,
            padding: "3px 9px", cursor: "pointer",
            background: value === o.v ? "#2563eb" : "rgba(255,255,255,0.08)",
          }}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

// ---- 按钮 ----

/// 字体下拉:优先本机枚举(queryLocalFonts),不可用时回退静态常用字体
function FontSelect({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [fonts, setFonts] = useState<FontEntry[]>(() => staticFontList());
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    if (loaded) return;
    setLoaded(true);
    void enumLocalFonts().then((local) => {
      if (local && local.length > 0) {
        // 系统默认置顶
        setFonts([{ id: SYSTEM_FONT_VALUE, display: "系统默认" }, ...local]);
      }
    });
  }, [loaded]);

  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      style={{
        background: "rgba(255,255,255,0.08)", color: "#e5e7eb", border: "none",
        borderRadius: 6, fontSize: 12, padding: "3px 6px", cursor: "pointer",
        outline: "none", maxWidth: 150,
      }}
    >
      {fonts.map((f) => (
        <option key={f.id} value={f.id} style={{ background: "#1f2937", color: "#e5e7eb" }}>
          {f.display}
        </option>
      ))}
    </select>
  );
}

function ToolButton({
  icon, markerNumber, label, active, onClick,
}: {
  icon: IconName | null;
  markerNumber?: number;
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <Tooltip title={label} placement="top" mouseEnterDelay={0.15}>
    <button
      onClick={onClick}
      style={{
        width: 30, height: 30, borderRadius: 6, border: "none", cursor: "pointer",
        background: active ? "#2563eb" : "transparent",
        color: active ? "#fff" : "#cbd5e1",
        display: "flex", alignItems: "center", justifyContent: "center",
        padding: 0, flexShrink: 0,
      }}
      onMouseEnter={(e) => { if (!active) e.currentTarget.style.background = "rgba(255,255,255,0.08)"; }}
      onMouseLeave={(e) => { if (!active) e.currentTarget.style.background = "transparent"; }}
    >
      {markerNumber !== undefined ? (
        // flashot MarkerIcon:编号圆徽章
        <span style={{
          width: 16, height: 16, borderRadius: "50%", background: "#ef4444",
          color: "#fff", fontSize: 10, fontWeight: 700, lineHeight: "16px",
          display: "inline-block", textAlign: "center",
        }}>
          {markerNumber}
        </span>
      ) : icon ? (
        <Icon name={icon} size={15} />
      ) : null}
    </button>
    </Tooltip>
  );
}

function IconBtn({ name, title, onClick, disabled }: { name: IconName; title: string; onClick: () => void; disabled?: boolean }) {
  return (
    <Tooltip title={title} placement="top" mouseEnterDelay={0.15}>
    <button
      onClick={onClick}
      disabled={disabled}
      style={{
        width: 30, height: 30, borderRadius: 6, border: "none",
        cursor: disabled ? "default" : "pointer",
        background: "transparent",
        color: disabled ? "rgba(255,255,255,0.35)" : "#cbd5e1",
        display: "flex", alignItems: "center", justifyContent: "center",
        padding: 0, flexShrink: 0,
      }}
      onMouseEnter={(e) => { if (!disabled) e.currentTarget.style.background = "rgba(255,255,255,0.08)"; }}
      onMouseLeave={(e) => { if (!disabled) e.currentTarget.style.background = "transparent"; }}
    >
      <Icon name={name} size={15} />
    </button>
    </Tooltip>
  );
}

const barStyle: React.CSSProperties = {
  display: "flex", alignItems: "center", gap: 2, padding: "4px 8px",
  borderRadius: 10, background: "rgba(30,30,30,0.88)",
  border: "1px solid rgba(255,255,255,0.1)",
  boxShadow: "0 4px 24px rgba(0,0,0,0.4)", userSelect: "none",
  flexWrap: "wrap", maxWidth: 560,
};
const panelStyle: React.CSSProperties = {
  display: "flex", alignItems: "center", gap: 10, padding: "7px 12px",
  borderRadius: 10, background: "rgba(30,30,30,0.92)",
  border: "1px solid rgba(255,255,255,0.1)",
  color: "#e5e7eb", fontSize: 12, boxShadow: "0 4px 16px rgba(0,0,0,0.4)",
  flexWrap: "wrap", maxWidth: 560,
};
const groupStyle: React.CSSProperties = { display: "flex", alignItems: "center", gap: 5 };
const labelStyle: React.CSSProperties = { color: "#9ca3af" };
const valueStyle: React.CSSProperties = { width: 24, textAlign: "right", fontVariantNumeric: "tabular-nums" };
const DividerV = () => <div style={{ width: 1, height: 18, background: "rgba(255,255,255,0.15)", flexShrink: 0 }} />;
const Divider = () => <div style={{ width: 1, height: 18, background: "rgba(255,255,255,0.15)", margin: "0 4px", flexShrink: 0 }} />;

// 默认配置供外部初始化
export { DEFAULT_CONFIG as DEFAULT_ANNOTATION_CONFIG };
