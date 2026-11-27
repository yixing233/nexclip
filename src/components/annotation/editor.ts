// Konva 标注编辑器:严格对齐 flashot(src/annotation/types.ts + Toolbar.tsx)。
// 13 工具:draw/line/arrow/rect/ellipse/text/blur/highlight/spotlight/marker/magnifier/eraser/measure
// (select 为默认态,不在工具条中;再次点击当前工具回 select)。
// 底图:选区物理像素画布(用于马赛克/模糊/放大镜烘焙)。
// 导出:stage.toDataURL 含效果层,隐藏节点自动跳过,透明 PNG 供 Rust 合成。
import Konva from "konva";
import { resolveFontStack } from "./fonts";

export type Tool =
  | "select"
  | "draw"
  | "line"
  | "arrow"
  | "rect"
  | "ellipse"
  | "text"
  | "blur"
  | "highlight"
  | "spotlight"
  | "marker"
  | "magnifier"
  | "eraser"
  | "measure";

/// flashot AnnotationStyle 的对齐子集
export interface AnnotationConfig {
  color: string;
  strokeWidth: number; // 1-20
  lineShape: "straight" | "wavy";
  lineStyle: "solid" | "dotted" | "dashed";
  arrowStyle: "v-shape" | "filled-triangle";
  fill: "hollow" | "solid";
  fontSize: number; // 1-72
  fontFamily: string; // system-ui 或本机字体名(flashot fonts.ts 对齐)
  blurMode: "mosaic" | "gaussian";
  blurIntensity: number; // 4-30
  highlightMode: "freehand" | "straight";
  spotlightShape: "rect" | "circle";
  measureMode: "free" | "axis";
  /// 橡皮模式:pixel = 像素擦除(擦掉笔画经过处) | stroke = 整笔擦除(点/划到哪条线整条删除)
  eraserMode: "pixel" | "stroke";
}

/// flashot PRESET_COLORS
export const PRESET_COLORS = [
  "#ff0000", "#ff6600", "#ffcc00", "#33cc33", "#0099ff",
  "#6633ff", "#cc00cc", "#ffffff", "#999999", "#000000",
];

export const DEFAULT_CONFIG: AnnotationConfig = {
  color: "#ff0000",
  strokeWidth: 4,
  lineShape: "straight",
  lineStyle: "solid",
  arrowStyle: "v-shape",
  fill: "hollow",
  fontSize: 24,
  fontFamily: "system-ui",
  blurMode: "mosaic",
  blurIntensity: 10,
  highlightMode: "freehand",
  spotlightShape: "rect",
  measureMode: "free",
  eraserMode: "pixel",
};

const HIGHLIGHT_OPACITY = 0.35;

/// 工具光标:内嵌 SVG data URI(白描边 + 深色填充,深浅背景都清晰)。
/// 热点坐标(6 6)指向笔尖/箭嘴,保证"落点即笔尖"。
const CURSORS = {
  pen: `url("data:image/svg+xml;utf8,${encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">' +
    '<path d="M3 21l3.5-1 11-11a2.1 2.1 0 0 0-3-3l-11 11L3 21z" fill="#fff" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/>' +
    '<path d="M14.5 6.5l3 3" stroke="#111" stroke-width="1.2"/>' +
    '</svg>'
  )}") 3 21, crosshair`,
  highlighter: `url("data:image/svg+xml;utf8,${encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">' +
    '<path d="M4 20h5l9-9-5-5-9 9v5z" fill="#fff23b" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/>' +
    '<path d="M13 6l5 5" stroke="#111" stroke-width="1.2"/>' +
    '</svg>'
  )}") 3 20, crosshair`,
  arrow: `url("data:image/svg+xml;utf8,${encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">' +
    '<path d="M3 21L20 4M20 4h-6M20 4v6" fill="none" stroke="#fff" stroke-width="3.4" stroke-linecap="round"/>' +
    '<path d="M3 21L20 4M20 4h-6M20 4v6" fill="none" stroke="#111" stroke-width="1.6" stroke-linecap="round"/>' +
    '</svg>'
  )}") 3 21, crosshair`,
  line: `url("data:image/svg+xml;utf8,${encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">' +
    '<path d="M3 20L21 5" stroke="#fff" stroke-width="3.4" stroke-linecap="round"/>' +
    '<path d="M3 20L21 5" stroke="#111" stroke-width="1.6" stroke-linecap="round"/>' +
    '</svg>'
  )}") 3 20, crosshair`,
  cross: "crosshair",
  text: "text",
  eraser: "crosshair",
  // 像素擦除:圆圈光标由 eraserCircleCursor() 按笔宽动态生成(此处占位)
  eraserCircle: "crosshair",
  // 整笔擦除:橡皮图标(热点在橡皮擦头左下)
  eraserIcon: `url("data:image/svg+xml;utf8,${encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">' +
    '<path d="M6 17l6-6 5 5-3 3H8l-2-2z" fill="#fff" stroke="#111" stroke-width="1.3" stroke-linejoin="round"/>' +
    '<path d="M12 11l5-5 4 4-5 5" fill="#fca5a5" stroke="#111" stroke-width="1.3" stroke-linejoin="round"/>' +
    '</svg>'
  )}") 4 18, crosshair`,
} as const;

