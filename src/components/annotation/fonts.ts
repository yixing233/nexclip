// 标注文字字体:对齐 flashot src/annotation/fonts.ts。
// system-ui 默认 + 本机字体枚举(queryLocalFonts,WebView2 可用)+ 常用字体白名单过滤。

/// 默认(跟随系统,Windows 显示 Segoe UI)
export const SYSTEM_FONT_VALUE = "system-ui";
/// 兼容 flashot 旧手写字体 ID(统一映射到系统字体)
const LEGACY_HANDWRITING = new Set(["handwriting", "Excalifont", "Xiaolai SC", "LXGW WenKai Screen"]);

/// CSS 字体栈:generic 原样;具名字体 → `"FONT", sans-serif`
export function resolveFontStack(fontFamily: string): string {
  if (!fontFamily || fontFamily === SYSTEM_FONT_VALUE || LEGACY_HANDWRITING.has(fontFamily)) {
    return "system-ui";
  }
  const generic = ["sans-serif", "serif", "monospace", "cursive", "fantasy"];
  if (generic.includes(fontFamily)) return fontFamily;
  return `"${fontFamily}", sans-serif`;
}

/// 常用字体白名单(过滤本机枚举结果;含 CJK)
const COMMON_FONTS = new Set([
  // Latin sans
  "Arial", "Arial Black", "Calibri", "Cambria", "Candara", "Century Gothic",
  "Corbel", "Franklin Gothic", "Segoe UI", "Segoe UI Variable", "Roboto",
  "Helvetica", "Helvetica Neue", "Verdana", "Tahoma", "Trebuchet MS",
  "Lato", "Open Sans", "Inter", "Noto Sans", "Ubuntu", "Oxygen", "Fira Sans",
  // Latin serif
  "Georgia", "Times New Roman", "Cambria Math", "Palatino Linotype",
  "Book Antiqua", "Garamond", "Playfair Display", "Merriweather", "Noto Serif",
  // Mono
  "Consolas", "Courier New", "JetBrains Mono", "Source Code Pro", "Menlo",
  "Monaco", "Cascadia Code", "Cascadia Mono", "IBM Plex Mono", "Roboto Mono",
  // 中文
  "Microsoft YaHei", "微软雅黑", "SimSun", "宋体", "SimHei", "黑体",
  "KaiTi", "楷体", "FangSong", "仿宋", "DengXian", "等线",
  "Source Han Sans SC", "思源黑体", "Noto Sans SC", "Noto Serif SC",
  "PingFang SC", "Hiragino Sans GB", "Sarasa Gothic SC",
  // 日文/韩文
  "Meiryo", "Yu Gothic", "MS Gothic", "Noto Sans CJK JP", "Source Han Sans JP",
  "Malgun Gothic", "맑은 고딕", "Noto Sans CJK KR", "Source Han Sans KR",
  // 手写/显示
  "Comic Sans MS", "Impact", "Rubik", "Work Sans", "Nunito", "Raleway",
]);

/// 排除模式:图标/符号/emoji 字体(flashot EXCLUDE_PATTERNS)
const EXCLUDE_PATTERNS: RegExp[] = [
  /icon/i, /symbol/i, /emoji/i, /webdings/i, /wingdings/i,
  /dingbats/i, /lastresort/i, /^\.?SF/i, /braille/i,
];

/// 显示名修剪(去除特殊字符过多的异常项)
function displayNameOk(name: string): boolean {
  const special = (name.match(/[^a-zA-Z0-9\u4e00-\u9fff\u3040-\u30ff\uac00-\ud7af ]/g) ?? []).length;
  return special <= 2;
}

export interface FontEntry {
  id: string;
  display: string;
}

/// 本机字体枚举(queryLocalFonts;不可用/拒绝授权返回 null)
export async function enumLocalFonts(): Promise<FontEntry[] | null> {
  try {
    const qlf = (window as unknown as { queryLocalFonts?: () => Promise<{ family: string }[]> }).queryLocalFonts;
    if (typeof qlf !== "function") return null;
    const fonts = await qlf.call(window);
    const seen = new Map<string, FontEntry>();
    for (const f of fonts) {
      const name = f.family;
      if (!name || seen.has(name)) continue;
      if (EXCLUDE_PATTERNS.some((p) => p.test(name))) continue;
      if (!COMMON_FONTS.has(name) && !displayNameOk(name)) continue;
      seen.set(name, { id: name, display: name });
    }
    return [...seen.values()].sort((a, b) => a.display.localeCompare(b.display, "zh"));
  } catch {
    return null;
  }
}

/// 静态常用字体下拉列表(本机枚举不可用时的回退;Windows 常见字体)
export function staticFontList(): FontEntry[] {
  return [
    { id: SYSTEM_FONT_VALUE, display: "系统默认" },
    { id: "Microsoft YaHei", display: "微软雅黑" },
    { id: "SimSun", display: "宋体" },
    { id: "SimHei", display: "黑体" },
    { id: "KaiTi", display: "楷体" },
    { id: "DengXian", display: "等线" },
    { id: "Segoe UI", display: "Segoe UI" },
    { id: "Arial", display: "Arial" },
    { id: "Calibri", display: "Calibri" },
    { id: "Georgia", display: "Georgia" },
    { id: "Times New Roman", display: "Times New Roman" },
    { id: "Consolas", display: "Consolas" },
    { id: "Courier New", display: "Courier New" },
    { id: "Comic Sans MS", display: "Comic Sans MS" },
    { id: "Impact", display: "Impact" },
    { id: "serif", display: "衬线 (serif)" },
    { id: "monospace", display: "等宽 (monospace)" },
  ];
}
