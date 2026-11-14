//! 智能识别引擎:色值 / 本地路径 / GitHub / 网盘提取码 / 通用网址。
//!
//! 移植自 C# SmartActionService + SmartAction(44+584 行)。
//! 动作表示为数据(而非闭包):`SmartAction.action` 描述主/次操作,
//! 由 toast 前端展示、`smart_exec` 命令执行。

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SmartKind {
    Url,
    GitHub,
    NetDisk,
    Color,
    LocalFolder,
    LocalFile,
}

/// 动作描述(前端按钮 → smart_exec 执行)。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SmartActionSpec {
    /// open_url | open_path | locate_path | open_terminal | open_file | copy_text
    pub kind: String,
    pub arg: String,
}

/// 智能动作结果(toast 卡片 / 列表色块渲染)。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SmartAction {
    pub kind: SmartKind,
    pub title: String,
    pub subtitle: String,
    pub primary: SmartActionSpec,
    pub secondary: Option<SmartActionSpec>,
    /// 颜色专属:预览色(#RRGGBB[AA])与三种格式串
    pub preview_color: Option<String>,
    pub hex_color: Option<String>,
    pub rgb_color: Option<String>,
    pub hsl_color: Option<String>,
    /// 网盘专属:提取码
    pub extraction_code: Option<String>,
    /// 本地路径专属
    pub target_path: Option<String>,
}

/// 各开关(设置页智能识别区;forToast 时生效)。
#[derive(Debug, Clone, Copy, Default)]
pub struct SmartToggles {
    pub color: bool,
    pub path: bool,
    pub deep_link: bool,
    pub netdisk: bool,
    pub url: bool,
}

const HEX_COLOR_RE: &str = r"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$";
const RGB_COLOR_RE: &str = r"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*([\d.]+))?\s*\)$";
const HSL_COLOR_RE: &str = r"^hsla?\(\s*(\d{1,3})\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%(?:\s*,\s*([\d.]+))?\s*\)$";
const GITHUB_REPO_RE: &str = r"https?://(?:www\.)?github\.com/([a-zA-Z0-9_\-\.]+)/([a-zA-Z0-9_\-\.]+)(?:/.*)?";
const EXTRACT_CODE_RE: &str =
    r"(?:提取码|密码|访问码|code|pwd)[:：\s]*([a-zA-Z0-9]{4,8})|[?&]pwd=([a-zA-Z0-9]{4,8})";
const URL_RE: &str = r"(https?://[a-zA-Z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+)";

fn re(pattern: &str) -> regex::Regex {
    regex::Regex::new(pattern).expect("内置正则编译失败")
}

/// 检测入口。forToast=true 时按开关过滤(列表卡片色块始终要完整元数据)。
pub fn detect(raw_text: &str, toggles: SmartToggles, for_toast: bool) -> Option<SmartAction> {
    let text = raw_text.trim();
    if text.is_empty() || text.chars().count() > 2000 {
        return None;
    }

    // 1. 颜色
    if let Some(a) = detect_color(text) {
        return if !for_toast || toggles.color { Some(a) } else { None };
    }
    // 2. 本地路径
    if let Some(a) = detect_local_path(text) {
        return if !for_toast || toggles.path { Some(a) } else { None };
    }
    // 3. GitHub / 网盘深链
    if let Some(a) = detect_deep_link(text) {
        return match a.kind {
            SmartKind::GitHub if !for_toast || toggles.deep_link => Some(a),
            SmartKind::NetDisk if !for_toast || toggles.netdisk => Some(a),
            SmartKind::GitHub | SmartKind::NetDisk => None,
            _ => Some(a),
        };
    }
    // 4. 通用网址
    if let Some(a) = detect_general_url(text) {
        return if !for_toast || toggles.url { Some(a) } else { None };
    }
    None
}

// ---- 颜色 ----

#[derive(Debug, Clone, Copy)]
struct Rgba {
    r: u8,
    g: u8,
    b: u8,
    a: u8,
}