/// 标号光标:红色圆底 + 白色数字(与画布上的标号样式一致),热点在圆心。
function markerCursor(num: number, color: string): string {
  const svg =
    '<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">' +
    `<circle cx="14" cy="14" r="11" fill="${color}" stroke="#fff" stroke-width="2"/>` +
    `<text x="14" y="14" text-anchor="middle" dominant-baseline="central"` +
    ` font-family="system-ui,sans-serif" font-size="13" font-weight="700" fill="#fff">${num}</text>` +
    '</svg>';
  return `url("data:image/svg+xml;utf8,${encodeURIComponent(svg)}") 14 14, crosshair`;
}

/// 像素擦除光标:空心圆,直径跟随笔宽(擦除范围 = strokeWidth*4,与绘制一致)
function eraserCircleCursor(strokeWidth: number): string {
  const d = Math.min(120, Math.max(10, strokeWidth * 4)); // 直径 CSS 像素
  const pad = 2;
  const size = d + pad * 2;
  const c = size / 2;
  const r = d / 2;
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    `<circle cx="${c}" cy="${c}" r="${r}" fill="rgba(255,255,255,0.12)" stroke="#111" stroke-width="1.4"/>` +
    `<circle cx="${c}" cy="${c}" r="${r}" fill="none" stroke="#fff" stroke-width="0.8"/>` +
    '</svg>';
  return `url("data:image/svg+xml;utf8,${encodeURIComponent(svg)}") ${c} ${c}, crosshair`;
}

/// 按工具返回 CSS cursor。
/// 绘图类工具用对应形状(笔/荧光笔/箭头/直线),其余用十字。
function cursorForTool(tool: Tool): string {
  switch (tool) {
    case "select": return "default";
    case "text": return CURSORS.text;
    case "draw": return CURSORS.pen;
    case "highlight": return CURSORS.highlighter;
    case "arrow": return CURSORS.arrow;
    case "line": return CURSORS.line;
    case "eraser": return CURSORS.eraser;
    default: return CURSORS.cross;
  }
}

interface ShapeRecord {
  node: Konva.Node;
  kind: "add" | "del";
}

export class AnnotationEditor {
  private stage: Konva.Stage;
  private layer: Konva.Layer;
  private effectsLayer: Konva.Layer;
  /// 选区物理像素底图(马赛克/模糊/放大镜的像素来源)
  private baseCanvas: HTMLCanvasElement;
  private scale: number;
  private width: number;
  private height: number;
  private tool: Tool = "select";
  private config: AnnotationConfig = { ...DEFAULT_CONFIG };
  private records: ShapeRecord[] = [];
  private redoStack: ShapeRecord[] = [];
  private drawing: Konva.Node | null = null;
  private drawStart = { x: 0, y: 0 };
  private mosaicCache: HTMLCanvasElement | null = null;
  private mosaicCell = 0;
  private blurCache: HTMLCanvasElement | null = null;
  private blurPx = 0;
  private transformer: Konva.Transformer;
  private selected: Konva.Node | null = null;
  private markerCounter = 1;
  /// 整笔擦除进行中(拖动连续命中)
  private erasing = false;
  /// 整笔擦除的虚线轨迹(记录拖动路径,松手即消失;不入撤销/导出)
  private eraserTrail: Konva.Line | null = null;
  private measureTmp: { line: Konva.Line; label: Konva.Text; startX: number; startY: number } | null = null;
  private onShapeEnd?: () => void;

  constructor(
    container: HTMLDivElement,
    baseCanvas: HTMLCanvasElement,
    width: number,
    height: number,
    scale: number,
    onShapeEnd?: () => void,
  ) {
    this.baseCanvas = baseCanvas;
    this.scale = scale;
    this.width = width;
    this.height = height;
    this.onShapeEnd = onShapeEnd;
    this.stage = new Konva.Stage({ container, width, height });
    this.effectsLayer = new Konva.Layer();
    this.layer = new Konva.Layer();
    this.transformer = new Konva.Transformer({
      rotateEnabled: false,
      keepRatio: false,
      enabledAnchors: ["top-left", "top-right", "bottom-left", "bottom-right"],
      anchorSize: 8,
      borderStroke: "#2563eb",
      anchorStroke: "#2563eb",
      anchorFill: "#fff",
    });
    this.stage.add(this.effectsLayer);
    this.stage.add(this.layer);
    this.layer.add(this.transformer);

    this.stage.on("mousedown touchstart", (e) => this.onDown(e));
    this.stage.on("mousemove touchmove", (e) => this.onMove(e));
    this.stage.on("mouseup touchend", () => this.onUp());
    this.stage.on("contextmenu", (e) => e.evt.preventDefault());
  }

  setTool(tool: Tool) {
    if (this.tool === "select" && tool !== "select") this.clearSelection();
    this.tool = tool;
    this.refreshCursor();
    // layer 始终监听:整笔擦除依赖 Konva hit graph(getIntersection)。
    // 图形本身不绑点击处理,拖动由 draggable 单独控制,不会干扰绘制。
    this.layer.listening(true);
    this.effectsLayer.listening(true);
  }

  /// 刷新光标(标号工具需带当前编号;橡皮按模式区分)
  private refreshCursor() {
    let cursor: string;
    if (this.tool === "marker") {
      cursor = markerCursor(this.markerCounter, this.config.color);
    } else if (this.tool === "eraser") {
      // 像素擦除 → 圆形光圈光标;整笔擦除 → 橡皮图标
      cursor = this.config.eraserMode === "stroke"
        ? CURSORS.eraserIcon
        : eraserCircleCursor(this.config.strokeWidth);
    } else {
      cursor = cursorForTool(this.tool);
    }
    this.stage.container().style.cursor = cursor;
  }

