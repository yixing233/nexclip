//! 同步引擎:本地捕获 → 上传;远端推送 → 入库(+可选自动粘贴);设备列表缓存。
//!
//! 与旧版 SyncEngine 对齐的语义:
//! - 上传:并发 4,失败重试 1/4/16s;文件条目不上传;凭证类失败 → 停止并标离线;
//! - 远端条目:hash 命中 → TouchByHash 置顶(带 server_id);未命中 → 插入(origin=1),
//!   图片先下载到本地缓存;手动推送/开启自动粘贴时写回剪贴板并注入当前前台应用;
//! - DevicesChanged → GET /api/devices 刷新 devices_cache.json(与旧版同格式)。

use std::sync::{Arc, Mutex, OnceLock};
use std::sync::atomic::{AtomicBool, Ordering};
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
/// 历史补齐互斥:连接抖动/多次 Connected 不应并发扫描全量历史。
static HISTORY_SYNC_RUNNING: AtomicBool = AtomicBool::new(false);
/// 本进程是否已做过一次「互传发送方对账」(启动后仅首次连接执行)。
static IDENTITY_RECONCILED: AtomicBool = AtomicBool::new(false);

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
                if let Ok(file) = serde_json::from_value::<api::FileTransferInfo>(v) {
                    let value = serde_json::to_value(&file).unwrap_or_default();
                    let cached = state.lock().unwrap_or_else(|p| p.into_inner())
                        .history.cache_transfer_file(&file.file_id, &value);
                    if let Err(e) = cached { log::warn!("文件记录缓存失败: {e}"); }
                    let _ = app.emit("file-transferred", value);
                }
            }
            EngineMsg::RemoteFileDeleted(file_id) => {
                let _ = state.lock().unwrap_or_else(|p| p.into_inner()).history.delete_transfer_file(&file_id);
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
                    // 推送可能漏帧(重连/短暂断线),用 REST 历史补齐本地缺失条目。
                    // 独立任务执行,避免阻塞引擎消息循。
                    let sync_app = app.clone();
                    let sync_state = Arc::clone(&state);
                    tauri::async_runtime::spawn(async move {
                        sync_remote_history(&sync_app, &sync_state).await;
                    });
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
        // 超限图片(原始 >10MB)仅本地保存,不参与同步。
        if item.oversized {
            log::info!("跳过超限图片同步(id={id})");
            return;
        }
        let (server_url, token, device_id, device_name, platform, version, rich_text) = {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            let s = &guard.settings;
            (
                s.server_url.clone(), s.auth_token.clone(), s.device_id.clone(),
                s.device_name.clone(), "Windows".to_string(),
                env!("CARGO_PKG_VERSION").to_string(),
                s.rich_text_enabled,
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
                    item.is_manual(), item.html.as_deref().filter(|_| rich_text),
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
                if png.len() > crate::capture::MAX_IMAGE_BYTES {
                    log::warn!("图片缓存超过 10MB,跳过同步(id={id})");
                    return;
                }
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
    store_remote_entry(app, state, entry, true).await.map(|_| ())
}

/// 远端条目入库(推送与 REST 补齐共用)。
///
/// `apply_side_effects=false` 用于历史补齐:只补本地记录,不重放旧的自动粘贴。
/// 返回是否新增了本地记录(false = 已存在/本机回显/重复内容置顶)。
async fn store_remote_entry(
    app: &AppHandle,
    state: &Arc<Mutex<AppState>>,
    entry: api::ClipboardEntry,
    apply_side_effects: bool,
) -> Result<bool, String> {

    if entry.is_manual && state.lock().unwrap_or_else(|p| p.into_inner())
        .history.is_chat_deleted(entry.id).map_err(|e| e.to_string())? {
        return Ok(false);
    }

    // 自己上传内容的回显:服务端会把本机刚上传的条目再广播回来。
    // 旧版 SyncEngine.HandlePushAsync 的铁律 —— DeviceId 等于本机就直接返回,
    // 只做 UI 提示,绝不再当成远端条目入库。否则会出现:
    // 本机复制一条内容,列表里立刻多出一条 origin=1(远端)的重复记录,
    // 且 device_id 却是本机自己,UI 上看起来像「别的设备同步过来的」。
    let self_id = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.device_id.clone()
    };
    if !self_id.is_empty() && entry.device_id.eq_ignore_ascii_case(&self_id) {
        log::debug!("忽略本机上传条目的服务端回显(server_id={})", entry.id);
        return Ok(false);
    }

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
        // 已存在的服务端条目直接跳过,补齐旧历史时不重复下载图片。
        let known_item = {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            guard
                .history
                .find_by_server_id(entry.id)
                .map_err(|e| e.to_string())?
        };
        if known_item.as_ref().and_then(|item| item.image_path.as_deref()).is_some_and(|path| std::path::Path::new(path).is_file()) {
            return Ok(false);
        }
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
        if known_item.is_some() {
            let guard = state.lock().unwrap_or_else(|p| p.into_inner());
            guard.history.restore_remote_image_cache(entry.id, content_hash.as_deref().unwrap_or_default(), image_path.as_deref().unwrap_or_default())
                .map_err(|e| e.to_string())?;
            let _ = app.emit("clipboard-changed", ());
            return Ok(false);
        }
    }

    let (auto_paste, device_id_local, _device_name_local, rich_text) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.auto_paste,
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
            guard.settings.rich_text_enabled,
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
        oversized: false,
    };

    let hash = item.content_hash.clone().unwrap_or_default();
    let mut touched = false;
    {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        // A user can delete/clear while an image download is in flight.
        if is_manual && guard.history.is_chat_deleted(entry.id).map_err(|e| e.to_string())? {
            return Ok(false);
        }
        if guard
            .history
            .find_by_server_id(entry.id)
            .ok()
            .flatten()
            .is_some()
        {
            return Ok(false);
        }
        // 旧版铁律:手动互传条目**绝不去重**,必须每次插入新记录,
        // 否则 (a) 对话流会丢消息、(b) touch_by_hash 会把已有条目的
        // device_id/device_name 覆盖成远端设备,导致自己发的消息在互传页
        // 显示成"对方发的"。只有普通剪贴板内容才按 hash 置顶。
        if !is_manual && !hash.is_empty() {
            touched = guard
                .history
                .touch_by_hash(&hash, Some(entry.id), &item.device_id, item.device_name.as_deref(), created_ticks, None, None, None)
                .unwrap_or(false);
            if touched {
                if let Some(path) = item.image_path.as_deref() {
                    guard.history.repair_image_cache(&hash, path, item.oversized).map_err(|e| e.to_string())?;
                    let _ = app.emit("clipboard-changed", ());
                }
            }
        }
        let mut inserted_new = false;
        if !touched {
            // server_id UNIQUE 冲突返回 0:这是推送重放/历史补齐已存在的条目,
            // 不能再弹通知或重复自动粘贴。
            let inserted = guard.history.insert(&item).map_err(|e| e.to_string())?;
            if inserted == 0 {
                return Ok(false);
            }
            inserted_new = true;
        }
        if !inserted_new {
            // 普通剪贴板内容命中已有 hash:只置顶,不算补齐新增。
            return Ok(false);
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
    if apply_side_effects && !touched && !(auto_paste) {
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

    // Write recent pushes to the clipboard only; receiving content must not type into an app.
    if should_write_remote(apply_side_effects, auto_paste, touched, created_ticks, crate::history::now_ticks()) {
        let payload = payload_for_remote(&item, rich_text);
        let _ = tauri::async_runtime::spawn_blocking(move || {
            crate::platform::writer::submit(payload, 3000)
        }).await;
    }

    // 智能直达 toast(远端文本)
    if apply_side_effects && item.entry_type == "Text" {
        if let Some(t) = &item.text {
            crate::show_smart_toast(app, state, t);
        }
    }
    Ok(true)
}

/// 连接建立后从服务端历史增量补齐本地缺失条目。
///
/// WebSocket 推送没有重放语义:断线、后台挂起或服务端漏发时,
/// 仅靠 `ClipboardUpdated` 会永久丢消息。这里按 server_id 补齐,
/// 已有条目由 UNIQUE(server_id) 自动跳过。
async fn sync_remote_history(app: &AppHandle, state: &Arc<Mutex<AppState>>) {
    if HISTORY_SYNC_RUNNING
        .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
        .is_err()
    {
        return;
    }
    struct SyncGuard;
    impl Drop for SyncGuard {
        fn drop(&mut self) {
            HISTORY_SYNC_RUNNING.store(false, Ordering::SeqCst);
        }
    }
    let _guard = SyncGuard;

    const PAGE: i64 = 100;
    // 只补最近一段缺口:命中整页已存在即停止,不会每次启动都扫全量历史。
    const MAX_PAGES: usize = 3;
    // 每个进程启动后的第一次连接做一次较深的历史对账(修正被本机采集
    // 覆盖掉的互传发送方),之后回到浅扫以控制请求量。
    const RECONCILE_PAGES: usize = 6;
    let full_reconcile = !IDENTITY_RECONCILED.swap(true, Ordering::SeqCst);
    let max_pages = if full_reconcile { RECONCILE_PAGES } else { MAX_PAGES };

    let (server_url, device_id, token, paired) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
            guard.settings.is_paired,
        )
    };
    if !paired || token.trim().is_empty() || !server_url.trim().starts_with("http") {
        return;
    }

    // 用户清空互传记录时写入的水位:低于它的旧消息不再从服务端补回。
    let chat_cleared_wave: i64 = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let p = crate::settings::resolve_storage_dir(&guard.settings.storage_dir)
            .join("chat-cleared.wave");
        std::fs::read_to_string(p)
            .ok()
            .and_then(|s| s.trim().parse::<i64>().ok())
            .unwrap_or(0)
    };

    for page in 0..max_pages {
        let entries = match api::get_remote_history(
            &server_url,
            &device_id,
            &token,
            (page as i64) * PAGE,
            PAGE,
            None,
        )
        .await
        {
            Ok(v) => v,
            Err(e) => {
                log::warn!("远端历史补齐失败: {e}");
                return;
            }
        };
        if entries.is_empty() {
            return;
        }
        // 逐条按 server_id 精确判断,不能只看“最大 id”:本地可能有较新的条目
        // 而中间仍存在空洞(某次推送漏掉),只看最大值会永久跳过缺口。
        let page_len = entries.len();
        let mut known_in_page = 0usize;
        let mut added = 0usize;
        for entry in entries {
            // 已清空的旧互传消息:跳过,不补回、不对账。
            if entry.is_manual && entry.id <= chat_cleared_wave {
                known_in_page += 1;
                continue;
            }
            // 不能按“最大 server_id”提前跳过:本地可能有较新的条目,
            // 而中间仍存在空洞(某次推送漏掉)。逐条精确判断。
            let (known_item, deleted) = {
                let guard = state.lock().unwrap_or_else(|p| p.into_inner());
                (guard.history.find_by_server_id(entry.id).ok().flatten(),
                    entry.is_manual && guard.history.is_chat_deleted(entry.id).unwrap_or(false))
            };
            let known = deleted || known_item.is_some_and(|item|
                item.entry_type != "Image" || item.image_path.as_deref().is_some_and(|path| std::path::Path::new(path).is_file()));
            if known {
                // 对账:本机采集去重可能把远端互传消息的发送方改写成本机,
                // 以服务端为准恢复,否则互传页会显示成「自己发的」或名字错乱。
                if entry.is_manual {
                    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
                    if let Err(e) = guard.history.set_device_by_server_id(
                        entry.id,
                        &entry.device_id,
                        entry.device_name.as_deref(),
                    ) {
                        log::warn!("互传发送方对账失败(server_id={}): {e}", entry.id);
                    }
                }
                known_in_page += 1;
                continue;
            }
            match store_remote_entry(app, state, entry, false).await {
                Ok(true) => added += 1,
                Ok(false) => known_in_page += 1,
                Err(e) => log::warn!("远端历史条目补齐失败: {e}"),
            }
        }
        if added > 0 {
            log::info!("远端历史补齐完成: {added} 条");
        }
        // 整页都已存在 → 已追平,停止翻页;首次对账时继续往后翻,
        // 让更早的互传消息也能被校正发送方。
        if known_in_page == page_len && !full_reconcile {
            return;
        }
        // 继续翻页只用于覆盖最近的缺口;MAX_PAGES 限制总耗时。
    }
}

