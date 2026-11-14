//! WebSocket 推送客户端 —— 双协议自动探测:
//!
//! 1. **SignalR 模式**(现有服务端,无需服务端改造):POST
//!    `/hubs/clipboard/negotiate` 成功 → SignalR JSON 协议
//!    (握手帧 `{"protocol":"json","version":1}\x1e`、type:1 调用帧、
//!    type:6 ping,记录分隔符 `\x1e`);
//! 2. **原生 WS 模式**(docs/ws-protocol.md,server 迁移后的目标协议):
//!    negotiate 404 → 直连 WS,解析 `{"event":..,"data":..}` 帧。
//!
//! 鉴权统一走请求头 X-Device-Id / X-Device-Token(绝不进 URL);
//! 重连策略与旧版 PushClient 一致:0/2/10/30/60s,设备凭证类失败永久停止。

use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use futures_util::FutureExt;
use tokio::sync::mpsc;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::Message;

/// 推送事件(转发给同步引擎处理)。
#[derive(Debug, Clone)]
pub enum PushEvent {
    EntryReceived(serde_json::Value),
    FileTransferred(serde_json::Value),
    FileDeleted(String),
    DevicesChanged,
}

/// 推送连接状态(与旧版 StateChanged 字符串一一对应)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PushState {
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
}

impl PushState {
    pub fn as_str(&self) -> &'static str {
        match self {
            PushState::Connecting => "connecting",
            PushState::Connected => "connected",
            PushState::Reconnecting => "reconnecting",
            PushState::Disconnected => "disconnected",
        }
    }
}

/// 连接参数(设置就绪时由引擎下发)。
#[derive(Debug, Clone)]
pub struct ConnParams {
    pub server_url: String,
    pub device_id: String,
    pub device_name: String,
    pub platform: String,
    pub version: String,
    pub token: String,
}

/// 推送客户端命令。
pub enum PushCommand {
    /// 以新参数(重)连接
    Connect(ConnParams),
    /// 停止推送(未配对/退出)
    Stop,
}

/// 推送客户端句柄:engine 持有 events/states,设置层持有 commands。
pub struct PushHandle {
    pub events: mpsc::Receiver<PushEvent>,
    pub states: mpsc::Receiver<PushState>,
    pub commands: mpsc::Sender<PushCommand>,
}

const RETRY_DELAYS_SECS: [u64; 4] = [2, 10, 30, 60];
/// SignalR 记录分隔符(帧定界)
const SIGNALR_DELIM: char = '\u{1e}';
/// 握手等待超时(秒)
const HANDSHAKE_TIMEOUT: u64 = 8;

/// 启动监督任务。
pub fn start() -> PushHandle {
    let (event_tx, event_rx) = mpsc::channel(64);
    let (state_tx, state_rx) = mpsc::channel(16);
    let (cmd_tx, cmd_rx) = mpsc::channel(8);
    // setup 同步上下文调用:必须用 tauri 的 async_runtime(裸 tokio::spawn 会 panic)
    tauri::async_runtime::spawn(async move {
        supervisor(cmd_rx, event_tx, state_tx).await;
    });
    PushHandle { events: event_rx, states: state_rx, commands: cmd_tx }
}

fn http_origin(server_url: &str) -> String {
    server_url.trim_end_matches('/').to_string()
}

fn ws_base(server_url: &str) -> String {
    let origin = server_url.trim_end_matches('/');
    let ws_scheme = if origin.starts_with("https://") { "wss" } else { "ws" };
    let host = origin
        .strip_prefix("https://")
        .or_else(|| origin.strip_prefix("http://"))
        .unwrap_or(origin);
    format!("{ws_scheme}://{host}")
}

/// 原生 WS 模式的注册 URL(携带非敏感登记信息)
fn build_ws_url(
    server_url: &str, device_id: &str, device_name: &str,
    platform: &str, version: &str,
) -> String {
    let mut url = format!(
        "{}/hubs/clipboard?deviceId={}",
        ws_base(server_url),
        urlencode(device_id)
    );
    if !device_name.trim().is_empty() {
        url += &format!("&deviceName={}", urlencode(device_name));
    }
    if !platform.trim().is_empty() {
        url += &format!("&platform={}", urlencode(platform));
    }
    if !version.trim().is_empty() {
        url += &format!("&version={}", urlencode(version));
    }
    url
}

fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(b as char)
            }
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn apply_auth_headers(
    request: &mut tokio_tungstenite::tungstenite::http::Request<()>,
    params: &ConnParams,
) -> Result<(), String> {
    let headers = request.headers_mut();
    if !params.token.trim().is_empty() {
        headers.insert(
            "X-Device-Token",
            HeaderValue::from_str(&params.token).map_err(|e| e.to_string())?,
        );
    }
    if !params.device_id.trim().is_empty() {
        headers.insert(
            "X-Device-Id",
            HeaderValue::from_str(&params.device_id).map_err(|e| e.to_string())?,
        );
    }
    Ok(())
}

/// POST /hubs/clipboard/negotiate:拿 connectionId。
/// Ok(Some(conn_id)) = SignalR 服务端;Ok(None) = 404(原生 WS 服务端)。
async fn negotiate(
    http: &reqwest::Client,
    params: &ConnParams,
) -> Result<Option<String>, String> {
    let url = format!(
        "{}/hubs/clipboard/negotiate?negotiateVersion=1",
        http_origin(&params.server_url)
    );
    let resp = http
        .post(&url)
        .header("X-Device-Id", &params.device_id)
        .header("X-Device-Token", &params.token)
        .send()
        .await
        .map_err(|e| format!("WS 连接失败: {e}"))?;
    let status = resp.status().as_u16();
    if status == 404 {
        return Ok(None); // 原生 WS 服务端
    }
    if [401, 403, 410].contains(&status) {
        return Err(format!("AUTH:{status} 设备凭证无效或已被移除,请重新配对"));
    }
    if !resp.status().is_success() {
        return Err(format!("negotiate 失败: HTTP {status}"));
    }
    let json: serde_json::Value = resp.json().await.map_err(|e| e.to_string())?;
    let conn_id = json
        .get("connectionId")
        .and_then(|v| v.as_str())
        .unwrap_or_default()
        .to_string();
    if conn_id.is_empty() {
        return Err("negotiate 未返回 connectionId".into());
    }
    Ok(Some(conn_id))
}

/// SignalR JSON 帧处理:返回需要回发的帧(如 ping 应答)。
fn handle_signalr_frame(
    frame: &str,
    event_tx: &mpsc::Sender<PushEvent>,
) -> Option<String> {
    let v: serde_json::Value = serde_json::from_str(frame).ok()?;
    match v.get("type").and_then(|t| t.as_i64()) {
        // type 1 = 调用:{"target":"...","arguments":[...]}
        Some(1) => {
            let target = v.get("target").and_then(|t| t.as_str()).unwrap_or("");
            let arg0 = v
                .get("arguments")
                .and_then(|a| a.as_array())
                .and_then(|a| a.first().cloned())
                .unwrap_or(serde_json::Value::Null);
            let pe = match target {
                "ClipboardUpdated" => PushEvent::EntryReceived(arg0),
                "FileTransferred" => PushEvent::FileTransferred(arg0),
                "FileDeleted" => {
                    PushEvent::FileDeleted(arg0.as_str().unwrap_or_default().to_string())
                }
                "DevicesChanged" => PushEvent::DevicesChanged,
                _ => return None,
            };
            let _ = event_tx.send(pe).now_or_never();
            None
        }
        // type 6 = ping → 回 pong 帧
        Some(6) => Some(format!("{{\"type\":6}}{SIGNALR_DELIM}")),
        // 其余(握手响应 {}、completion、close)忽略
        _ => None,
    }
}