  setConfig(cfg: Partial<AnnotationConfig>) {
    this.config = { ...this.config, ...cfg };
    // 光标随工具状态更新(标号编号/颜色、橡皮模式)
    if (this.tool === "marker" || this.tool === "eraser") this.refreshCursor();
    // 选中图形时实时应用样式
    if (this.selected) this.applyStyleToNode(this.selected);
  }

  /// 载入已有标注层(继续编辑):作为底层背景图铺入,不参与选择/撤销,
  /// 但会随导出一并输出,视觉与保存结果与历史标注一致。
  loadAnnotationImage(src: string | HTMLImageElement): Promise<void> {
    return new Promise((resolve, reject) => {
      const finish = (img: HTMLImageElement) => {
        const node = new Konva.Image({
          image: img,
          width: this.width,
          height: this.height,
          listening: false,
          name: "loaded-annotation",
        });
        // 放到 both layers 的最底部,保证新绘制内容在上方
        node.moveToBottom();
        // effectsLayer 必须在下,所以放到 layer 底部即可(两 layer 按添加顺序堆叠)
        this.layer.add(node);
        this.layer.batchDraw();
        resolve();
      };
      if (typeof src === "string") {
        const img = new Image();
        img.crossOrigin = "anonymous";
        img.onload = () => finish(img);
        img.onerror = () => reject(new Error("标注层图片加载失败"));
        img.src = src;
      } else {
        finish(src);
      }
    });
  }

  /// 把已加载的标注背景图并入导出结果(导出时 stage 会自然包含它)
  hasLoadedAnnotation(): boolean {
    return this.layer.find(".loaded-annotation").length > 0;
  }

  /// 箭头几何统一布局(绘制/改样式共用,保证粗细变化时头部同步缩放)
  /// 实心三角 → 主干连底边中心;V 形 → 主干连尖端
  private layoutArrow(
    shaft: Konva.Line,
    head: Konva.Line,
    x0: number, y0: number, x1: number, y1: number,
    cfg: AnnotationConfig,
  ) {
    const dx = x1 - x0, dy = y1 - y0;
    const len = Math.hypot(dx, dy);
    if (len < 1) return;
    const ux = dx / len, uy = dy / len;
    // 头部尺寸:随粗细非线性增长(粗线上三角按视觉比例放大,否则显得又短又窄)
    const w = cfg.strokeWidth;
    const headLen = Math.min(12 + w * 2.6 + w * w * 0.16, len * 0.8);
    const halfW = 5 + w * 1.1 + w * w * 0.05;
    const bx = x1 - ux * headLen, by = y1 - uy * headLen;
    const px = -uy, py = ux;
    const filled = cfg.arrowStyle === "filled-triangle";
    const shaftEnd = filled ? { x: bx, y: by } : { x: x1, y: y1 };
    shaft.points([x0, y0, shaftEnd.x, shaftEnd.y]);
    shaft.stroke(cfg.color);
    shaft.strokeWidth(cfg.strokeWidth);
    head.points(filled
      ? [x1, y1, bx + px * halfW, by + py * halfW, bx - px * halfW, by - py * halfW]
      : [bx + px * halfW, by + py * halfW, x1, y1, bx - px * halfW, by - py * halfW]);
    head.closed(filled);
    head.fill(filled ? cfg.color : undefined);
    head.stroke(filled ? undefined : cfg.color);
    head.strokeWidth(filled ? 0 : Math.max(1.5, cfg.strokeWidth * 0.8));
  }

  private applyStyleToNode(node: Konva.Node) {
    const c = this.config;
    if (node instanceof Konva.Text) {
      node.fontSize(c.fontSize);
      node.fill(c.color);
      node.fontFamily(resolveFontStack(c.fontFamily));
    } else if (node instanceof Konva.Group && node.getAttr("__nodeType") === "arrow") {
      const shaft = node.getChildren()[0] as Konva.Line;
      const head = node.getChildren()[1] as Konva.Line;
      const anchor = node.getAttr("__arrowAnchor") as { x0: number; y0: number; x1: number; y1: number } | undefined;
      if (anchor) {
        this.layoutArrow(shaft, head, anchor.x0, anchor.y0, anchor.x1, anchor.y1, c);
      }
      node.getLayer()?.batchDraw();
    } else if (node instanceof Konva.Group && node.getAttr("__nodeType") === "marker") {
      // 标号:更新气泡底色(文字保持白色居中)
      const bubble = node.getChildren()[0];
      if (bubble instanceof Konva.Circle) bubble.fill(c.color);
      node.getLayer()?.batchDraw();
    } else if (node instanceof Konva.Rect || node instanceof Konva.Ellipse) {
      if (node.getLayer() === this.layer) {
        node.stroke(c.color);
        node.strokeWidth(c.strokeWidth);
        node.fill(c.fill === "solid" ? c.color : undefined);
      }
    } else if (node instanceof Konva.Line && node.getLayer() === this.layer) {
      node.stroke(c.color);
      node.strokeWidth(c.strokeWidth);
      node.dash(this.lineDash());
    }
    this.layer.batchDraw();
  }

