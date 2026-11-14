//! 内容哈希:SHA-256 十六进制小写(与 C# Convert.ToHexStringLower 一致)。

use sha2::{Digest, Sha256};

/// SHA-256 十六进制小写。
pub fn hash_bytes(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let mut out = String::with_capacity(64);
    for b in digest {
        out.push_str(&format!("{b:02x}"));
    }
    out
}

/// 文本内容哈希。html 非空时把它一并纳入(\u0001 分隔),
/// 使同一段文字的"纯文本版"与"富文本版"成为两条独立记录,
/// 而不会互相 TouchByHash 置顶把富文本吃掉。
pub fn hash_text(text: &str, html: Option<&str>) -> String {
    match html {
        Some(h) if !h.is_empty() => {
            let mut joined = String::with_capacity(text.len() + h.len() + 1);
            joined.push_str(text);
            joined.push('\u{0001}');
            joined.push_str(h);
            hash_bytes(joined.as_bytes())
        }
        _ => hash_bytes(text.as_bytes()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_lowercase_hex() {
        // 已知向量:sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
        assert_eq!(
            hash_bytes(b"hello"),
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"
        );
    }

    #[test]
    fn hash_text_html_separates() {
        let plain = hash_text("hello", None);
        assert_eq!(plain, hash_text("hello", Some("")));
        assert_ne!(plain, hash_text("hello", Some("<b>hello</b>")));
    }
}