/// SignalR 泵:连接 → 握手 → 帧循环。
async fn pump_signalr(
    _http: &reqwest::Client,
    conn_id: &str,
    params: &ConnParams,
    event_tx: mpsc::Sender<PushEvent>,
) -> Result<(), String> {
    let url = format!(
        "{}/hubs/clipboard?id={}&deviceId={}",
        ws_base(&params.server_url),
        urlencode(conn_id),
        urlencode(&params.device_id)
    );
    let mut request = url.into_client_request().map_err(|e| e.to_string())?;
    apply_auth_headers(&mut request, &params)?;

    let (ws, _resp) = tokio_tungstenite::connect_async(request)
        .await
        .map_err(|e| format!("WS 连接失败: {e}"))?;
    let (mut sink, mut stream) = ws.split();

    // SignalR 握手帧
    let handshake = format!("{{\"protocol\":\"json\",\"version\":1}}{SIGNALR_DELIM}");
    sink.send(Message::Text(handshake.into()))
        .await
        .map_err(|e| e.to_string())?;

    // 握手应答:{} 成功 / {"error":"..."} 失败(带超时)
    let mut buf = String::new();
    let handshake_result = tokio::time::timeout(Duration::from_secs(HANDSHAKE_TIMEOUT), async {
        while let Some(msg) = stream.next().await {
            match msg {
                Ok(Message::Text(text)) => {
                    buf.push_str(&text);
                    while let Some(pos) = buf.find(SIGNALR_DELIM) {
                        let frame: String = buf.drain(..pos + 1).collect();
                        let frame = frame.trim_end_matches(SIGNALR_DELIM).trim();
                        if frame.is_empty() {
                            continue;
                        }
                        if let Ok(v) = serde_json::from_str::<serde_json::Value>(frame) {
                            if let Some(err) = v.get("error").and_then(|e| e.as_str()) {
                                return Err(err.to_string());
                            }
                            // 无 error 字段 = 握手成功({})
                            return Ok(());
                        }
                    }
                }
                Ok(_) => continue,
                Err(e) => return Err(format!("WS 错误: {e}")),
            }
        }
        Err("连接已关闭".into())
    })
    .await
    .unwrap_or_else(|_| Err("SignalR 握手超时".into()));
    handshake_result.map_err(|e| {
        if e.contains("401") || e.contains("403") || e.contains("410") || e.contains("凭证") {
            format!("AUTH:{e}")
        } else {
            format!("SignalR 握手失败: {e}")
        }
    })?;

    log::info!("推送通道已连接(SignalR 模式)");

    // 帧循环:解析调用帧、应答 ping
    let mut keepalive = tokio::time::interval(Duration::from_secs(10));
    keepalive.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    loop {
        tokio::select! {
            _ = keepalive.tick() => {
                let ping = format!("{{\"type\":6}}{SIGNALR_DELIM}");
                if sink.send(Message::Text(ping.into())).await.is_err() {
                    return Ok(());
                }
            }
            msg = stream.next() => {
                match msg {
                    Some(Ok(Message::Text(text))) => {
                        buf.push_str(&text);
                        // 按分隔符切出完整帧(残留半帧留在 buf)
                        while let Some(pos) = buf.find(SIGNALR_DELIM) {
                            let frame: String = buf.drain(..pos + 1).collect();
                            let frame = frame.trim_end_matches(SIGNALR_DELIM).trim();
                            if frame.is_empty() {
                                continue;
                            }
                            if let Some(reply) = handle_signalr_frame(frame, &event_tx) {
                                if sink.send(Message::Text(reply.into())).await.is_err() {
                                    return Ok(());
                                }
                            }
                        }
                    }
                    Some(Ok(Message::Ping(data))) => {
                        if sink.send(Message::Pong(data)).await.is_err() {
                            return Ok(());
                        }
                    }
                    Some(Ok(Message::Close(_))) | None => return Ok(()),
                    Some(Ok(_)) => {}
                    Some(Err(e)) => {
                        log::warn!("推送通道错误: {e}");
                        return Ok(());
                    }
                }
            }
        }
    }
}

/// 原生 WS 泵:直接解析 {"event":..,"data":..} 帧(docs/ws-protocol.md)。
async fn pump_native(
    params: ConnParams,
    event_tx: mpsc::Sender<PushEvent>,
) -> Result<(), String> {
    let url = build_ws_url(
        &params.server_url, &params.device_id, &params.device_name,
        &params.platform, &params.version,
    );
    let mut request = url.into_client_request().map_err(|e| e.to_string())?;
    apply_auth_headers(&mut request, &params)?;

    let (ws, _resp) = tokio_tungstenite::connect_async(request)
        .await
        .map_err(|e| format!("WS 连接失败: {e}"))?;
    let (mut sink, mut stream) = ws.split();

    log::info!("推送通道已连接(原生 WS 模式)");
    let mut keepalive = tokio::time::interval(Duration::from_secs(10));
    keepalive.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);

    loop {
        tokio::select! {
            _ = keepalive.tick() => {
                if sink.send(Message::Ping(Vec::new().into())).await.is_err() {
                    return Ok(());
                }
            }
            msg = stream.next() => {
                match msg {
                    Some(Ok(Message::Text(text))) => {
                        if let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) {
                            let event = v.get("event").and_then(|x| x.as_str()).unwrap_or("");
                            let data = v.get("data").cloned().unwrap_or(serde_json::Value::Null);
                            let pe = match event {
                                "ClipboardUpdated" => PushEvent::EntryReceived(data),
                                "FileTransferred" => PushEvent::FileTransferred(data),
                                "FileDeleted" => PushEvent::FileDeleted(
                                    data.as_str().unwrap_or_default().to_string(),
                                ),
                                "DevicesChanged" => PushEvent::DevicesChanged,
                                _ => continue,
                            };
                            if event_tx.send(pe).await.is_err() {
                                return Ok(());
                            }
                        }
                    }
                    Some(Ok(Message::Ping(data))) => {
                        if sink.send(Message::Pong(data)).await.is_err() {
                            return Ok(());
                        }
                    }
                    Some(Ok(Message::Close(_))) | None => return Ok(()),
                    Some(Ok(_)) => {}
                    Some(Err(e)) => {
                        log::warn!("推送通道错误: {e}");
                        return Ok(());
                    }
                }
            }
        }
    }
}

