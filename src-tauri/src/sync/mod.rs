//! 同步层(Phase 3):REST 客户端 + WebSocket 推送 + 同步引擎。
//!
//! REST 端点与鉴权方式与旧版 ServerApi 逐一对齐(X-Device-Id/X-Device-Token 请求头);
//! 错误码语义沿用旧版 ApiException:凭证类错误按响应体 code 区分
//! (credential_missing / credential_invalid / credential_revoked / device_identity_mismatch),
//! 避免把 401 一刀切清掉有效凭证。
//!
//! WebSocket 推送替代 SignalR:原生 WS 协议见 docs/ws-protocol.md
//! (server 分支需按该文档把 /hubs/clipboard 改造为原生 WS)。

pub mod api;
pub mod engine;
pub mod push;