/// 远端条目 → 写入载荷。
fn should_write_remote(side_effects: bool, enabled: bool, touched: bool, created: i64, now: i64) -> bool {
    side_effects && enabled && !touched && now.abs_diff(created) <= 5 * 60 * 10_000_000
}

#[cfg(test)]
mod remote_write_tests {
    use super::*;

    #[test]
    fn only_recent_enabled_pushes_write_the_clipboard() {
        let now = crate::history::now_ticks();
        assert!(should_write_remote(true, true, false, now - 60 * 10_000_000, now));
        assert!(!should_write_remote(true, true, false, now - 301 * 10_000_000, now));
        assert!(!should_write_remote(true, false, false, now, now));
        assert!(!should_write_remote(false, true, false, now, now));
        assert!(!should_write_remote(true, true, true, now, now));
    }
}

fn payload_for_remote(item: &HistoryItem, rich_text: bool) -> crate::platform::writer::WritePayload {
    if item.entry_type == "Image" {
        if let Some(path) = item.image_path.as_deref() {
            if let Ok(png) = std::fs::read(path) {
                return crate::platform::writer::WritePayload::Image { png };
            }
        }
    }
    crate::platform::writer::WritePayload::Text {
        text: item.text.clone().unwrap_or_default(),
        html: item.html.clone().filter(|_| rich_text),
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
