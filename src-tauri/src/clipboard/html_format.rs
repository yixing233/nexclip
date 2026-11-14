//! CF_HTML 剪贴板 HTML 格式的生成与解析。
//!
//! 对应 C# 侧 WinRT 的 `HtmlFormatHelper.CreateHtmlFormat` / `GetStaticFragment`。
//! Rust 端没有 WinRT 投影,按 MSDN「HTML Clipboard Format」规范手搓:
//! 头部为固定字段 + 10 位补零的偏移量,偏移量按 UTF-8 字节计
//! (与 Chrome/Edge/Office 的写法一致;含中文等非 ASCII 片段时字节偏移 ≠ 字符偏移)。

const START_FRAGMENT_MARK: &str = "<!--StartFragment-->";
const END_FRAGMENT_MARK: &str = "<!--EndFragment-->";
// Phase 2 剪贴板写入路径诊断也会用到
#[allow(dead_code)]
const HEADER_LINE_COUNT: usize = 5;

/// 把 HTML 片段包装成完整 CF_HTML(含头部、HTML 骨架与 StartFragment/EndFragment 注释)。
pub fn create_html_format(fragment: &str) -> String {
    let fragment = fragment.trim_start_matches('\u{feff}');

    // 先用占位偏移量构造骨架,再回填真实偏移(字段宽度固定 10,骨架长度稳定)
    let header = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
    let html_prefix = "<html><body>\r\n";
    let html_suffix = "\r\n</body>\r\n</html>";

    let header_len = header.len(); // ASCII,字符数 == 字节数
    let start_html = header_len;
    let start_fragment = start_html + html_prefix.len() + START_FRAGMENT_MARK.len();
    let end_fragment = start_fragment + fragment.len();
    let end_html = end_fragment + END_FRAGMENT_MARK.len() + html_suffix.len();

    format!(
        "Version:0.9\r\nStartHTML:{start_html:010}\r\nEndHTML:{end_html:010}\r\nStartFragment:{start_fragment:010}\r\nEndFragment:{end_fragment:010}\r\n<html><body>\r\n{START_FRAGMENT_MARK}{fragment}{END_FRAGMENT_MARK}\r\n</body>\r\n</html>"
    )
}

/// 从 CF_HTML 中提取 HTML 片段(即 StartFragment/EndFragment 注释之间的内容)。
///
/// 行为对齐 WinRT `GetStaticFragment`:头部缺失或注释标记不存在时返回 None
/// (调用方按"剪贴板无可用 HTML"处理,退回纯文本)。
pub fn get_static_fragment(html_format: &str) -> Option<String> {
    let start = find_header_offset(html_format, "StartHTML:")?;
    let end = find_header_offset(html_format, "EndHTML:")?;
    if end < start {
        return None;
    }
    // 部分来源(如剪贴板代理)会把 EndHTML 写成越界值,钳制到实际长度
    let bytes = html_format.as_bytes();
    let end = end.min(bytes.len());
    let body = &html_format[start..end];

    let fs = body.find(START_FRAGMENT_MARK)? + START_FRAGMENT_MARK.len();
    let fe = body[fs..].find(END_FRAGMENT_MARK)? + fs;
    Some(body[fs..fe].to_string())
}

/// 解析头部 10 位偏移字段(按 UTF-8 字节计)。
fn find_header_offset(s: &str, field: &str) -> Option<usize> {
    let pos = s.find(field)?;
    let digits_start = pos + field.len();
    let digits: String = s[digits_start..].chars().take(10).collect();
    if digits.len() < 10 || !digits.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    digits.parse::<usize>().ok()
}

/// 校验头部的行数(测试与诊断用)。
#[cfg(test)]
pub fn header_field_count(s: &str) -> usize {
    s.lines().take(HEADER_LINE_COUNT).count()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn create_then_parse_roundtrip_ascii() {
        let cf = create_html_format("<b>hello</b>");
        // 头部五行齐全
        assert!(cf.starts_with("Version:0.9\r\n"));
        assert_eq!(header_field_count(&cf), 5);
        // 标记必须落在 HTML 骨架内,且骨架在偏移量之后
        assert!(cf.contains(START_FRAGMENT_MARK));
        let frag = get_static_fragment(&cf).unwrap();
        assert_eq!(frag, "<b>hello</b>");
    }

    #[test]
    fn offsets_are_byte_based_and_consistent() {
        let cf = create_html_format("中文<b>片段</b>");
        let start_fragment = find_header_offset(&cf, "StartFragment:").unwrap();
        let end_fragment = find_header_offset(&cf, "EndFragment:").unwrap();
        // StartFragment 指向的应是标记结尾之后(字节偏移落在非 ASCII 内容上)
        assert!(cf[start_fragment..].starts_with("中文"));
        let frag = get_static_fragment(&cf).unwrap();
        assert_eq!(frag, "中文<b>片段</b>");
        assert!(end_fragment > start_fragment);
    }

    #[test]
    fn parse_legacy_borwsers_format() {
        // 模拟浏览器写入的真实 CF_HTML(EndHTML 覆盖整个文档,而非仅片段)
        let cf = "Version:0.9\r\nStartHTML:0000000107\r\nEndHTML:0000000314\r\nStartFragment:0000000143\r\nEndFragment:0000000277\r\n<html><body>\r\n<!--StartFragment--><p style=\"color:red\">你好世界</p><!--EndFragment-->\r\n</body>\r\n</html>";
        let frag = get_static_fragment(&cf).unwrap();
        assert_eq!(frag, "<p style=\"color:red\">你好世界</p>");
    }

    #[test]
    fn malformed_returns_none() {
        assert!(get_static_fragment("<b>no header</b>").is_none());
        assert!(get_static_fragment("").is_none());
        let no_mark = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000100\r\nStartFragment:0000000020\r\nEndFragment:0000000060\r\n<html><body>plain</body></html>";
        assert!(get_static_fragment(no_mark).is_none());
        // EndHTML 写爆:钳制到实际长度后仍能解析
        let over = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:9999999999\r\nStartFragment:0000000020\r\nEndFragment:0000000060\r\n<html><body>\r\n<!--StartFragment-->abc<!--EndFragment-->\r\n</body>\r\n</html>";
        assert_eq!(get_static_fragment(over).unwrap(), "abc");
    }
}
