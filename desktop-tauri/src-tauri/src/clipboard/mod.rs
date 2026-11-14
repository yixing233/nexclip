//! 剪贴板数据格式层(Phase 1):文件元数据、内容哈希、CF_HTML。
//!
//! 本阶段只包含与存储/去重相关的纯逻辑;读剪贴板、写剪贴板的平台实现
//! 在 Phase 2 的 `clipboard/monitor.rs` / `clipboard/writer.rs` 中。

pub mod files_meta;
pub mod hash;
pub mod html_format;