  private lineDash(): number[] | undefined {
    const w = Math.max(1, this.config.strokeWidth);
    switch (this.config.lineStyle) {
      case "dotted": return [0.1, w * 2.2];
      case "dashed": return [w * 3, w * 2.2];
      default: return undefined;
    }
  }

  // ---- 效果底图缓存(按 intensity 重建) ----

  private mosaicPattern(cell: number): HTMLCanvasElement {
    if (this.mosaicCache && this.mosaicCell === cell) return this.mosaicCache;
    const c = document.createElement("canvas");
    c.width = Math.max(1, Math.ceil(this.width / cell));
    c.height = Math.max(1, Math.ceil(this.height / cell));
    const ctx = c.getContext("2d")!;
    ctx.drawImage(this.baseCanvas, 0, 0, c.width, c.height);
    const scaled = document.createElement("canvas");
    scaled.width = this.width;
    scaled.height = this.height;
    const sctx = scaled.getContext("2d")!;
    sctx.imageSmoothingEnabled = false;
    sctx.drawImage(c, 0, 0, scaled.width, scaled.height);
    this.mosaicCache = scaled;
    this.mosaicCell = cell;
    return scaled;
  }

  private blurPattern(px: number): HTMLCanvasElement {
    if (this.blurCache && this.blurPx === px) return this.blurCache;
    const c = document.createElement("canvas");
    c.width = this.width;
    c.height = this.height;
    const ctx = c.getContext("2d")!;
    // 边缘外扩绘制:避免 blur 滤镜把四周淡化成半透明(会让矩形边缘发黑/露底)
    const pad = px * 2;
    ctx.filter = `blur(${px}px)`;
    // 先把整块底图放大绘制到外扩区域,保证模糊后边缘仍有不透明像素
    ctx.drawImage(this.baseCanvas, -pad, -pad, this.width + pad * 2, this.height + pad * 2);
    ctx.filter = "none";
    this.blurCache = c;
    this.blurPx = px;
    return c;
  }

  // ---- 选择/删除 ----

  private selectShape(node: Konva.Node) {
    if (this.selected && this.selected !== node) this.selected.draggable(false);
    this.selected = node;
    node.draggable(true);
    this.transformer.nodes([node]);
    this.layer.batchDraw();
  }

  private clearSelection() {
    // 取消残留的 draggable,避免绘制/擦除模式下误拖图形
    this.selected?.draggable(false);
    this.selected = null;
    this.transformer.nodes([]);
    this.layer.batchDraw();
  }

  /// 整笔擦除虚线轨迹:把拖动路径画成虚线,松手后销毁
  private pushEraserTrail(x: number, y: number) {
    if (!this.eraserTrail) {
      this.eraserTrail = new Konva.Line({
        points: [x, y],
        stroke: "rgba(255,255,255,0.9)",
        strokeWidth: 2,
        dash: [6, 4],
        listening: false,
        name: "eraser-trail",
        shadowColor: "#000",
        shadowBlur: 2,
        shadowOpacity: 0.8,
      });
      this.layer.add(this.eraserTrail);
    } else {
      const pts = this.eraserTrail.points() as number[];
      pts.push(x, y);
      this.eraserTrail.points(pts as never);
    }
    this.layer.batchDraw();
  }

  private clearEraserTrail() {
    this.eraserTrail?.destroy();
    this.eraserTrail = null;
    this.layer.batchDraw();
  }

  /// 整笔擦除:命中坐标处的图形并隐藏(进撤销栈,del 语义)。
  /// 命中检测用 Konva 的 client 坐标 + 节点精确 hit;效果层(马赛克/模糊)也可擦。
  private eraseAt(x: number, y: number) {
    const all = [
      ...this.layer.getChildren(),
      ...this.effectsLayer.getChildren(),
    ];
    const hits: Konva.Node[] = [];
    for (const node of all) {
      // 跳过 transformer / 已隐藏 / 载入的背景标注层
      if (node === (this.transformer as unknown as Konva.Node)) continue;
      if (!node.visible()) continue;
      if (node.hasName("loaded-annotation")) continue;
      if (node.hasName("eraser-trail")) continue;
      if (this.isHit(node, x, y)) hits.push(node);
    }
    for (const node of hits) {
      node.hide();
      this.records.push({ node, kind: "del" });
    }
    if (hits.length) {
      this.redoStack = [];
      this.layer.batchDraw();
      this.effectsLayer.batchDraw();
      this.onShapeEnd?.();
    }
  }

  /// 节点命中检测:严格按 Konva hit 图判断该点是否真的落在图形笔迹上。
  /// 不使用包围盒容差 —— 必须"轨迹碰到真实笔迹"才擦除。
  private isHit(node: Konva.Node, x: number, y: number): boolean {
    // Konva:getIntersection 接收 stage 坐标,内部换算;这里直接用 stage 坐标
    const hit = this.stage.getIntersection({ x, y });
    if (!hit) return false;
    let p: Konva.Node | null = hit;
    while (p) {
      if (p === node) return true;
      p = p.getParent() as Konva.Node | null;
    }
    return false;
  }

  deleteSelected() {
    if (!this.selected) return;
    const node = this.selected;
    this.clearSelection();
    node.hide();
    this.records.push({ node, kind: "del" });
    this.redoStack = [];
    this.onShapeEnd?.();
  }

