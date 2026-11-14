//! 同步引擎:本地捕获 → 上传;远端推送 → 入库(+可选自动粘贴);设备列表缓存。
//!
//! 与旧版 SyncEngine 对齐的语义:
//! - 上传:并发 4,失败重试 1/4/16s;文件条目不上传;凭证类失败 → 停止并标离线;
//! - 远端条目:hash 命中 → TouchByHash 置顶(带 server_id);未命中 → 插入(origin=1),
//!   图片先下载到本地缓存;手动推送/开启自动粘贴时写回剪贴板并注入当前前台应用;
//! - DevicesChanged → GET /api/devices 刷新 devices_cache.json(与旧版同格式)。

use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use serde_json::json;
use tauri::{AppHandle, Emitter};
use tokio::sync::{mpsc, Semaphore};

use super::api;
use super::push::{self, PushCommand, PushEvent, PushState};
use crate::clipboard::hash;
use crate::history::{millis_to_ticks, HistoryItem, ORIGIN_REMOTE};
use crate::AppState;

/// 上传并发上限(与旧版 SemaphoreSlim(4) 一致)。
const UPLOAD_CONCURRENCY: usize = 4;
/// 上传失败重试间隔(与旧版 1/4/16s 一致)。
const UPLOAD_RETRY_MS: [u64; 3] = [1000, 4000, 16000];

/// 引擎收件箱消息。
#[derive(Debug)]
pub enum EngineMsg {
    /// 本地新捕获条目待上传(文件条目在入队前已被过滤)
    LocalUpload { id: i64 },
    /// 远端推送:剪贴板条目
    RemoteEntry(serde_json::Value),
    /// 远端推送:文件互传
    RemoteFile(serde_json::Value),
    /// 远端推送:文件删除
    RemoteFileDeleted(String),
    /// 远端推送:设备列表变化
    DevicesChanged,
    /// 设置变化(服务器/凭证/开关)→ 重新评估连接
    SettingsChanged,
    /// 推送通道状态变化(connecting/connected/reconnecting/disconnected)
    PushState(super::push::PushState),
}

static ENGINE_TX: OnceLock<mpsc::Sender<EngineMsg>> = OnceLock::new();

/// 本地捕获后调用(文件条目跳过)。
pub fn submit_upload(id: i64) {
    if let Some(tx) = ENGINE_TX.get() {
        let _ = tx.try_send(EngineMsg::LocalUpload { id });
    }
}

/// 设置变化后调用(重评估连接:配对状态/服务器地址/令牌)。
pub fn notify_settings_changed() {
    if let Some(tx) = ENGINE_TX.get() {
        let _ = tx.try_send(EngineMsg::SettingsChanged);
    }
}

/// 启动引擎(设置/监听就绪后调用一次)。
pub fn start(app: AppHandle, state: Arc<Mutex<AppState>>) {
    let (tx, rx) = mpsc::channel(128);
    let _ = ENGINE_TX.set(tx.clone());

    let handle = push::start();
    let push::PushHandle { commands: push_commands, events: push_events, states: push_states } = handle;
    tauri::async_runtime::spawn(async move {
        run_engine(app, state, push_commands, push_events, push_states, tx, rx).await;
    });
}

