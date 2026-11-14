//! 剪贴板捕获管线:监听回调 → 读取 → 入库 → 广播。
//!
//! 与旧版 ClipboardMonitor 的捕获分支一致:文件只记元数据(绝不读内容/上传)、
//! 图片超 4096px 缩放、超 10MB 只存本地不同步(Phase 3)、
//! 重复内容 TouchByHash 置顶而非新增。

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use tauri::{AppHandle, Emitter};

use crate::clipboard::files_meta as file_meta;
use crate::clipboard::hash;
use crate::history::{now_ticks, HistoryItem, ORIGIN_LOCAL, TYPE_FILE, TYPE_IMAGE, TYPE_TEXT};
use crate::platform::monitor;
use crate::AppState;

/// 捕获入口(剪贴板捕获工作线程内调用)。
pub fn capture(app: &AppHandle, state: &Arc<Mutex<AppState>>, seq: u32) {
    // 熔断:剪贴板被独占时暂停(等下一次内容变化自动恢复)
    if monitor::circuit_open() {
        return;
    }
    let (monitor_enabled, custom_filter, device_id) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.monitor_enabled,
            guard.settings.custom_filtered_processes.clone(),
            guard.settings.device_id.clone(),
        )
    };
    if !monitor_enabled {
        return;
    }

    // 来源应用过滤(远程控制 + 自定义名单)
    let owner_path = unsafe { monitor::owner_process_path() };
    if monitor::should_filter_process(owner_path.as_deref(), &custom_filter) {
        log::debug!("忽略被过滤应用的剪贴板内容: {owner_path:?}");
        return;
    }

    // 读取剪贴板(工作线程,阻塞安全)
    let read = unsafe { monitor::read_clipboard() };
    monitor::note_open_result(read.is_ok());
    let (text, html, files, png) = match read {
        Ok(v) => v,
        Err(()) => {
            log::debug!("剪贴板读取失败(可能被独占),本次跳过");
            return;
        }
    };
    // 读取完立刻对齐序列号,防止同一内容重复入库
    if seq != 0 {
        LAST_CAPTURED_SEQ_STORE
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .replace(seq);
    }

    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let storage_dir = crate::settings::resolve_storage_dir(&guard.settings.storage_dir);
    let max_entries = guard.settings.max_history;

    let now_ticks = now_ticks();
    // 来源应用图标(旧版 AppIconCache 语义:提取一次缓存 PNG,复用文件名)
    let source_icon = owner_path
        .as_deref()
        .and_then(crate::platform::app_icon::get_or_create);
    let item = match (&png, &files, &text) {
        // 文件条目:只记元数据,绝不读文件内容
        (_, paths, _) if !paths.is_empty() => {
            let metas: Vec<file_meta::ClipboardFileInfo> = paths
                .iter()
                .map(|p| {
                    let is_dir = std::fs::metadata(p).map(|m| m.is_dir()).unwrap_or(false);
                    let size = if is_dir {
                        0
                    } else {
                        std::fs::metadata(p).map(|m| m.len() as i64).unwrap_or(0)
                    };
                    let name = PathBuf::from(p)
                        .file_name()
                        .map(|n| n.to_string_lossy().into_owned())
                        .unwrap_or_else(|| p.clone());
                    file_meta::ClipboardFileInfo {
                        path: p.clone(),
                        name,
                        is_directory: is_dir,
                        size_bytes: size,
                    }
                })
                .collect();
            let hash = file_meta::compute_hash(&metas);
            HistoryItem {
                id: 0,
                server_id: None,
                entry_type: TYPE_FILE.into(),
                text: None,
                html: None,
                image_path: None, // 文件条目必须保持 NULL(清理路径会删除该列指向的文件)
                image_ref: None,
                file_paths_json: Some(file_meta::serialize(&metas)),
                content_hash: if hash.is_empty() { None } else { Some(hash) },
                device_id: device_id.clone(),
                device_name: Some(guard.settings.device_name.clone()),
                source_app_name: owner_process_name(&owner_path),
                source_app_path: owner_path.clone(),
                source_app_icon: source_icon.clone(),
                created_at_ticks: now_ticks,
                origin: ORIGIN_LOCAL,
                starred: false,
                remark: None,
            }
        }
        // 图片条目
        (Some(png), _, _) => {
            let bytes = maybe_downscale(png);
            let hash = hash::hash_bytes(&bytes);
            let dir = storage_dir.join("images");
            let _ = std::fs::create_dir_all(&dir);
            let path = dir.join(format!("{hash}.png"));
            if !path.exists() && std::fs::write(&path, &bytes).is_err() {
                log::warn!("图片缓存写入失败");
            }
            HistoryItem {
                id: 0,
                server_id: None,
                entry_type: TYPE_IMAGE.into(),
                text: None,
                html: None,
                image_path: Some(path.to_string_lossy().into_owned()),
                image_ref: None,
                file_paths_json: None,
                content_hash: Some(hash),
                device_id: device_id.clone(),
                device_name: Some(guard.settings.device_name.clone()),
                source_app_name: owner_process_name(&owner_path),
                source_app_path: owner_path.clone(),
                source_app_icon: source_icon.clone(),
                created_at_ticks: now_ticks,
                origin: ORIGIN_LOCAL,
                starred: false,
                remark: None,
            }
        }
        // 文本条目(含富文本 HTML)
        (_, _, Some(text)) => {
            let text = text.trim_end_matches(['\r', '\n']).to_string();
            if text.is_empty() {
                return;
            }
            let hash = hash::hash_text(&text, html.as_deref());
            HistoryItem {
                id: 0,
                server_id: None,
                entry_type: TYPE_TEXT.into(),
                text: Some(text),
                html: html.clone(),
                image_path: None,
                image_ref: None,
                file_paths_json: None,
                content_hash: Some(hash),
                device_id: device_id.clone(),
                device_name: Some(guard.settings.device_name.clone()),
                source_app_name: owner_process_name(&owner_path),
                source_app_path: owner_path.clone(),
                source_app_icon: source_icon.clone(),
                created_at_ticks: now_ticks,
                origin: ORIGIN_LOCAL,
                starred: false,
                remark: None,
            }
        }
        _ => return,
    };

    let hash = item.content_hash.clone().unwrap_or_default();
    // 重复内容 → TouchByHash 置顶;否则插入(插入内部会自动裁剪超限条目)
    let touched = !hash.is_empty()
        && guard
            .history
            .touch_by_hash(&hash, None, &item.device_id, item.device_name.as_deref(), now_ticks, None, None, None)
            .unwrap_or(false);
    let changed_kind = if touched { "touch" } else { "insert" };
    let mut inserted_id = 0i64;
    if !touched {
        match guard.history.insert(&item) {
            Ok(id) => inserted_id = id,
            Err(e) => {
                log::error!("历史入库失败: {e}");
                return;
            }
        }
        guard.history.set_max_entries(max_entries);
    }

    let item_for_emit = guard.history.find_by_hash(&hash).ok().flatten();
    drop(guard);

    // 本地新内容(非置顶、非文件)→ 提交同步引擎上传(未配对时引擎静默丢弃)
    if !touched && inserted_id > 0 && item.entry_type != "File" {
        crate::sync::engine::submit_upload(inserted_id);
    }

    // 智能直达 toast(仅文本;命中规则才弹)
    if item.entry_type == "Text" {
        if let Some(t) = &item.text {
            crate::show_smart_toast(app, state, t);
        }
    }

    log::info!("捕获入库({changed_kind}): {}", match (&png, &files, &text) {
        (Some(_), _, _) => "[图片]".into(),
        (_, f, _) if !f.is_empty() => format!("[文件×{}]", f.len()),
        (_, _, Some(t)) => t.chars().take(30).collect(),
        _ => "[空]".into(),
    });
    // 广播给所有窗口(弹窗列表置顶刷新;完整 DTO,前端卡片可直接渲染)
    let payload = item_for_emit.map(crate::commands::to_dto);
    let _ = app.emit("clipboard-changed", payload);
}