  // ---- 绘制事件 ----

  private onDown(e: Konva.KonvaEventObject<MouseEvent | TouchEvent>) {
    if (this.tool === "select") {
      const target = e.target;
      if (target === this.stage || target.getLayer() === this.effectsLayer || target === (this.transformer as unknown as Konva.Node)) {
        this.clearSelection();
      } else {
        this.selectShape(target);
      }
      return;
    }
    if (e.evt instanceof MouseEvent && e.evt.button !== 0) return;
    const pos = this.stage.getPointerPosition();
    if (!pos) return;
    this.drawStart = { x: pos.x, y: pos.y };

    switch (this.tool) {
      case "draw":
      case "highlight": {
        const isHl = this.tool === "highlight";
        if (isHl && this.config.highlightMode === "straight") {
          // 两点直线高亮
          const node = new Konva.Line({
            points: [pos.x, pos.y, pos.x, pos.y],
            stroke: this.config.color,
            strokeWidth: Math.max(this.config.strokeWidth * 4, 16),
            opacity: HIGHLIGHT_OPACITY,
            lineCap: "square",
          });
          this.layer.add(node);
          this.drawing = node;
          break;
        }
        const line = new Konva.Line({
          points: [pos.x, pos.y],
          stroke: this.config.color,
          strokeWidth: isHl ? Math.max(this.config.strokeWidth * 4, 16) : this.config.strokeWidth,
          opacity: isHl ? HIGHLIGHT_OPACITY : 1,
          lineCap: isHl ? "square" : "round",
          lineJoin: "round",
          tension: 0.3,
        });
        this.layer.add(line);
        this.drawing = line;
        break;
      }
      case "line": {
        const node = new Konva.Line({
          points: [pos.x, pos.y, pos.x, pos.y],
          stroke: this.config.color,
          strokeWidth: this.config.strokeWidth,
          dash: this.lineDash(),
        });
        this.layer.add(node);
        this.drawing = node;
        break;
      }
      case "blur": {
        const cell = Math.max(4, this.config.blurIntensity);
        const px = Math.max(4, this.config.blurIntensity);
        const img = this.config.blurMode === "mosaic" ? this.mosaicPattern(cell) : this.blurPattern(px);
        const rect = new Konva.Rect({
          x: pos.x, y: pos.y, width: 0, height: 0,
          fillPatternImage: img as unknown as HTMLImageElement,
          // pattern 以画布 (0,0) 为基准偏移,保证模糊/马赛克内容与原图位置严格对齐
          fillPatternOffset: { x: 0, y: 0 },
          fillPatternScaleX: 1,
          fillPatternScaleY: 1,
        });
        this.effectsLayer.add(rect);
        this.drawing = rect;
        break;
      }
      case "spotlight": {
        const group = new Konva.Group({ x: pos.x, y: pos.y });
        const mask = new Konva.Rect({
          x: -pos.x, y: -pos.y, width: this.width, height: this.height,
          fill: "rgba(0,0,0,0.62)",
        });
        const shape = this.config.spotlightShape === "circle"
          ? new Konva.Circle({ radius: 0, fill: "#fff", globalCompositeOperation: "destination-out" })
          : new Konva.Rect({ width: 0, height: 0, fill: "#fff", globalCompositeOperation: "destination-out" });
        group.add(mask);
        group.add(shape);
        this.effectsLayer.add(group);
        this.drawing = group;
        break;
      }
      case "marker": {
        // 编号气泡:点击放置,自动递增(flashot marker)
        const n = this.markerCounter++;
        const group = new Konva.Group({ x: pos.x, y: pos.y, draggable: true });
        const r = Math.max(12, this.config.strokeWidth * 3);
        const bubble = new Konva.Circle({ radius: r, fill: this.config.color, stroke: "#fff", strokeWidth: 2 });
        // 编号居中:用 width/height + align/verticalAlign(offset 是锚点位秷,不是居中)
        const label = new Konva.Text({
          text: String(n),
          fill: "#fff",
          fontSize: Math.round(r * 1.15),
          fontStyle: "700",
          fontFamily: "system-ui, sans-serif",
          width: r * 2,
          height: r * 2,
          x: -r,
          y: -r,
          align: "center",
          verticalAlign: "middle",
          listening: false,
        });
        group.add(bubble);
        group.add(label);
        group.setAttr("__nodeType", "marker");
        this.layer.add(group);
        // 编号递增 → 光标徽章同步更新
        this.refreshCursor();
        this.records.push({ node: group, kind: "add" });
        this.redoStack = [];
        this.onShapeEnd?.();
        this.layer.batchDraw();
        return;
      }
      case "magnifier": {
        // 放大镜:拖出圆形区域,内容为该处 2x 放大(flashot magnifier)
        const clipRadius = { r: 5 };
        const outer = new Konva.Group({ x: pos.x, y: pos.y });
        outer.setAttr("__magnifierStart", { x: pos.x, y: pos.y });
        outer.setAttr("__clipRadius", clipRadius);
        const clip = new Konva.Group({
          clipFunc: (ctx) => {
            ctx.beginPath();
            ctx.arc(0, 0, clipRadius.r, 0, Math.PI * 2, false);
          },
        });
        const img = new Konva.Image({
          image: this.baseCanvas as unknown as CanvasImageSource,
          crop: { x: pos.x * this.scale, y: pos.y * this.scale, width: 10, height: 10 },
          width: 10, height: 10,
        });
        clip.add(img);
        // 无边框(按需求去掉描边圆环;仍保留 ring 节点用于拖拽时定位,但 stroke 透明)
        const ring = new Konva.Circle({
          radius: 5, stroke: "rgba(0,0,0,0)", strokeWidth: 0,
        });
        outer.add(clip);
        outer.add(ring);
        this.effectsLayer.add(outer);
        this.drawing = outer;
        break;
      }
      case "eraser": {
        if (this.config.eraserMode === "stroke") {
          // 整笔擦除:按下即命中检测,命中者隐藏(可撤销);显示虚线指示圈
          this.pushEraserTrail(pos.x, pos.y);
          this.eraseAt(pos.x, pos.y);
          this.erasing = true;
          return;
        }
        // 像素擦除:destination-out 笔画
        const line = new Konva.Line({
          points: [pos.x, pos.y],
          stroke: "rgba(0,0,0,1)",
          strokeWidth: this.config.strokeWidth * 4,
          globalCompositeOperation: "destination-out",
          lineCap: "round",
          lineJoin: "round",
        });
        this.layer.add(line);
        this.drawing = line;
        break;
      }
      case "text": {
        this.spawnTextInput(pos.x, pos.y);
        return;
      }
      case "measure": {
        const group = new Konva.Group();
        const line = new Konva.Line({
          points: [pos.x, pos.y, pos.x, pos.y],
          stroke: this.config.color,
          strokeWidth: 1.5,
          dash: [6, 4],
        });
        const label = new Konva.Text({
          x: pos.x + 8, y: pos.y - 18, text: "0 px",
          fill: this.config.color, fontSize: 12, fontStyle: "600",
        });
        group.add(line);
        group.add(label);
        this.layer.add(group);
        this.drawing = group;
        this.measureTmp = { line, label, startX: pos.x, startY: pos.y };
        break;
      }
      case "arrow": {
        // 自绘箭头:主干线 + 独立计算的头部(V 形/实心三角),完全可控
        const group = new Konva.Group();
        group.setAttr("__nodeType", "arrow");
        const shaft = new Konva.Line({
          points: [pos.x, pos.y, pos.x, pos.y],
          stroke: this.config.color,
          strokeWidth: this.config.strokeWidth,
          lineCap: "round",
        });
        const head = new Konva.Line({
          closed: this.config.arrowStyle === "filled-triangle",
          fill: this.config.arrowStyle === "filled-triangle" ? this.config.color : undefined,
          stroke: this.config.color,
          strokeWidth: this.config.strokeWidth,
          lineCap: "round",
          lineJoin: "round",
        });
        group.add(shaft);
        group.add(head);
        this.layer.add(group);
        this.drawing = group;
        break;
      }
      case "rect": {
        const node = new Konva.Rect({
          x: pos.x, y: pos.y,
          stroke: this.config.color, strokeWidth: this.config.strokeWidth,
          fill: this.config.fill === "solid" ? this.config.color : undefined,
          width: 0, height: 0,
        });
        this.layer.add(node);
        this.drawing = node;
        break;
      }
      case "ellipse": {
        const node = new Konva.Ellipse({
          x: pos.x, y: pos.y,
          stroke: this.config.color, strokeWidth: this.config.strokeWidth,
          fill: this.config.fill === "solid" ? this.config.color : undefined,
          radiusX: 0, radiusY: 0,
        });
        this.layer.add(node);
        this.drawing = node;
        break;
      }
      default:
        return;
    }
    (this.drawing as Konva.Shape).draw();
  }