fn detect_color(text: &str) -> Option<SmartAction> {
    // HEX
    let hex_re = re(HEX_COLOR_RE);
    if hex_re.is_match(text) {
        if let Some(c) = parse_hex_color(text) {
            return Some(color_action(c, "HEX"));
        }
    }
    // RGB / RGBA
    let rgb_re = re(RGB_COLOR_RE);
    if let Some(m) = rgb_re.captures(text) {
        let g1 = |i: usize| m.get(i).map(|x| x.as_str().to_string());
        let r: u8 = g1(1)?.parse().ok()?;
        let g: u8 = g1(2)?.parse().ok()?;
        let b: u8 = g1(3)?.parse().ok()?;
        let a: u8 = match g1(4) {
            Some(av) => ((av.parse::<f64>().ok()?.clamp(0.0, 1.0)) * 255.0).round() as u8,
            None => 255,
        };
        return Some(color_action(Rgba { r, g, b, a }, "HEX"));
    }
    // HSL / HSLA
    let hsl_re = re(HSL_COLOR_RE);
    if let Some(m) = hsl_re.captures(text) {
        let g1 = |i: usize| m.get(i).map(|x| x.as_str().to_string());
        let h = g1(1)?.parse::<f64>().ok()? % 360.0;
        let sat = (g1(2)?.parse::<f64>().ok()? / 100.0).clamp(0.0, 1.0);
        let light = (g1(3)?.parse::<f64>().ok()? / 100.0).clamp(0.0, 1.0);
        let a: u8 = match g1(4) {
            Some(av) => ((av.parse::<f64>().ok()?.clamp(0.0, 1.0)) * 255.0).round() as u8,
            None => 255,
        };
        return Some(color_action(hsl_to_rgb(h, sat, light, a), "HEX"));
    }
    None
}

fn parse_hex_color(hex: &str) -> Option<Rgba> {
    let clean = hex.trim_start_matches('#');
    let pair = |s: &str| u8::from_str_radix(s, 16).ok();
    match clean.len() {
        3 => Some(Rgba {
            r: pair(&format!("{}", &clean[0..1].repeat(2)))?,
            g: pair(&format!("{}", &clean[1..2].repeat(2)))?,
            b: pair(&format!("{}", &clean[2..3].repeat(2)))?,
            a: 255,
        }),
        4 => Some(Rgba {
            r: pair(&clean[0..1].repeat(2))?,
            g: pair(&clean[1..2].repeat(2))?,
            b: pair(&clean[2..3].repeat(2))?,
            a: pair(&clean[3..4].repeat(2))?,
        }),
        6 => Some(Rgba {
            r: pair(&clean[0..2])?,
            g: pair(&clean[2..4])?,
            b: pair(&clean[4..6])?,
            a: 255,
        }),
        8 => Some(Rgba {
            r: pair(&clean[0..2])?,
            g: pair(&clean[2..4])?,
            b: pair(&clean[4..6])?,
            a: pair(&clean[6..8])?,
        }),
        _ => None,
    }
}

fn to_hex(c: Rgba) -> String {
    if c.a == 255 {
        format!("#{:02X}{:02X}{:02X}", c.r, c.g, c.b)
    } else {
        format!("#{:02X}{:02X}{:02X}{:02X}", c.r, c.g, c.b, c.a)
    }
}

fn to_rgb(c: Rgba) -> String {
    if c.a == 255 {
        format!("rgb({}, {}, {})", c.r, c.g, c.b)
    } else {
        format!("rgba({}, {}, {}, {})", c.r, c.g, c.b, round2(c.a as f64 / 255.0))
    }
}

fn round2(v: f64) -> String {
    format!("{:.2}", v)
}

fn to_hsl(c: Rgba) -> String {
    let r = c.r as f64 / 255.0;
    let g = c.g as f64 / 255.0;
    let b = c.b as f64 / 255.0;
    let max = r.max(g.max(b));
    let min = r.min(g.min(b));
    let l = (max + min) / 2.0;
    let (h, s) = if (max - min).abs() < 0.00001 {
        (0.0, 0.0)
    } else {
        let d = max - min;
        let s = if l > 0.5 { d / (2.0 - max - min) } else { d / (max + min) };
        let h = if (max - r).abs() < 0.00001 {
            (g - b) / d + if g < b { 6.0 } else { 0.0 }
        } else if (max - g).abs() < 0.00001 {
            (b - r) / d + 2.0
        } else {
            (r - g) / d + 4.0
        } / 6.0;
        (h, s)
    };
    let h_deg = (h * 360.0).round() as i64;
    let s_pct = (s * 100.0).round() as i64;
    let l_pct = (l * 100.0).round() as i64;
    if c.a == 255 {
        format!("hsl({}, {}%, {}%)", h_deg, s_pct, l_pct)
    } else {
        format!("hsla({}, {}%, {}%, {})", h_deg, s_pct, l_pct, round2(c.a as f64 / 255.0))
    }
}