#[allow(clippy::too_many_arguments)]
async fn run_engine(
    app: AppHandle,
    state: Arc<Mutex<AppState>>,
    push_commands: mpsc::Sender<PushCommand>,
    mut push_events: mpsc::Receiver<PushEvent>,
    mut push_states: mpsc::Receiver<PushState>,
    tx: mpsc::Sender<EngineMsg>,
    mut rx: mpsc::Receiver<EngineMsg>,
) {
    // 推送事件 → 引擎消息(此前无人消费,推送形同虚设——修复)
    {
        let tx = tx.clone();
        tauri::async_runtime::spawn(async move {
            while let Some(ev) = push_events.recv().await {
                let msg = match ev {
                    PushEvent::EntryReceived(v) => EngineMsg::RemoteEntry(v),
                    PushEvent::FileTransferred(v) => EngineMsg::RemoteFile(v),
                    PushEvent::FileDeleted(id) => EngineMsg::RemoteFileDeleted(id),
                    PushEvent::DevicesChanged => EngineMsg::DevicesChanged,
                };
                if tx.send(msg).await.is_err() {
                    break;
                }
            }
        });
    }
    // 推送状态 → 引擎(connected 时刷新设备列表:REST 真值覆盖本地缓存)
    {
        let tx = tx.clone();
        tauri::async_runtime::spawn(async move {
            while let Some(st) = push_states.recv().await {
                if tx.send(EngineMsg::PushState(st)).await.is_err() {
                    break;
                }
            }
        });
    }
    let semaphore = Arc::new(Semaphore::new(UPLOAD_CONCURRENCY));
    let mut status = String::from("not_configured");
    // 断线通知去抖:只在状态翻转时发一次
    let mut was_disconnected = false;

    // 启动即评估一次(已配对则直接连接):通过独立任务重投递
    {
        let tx2 = tx.clone();
        tokio::spawn(async move {
            let _ = tx2.send(EngineMsg::SettingsChanged).await;
        });
    }

    loop {
        let msg = match rx.recv().await {
            Some(m) => m,
            None => return,
        };
        match msg {
            EngineMsg::SettingsChanged => {
                let (paired, token, server_url, device_id, device_name, platform, version) = {
                    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
                    let s = &guard.settings;
                    (
                        s.is_paired, s.auth_token.clone(), s.server_url.clone(),
                        s.device_id.clone(), s.device_name.clone(),
                        "Windows".to_string(),
                        env!("CARGO_PKG_VERSION").to_string(),
                    )
                };
                if paired && !token.trim().is_empty() && !server_url.trim().is_empty() {
                    status = "connecting".into();
                    if let Ok(g) = state.lock() {
                        *g.sync_status.lock().unwrap_or_else(|p| p.into_inner()) = status.clone();
                    }
                    let _ = app.emit("sync-state", status.clone());
                    let _ = push_commands
                        .send(PushCommand::Connect(push::ConnParams {
                            server_url,
                            device_id,
                            device_name,
                            platform,
                            version,
                            token,
                        }))
                        .await;
                    // REST 拉设备真值(不依赖 WS;清掉本地缓存里的僵尸设备)
                    refresh_devices(&app, &state).await;
                } else {
                    status = "not_configured".into();
                    if let Ok(g) = state.lock() {
                        *g.sync_status.lock().unwrap_or_else(|p| p.into_inner()) = status.clone();
                    }
                    let _ = app.emit("sync-state", status.clone());
                    let _ = push_commands.send(PushCommand::Stop).await;
                }
            }
            EngineMsg::LocalUpload { id } => {
                let upload_state = Arc::clone(&state);
                let upload_app = app.clone();
                let sem = Arc::clone(&semaphore);
                tokio::spawn(async move {
                    upload_entry(upload_app, upload_state, sem, id).await;
                });
            }
            EngineMsg::RemoteEntry(v) => {
                if let Err(e) = handle_remote_entry(&app, &state, &v).await {
                    log::warn!("远端条目处理失败: {e}");
                }
            }
            EngineMsg::RemoteFile(v) => {
                // 文件互传:展示层事件(绝不写入剪贴板/自动粘贴,旧版铁律)
                let _ = app.emit("file-transferred", v);
            }
            EngineMsg::RemoteFileDeleted(file_id) => {
                let _ = app.emit("file-deleted", file_id);
            }
            EngineMsg::DevicesChanged => {
                refresh_devices(&app, &state).await;
            }
            EngineMsg::PushState(st) => {
                status = st.as_str().to_string();
                if let Ok(g) = state.lock() {
                    *g.sync_status.lock().unwrap_or_else(|p| p.into_inner()) = status.clone();
                }
                if st == super::push::PushState::Connected {
                    refresh_devices(&app, &state).await;
                    // 重连成功提示(从断线恢复时,一次性)
                    if was_disconnected {
                        crate::notify(&app, &state, "NexClip 同步", "推送通道已重新连接");
                        was_disconnected = false;
                    }
                }
                if st == super::push::PushState::Disconnected {
                    // 断线通知(只在状态翻转时发一次,重连过程中的 Reconnecting 不发)
                    if !was_disconnected {
                        crate::notify(&app, &state, "NexClip 同步", "与同步服务器的连接已断开,将自动重连");
                        was_disconnected = true;
                    }
                }
            }
        }
        let _ = app.emit("sync-state", status.clone());
    }
}