  /** 波形线点生成(flashot lineShape=wavy 的等价实现) */
  private wavyPoints(x0: number, y0: number, x1: number, y1: number): number[] {
    const dx = x1 - x0, dy = y1 - y0;
    const len = Math.hypot(dx, dy) || 1;
    const nx = -dy / len, ny = dx / len;
    const seg = Math.max(10, this.config.strokeWidth * 3);
    const n = Math.max(4, Math.round(len / seg) * 2);
    const amp = Math.max(2, this.config.strokeWidth);
    const pts: number[] = [x0, y0];
    for (let i = 1; i < n; i++) {
      const t = i / n;
      const off = Math.sin(t * (len / seg) * Math.PI) * amp;
      pts.push(x0 + dx * t + nx * off, y0 + dy * t + ny * off);
    }
    pts.push(x1, y1);
    return pts;
  }

  private onMove(e?: Konva.KonvaEventObject<MouseEvent | TouchEvent>) {
    // 整笔擦除:拖动时连续命中
    if (this.erasing) {
      const p = this.stage.getPointerPosition();
      if (p) {
        this.pushEraserTrail(p.x, p.y);
        this.eraseAt(p.x, p.y);
      }
      return;
    }
    if (!this.drawing) return;
    const pos = this.stage.getPointerPosition();
    if (!pos) return;
    const shiftKey = e?.evt instanceof MouseEvent ? e.evt.shiftKey : false;
    const s = this.drawStart;
    const node = this.drawing;

    if (this.tool === "measure" && this.measureTmp) {
      const m = this.measureTmp;
      let ex = pos.x, ey = pos.y;
      if (this.config.measureMode === "axis") {
        // 锁定水平/垂直(取位移较大方向)
        if (Math.abs(pos.x - m.startX) >= Math.abs(pos.y - m.startY)) ey = m.startY;
        else ex = m.startX;
      }
      m.line.points([m.startX, m.startY, ex, ey]);
      const d = Math.round(Math.hypot(ex - m.startX, ey - m.startY));
      m.label.text(`${d} px`);
      m.label.x((m.startX + ex) / 2 + 8);
      m.label.y((m.startY + ey) / 2 - 18);
      this.layer.batchDraw();
      return;
    }

    if (node instanceof Konva.Group && node.getAttr("__magnifierStart")) {
      // 放大镜:半径=拖动距离,内容为中心 2x 放大
      const st = node.getAttr("__magnifierStart") as { x: number; y: number };
      const clipRadius = node.getAttr("__clipRadius") as { r: number };
      const r = Math.max(12, Math.hypot(pos.x - st.x, pos.y - st.y) / 2);
      const zoom = 2;
      const srcW = (r * 2) / zoom;
      node.position({ x: pos.x, y: pos.y });
      const clip = node.getChildren()[0] as Konva.Group;
      const ring = node.getChildren()[1] as Konva.Circle;
      const img = clip.getChildren()[0] as Konva.Image;
      img.crop({
        x: (pos.x - srcW / 2) * this.scale,
        y: (pos.y - srcW / 2) * this.scale,
        width: srcW * this.scale,
        height: srcW * this.scale,
      });
      img.width(r * 2);
      img.height(r * 2);
      img.x(-r); // Image 原点对齐圆心,否则只露出右下四分之一
      img.y(-r);
      clipRadius.r = r;
      ring.radius(r);
      this.effectsLayer.batchDraw();
      return;
    }

    if (node instanceof Konva.Group && node.getAttr("__nodeType") === "arrow") {
      const shaft = node.getChildren()[0] as Konva.Line;
      const head = node.getChildren()[1] as Konva.Line;
      node.setAttr("__arrowAnchor", { x0: s.x, y0: s.y, x1: pos.x, y1: pos.y });
      this.layoutArrow(shaft, head, s.x, s.y, pos.x, pos.y, this.config);
      this.layer.batchDraw();
      return;
    }

    if (node instanceof Konva.Group && this.tool === "spotlight") {
      // 双向支持:矩形聚光随拖动方向生长;圆形以距离为半径
      const shape = node.getChildren()[1];
      const dx = pos.x - s.x, dy = pos.y - s.y;
      if (this.config.spotlightShape === "circle") {
        if (shape instanceof Konva.Circle) shape.radius(Math.hypot(dx, dy));
      } else if (shape instanceof Konva.Rect) {
        shape.x(Math.min(0, dx));
        shape.y(Math.min(0, dy));
        shape.width(Math.abs(dx));
        shape.height(Math.abs(dy));
      }
      this.effectsLayer.batchDraw();
      return;
    }

    if (node instanceof Konva.Line) {
      const isStraight =
        node.globalCompositeOperation() !== "destination-out" &&
        (this.tool === "line" || (this.tool === "highlight" && this.config.highlightMode === "straight"));
      if (isStraight) {
        if (this.tool === "line" && this.config.lineShape === "wavy") {
          node.points(this.wavyPoints(s.x, s.y, pos.x, pos.y) as never);
        } else {
          node.points([s.x, s.y, pos.x, pos.y] as never);
        }
      } else {
        // 画笔/自由高亮/橡皮:逐点追加
        node.points([...(node.points() as number[]), pos.x, pos.y] as never);
      }
      node.draw();
      return;
    }
    if (node instanceof Konva.Rect) {
      node.x(Math.min(s.x, pos.x));
      node.y(Math.min(s.y, pos.y));
      node.width(Math.abs(pos.x - s.x));
      node.height(Math.abs(pos.y - s.y));
      node.draw();
      return;
    }
    if (node instanceof Konva.Ellipse) {
      if (shiftKey) {
        // Shift = 正圆:取较大半径
        const r = Math.max(Math.abs(pos.x - s.x), Math.abs(pos.y - s.y)) / 2;
        node.radiusX(r);
        node.radiusY(r);
        node.x(s.x + Math.sign(pos.x - s.x) * r);
        node.y(s.y + Math.sign(pos.y - s.y) * r);
      } else {
        node.radiusX(Math.abs(pos.x - s.x) / 2);
        node.radiusY(Math.abs(pos.y - s.y) / 2);
        node.x((s.x + pos.x) / 2);
        node.y((s.y + pos.y) / 2);
      }
      node.draw();
      return;
    }
  }