fn hsl_to_rgb(h: f64, s: f64, l: f64, a: u8) -> Rgba {
    fn hue_to_rgb(p: f64, q: f64, mut t: f64) -> f64 {
        if t < 0.0 {
            t += 1.0;
        }
        if t > 1.0 {
            t -= 1.0;
        }
        if t < 1.0 / 6.0 {
            p + (q - p) * 6.0 * t
        } else if t < 0.5 {
            q
        } else if t < 2.0 / 3.0 {
            p + (q - p) * (2.0 / 3.0 - t) * 6.0
        } else {
            p
        }
    }
    let (r, g, b) = if s.abs() < 0.00001 {
        (l, l, l)
    } else {
        let q = if l < 0.5 { l * (1.0 + s) } else { l + s - l * s };
        let p = 2.0 * l - q;
        let hk = h / 360.0;
        (
            hue_to_rgb(p, q, hk + 1.0 / 3.0),
            hue_to_rgb(p, q, hk),
            hue_to_rgb(p, q, hk - 1.0 / 3.0),
        )
    };
    Rgba {
        r: (r * 255.0).round().clamp(0.0, 255.0) as u8,
        g: (g * 255.0).round().clamp(0.0, 255.0) as u8,
        b: (b * 255.0).round().clamp(0.0, 255.0) as u8,
        a,
    }
}

fn color_action(c: Rgba, primary_source: &str) -> SmartAction {
    let hex = to_hex(c);
    let rgb = to_rgb(c);
    let hsl = to_hsl(c);
    // 主按钮:与来源格式不同的首选转换(HEX 来源→复制 RGB;RGB/HSL 来源→复制 HEX),与旧版一致
    let primary = if primary_source == "HEX" {
        SmartActionSpec { kind: "copy_text".into(), arg: rgb.clone() }
    } else {
        SmartActionSpec { kind: "copy_text".into(), arg: hex.clone() }
    };
    SmartAction {
        kind: SmartKind::Color,
        title: format!("色值预览 · {hex}"),
        subtitle: format!("{rgb}  ·  {hsl}"),
        primary,
        secondary: Some(SmartActionSpec { kind: "copy_text".into(), arg: hsl.clone() }),
        preview_color: Some(hex.clone()),
        hex_color: Some(hex),
        rgb_color: Some(rgb),
        hsl_color: Some(hsl),
        extraction_code: None,
        target_path: None,
    }
}

// ---- 本地路径 ----

fn detect_local_path(text: &str) -> Option<SmartAction> {
    let mut raw = text.trim().trim_matches(['"', '\'']).to_string();
    if raw.to_lowercase().starts_with("file:///") {
        raw = urldecode(&raw[8..]).replace('/', "\\");
    }
    // 必须为绝对盘符路径或 UNC
    let is_drive = raw.chars().next().is_some_and(|c| c.is_ascii_alphabetic())
        && raw.chars().nth(1) == Some(':')
        && raw.chars().nth(2).is_some_and(|c| c == '\\' || c == '/');
    if !is_drive && !raw.starts_with("\\\\") {
        return None;
    }
    let normalized = raw.replace('/', "\\");
    let meta = std::fs::metadata(&normalized).ok()?;

    if meta.is_dir() {
        let dir_name = std::path::Path::new(&normalized)
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_else(|| normalized.clone());
        Some(SmartAction {
            kind: SmartKind::LocalFolder,
            title: format!("打开文件夹 · {dir_name}"),
            subtitle: normalized.clone(),
            primary: SmartActionSpec { kind: "open_path".into(), arg: normalized.clone() },
            secondary: Some(SmartActionSpec { kind: "open_terminal".into(), arg: normalized.clone() }),
            preview_color: None,
            hex_color: None,
            rgb_color: None,
            hsl_color: None,
            extraction_code: None,
            target_path: Some(normalized),
        })
    } else {
        let file_name = std::path::Path::new(&normalized)
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_else(|| normalized.clone());
        let size = meta.len();
        let size_str = if size < 1024 {
            format!("{size} B")
        } else if size < 1024 * 1024 {
            format!("{:.1} KB", size as f64 / 1024.0)
        } else if size < 1024 * 1024 * 1024 {
            format!("{:.1} MB", size as f64 / (1024.0 * 1024.0))
        } else {
            format!("{:.2} GB", size as f64 / (1024.0 * 1024.0 * 1024.0))
        };
        Some(SmartAction {
            kind: SmartKind::LocalFile,
            title: format!("定位文件 · {file_name}"),
            subtitle: format!("{normalized} ({size_str})"),
            primary: SmartActionSpec { kind: "locate_path".into(), arg: normalized.clone() },
            secondary: Some(SmartActionSpec { kind: "open_file".into(), arg: normalized.clone() }),
            preview_color: None,
            hex_color: None,
            rgb_color: None,
            hsl_color: None,
            extraction_code: None,
            target_path: Some(normalized),
        })
    }
}