/// 上传一条本地条目(文本/图片;文件条目永不经过服务端)。
async fn upload_entry(
    app: AppHandle,
    state: Arc<Mutex<AppState>>,
    sem: Arc<Semaphore>,
    id: i64,
) {
    let _permit = sem.acquire().await;
    for (attempt, delay_ms) in UPLOAD_RETRY_MS.iter().enumerate() {
        if attempt > 0 {
            tokio::time::sleep(Duration::from_millis(*delay_ms)).await;
        }
        let item = {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            match guard.history.query(None, None, false, false, 500, 0) {
                Ok(rows) => match rows.into_iter().find(|r| r.id == id) {
                    Some(i) => i,
                    None => return, // 已被删除(清理/裁剪),放弃上传
                },
                Err(e) => {
                    log::error!("上传前读取历史失败: {e}");
                    return;
                }
            }
        };
        // 文件条目不经过服务端(铁律)
        if item.is_file() {
            return;
        }
        let (server_url, token, device_id, device_name, platform, version) = {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            let s = &guard.settings;
            (
                s.server_url.clone(), s.auth_token.clone(), s.device_id.clone(),
                s.device_name.clone(), "Windows".to_string(),
                env!("CARGO_PKG_VERSION").to_string(),
            )
        };
        if token.trim().is_empty() || !server_url.trim().starts_with("http") {
            return; // 未配置,静默放弃(重连时不会补传,与旧版一致)
        }

        let result = match item.entry_type.as_str() {
            "Text" => {
                api::put_text(
                    &server_url, &token,
                    item.text.as_deref().unwrap_or(""),
                    &device_id, &device_name, &platform, &version,
                    item.is_manual(), item.html.as_deref(),
                )
                .await
            }
            "Image" => {
                let Some(path) = item.image_path.as_deref() else { return };
                let png = match std::fs::read(path) {
                    Ok(b) => b,
                    Err(e) => {
                        log::warn!("图片缓存读取失败({path}): {e}");
                        return;
                    }
                };
                api::upload_image(
                    &server_url, &token, png, &device_id, &device_name,
                    &platform, &version, item.is_manual(),
                )
                .await
            }
            _ => return,
        };

        match result {
            Ok(_) => {
                log::info!("条目上传成功(id={id})");
                return;
            }
            Err(e) => {
                if e.is_credential_dead() {
                    // 凭证失效/被移除:停止重试并标离线(不清 token,由用户重新配对)
                    log::error!("上传失败(凭证失效): {e}");
                    let _ = app.emit("sync-state", "offline");
                    return;
                }
                log::warn!("上传失败(第 {} 次): {e}", attempt + 1);
            }
        }
    }
}

/// 处理远端推送条目:hash 命中 → 置顶;未命中 → 下载图片 + 插入(origin=1)。
async fn handle_remote_entry(
    app: &AppHandle,
    state: &Arc<Mutex<AppState>>,
    v: &serde_json::Value,
) -> Result<(), String> {
    let entry: api::ClipboardEntry = serde_json::from_value(v.clone())
        .map_err(|e| format!("条目解析失败: {e}"))?;

    // created_at:ISO → .NET Ticks(本地历史统一用 Ticks)
    let created_ticks = entry
        .created_at
        .as_deref()
        .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
        .map(|d| millis_to_ticks(d.with_timezone(&chrono::Utc).timestamp_millis()))
        .unwrap_or_else(|| crate::history::now_ticks());

    // 图片条目:先下载字节(命中本地缓存也重新落盘,保持 image_path 有效)
    let mut image_path: Option<String> = None;
    let mut content_hash: Option<String> = None;
    if entry.entry_type == "Image" {
        let Some(r) = entry.image_ref.as_deref().filter(|r| !r.is_empty()) else {
            return Err("图片条目缺少 image_ref".into());
        };
        let (server_url, device_id, token) = {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            (
                guard.settings.server_url.clone(),
                guard.settings.device_id.clone(),
                guard.settings.auth_token.clone(),
            )
        };
        let png = api::download_image(&server_url, &device_id, &token, r)
            .await
            .map_err(|e| format!("图片下载失败: {e}"))?
            .ok_or("图片在服务端已不存在")?;
        content_hash = Some(hash::hash_bytes(&png));
        let dir = crate::settings::resolve_storage_dir(
            &state.lock().unwrap_or_else(|p| p.into_inner()).settings.storage_dir,
        )
        .join("images");
        let _ = std::fs::create_dir_all(&dir);
        let path = dir.join(format!("{}.png", content_hash.as_deref().unwrap_or("img")));
        if !path.exists() {
            std::fs::write(&path, &png).map_err(|e| format!("图片缓存写入失败: {e}"))?;
        }
        image_path = Some(path.to_string_lossy().into_owned());
    }

    let (auto_paste, device_id_local, _device_name_local) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.auto_paste,
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
        )
    };

    let is_manual = entry.is_manual;
    let item = HistoryItem {
        id: 0,
        server_id: Some(entry.id),
        entry_type: entry.entry_type.clone(),
        text: entry.text.clone(),
        html: entry.html.clone(),
        image_path,
        image_ref: entry.image_ref.clone(),
        file_paths_json: None,
        content_hash: content_hash.or_else(|| {
            // 文本条目:内容哈希与本地算法一致(同一段内容跨端去重命中)
            entry.text.as_deref().map(|t| {
                hash::hash_text(t, entry.html.as_deref().filter(|h| !h.is_empty()))
            })
        }),
        device_id: if entry.device_id.is_empty() { device_id_local } else { entry.device_id.clone() },
        device_name: entry.device_name.clone(),
        source_app_name: if is_manual { Some("即时互传".into()) } else { None },
        source_app_path: None,
        source_app_icon: None,
        created_at_ticks: created_ticks,
        origin: ORIGIN_REMOTE,
        starred: entry.starred,
        remark: entry.remark.clone(),
    };

    let hash = item.content_hash.clone().unwrap_or_default();
    let mut touched = false;
    {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        if !hash.is_empty() {
            touched = guard
                .history
                .touch_by_hash(&hash, Some(entry.id), &item.device_id, item.device_name.as_deref(), created_ticks, None, None, None)
                .unwrap_or(false);
        }
        if !touched {
            guard.history.insert(&item).map_err(|e| e.to_string())?;
        }
    }

    // 广播给弹窗列表(完整 DTO)
    if let Some(updated) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.history.find_by_hash(&hash).ok().flatten()
    } {
        let _ = app.emit("clipboard-changed", crate::commands::to_dto(updated));
    }

    // 新内容通知(开关控制;自动粘贴的不再弹,避免噪声)
    if !touched && !(auto_paste) {
        let from = item.device_name.clone().unwrap_or_else(|| "其他设备".into());
        let what = match item.entry_type.as_str() {
            "Text" => item.text.as_deref().map(|t| {
                let t = t.trim();
                let cut: String = t.chars().take(40).collect();
                if t.chars().count() > 40 { format!("{cut}…") } else { cut }
            }),
            "Image" => Some("[图片]".into()),
            "File" => Some("[文件]".into()),
            _ => None,
        };
        let body = what.unwrap_or_else(|| "新内容".into());
        crate::notify(app, state, "NexClip 同步", &format!("{from} · {body}"));
    }

    // 手动推送(即时互传)或开启自动粘贴:写回剪贴板并注入当前前台应用
    // (旧版 HandlePushAsync 行为;目标取"此刻的前台",而不是弹窗呼出时的记录)
    if is_manual || (auto_paste && !touched) {
        if let Some(target) = crate::platform::paste::capture_target() {
            let payload = payload_for_remote(&item);
            if crate::platform::writer::submit(payload, 3000).is_ok() {
                let _ = crate::platform::paste::inject(&target, false);
            }
        }
    }

    // 智能直达 toast(远端文本)
    if item.entry_type == "Text" {
        if let Some(t) = &item.text {
            crate::show_smart_toast(app, state, t);
        }
    }
    Ok(())
}