/// 最近一次已处理的剪贴板序列号(防重复入库的二级防线)。
static LAST_CAPTURED_SEQ_STORE: Mutex<Option<u32>> = Mutex::new(None);

/// 超长边 > 4096 时等比缩放(与旧版 ImageCodec 一致)。
fn maybe_downscale(png: &[u8]) -> Vec<u8> {
    const MAX_SIDE: u32 = 4096;
    let Ok(img) = image::load_from_memory(png) else {
        return png.to_vec();
    };
    let (w, h) = (img.width(), img.height());
    if w <= MAX_SIDE && h <= MAX_SIDE {
        return png.to_vec();
    }
    let scale = MAX_SIDE as f64 / w.max(h) as f64;
    let nw = ((w as f64 * scale) as u32).max(1);
    let nh = ((h as f64 * scale) as u32).max(1);
    let resized = img.resize(nw, nh, image::imageops::FilterType::Triangle);
    let mut buf = std::io::Cursor::new(Vec::new());
    if resized
        .write_to(&mut buf, image::ImageFormat::Png)
        .is_ok()
    {
        buf.into_inner()
    } else {
        png.to_vec()
    }
}

fn owner_process_name(path: &Option<String>) -> Option<String> {
    path.as_ref().map(|p| {
        PathBuf::from(p)
            .file_stem()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_else(|| p.clone())
    })
}