fn urldecode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let Ok(v) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

// ---- GitHub / 网盘 ----

fn detect_deep_link(text: &str) -> Option<SmartAction> {
    // GitHub 仓库
    let gh_re = re(GITHUB_REPO_RE);
    if let Some(m) = gh_re.captures(text) {
        let owner = m.get(1)?.as_str().to_string();
        let mut repo = m.get(2)?.as_str().trim_end_matches('/').to_string();
        if repo.to_lowercase().ends_with(".git") {
            repo = repo[..repo.len() - 4].to_string();
        }
        let repo_url = format!("https://github.com/{owner}/{repo}");
        return Some(SmartAction {
            kind: SmartKind::GitHub,
            title: format!("GitHub 仓库 · {owner}/{repo}"),
            subtitle: repo_url.clone(),
            primary: SmartActionSpec { kind: "open_url".into(), arg: repo_url.clone() },
            secondary: Some(SmartActionSpec {
                kind: "copy_text".into(),
                arg: format!("git clone {repo_url}.git"),
            }),
            preview_color: None,
            hex_color: None,
            rgb_color: None,
            hsl_color: None,
            extraction_code: None,
            target_path: None,
        });
    }
    detect_netdisk(text)
}

const NETDISK_HOSTS: &[(&str, &str)] = &[
    ("pan.baidu.com", "百度网盘"),
    ("百度网盘", "百度网盘"),
    ("aliyundrive.com", "阿里云盘"),
    ("alipan.com", "阿里云盘"),
    ("阿里云盘", "阿里云盘"),
    ("pan.quark.cn", "夸克网盘"),
    ("夸克网盘", "夸克网盘"),
    ("123pan.com", "123云盘"),
    ("123684.com", "123云盘"),
    ("123云盘", "123云盘"),
    ("lanzou", "蓝奏云"),
    ("蓝奏云", "蓝奏云"),
    ("115.com", "115网盘"),
    ("115网盘", "115网盘"),
    ("cloud.189.cn", "天翼云盘"),
    ("天翼云盘", "天翼云盘"),
    ("pan.xunlei.com", "迅雷云盘"),
    ("迅雷云盘", "迅雷云盘"),
];

fn detect_netdisk(text: &str) -> Option<SmartAction> {
    let lower = text.to_lowercase();
    let (_, name) = NETDISK_HOSTS.iter().find(|(host, _)| {
        if host.chars().all(|c| c.is_ascii()) {
            lower.contains(host)
        } else {
            text.contains(host)
        }
    })?;
    let name = name.to_string();

    let url_re = re(URL_RE);
    let netdisk_url = url_re.find(text).map(|m| m.as_str().to_string());

    let code_re = re(EXTRACT_CODE_RE);
    let extraction_code = code_re.captures(text).and_then(|m| {
        m.get(1).or_else(|| m.get(2)).map(|g| g.as_str().to_string())
    });

    if netdisk_url.is_none() && extraction_code.is_none() {
        return None;
    }

    let title = match &extraction_code {
        Some(c) => format!("{name} · 提取码: {c}"),
        None => format!("{name} 链接"),
    };
    let subtitle = netdisk_url.clone().unwrap_or_else(|| text.to_string());
    Some(SmartAction {
        kind: SmartKind::NetDisk,
        title,
        subtitle,
        primary: SmartActionSpec {
            kind: "open_url".into(),
            arg: netdisk_url.unwrap_or_default(),
        },
        secondary: extraction_code.as_ref().map(|c| SmartActionSpec {
            kind: "copy_text".into(),
            arg: c.clone(),
        }),
        preview_color: None,
        hex_color: None,
        rgb_color: None,
        hsl_color: None,
        extraction_code,
        target_path: None,
    })
}