/// 远端条目 → 写入载荷。
fn payload_for_remote(item: &HistoryItem) -> crate::platform::writer::WritePayload {
    if item.entry_type == "Image" {
        if let Some(path) = item.image_path.as_deref() {
            if let Ok(png) = std::fs::read(path) {
                return crate::platform::writer::WritePayload::Image { png };
            }
        }
    }
    crate::platform::writer::WritePayload::Text {
        text: item.text.clone().unwrap_or_default(),
        html: item.html.clone(),
    }
}

/// 刷新设备列表缓存(devices_cache.json,与旧版 PascalCase 格式一致)。
pub async fn refresh_devices(app: &AppHandle, state: &Arc<Mutex<AppState>>) {
    let (server_url, device_id, token, storage_dir, paired) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let dir = crate::settings::resolve_storage_dir(&guard.settings.storage_dir);
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
            dir,
            guard.settings.is_paired,
        )
    };
    // 未配对时不覆盖缓存(保留旧数据供参考;服务端真值只在配对后拉取)
    if !paired || token.trim().is_empty() || !server_url.trim().starts_with("http") {
        return;
    }
    match api::get_devices(&server_url, &device_id, &token).await {
        Ok(mut devices) => {
            // 本地兜底判定"本机":服务端 IsCurrent 不可靠,按本机 device_id 强制标记
            for d in devices.iter_mut() {
                if d.id == device_id {
                    d.is_current = true;
                }
            }
            // 写 PascalCase JSON(与 C# System.Text.Json 序列化的旧缓存互换使用)
            let list: Vec<serde_json::Value> = devices
                .iter()
                .map(|d| {
                    json!({
                        "Id": d.id, "Name": d.name, "Platform": d.platform,
                        "Ip": d.ip, "Version": d.version, "Online": d.online,
                        "LastSeenAt": d.last_seen_at, "IsCurrent": d.is_current,
                    })
                })
                .collect();
            let path = crate::settings::devices_cache_file_path(&storage_dir);
            if let Ok(text) = serde_json::to_string_pretty(&list) {
                let _ = std::fs::write(path, text);
            }
            let _ = app.emit("devices-changed", devices);
        }
        Err(e) => log::warn!("设备列表刷新失败: {e}"),
    }
}
