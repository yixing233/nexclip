//! Windows 平台层(Phase 2):全局热键、剪贴板监听/写入、粘贴回注。
//!
//! 设计约束(沿用旧版踩坑注释,不得违背):
//! - 剪贴板读写绝不裸跑在 GUI 主线程(OLE 跨进程调用无超时,会卡死 UI);
//!   写入走专用线程,监听走专用消息窗口线程。
//! - 写入后记录剪贴板序列号,监听端据此跳过自己写的内容(防回环)。
//! - 粘贴回注:写入 → 恢复前台焦点 → SendInput 原子注入;注入前 AllKeysUp
//!   并等待修饰键抬起,避免 Ctrl+V 被游戏/输入法吞键。

pub mod app_icon;
pub mod hotkey;
pub mod monitor;
pub mod paste;
pub mod processes;
pub mod writer;