  private onUp() {
    if (this.erasing) {
      this.erasing = false;
      this.clearEraserTrail();
      return;
    }
    if (this.tool === "measure") {
      if (this.measureTmp) {
        const group = new Konva.Group();
        group.add(this.measureTmp.line);
        group.add(this.measureTmp.label);
        group.setAttr("__tool", "measure");
        this.records.push({ node: group, kind: "add" });
        this.redoStack = [];
        this.onShapeEnd?.();
        this.measureTmp = null;
      }
      this.drawing = null;
      return;
    }
    if (!this.drawing) return;
    const node = this.drawing;
    this.drawing = null;
    if (node instanceof Konva.Rect && node.width() < 2 && node.height() < 2) {
      node.destroy();
      return;
    }
    if (node instanceof Konva.Ellipse && node.radiusX() < 2 && node.radiusY() < 2) {
      node.destroy();
      return;
    }
    if (node instanceof Konva.Group && node.getAttr("__magnifierStart") && node.getChildren()[1] instanceof Konva.Circle) {
      const ring = node.getChildren()[1] as Konva.Circle;
      if (ring.radius() < 8) {
        node.destroy();
        return;
      }
    }
    if (node instanceof Konva.Group && node.getAttr("__nodeType") === "arrow") {
      const shaft = node.getChildren()[0] as Konva.Line;
      const pts = shaft.points() as number[];
      if (Math.hypot(pts[2] - pts[0], pts[3] - pts[1]) < 4) {
        node.destroy();
        return;
      }
    }
    // 标记工具类型(序列化恢复用)
    (node as Konva.Node).setAttr("__tool", this.tool);
    this.records.push({ node, kind: "add" });
    this.redoStack = [];
    this.onShapeEnd?.();
  }

