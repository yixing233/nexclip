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
    let (
        monitor_enabled,
        app_filter_enabled,
        custom_filter,
        device_id,
        storage_dir,
        max_entries,
        dev_name,
        rich_text_enabled,
    ) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.monitor_enabled,
            guard.settings.app_filter_enabled,
            guard.settings.custom_filtered_processes.clone(),
            guard.settings.device_id.clone(),
            crate::settings::resolve_storage_dir(&guard.settings.storage_dir),
            guard.settings.max_history,
            guard.settings.device_name.clone(),
            guard.settings.rich_text_enabled,
        )
    };
    if !monitor_enabled {
        monitor::mark_captured(seq);
        return;
    }

    // 来源应用过滤(远程控制 + 自定义名单)
    let owner_path = unsafe { monitor::owner_process_path() };
    if app_filter_enabled && monitor::should_filter_process(owner_path.as_deref(), &custom_filter) {
        log::debug!("忽略被过滤应用的剪贴板内容: {owner_path:?}");
        monitor::mark_captured(seq);
        return;
    }

    // 读取剪贴板(工作线程,阻塞安全)
    monitor::capture_stage(seq, "read-clipboard");
    let (read, opened) = unsafe { monitor::read_clipboard_with_status() };
    monitor::note_open_result(opened);
    let (text, mut html, files, png) = match read {
        Ok(v) => v,
        Err(()) => {
            if opened {
                // 打开成功但没有可读格式(空剪贴板/未知格式):这是正常状态,
                // 不能每 2 秒重复重试,也不能计入熔断。
                monitor::mark_captured(seq);
            } else {
                log::debug!("剪贴板打开失败(可能被独占),等待轮询重试");
            }
            return;
        }
    };
    if unsafe { windows::Win32::System::DataExchange::GetClipboardSequenceNumber() } != seq {
        return;
    }
    // 富文本开关关闭:只保留纯文本(与旧版 RichTextEnabled 语义一致)。
    if !rich_text_enabled {
        html = None;
    }
    // 注意:这里**不**记账序列号。序列号必须等到确认产出有效条目之后再记账 ——
    // 否则中间任何一步 return(空内容/未知格式/文件路径异常)都会让这份内容
    // 被永久跳过,直到剪贴板再次变化才恢复(旧版 ClipboardMonitor 同款约束)。

    let now_ticks = now_ticks();
    // 来源应用图标(旧版 AppIconCache 语义:提取一次缓存 PNG,复用文件名)
    // 放在状态锁外:首次提取会访问 exe 所在磁盘和 Shell,慢盘上可达数十秒。
    monitor::capture_stage(seq, "source-icon");
    let source_icon = owner_path
        .as_deref()
        .and_then(crate::platform::app_icon::get_or_create);
    // 文本/文件条目共用的来源字段(两个分支都要用,提前算一次)
    let src_name = owner_process_name(&owner_path);
    // 构造文件条目(CF_HDROP 与「文本形式的路径列表」共用)
    let make_file_item = |metas: Vec<file_meta::ClipboardFileInfo>| -> HistoryItem {
        let h = file_meta::compute_hash(&metas);
        HistoryItem {
            id: 0,
            server_id: None,
            entry_type: TYPE_FILE.into(),
            text: None,
            html: None,
            image_path: None, // 文件条目必须保持 NULL(清理会删除该列指向的文件)
            image_ref: None,
            file_paths_json: Some(file_meta::serialize(&metas)),
            content_hash: if h.is_empty() { None } else { Some(h) },
            device_id: device_id.clone(),
            device_name: Some(dev_name.clone()),
            source_app_name: src_name.clone(),
            source_app_path: owner_path.clone(),
            source_app_icon: source_icon.clone(),
            created_at_ticks: now_ticks,
            origin: ORIGIN_LOCAL,
            starred: false,
            remark: None,
            oversized: false,
        }
    };
    monitor::capture_stage(seq, "process-content");
    let item = match (&png, &files, &text) {
        // 文件条目:只记元数据,绝不读文件内容
        (_, paths, _) if !paths.is_empty() => {
            // 路径字符串本身就是条目内容:stat 失败(owner 持有期间/网络盘断开)
            // 也要记录,只是大小缺失填 0。真正复制过文件就必须出现在列表里。
            // 与旧版一致:跳过虚拟条目(压缩包内文件、邮件附件等没有本地路径的项),
            // 它们无法在资源管理器中还原;stat 失败不跳过(大小填 0 即可)。
            let metas: Vec<file_meta::ClipboardFileInfo> = paths
                .iter()
                .filter(|p| !p.trim().is_empty())
                .map(|p| file_meta::info_from_path(p))
                .collect();
            if metas.is_empty() {
                log::warn!("剪贴板文件格式存在但没有可用的本地路径,跳过本次捕获");
                return;
            }
            make_file_item(metas)
        }
        // 图片条目
        (Some(png), _, _) => {
            // 超限处理:先缩到 4096,再按 10MB 字节上限逐级降采样。
            // 能降出可看预览就保留本地文件;降不出来则只记录条目,不落缓存。
            let (encoded, oversized) = match process_image(png) {
                Ok(result) => result,
                Err(e) => { log::warn!("图片处理失败: {e}"); return; }
            };
            // Identity belongs to the original content, not a potentially shared preview.
            let hash = hash::hash_bytes(png);
            let image_path = if let Some(bytes) = encoded {
                let dir = storage_dir.join("images");
                let _ = std::fs::create_dir_all(&dir);
                let path = dir.join(format!("{hash}.png"));
                if !path.exists() && std::fs::write(&path, &bytes).is_err() {
                    log::warn!("图片缓存写入失败");
                    return;
                }
                Some(path.to_string_lossy().into_owned())
            } else { None };
            HistoryItem {
                id: 0,
                server_id: None,
                entry_type: TYPE_IMAGE.into(),
                text: None,
                html: None,
                image_path,
                image_ref: None,
                file_paths_json: None,
                content_hash: Some(hash),
                device_id: device_id.clone(),
                device_name: Some(dev_name.clone()),
                source_app_name: owner_process_name(&owner_path),
                source_app_path: owner_path.clone(),
                source_app_icon: source_icon.clone(),
                created_at_ticks: now_ticks,
                origin: ORIGIN_LOCAL,
                starred: false,
                remark: None,
                oversized,
            }
        }
        // 文本条目(含富文本 HTML)
        (_, _, Some(text)) => {
            let text = text.trim_end_matches(['\r', '\n']).to_string();
            if text.is_empty() {
                return;
            }
            // 文本就是文本:路径文本、代码、URL 一律按文本条目入库。
            // 只有 CF_HDROP(资源管理器里真正 Ctrl+C 复制文件)才算文件条目。
            let h = hash::hash_text(&text, html.as_deref());
            HistoryItem {
                id: 0,
                server_id: None,
                entry_type: TYPE_TEXT.into(),
                text: Some(text),
                html: html.clone(),
                image_path: None,
                image_ref: None,
                file_paths_json: None,
                content_hash: Some(h),
                device_id: device_id.clone(),
                device_name: Some(dev_name.clone()),
                source_app_name: src_name.clone(),
                source_app_path: owner_path.clone(),
                source_app_icon: source_icon.clone(),
                created_at_ticks: now_ticks,
                origin: ORIGIN_LOCAL,
                starred: false,
                remark: None,
                oversized: false,
            }
        }
        _ => return,
    };

    if unsafe { windows::Win32::System::DataExchange::GetClipboardSequenceNumber() } != seq {
        return;
    }
    monitor::capture_stage(seq, "store-history");

    let hash = item.content_hash.clone().unwrap_or_default();
    // 重复内容 → TouchByHash 置顶;否则插入(插入内部会自动裁剪超限条目)
    let (touched, inserted_id, item_for_emit) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let touched = !hash.is_empty()
            && guard
                .history
                .touch_by_hash(&hash, None, &item.device_id, item.device_name.as_deref(), now_ticks,
                    item.source_app_name.as_deref(), item.source_app_path.as_deref(), item.source_app_icon.as_deref())
                .unwrap_or(false);
        if touched && item.entry_type == TYPE_IMAGE {
            if let Some(path) = item.image_path.as_deref() {
                if let Err(e) = guard.history.repair_image_cache(&hash, path, item.oversized) {
                    log::error!("图片缓存记录修复失败: {e}");
                    return;
                }
            }
        }
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
        (touched, inserted_id, item_for_emit)
    };
    // 入库成功(含去重置顶)后才确认序列号;读取/入库失败留给轮询重试。
    monitor::mark_captured(seq);
    let changed_kind = if touched { "touch" } else { "insert" };

    // 本地新内容(非置顶、非文件)→ 提交同步引擎上传(未配对时引擎静默丢弃)
    if !touched && inserted_id > 0 && item.entry_type != "File" && !item.oversized {
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

/// 图片同步上限(与旧版 ImageCodec.MaxImageBytes 一致)。
pub(crate) const MAX_IMAGE_BYTES: usize = 10 * 1024 * 1024;
/// 长边硬上限(与旧版一致)。
const MAX_IMAGE_SIDE: u32 = 4096;
/// 超限降采样的最小长边;到这一步仍超限就放弃本地预览。
const MIN_OVERSIZE_SIDE: u32 = 512;

fn encode_png(img: &image::DynamicImage) -> Option<Vec<u8>> {
    let mut buf = std::io::Cursor::new(Vec::new());
    img.write_to(&mut buf, image::ImageFormat::Png).ok()?;
    Some(buf.into_inner())
}

/// 返回 (可用的本地 PNG 字节, 是否超限)。
/// 超限但降采样成功时返回 (Some(降采样), true);降不出来返回 (None, true)。
fn process_image(png: &[u8]) -> Result<(Option<Vec<u8>>, bool), String> {
    let img = image::load_from_memory(png).map_err(|e| e.to_string())?;
    let original_oversized = png.len() > MAX_IMAGE_BYTES;
    let (w, h) = (img.width(), img.height());
    if !original_oversized && w <= MAX_IMAGE_SIDE && h <= MAX_IMAGE_SIDE {
        return Ok((Some(png.to_vec()), false));
    }
    let resized = if w > MAX_IMAGE_SIDE || h > MAX_IMAGE_SIDE {
        let scale = MAX_IMAGE_SIDE as f64 / w.max(h) as f64;
        let nw = ((w as f64 * scale) as u32).max(1);
        let nh = ((h as f64 * scale) as u32).max(1);
        img.resize(nw, nh, image::imageops::FilterType::Triangle)
    } else {
        img
    };
    let bytes = encode_png(&resized).ok_or("图片编码失败")?;
    if bytes.len() <= MAX_IMAGE_BYTES {
        return Ok((Some(bytes), original_oversized));
    }
    // 超过字节上限:继续等比减半,直到能生成可看的本地预览。
    let mut current = resized;
    loop {
        let (cw, ch) = (current.width(), current.height());
        if cw.max(ch) <= MIN_OVERSIZE_SIDE {
            break;
        }
        let nw = (cw / 2).max(1);
        let nh = (ch / 2).max(1);
        current = current.resize(nw, nh, image::imageops::FilterType::Triangle);
        if let Some(small) = encode_png(&current) {
            if small.len() <= MAX_IMAGE_BYTES {
                log::info!(
                    "图片超过 {MAX_IMAGE_BYTES} 字节,已降采样到 {nw}x{nh} 作为本地预览(不同步)"
                );
                return Ok((Some(small), true));
            }
        }
    }
    log::warn!("图片超过 {MAX_IMAGE_BYTES} 字节且降采样后仍超限,仅记录条目、不生成预览");
    Ok((None, true))
}

fn owner_process_name(path: &Option<String>) -> Option<String> {
    path.as_ref().map(|p| {
        PathBuf::from(p)
            .file_stem()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_else(|| p.clone())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 构造高噪声图,确保 PNG 编码超过 10MB,再验证降采样后的预览与超限标记。
    #[test]
    fn oversized_image_is_downscaled_and_flagged() {
        let side = 2600u32;
        let mut img = image::RgbaImage::new(side, side);
        for (x, y, px) in img.enumerate_pixels_mut() {
            // 高频噪声几乎无法压缩,编码后体积会明显超过 10MB。
            px.0 = [
                (x.wrapping_mul(31) ^ y.wrapping_mul(17)) as u8,
                (x.wrapping_mul(7) ^ y.wrapping_mul(53)) as u8,
                (x.wrapping_mul(97) ^ y.wrapping_mul(11)) as u8,
                255,
            ];
        }
        let png = encode_png(&image::DynamicImage::ImageRgba8(img)).expect("编码失败");
        assert!(
            png.len() > MAX_IMAGE_BYTES,
            "测试图应超过上限,实际 {} 字节",
            png.len()
        );

        let (preview, oversized) = process_image(&png).unwrap();
        assert!(oversized, "超过 10MB 必须标记为 oversized");
        let preview = preview.expect("应能降采样出本地预览");
        assert!(preview.len() <= MAX_IMAGE_BYTES);
        let decoded = image::load_from_memory(&preview).expect("预览应可解码");
        assert!(decoded.width().max(decoded.height()) <= MAX_IMAGE_SIDE);
    }

    #[test]
    fn small_image_is_not_oversized() {
        let img = image::RgbaImage::from_pixel(32, 32, image::Rgba([255, 0, 0, 255]));
        let png = encode_png(&image::DynamicImage::ImageRgba8(img)).unwrap();
        let (preview, oversized) = process_image(&png).unwrap();
        assert!(!oversized);
        assert_eq!(preview.unwrap(), png);
    }

    #[test]
    fn original_size_limit_survives_recompression() {
        let img = image::RgbaImage::from_pixel(16, 16, image::Rgba([255, 0, 0, 255]));
        let mut png = encode_png(&image::DynamicImage::ImageRgba8(img)).unwrap();
        png.resize(MAX_IMAGE_BYTES + 1, 0);
        let (preview, oversized) = process_image(&png).unwrap();
        assert!(oversized);
        assert!(preview.unwrap().len() < MAX_IMAGE_BYTES);
    }

    #[test]
    fn invalid_image_is_rejected() {
        assert!(process_image(b"broken png").is_err());
    }
}
