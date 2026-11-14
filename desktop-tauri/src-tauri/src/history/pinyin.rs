//! 中文拼音检索工具。
//!
//! 移植自 C# `Services/PinyinHelper.cs`:基于嵌入的「音节 → 汉字」表
//! (源自 Unicode Unihan 的 kMandarin / kXHC1983),同时提供全拼与首字母两种检索形态。
//!
//! 多音字处理:每个汉字同时给出「主读音」与「一个最常用备选读音」两组结果,
//! 例如「重庆」主读音 zhongqing、备选 chongqing,两种输入都能命中。
//! 备选只取一个,不做读音组合爆炸。

use std::sync::OnceLock;

/// 音节表;下标 0 保留表示"无数据"。
const PRIMARY_TABLE: &str = include_str!("../../assets/pinyin.txt");

/// CJK 统一汉字基本区起始码点。
const BASE: u32 = 0x4E00;
/// 基本区码点跨度(U+4E00..U+9FFF)。
const SPAN: usize = (0x9FFF - 0x4E00 + 1) as usize;

struct PinyinTable {
    /// 音节表;下标 0 保留表示"无数据"。
    syllables: Vec<String>,
    /// 码点 → 主读音音节下标(0 表示无数据)。
    primary: Vec<u16>,
    /// 码点 → 备选读音音节下标(0 表示无备选)。
    alt: Vec<u16>,
}

impl PinyinTable {
    fn load() -> PinyinTable {
        let mut table = PinyinTable {
            syllables: vec![String::new()],
            primary: vec![0u16; SPAN],
            alt: vec![0u16; SPAN],
        };
        let mut index: std::collections::HashMap<&str, usize> =
            std::collections::HashMap::new();
        index.insert("", 0);
        // 0=未进入任何段;1=主读音段;2=备选读音段
        let mut section: u8 = 0;

        for line in PRIMARY_TABLE.lines() {
            if line.is_empty() {
                continue;
            }
            if let Some(rest) = line.strip_prefix('#') {
                section = if rest.to_ascii_lowercase().contains("alt") { 2 } else { 1 };
                continue;
            }
            let sep = match line.find(':') {
                Some(i) if i > 0 => i,
                _ => continue,
            };

            let pinyin = &line[..sep];
            let slot = match index.get(pinyin) {
                Some(&s) => s,
                None => {
                    let s = table.syllables.len();
                    if s > u16::MAX as usize {
                        log::warn!("拼音表音节数超出索引上限,后续音节将被忽略");
                        break;
                    }
                    table.syllables.push(pinyin.to_string());
                    index.insert(pinyin, s);
                    s
                }
            };

            let target = if section == 2 { &mut table.alt } else { &mut table.primary };
            for ch in line[sep + 1..].chars() {
                let offset = match ch as u32 >= BASE && (ch as u32) < BASE + SPAN as u32 {
                    true => (ch as u32 - BASE) as usize,
                    false => continue,
                };
                // 每个字在每个段内只应出现一次;保留首次出现的音节,后续重复忽略
                if target[offset] == 0 {
                    target[offset] = slot as u16;
                }
            }
        }
        table
    }

    #[inline]
    fn slot_for(&self, use_alt: bool, offset: usize) -> usize {
        let primary = self.primary[offset];
        let alt = self.alt[offset];
        let slot = if use_alt && alt != 0 { alt } else { primary };
        slot as usize
    }
}

static TABLE: OnceLock<PinyinTable> = OnceLock::new();

fn table() -> Option<&'static PinyinTable> {
    // 表缺失(编译异常等)时拼音检索整体降级为"无命中",字面检索不受影响
    Some(TABLE.get_or_init(PinyinTable::load))
}

/// 全拼串(主读音)。标点与空白被跳过,保证音节连续。
pub fn full_pinyin(text: &str) -> String {
    build(text, false, false)
}

/// 全拼串的备选读音变体;文本不含多音字时与 [`full_pinyin`] 结果相同。
pub fn full_pinyin_variant(text: &str) -> String {
    build(text, true, false)
}

/// 首字母串(主读音)。
pub fn initials(text: &str) -> String {
    build(text, false, true)
}

/// 首字母串的备选读音变体;文本不含多音字时与 [`initials`] 结果相同。
pub fn initials_variant(text: &str) -> String {
    build(text, true, true)
}

fn build(text: &str, use_alt: bool, as_initial: bool) -> String {
    if text.is_empty() {
        return String::new();
    }
    let t = match table() {
        Some(t) => t,
        None => return String::new(),
    };
    let mut out = String::with_capacity(if as_initial { text.len() } else { text.len() * 3 });
    for c in text.chars() {
        if (c as u32) < 128 {
            // 半角字母数字原样保留(小写),使 "v2"、"git" 这类混合内容也能被拼音串命中
            if c.is_ascii_alphanumeric() {
                out.push(c.to_ascii_lowercase());
            }
            continue;
        }

        let code = c as u32;
        if code < BASE || code >= BASE + SPAN as u32 {
            continue;
        }
        let slot = t.slot_for(use_alt, (code - BASE) as usize);
        if slot == 0 {
            continue;
        }
        let pinyin = &t.syllables[slot];
        if pinyin.is_empty() {
            continue;
        }
        if as_initial {
            out.push(pinyin.chars().next().unwrap());
        } else {
            out.push_str(pinyin);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn table_loads() {
        let t = table().unwrap();
        assert!(t.syllables.len() > 400, "音节数异常: {}", t.syllables.len());
        assert!(t.primary.iter().any(|&s| s != 0), "主读音表为空");
        assert!(t.alt.iter().any(|&s| s != 0), "备选读音表为空");
    }

    #[test]
    fn full_pinyin_basic() {
        assert_eq!(full_pinyin("我的文件"), "wodewenjian");
        assert_eq!(initials("我的文件"), "wdwj");
    }

    #[test]
    fn polyphonic_variants() {
        // 重庆:主读音 zhongqing,备选 chongqing
        let main = full_pinyin("重庆");
        let alt = full_pinyin_variant("重庆");
        assert!(main.starts_with("zhongqing"), "got {main}");
        assert!(alt.starts_with("chongqing"), "got {alt}");
        // 无多音字时变体与主读音相同("的""件"等在表中有备选读音,用单音字样本)
        assert_eq!(full_pinyin("人"), full_pinyin_variant("人"));
        assert_eq!(initials("人民"), initials_variant("人民"));
    }

    #[test]
    fn mixed_ascii_and_punctuation_skipped() {
        // 标点/空白跳过、半角字母数字小写保留
        assert_eq!(full_pinyin("我的文件 v2"), "wodewenjianv2");
        assert_eq!(initials("GitHub Gist!"), "githubgist");
    }

    #[test]
    fn non_cjk_chars_ignored() {
        assert_eq!(full_pinyin("🙂👍"), "");
        // 日文假名不在基本区映射表内,不产出音节
        assert_eq!(full_pinyin("あ"), "");
    }
}