  private spawnTextInput(x: number, y: number) {
    const container = this.stage.container();
    const wrapper = document.createElement("div");
    Object.assign(wrapper.style, {
      position: "absolute", left: `${x}px`, top: `${y}px`, zIndex: "10",
    });
    const ta = document.createElement("textarea");
    Object.assign(ta.style, {
      background: "transparent", border: "none", outline: "none",
      color: this.config.color,
      font: `600 ${this.config.fontSize}px ${resolveFontStack(this.config.fontFamily)}`,
      resize: "none", overflow: "hidden",
      minWidth: "120px", minHeight: "28px", whiteSpace: "pre",
    });
    wrapper.appendChild(ta);
    container.appendChild(wrapper);
    ta.focus();
    const commit = () => {
      const text = ta.value.trim();
      wrapper.remove();
      if (!text) return;
      const node = new Konva.Text({
        x, y, text,
        fill: this.config.color,
        fontSize: this.config.fontSize,
        fontStyle: "600",
        fontFamily: resolveFontStack(this.config.fontFamily),
        draggable: true,
      });
      this.layer.add(node);
      this.records.push({ node, kind: "add" });
      this.redoStack = [];
      this.onShapeEnd?.();
      this.layer.batchDraw();
    };
    ta.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter" && !ev.shiftKey) {
        ev.preventDefault();
        commit();
      } else if (ev.key === "Escape") {
        wrapper.remove();
      }
      ev.stopPropagation();
    });
    ta.addEventListener("blur", commit);
  }

  // ---- 撤销/重做(命令语义:add 显示↔隐藏;del 隐藏↔显示;隐藏节点不导出) ----

  undo() {
    this.clearSelection();
    const rec = this.records.pop();
    if (!rec) return;
    if (rec.kind === "add") rec.node.hide();
    else rec.node.show();
    this.redoStack.push(rec);
    this.layer.batchDraw();
    this.effectsLayer.batchDraw();
  }

  redo() {
    const rec = this.redoStack.pop();
    if (!rec) return;
    if (rec.kind === "add") rec.node.show();
    else rec.node.hide();
    this.records.push(rec);
    this.layer.batchDraw();
    this.effectsLayer.batchDraw();
  }

  /** 导出标注层为透明 PNG dataURL(物理像素,隐藏节点自动排除) */
  toDataURL(outputScale = 1): string {
    // 导出时排除擦除指示圈
    const trailVisible = this.eraserTrail?.visible() ?? false;
    this.eraserTrail?.visible(false);
    const url = this.stage.toDataURL({
      mimeType: "image/png",
      pixelRatio: outputScale,
      width: this.width,
      height: this.height,
    });
    this.eraserTrail?.visible(trailVisible);
    return url;
  }

  hasShapes(): boolean {
    return this.records.some((r) => r.kind === "add" && r.node.visible())
      || this.hasLoadedAnnotation();
  }

  // ---- 序列化(贴图再次编辑用) ----

  /// 导出当前所有绘制对象(可见的 add 记录),含 Konva 节点树与 attrs。
  /// 不含 transformer / 载入的背景标注图。
  serialize(): string {
    const nodes = this.records
      .filter((r) => r.kind === "add" && r.node.visible())
      .map((r) => r.node.toObject());
    return JSON.stringify({ width: this.width, height: this.height, nodes });
  }

  /// 从 serialize 输出恢复对象(可继续选择/移动/删除/撤销)。
  restore(json: string): void {
    try {
      const data = JSON.parse(json) as { nodes?: unknown[] };
      if (!Array.isArray(data.nodes)) return;
      for (const raw of data.nodes) {
        // Konva.Node.create 需要完整节点 JSON;此处用 stage 临时创建后取出
        const tmp = Konva.Node.create(JSON.stringify(raw)) as Konva.Node;
        this.layer.add(tmp as Konva.Shape);
        // 保持可选中/可删除语义
        if (!(tmp instanceof Konva.Group)) tmp.draggable(false);
        this.records.push({ node: tmp, kind: "add" });
      }
      this.layer.batchDraw();
    } catch (e) {
      console.warn("标注恢复失败", e);
    }
  }

  canUndo(): boolean {
    return this.records.length > 0;
  }

  canRedo(): boolean {
    return this.redoStack.length > 0;
  }

  hasSelection(): boolean {
    return this.selected !== null;
  }

  destroy() {
    this.stage.destroy();
  }
}

export { DEFAULT_CONFIG as DEFAULT_ANNOTATION_CONFIG };
