//! 本地历史(SQLite)。
//!
//! 移植自 C# `Services/HistoryStore.cs`,数据库文件、schema、迁移路径、
//! 查询语义与旧版完全兼容:直接打开旧版生成的 history.db 即可继承全部历史。

pub mod pinyin;
mod store;

pub use store::{millis_to_ticks, now_ticks, ticks_to_millis, HistoryItem, HistoryStore, ORIGIN_LOCAL, ORIGIN_MANUAL, ORIGIN_REMOTE, TYPE_FILE, TYPE_IMAGE, TYPE_TEXT};