// ---- 通用网址 ----

fn detect_general_url(text: &str) -> Option<SmartAction> {
    let lower = text.to_lowercase();
    if !lower.starts_with("http://") && !lower.starts_with("https://") {
        return None;
    }
    let url = text.trim();
    let host = url
        .strip_prefix("https://")
        .or_else(|| url.strip_prefix("http://"))
        .and_then(|rest| rest.split(['/', '?', ':']).next())
        .unwrap_or("");
    let title = if !host.is_empty() {
        format!("访问 {host}")
    } else {
        "打开网页链接".into()
    };
    Some(SmartAction {
        kind: SmartKind::Url,
        title,
        subtitle: url.to_string(),
        primary: SmartActionSpec { kind: "open_url".into(), arg: url.to_string() },
        secondary: None,
        preview_color: None,
        hex_color: None,
        rgb_color: None,
        hsl_color: None,
        extraction_code: None,
        target_path: None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const ALL: SmartToggles = SmartToggles { color: true, path: true, deep_link: true, netdisk: true, url: true };

    #[test]
    fn hex_color() {
        let a = detect("#FF8800", ALL, false).unwrap();
        assert!(matches!(a.kind, SmartKind::Color));
        assert_eq!(a.hex_color.as_deref(), Some("#FF8800"));
        assert_eq!(a.rgb_color.as_deref(), Some("rgb(255, 136, 0)"));
        // 短格式展开
        let a = detect("#F80", ALL, false).unwrap();
        assert_eq!(a.hex_color.as_deref(), Some("#FF8800"));
        // RGBA
        let a = detect("#FF880080", ALL, false).unwrap();
        assert_eq!(a.hex_color.as_deref(), Some("#FF880080"));
    }

    #[test]
    fn rgb_hsl_color() {
        let a = detect("rgb(255, 136, 0)", ALL, false).unwrap();
        assert_eq!(a.hex_color.as_deref(), Some("#FF8800"));
        let a = detect("hsl(32, 100%, 50%)", ALL, false).unwrap();
        assert_eq!(a.hex_color.as_deref(), Some("#FF8800"));
    }

    #[test]
    fn github_repo() {
        let a = detect("https://github.com/yixing233/nexclip/issues", ALL, false).unwrap();
        assert!(matches!(a.kind, SmartKind::GitHub));
        assert_eq!(a.title, "GitHub 仓库 · yixing233/nexclip");
        // .git 后缀剥离
        let a = detect("https://github.com/u/r.git", ALL, false).unwrap();
        assert_eq!(a.title, "GitHub 仓库 · u/r");
    }

    #[test]
    fn netdisk_extraction() {
        let text = "链接:https://pan.baidu.com/s/1abc 提取码:ab12 复制这段内容";
        let a = detect(text, ALL, false).unwrap();
        assert!(matches!(a.kind, SmartKind::NetDisk));
        assert_eq!(a.extraction_code.as_deref(), Some("ab12"));
        assert_eq!(a.title, "百度网盘 · 提取码: ab12");
        // pwd= 参数形式
        let a = detect("https://pan.quark.cn/s/1?pwd=xyz9", ALL, false).unwrap();
        assert_eq!(a.extraction_code.as_deref(), Some("xyz9"));
    }

    #[test]
    fn general_url() {
        let a = detect("https://example.com/page?x=1", ALL, false).unwrap();
        assert!(matches!(a.kind, SmartKind::Url));
        assert_eq!(a.title, "访问 example.com");
    }

    #[test]
    fn toggles_gate_toast() {
        let off = SmartToggles { color: false, path: false, deep_link: false, netdisk: false, url: false };
        // forToast=true 且开关关闭 → None;列表卡片(forToast=false)仍返回完整元数据
        assert!(detect("#FF8800", off, true).is_none());
        assert!(detect("#FF8800", off, false).is_some());
        let url_only = SmartToggles { color: false, path: false, deep_link: false, netdisk: false, url: true };
        assert!(detect("https://example.com", url_only, true).is_some());
        assert!(detect("#FF8800", url_only, true).is_none());
    }

    #[test]
    fn rejects_long_and_garbage() {
        assert!(detect("", ALL, false).is_none());
        assert!(detect(&"x".repeat(2001), ALL, false).is_none());
        assert!(detect("普通文本内容", ALL, false).is_none());
    }
}
