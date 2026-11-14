//! 文件条目元数据(仅路径与大小,不包含任何文件内容)。
//!
//! 移植自 C# `Models/ClipboardFileInfo.cs` + `ClipboardFileMeta`。
//! JSON 字段名必须与旧版 C# 保持一致(PascalCase):历史库 file_paths 列
//! 存的是旧版序列化的 JSON,字段名变化会导致旧数据解析失败。

use serde::{Deserialize, Serialize};

use super::hash;

/// 单条记录最多保留的文件数。整目录选中时文件数可达数万,
/// 不设上限会生成超大 JSON,既拖慢列表渲染也放大数据库体积。
pub const MAX_FILES: usize = 100;

/// 文件或文件夹的元数据(仅路径与大小)。
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ClipboardFileInfo {
    /// 文件或文件夹的本地绝对路径。
    #[serde(rename = "Path")]
    pub path: String,
    /// 文件名(含扩展名)或文件夹名,用于列表展示。
    #[serde(rename = "Name")]
    pub name: String,
    /// 是否为文件夹。
    #[serde(rename = "IsDirectory")]
    pub is_directory: bool,
    /// 文件字节数;文件夹不递归统计,固定为 0。
    #[serde(rename = "SizeBytes")]
    pub size_bytes: i64,
}

/// 序列化为紧凑 JSON(旧版兼容的 PascalCase 字段名)。
pub fn serialize(files: &[ClipboardFileInfo]) -> String {
    if files.is_empty() {
        return String::new();
    }
    serde_json::to_string(files).unwrap_or_default()
}

/// 反序列化;内容缺失或损坏时返回空列表,不抛异常。
pub fn parse(json: Option<&str>) -> Vec<ClipboardFileInfo> {
    let json = match json {
        Some(s) if !s.trim().is_empty() => s,
        _ => return Vec::new(),
    };
    serde_json::from_str::<Vec<ClipboardFileInfo>>(json).unwrap_or_default()
}

/// 内容哈希(仅元数据)。输入为元数据串而非文件字节,大文件也不产生磁盘读取开销。
///
/// \u0001 分隔字段、\u0002 分隔记录:路径中不可能出现这两个控制字符,不会产生歧义拼接。
pub fn compute_hash(files: &[ClipboardFileInfo]) -> String {
    if files.is_empty() {
        return String::new();
    }
    let mut buf = String::with_capacity(files.len() * 64);
    for f in files {
        buf.push_str(&f.path);
        buf.push('\u{0001}');
        buf.push_str(&f.size_bytes.to_string());
        buf.push('\u{0001}');
        buf.push(if f.is_directory { '1' } else { '0' });
        buf.push('\u{0002}');
    }
    hash::hash_bytes(buf.as_bytes())
}

/// 字节数转可读文本(1.5 MB 形式);用于列表元信息展示。
pub fn format_size(bytes: i64) -> String {
    if bytes <= 0 {
        return "0 B".to_string();
    }
    if bytes < 1024 {
        return format!("{bytes} B");
    }
    let mut v = bytes as f64 / 1024.0;
    if v < 1024.0 {
        return format!("{} KB", trim_num(v));
    }
    v /= 1024.0;
    if v < 1024.0 {
        return format!("{} MB", trim_num(v));
    }
    v /= 1024.0;
    format!("{} GB", trim_num2(v))
}

/// "0.#" 一位小数,整数不带小数点(对应 C# 的 {v:0.#})。
fn trim_num(v: f64) -> String {
    let rounded = (v * 10.0).round() / 10.0;
    if (rounded - rounded.trunc()).abs() < f64::EPSILON {
        format!("{}", rounded as i64)
    } else {
        format!("{rounded:.1}")
    }
}

/// "0.##" 至多两位小数(对应 C# 的 {v:0.##})。
fn trim_num2(v: f64) -> String {
    let rounded = (v * 100.0).round() / 100.0;
    if (rounded - rounded.trunc()).abs() < f64::EPSILON {
        format!("{}", rounded as i64)
    } else if ((rounded * 100.0).round() as i64) % 10 == 0 {
        // 第二位小数为 0,如 1.40 → 1.4
        format!("{rounded:.1}")
    } else {
        format!("{rounded:.2}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_field_names_are_pascal_case() {
        // 旧版 C# 序列化产物(PascalCase),新代码必须能解析
        let legacy = r#"[{"Path":"C:\\a\\b.txt","Name":"b.txt","IsDirectory":false,"SizeBytes":123}]"#;
        let files = parse(Some(legacy));
        assert_eq!(files.len(), 1);
        assert_eq!(files[0].path, r"C:\a\b.txt");
        assert_eq!(files[0].size_bytes, 123);

        // 新代码序列化同样产出 PascalCase
        let out = serialize(&files);
        assert!(out.contains("\"Path\""));
        assert!(out.contains("\"IsDirectory\""));
    }

    #[test]
    fn parse_invalid_returns_empty() {
        assert!(parse(None).is_empty());
        assert!(parse(Some("")).is_empty());
        assert!(parse(Some("not json")).is_empty());
    }

    #[test]
    fn file_meta_hash_stable() {
        let files = vec![ClipboardFileInfo {
            path: r"C:\a\b.txt".into(),
            name: "b.txt".into(),
            is_directory: false,
            size_bytes: 5,
        }];
        let h1 = compute_hash(&files);
        let h2 = compute_hash(&files);
        assert_eq!(h1, h2);
        assert_eq!(h1.len(), 64);
        assert!(h1.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
        assert!(compute_hash(&[]).is_empty());
    }

    #[test]
    fn format_sizes() {
        assert_eq!(format_size(0), "0 B");
        assert_eq!(format_size(-5), "0 B");
        assert_eq!(format_size(512), "512 B");
        assert_eq!(format_size(1024), "1 KB");
        assert_eq!(format_size(1536), "1.5 KB");
        assert_eq!(format_size(1024 * 1024), "1 MB");
        assert_eq!(format_size(1_572_864), "1.5 MB");
        assert_eq!(format_size(3 * 1024 * 1024 * 1024), "3 GB");
        assert_eq!(format_size(1_500_000_000), "1.4 GB");
    }
}