/// 统一入口:negotiate 探测服务端类型 → 分发到对应协议泵。
async fn pump_once(
    params: ConnParams,
    event_tx: mpsc::Sender<PushEvent>,
) -> Result<(), String> {
    let http = reqwest::Client::builder()
        .timeout(Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())?;

    match negotiate(&http, &params).await? {
        Some(conn_id) => pump_signalr(&http, &conn_id, &params, event_tx).await,
        None => pump_native(params, event_tx).await,
    }
}

/// 监督任务:连接维持 + 重连策略(0/2/10/30/60s;凭证类失败永久停止等新指令)。
async fn supervisor(
    mut cmd_rx: mpsc::Receiver<PushCommand>,
    event_tx: mpsc::Sender<PushEvent>,
    state_tx: mpsc::Sender<PushState>,
) {
    // 未配对阶段:等待第一条 Connect 指令(Stop 后回到这里继续等)
    loop {
        let params = match cmd_rx.recv().await {
            Some(PushCommand::Connect(p)) => p,
            Some(PushCommand::Stop) | None => {
                let _ = state_tx.send(PushState::Disconnected).await;
                continue;
            }
        };

        let mut current = Some(params);
        let mut attempt: usize = 0;
        loop {
            let Some(params) = current.clone() else {
                break;
            };
            let state = if attempt == 0 {
                PushState::Connecting
            } else {
                PushState::Reconnecting
            };
            let _ = state_tx.send(state).await;

            // 泵送当前连接;期间新 Connect/Stop 命令会中断它
            let pump_params = params.clone();
            let pump_tx = event_tx.clone();
            let pump = tokio::spawn(pump_once(pump_params, pump_tx));

            enum Step {
                Pump(Result<(), String>),
                Cmd(Option<PushCommand>),
            }
            let step = tokio::select! {
                res = pump => Step::Pump(res.unwrap_or_else(|_| Ok(()))),
                cmd = cmd_rx.recv() => Step::Cmd(cmd),
            };

            match step {
                // 泵自然结束(网络断开/远端关闭)→ 退避重连(与旧版一致:非凭证错误无限重试)
                Step::Pump(outcome) => {
                    if let Err(e) = &outcome {
                        if e.starts_with("AUTH:") {
                            log::error!("推送通道鉴权失败: {e}");
                            let _ = state_tx.send(PushState::Disconnected).await;
                            break; // 回到等待 Connect 指令
                        }
                    }
                    let delay = if attempt < RETRY_DELAYS_SECS.len() {
                        Duration::from_secs(RETRY_DELAYS_SECS[attempt])
                    } else {
                        Duration::from_secs(60)
                    };
                    attempt += 1;
                    let _ = state_tx.send(PushState::Reconnecting).await;
                    tokio::time::sleep(delay).await;
                }
                // 期间来了新指令:优先处理(新参数重连/停止)
                Step::Cmd(Some(PushCommand::Connect(p))) => {
                    current = Some(p);
                    attempt = 0;
                }
                Step::Cmd(Some(PushCommand::Stop)) => {
                    let _ = state_tx.send(PushState::Disconnected).await;
                    break;
                }
                Step::Cmd(None) => return, // 命令通道关闭(应用退出)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ws_url_building() {
        assert_eq!(
            build_ws_url("http://127.0.0.1:5033", "dev1", "我的电脑", "Windows", "1.0"),
            "ws://127.0.0.1:5033/hubs/clipboard?deviceId=dev1&deviceName=%E6%88%91%E7%9A%84%E7%94%B5%E8%84%91&platform=Windows&version=1.0"
        );
        assert_eq!(
            build_ws_url("https://nexclip.example.com", "d", "", "", ""),
            "wss://nexclip.example.com/hubs/clipboard?deviceId=d"
        );
    }

    #[test]
    fn urlencode_specials() {
        assert_eq!(urlencode("a b&c"), "a%20b%26c");
        assert_eq!(urlencode("safe-id_9.~"), "safe-id_9.~");
    }
}
