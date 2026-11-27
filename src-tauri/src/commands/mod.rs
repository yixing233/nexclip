//! Tauri IPC 命令层:前端 ↔ Rust 数据层的唯一入口。
//!
//! 序列化为 camelCase(serde rename),前端 TS 类型与之对应。

use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

use crate::clipboard::files_meta::FileInfoDto;
use crate::history::HistoryItem;
use crate::settings;
use crate::AppState;

type SharedState = Arc<Mutex<AppState>>;

/// 前端视图模型:history 结构 + 解析后的文件列表 + 前端友好的时间戳。
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct HistoryItemDto {
    pub id: i64,
    pub server_id: Option<i64>,
    pub entry_type: String,
    pub text: Option<String>,
    pub html: Option<String>,
    pub image_path: Option<String>,
    pub image_ref: Option<String>,
    /// 文件列表(camelCase DTO;库里存的是 PascalCase,这里做转换)
    pub files: Vec<FileInfoDto>,
    pub device_id: String,
    pub device_name: Option<String>,
    /// 设备平台(Android / Windows / Web …),来自 devices_cache.json;
    /// 用于列表里区分手机/电脑/网页图标。缓存缺失时为 None。
    pub device_platform: Option<String>,
    pub source_app_name: Option<String>,
    pub source_app_path: Option<String>,
    pub source_app_icon: Option<String>,
    /// UTC 毫秒时间戳
    pub created_at: i64,
    pub origin: i32,
    pub starred: bool,
    pub remark: Option<String>,
    /// 图片原始体积超过同步上限(10MB):仅本地保存,不参与同步。
    pub oversized: bool,
    pub is_manual: bool,
}

/// 设备 id → 平台 的映射(来自 devices_cache.json,查询时读一次)。
pub(crate) type PlatformMap = std::collections::HashMap<String, String>;

/// 读取设备平台映射(缓存文件缺失/损坏时返回空表,不影响列表查询)。
fn load_platform_map(state: &State<SharedState>) -> PlatformMap {
    let path = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        settings::devices_cache_file_path(&settings::resolve_storage_dir(&guard.settings.storage_dir))
    };
    let Ok(text) = std::fs::read_to_string(path) else {
        return PlatformMap::new();
    };
    let Ok(list) = serde_json::from_str::<Vec<DeviceInfoDto>>(&text) else {
        return PlatformMap::new();
    };
    list.into_iter()
        .filter_map(|d| {
            let platform = d.platform.filter(|p| !p.trim().is_empty())?;
            Some((d.id.to_lowercase(), platform))
        })
        .collect()
}

pub(crate) fn to_dto(item: HistoryItem) -> HistoryItemDto {
    to_dto_with_platform(item, None)
}

/// 带设备平台信息的 DTO 转换(列表查询用;事件广播拿不到映射时传 None)。
pub(crate) fn to_dto_with_platform(
    item: HistoryItem,
    platforms: Option<&PlatformMap>,
) -> HistoryItemDto {
    let device_platform = platforms
        .and_then(|m| m.get(&item.device_id.to_lowercase()).cloned());
    HistoryItemDto {
        id: item.id,
        server_id: item.server_id,
        entry_type: item.entry_type.clone(),
        text: item.text.clone(),
        html: item.html.clone(),
        image_path: item.image_path.clone(),
        image_ref: item.image_ref.clone(),
        files: item.files().into_iter().map(FileInfoDto::from).collect(),
        device_id: item.device_id.clone(),
        device_name: item.device_name.clone(),
        device_platform,
        source_app_name: item.source_app_name.clone(),
        source_app_path: item.source_app_path.clone(),
        source_app_icon: item.source_app_icon.clone(),
        created_at: item.created_at_millis(),
        origin: item.origin,
        starred: item.starred,
        remark: item.remark.clone(),
        oversized: item.oversized,
        is_manual: item.is_manual(),
    }
}

fn with_history<T>(
    state: &State<SharedState>,
    f: impl FnOnce(&crate::history::HistoryStore) -> Result<T, rusqlite::Error>,
) -> Result<T, String> {
    let guard = state
        .lock()
        .unwrap_or_else(|p| p.into_inner());
    f(&guard.history).map_err(|e| e.to_string())
}

/// 查询历史(新→旧)。
#[tauri::command]
pub fn history_query(
    state: State<SharedState>,
    search: Option<String>,
    entry_type: Option<String>,
    starred_only: Option<bool>,
    url_only: Option<bool>,
    limit: Option<i64>,
    offset: Option<i64>,
) -> Result<Vec<HistoryItemDto>, String> {
    let items = with_history(&state, |h| {
        h.query(
            search.as_deref(),
            entry_type.as_deref(),
            starred_only.unwrap_or(false),
            url_only.unwrap_or(false),
            limit.unwrap_or(500),
            offset.unwrap_or(0),
        )
    })?;
    let platforms = load_platform_map(&state);
    Ok(items
        .into_iter()
        .map(|it| to_dto_with_platform(it, Some(&platforms)))
        .collect())
}

#[tauri::command]
pub fn history_count(state: State<SharedState>) -> Result<i64, String> {
    with_history(&state, |h| Ok(h.count()))
}

#[tauri::command]
pub fn history_delete(state: State<SharedState>, id: i64) -> Result<(), String> {
    with_history(&state, |h| h.delete(id))
}

#[tauri::command]
pub fn history_toggle_star(
    state: State<SharedState>,
    id: i64,
    starred: bool,
) -> Result<(), String> {
    with_history(&state, |h| h.toggle_star(id, starred))
}

#[tauri::command]
pub fn history_update_remark(
    state: State<SharedState>,
    id: i64,
    remark: Option<String>,
) -> Result<(), String> {
    with_history(&state, |h| h.update_remark(id, remark.as_deref()))
}

/// 编辑条目文本(仅本地;拼音检索串由 store 自动重算)。
#[tauri::command]
pub fn history_update_text(state: State<SharedState>, id: i64, text: String) -> Result<(), String> {
    with_history(&state, |h| h.update_text(id, &text))
}

/// 清空历史(keep_starred 默认 true)。
#[tauri::command]
pub fn history_clear(state: State<SharedState>, keep_starred: Option<bool>) -> Result<(), String> {
    with_history(&state, |h| h.clear(keep_starred.unwrap_or(true)))
}

/// 清空互传会话记录(仅本机保存的互传/手动推送条目)。返回删除条数。
#[tauri::command]
pub fn chat_clear(state: State<SharedState>) -> Result<usize, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let wave = crate::settings::resolve_storage_dir(&guard.settings.storage_dir)
        .join("chat-cleared.wave");
    // 记录"清空时的最大 server_id",防止下次连接服务器又把删掉的旧消息补齐回来。
    let max_server_id = guard.history.max_chat_server_id().map_err(|e| e.to_string())?;
    let removed = guard.history.clear_chat().map_err(|e| e.to_string())?
        + guard.history.clear_transfer_files().map_err(|e| e.to_string())?;
    if let Some(id) = max_server_id {
        if let Some(dir) = wave.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        let _ = std::fs::write(&wave, id.to_string());
    }
    Ok(removed)
}

/// 把历史图片另存为(系统"另存为"对话框)。返回保存路径,用户取消返回 None。
#[tauri::command]
pub fn save_image_as(state: State<SharedState>, id: i64) -> Result<Option<String>, String> {
    let item = item_by_id(state.inner(), id)?;
    if item.entry_type != "Image" {
        return Err("该条目不是图片".into());
    }
    let src = item.image_path.clone().ok_or("图片缓存缺失")?;
    let stem = std::path::Path::new(&src)
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("image")
        .to_string();
    let Some(dest) = save_png_dialog(&stem, "")? else {
        return Ok(None);
    };
    std::fs::copy(&src, &dest).map_err(|e| format!("保存失败: {e}"))?;
    Ok(Some(dest))
}

// ---- 设置 ----

/// 设置快照(auth_token 不出后端)。字段与旧版设置页对齐。
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsDto {
    pub server_url: String,
    pub is_paired: bool,
    pub device_id: String,
    pub device_name: String,
    pub hotkey: String,
    pub hotkey_settings: String,
    pub hotkey_open_url: String,
    pub hotkey_topmost: String,
    pub hotkey_screenshot: String,
    pub hotkey_screenshot_screen: String,
    pub hotkey_screenshot_window: String,
    pub hotkey_pin: String,
    pub screenshot_enabled: bool,
    pub screenshot_save_dir: String,
    pub theme_mode: String,
    pub icon_library: String,
    pub monitor_enabled: bool,
    pub app_filter_enabled: bool,
    pub custom_filtered_processes: Vec<String>,
    pub auto_paste: bool,
    pub notify_enabled: bool,
    pub copy_direct_enabled: bool,
    pub smart_color_enabled: bool,
    pub smart_path_enabled: bool,
    pub smart_deep_link_enabled: bool,
    pub smart_netdisk_enabled: bool,
    pub smart_url_enabled: bool,
    pub max_history: i64,
    pub remember_scroll_position: bool,
    pub rich_text_enabled: bool,
    pub window_position_mode: String,
    pub retention_days: i64,
    pub paste_key: String,
    pub storage_dir: String,
    pub boot_start_enabled: bool,
    pub start_minimized: bool,
    pub close_to_tray: bool,
    pub auto_check_update: bool,
    pub update_source: String,
    /// 实际生效的数据目录(解析默认值后)
    pub resolved_storage_dir: String,
}

#[tauri::command]
pub fn settings_get(state: State<SharedState>) -> Result<SettingsDto, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let s = &guard.settings;
    Ok(SettingsDto {
        server_url: s.server_url.clone(),
        is_paired: s.is_paired,
        device_id: s.device_id.clone(),
        device_name: s.device_name.clone(),
        hotkey: s.hotkey.clone(),
        hotkey_settings: s.hotkey_settings.clone(),
        hotkey_open_url: s.hotkey_open_url.clone(),
        hotkey_topmost: s.hotkey_topmost.clone(),
        hotkey_screenshot: s.hotkey_screenshot.clone(),
        hotkey_screenshot_screen: s.hotkey_screenshot_screen.clone(),
        hotkey_screenshot_window: s.hotkey_screenshot_window.clone(),
        hotkey_pin: s.hotkey_pin.clone(),
        screenshot_enabled: s.screenshot_enabled,
        screenshot_save_dir: s.screenshot_save_dir.clone(),
        theme_mode: s.theme_mode.clone(),
        icon_library: s.icon_library.clone(),
        monitor_enabled: s.monitor_enabled,
        app_filter_enabled: s.app_filter_enabled,
        custom_filtered_processes: s.custom_filtered_processes.clone(),
        auto_paste: s.auto_paste,
        notify_enabled: s.notify_enabled,
        copy_direct_enabled: s.copy_direct_enabled,
        smart_color_enabled: s.smart_color_enabled,
        smart_path_enabled: s.smart_path_enabled,
        smart_deep_link_enabled: s.smart_deep_link_enabled,
        smart_netdisk_enabled: s.smart_netdisk_enabled,
        smart_url_enabled: s.smart_url_enabled,
        max_history: s.max_history,
        remember_scroll_position: s.remember_scroll_position,
        rich_text_enabled: s.rich_text_enabled,
        window_position_mode: s.window_position_mode.clone(),
        retention_days: s.retention_days,
        paste_key: s.paste_key.clone(),
        storage_dir: s.storage_dir.clone(),
        boot_start_enabled: s.boot_start_enabled,
        start_minimized: s.start_minimized,
        close_to_tray: s.close_to_tray,
        auto_check_update: s.auto_check_update,
        update_source: s.update_source.clone(),
        resolved_storage_dir: settings::resolve_storage_dir(&s.storage_dir)
            .to_string_lossy()
            .into_owned(),
    })
}

/// 设置补丁:只应用携带的字段(None = 保持原值)。
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SettingsPatch {
    pub server_url: Option<String>,
    pub device_name: Option<String>,
    pub hotkey: Option<String>,
    pub hotkey_settings: Option<String>,
    pub hotkey_open_url: Option<String>,
    pub hotkey_topmost: Option<String>,
    pub hotkey_screenshot: Option<String>,
    pub hotkey_screenshot_screen: Option<String>,
    pub hotkey_screenshot_window: Option<String>,
    pub hotkey_pin: Option<String>,
    pub screenshot_enabled: Option<bool>,
    pub screenshot_save_dir: Option<String>,
    pub theme_mode: Option<String>,
    pub icon_library: Option<String>,
    pub monitor_enabled: Option<bool>,
    pub app_filter_enabled: Option<bool>,
    pub custom_filtered_processes: Option<Vec<String>>,
    pub auto_paste: Option<bool>,
    pub notify_enabled: Option<bool>,
    pub copy_direct_enabled: Option<bool>,
    pub smart_color_enabled: Option<bool>,
    pub smart_path_enabled: Option<bool>,
    pub smart_deep_link_enabled: Option<bool>,
    pub smart_netdisk_enabled: Option<bool>,
    pub smart_url_enabled: Option<bool>,
    pub max_history: Option<i64>,
    pub remember_scroll_position: Option<bool>,
    pub rich_text_enabled: Option<bool>,
    pub window_position_mode: Option<String>,
    pub retention_days: Option<i64>,
    pub paste_key: Option<String>,
    pub storage_dir: Option<String>,
    pub boot_start_enabled: Option<bool>,
    pub start_minimized: Option<bool>,
    pub close_to_tray: Option<bool>,
    pub auto_check_update: Option<bool>,
    pub update_source: Option<String>,
}

impl Default for SettingsPatch {
    fn default() -> Self {
        SettingsPatch {
            server_url: None,
            device_name: None,
            hotkey: None,
            hotkey_settings: None,
            hotkey_open_url: None,
            hotkey_topmost: None,
            hotkey_screenshot: None,
            hotkey_screenshot_screen: None,
            hotkey_screenshot_window: None,
            hotkey_pin: None,
            screenshot_enabled: None,
            screenshot_save_dir: None,
            theme_mode: None,
            icon_library: None,
            monitor_enabled: None,
            app_filter_enabled: None,
            custom_filtered_processes: None,
            auto_paste: None,
            notify_enabled: None,
            copy_direct_enabled: None,
            smart_color_enabled: None,
            smart_path_enabled: None,
            smart_deep_link_enabled: None,
            smart_netdisk_enabled: None,
            smart_url_enabled: None,
            max_history: None,
            remember_scroll_position: None,
            rich_text_enabled: None,
            window_position_mode: None,
            retention_days: None,
            paste_key: None,
            storage_dir: None,
            boot_start_enabled: None,
            start_minimized: None,
            close_to_tray: None,
            auto_check_update: None,
            update_source: None,
        }
    }
}

/// 更新设置并持久化;带副作用的字段(条目上限/保留天数/储存目录)立即生效。
/// 保存后向全部窗口广播 `settings-changed`(前端据此刷新主题等)。
#[tauri::command]
pub fn settings_set(
    app: AppHandle,
    state: State<SharedState>,
    patch: SettingsPatch,
) -> Result<(), String> {
    let mut guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let crate::AppState { settings: s, history, sync_status: _ } = &mut *guard;

    if let Some(v) = patch.server_url {
        s.server_url = v.trim().trim_end_matches('/').to_string();
    }
    if let Some(v) = patch.device_name {
        let v = v.trim().to_string();
        if !v.is_empty() {
            s.device_name = v;
        }
    }
    // 快捷键:非空才应用(捕获框清空 = 放弃修改)
    for (dst, src) in [
        (&mut s.hotkey, &patch.hotkey),
        (&mut s.hotkey_settings, &patch.hotkey_settings),
        (&mut s.hotkey_open_url, &patch.hotkey_open_url),
        (&mut s.hotkey_topmost, &patch.hotkey_topmost),
        (&mut s.hotkey_screenshot, &patch.hotkey_screenshot),
        (&mut s.hotkey_screenshot_screen, &patch.hotkey_screenshot_screen),
        (&mut s.hotkey_screenshot_window, &patch.hotkey_screenshot_window),
        (&mut s.hotkey_pin, &patch.hotkey_pin),
    ] {
        if let Some(v) = src {
            let v = v.trim().to_string();
            if !v.is_empty() {
                *dst = v;
            }
        }
    }
    if let Some(v) = patch.theme_mode {
        if ["light", "dark", "system"].contains(&v.as_str()) {
            s.theme_mode = v;
        }
    }
    if let Some(v) = patch.icon_library {
        if ["lucide", "tabler", "phosphor", "remix"].contains(&v.as_str()) {
            s.icon_library = v;
        }
    }
    if let Some(v) = patch.screenshot_enabled {
        s.screenshot_enabled = v;
    }
    if let Some(v) = patch.screenshot_save_dir {
        s.screenshot_save_dir = v.trim().to_string();
    }
    if let Some(v) = patch.monitor_enabled {
        s.monitor_enabled = v;
    }
    if let Some(v) = patch.app_filter_enabled {
        s.app_filter_enabled = v;
    }
    if let Some(v) = patch.custom_filtered_processes {
        // 去空白、去重(忽略大小写),与旧版一致
        let mut out: Vec<String> = Vec::new();
        let mut seen: Vec<String> = Vec::new();
        for x in v {
            let t = x.trim().to_string();
            if t.is_empty() {
                continue;
            }
            let lower = t.to_lowercase();
            if !seen.contains(&lower) {
                seen.push(lower);
                out.push(t);
            }
        }
        s.custom_filtered_processes = out;
    }
    if let Some(v) = patch.auto_paste {
        s.auto_paste = v;
    }
    if let Some(v) = patch.notify_enabled {
        s.notify_enabled = v;
    }
    if let Some(v) = patch.copy_direct_enabled {
        s.copy_direct_enabled = v;
    }
    if let Some(v) = patch.smart_color_enabled {
        s.smart_color_enabled = v;
    }
    if let Some(v) = patch.smart_path_enabled {
        s.smart_path_enabled = v;
    }
    if let Some(v) = patch.smart_deep_link_enabled {
        s.smart_deep_link_enabled = v;
    }
    if let Some(v) = patch.smart_netdisk_enabled {
        s.smart_netdisk_enabled = v;
    }
    if let Some(v) = patch.smart_url_enabled {
        s.smart_url_enabled = v;
    }
    if let Some(v) = patch.max_history {
        if v > 0 {
            s.max_history = v;
            history.set_max_entries(v);
        }
    }
    if let Some(v) = patch.remember_scroll_position {
        s.remember_scroll_position = v;
    }
    if let Some(v) = patch.rich_text_enabled {
        s.rich_text_enabled = v;
    }
    if let Some(v) = patch.window_position_mode {
        if ["cursor", "center"].contains(&v.as_str()) {
            s.window_position_mode = v;
        }
    }
    if let Some(v) = patch.retention_days {
        if v >= 0 {
            s.retention_days = v;
            if v > 0 {
                if let Err(e) = history.prune_older_than(v) {
                    log::warn!("按保留天数清理历史失败: {e}");
                }
            }
        }
    }
    if let Some(v) = patch.paste_key {
        if ["CtrlV", "ShiftInsert"].contains(&v.as_str()) {
            s.paste_key = v;
        }
    }
    if let Some(v) = patch.storage_dir {
        let trimmed = v.trim().to_string();
        if trimmed != s.storage_dir {
            s.storage_dir = trimmed;
            // 立即切换历史库到新目录(schema 按需迁移,空目录等于全新历史)
            let dir = settings::resolve_storage_dir(&s.storage_dir);
            history
                .reopen(&dir)
                .map_err(|e| format!("切换储存目录失败: {e}"))?;
        }
    }
    if let Some(v) = patch.boot_start_enabled {
        crate::platform::autostart::apply(v)?;
        s.boot_start_enabled = v;
    }
    if let Some(v) = patch.start_minimized {
        s.start_minimized = v;
    }
    if let Some(v) = patch.close_to_tray {
        s.close_to_tray = v;
    }
    if let Some(v) = patch.auto_check_update {
        s.auto_check_update = v;
    }
    if let Some(v) = patch.update_source {
        if ["github", "direct"].contains(&v.as_str()) {
            s.update_source = v;
        }
    }

    settings::save_settings(s);
    // 热键可能被修改:立即重注册
    crate::platform::hotkey::reload(crate::build_hotkey_specs(s));
    // 截图热键/监听开关改动:同步刷新托盘菜单(传入当前值避免重入 AppState 锁)
    if patch.hotkey_screenshot.is_some() || patch.monitor_enabled.is_some() {
        crate::update_tray_menu(&app, &s.hotkey_screenshot, s.monitor_enabled);
    }
    // 广播给所有窗口(主题等即时生效)
    let _ = app.emit("settings-changed", ());
    Ok(())
}

// ---- 数据管理 / 关于 ----

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoryStats {
    pub count: i64,
    pub starred_count: i64,
    /// history.db 文件大小(字节,不含 WAL)
    pub db_size_bytes: u64,
}

#[tauri::command]
pub fn history_stats(state: State<SharedState>) -> Result<HistoryStats, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let db_size = std::fs::metadata(guard.history.db_path())
        .map(|m| m.len())
        .unwrap_or(0);
    Ok(HistoryStats {
        count: guard.history.count(),
        starred_count: guard.history.count_starred(),
        db_size_bytes: db_size,
    })
}

/// 用资源管理器打开目录或链接(explorer 打开 URL 会走默认浏览器)。
#[tauri::command]
pub fn open_in_explorer(path: String) -> Result<(), String> {
    std::process::Command::new("explorer")
        .arg(&path)
        .spawn()
        .map_err(|e| format!("打开失败: {e}"))?;
    Ok(())
}

/// 系统文件夹选择对话框(模态到指定窗口;返回所选目录,取消返回 None)。
#[tauri::command]
pub fn pick_folder(_app: AppHandle) -> Result<Option<String>, String> {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::HWND;
    use windows::Win32::System::Com::{CoCreateInstance, CoTaskMemFree, CLSCTX_INPROC_SERVER};
    use windows::Win32::UI::Shell::{FileOpenDialog, IFileOpenDialog, FOS_PICKFOLDERS, SIGDN_FILESYSPATH};
    use windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow;

    // 父窗口:前台窗口(设置窗口),否则无主
    let hwnd: HWND = unsafe { GetForegroundWindow() };

    unsafe {
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
            .map_err(|e| format!("创建对话框失败: {e}"))?;
        let _ = dialog.SetOptions(FOS_PICKFOLDERS);
        let _ = dialog.SetTitle(w!("选择数据目录"));
        if dialog.Show(hwnd).is_err() {
            return Ok(None); // 用户取消
        }
        let item = dialog.GetResult().map_err(|e| format!("获取结果失败: {e}"))?;
        let path_pwstr = item
            .GetDisplayName(SIGDN_FILESYSPATH)
            .map_err(|e| format!("获取路径失败: {e}"))?;
        let mut len = 0usize;
        while *path_pwstr.0.add(len) != 0 {
            len += 1;
        }
        let path = String::from_utf16_lossy(std::slice::from_raw_parts(path_pwstr.0, len));
        CoTaskMemFree(Some(path_pwstr.0 as *const _));
        let _ = PCWSTR::null();
        Ok(Some(path))
    }
}

/// 通用多选文件对话框。`filter` 为 (显示名, 通配符);None 表示仅“所有文件”。
fn open_paths_dialog(
    title: &str,
    filter: Option<(&str, &str)>,
) -> Result<Vec<String>, String> {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::System::Com::{CoCreateInstance, CoTaskMemFree, CLSCTX_INPROC_SERVER};
    use windows::Win32::UI::Shell::{
        FileOpenDialog, IFileOpenDialog, Common::COMDLG_FILTERSPEC, FOS_ALLOWMULTISELECT,
        FOS_FILEMUSTEXIST, FOS_PATHMUSTEXIST, SIGDN_FILESYSPATH,
    };
    use windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow;

    let hwnd: HWND = unsafe { GetForegroundWindow() };
    let title_w: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();
    let wide = |s: &str| s.encode_utf16().chain(std::iter::once(0)).collect::<Vec<u16>>();
    let name_owned = filter.map(|(name, _)| wide(name));
    let spec_owned = filter.map(|(_, spec)| wide(spec));
    let all_name = wide("所有文件");
    let all_spec = wide("*.*");

    unsafe {
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
            .map_err(|e| format!("创建对话框失败: {e}"))?;
        let _ = dialog.SetOptions(FOS_ALLOWMULTISELECT | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST);
        let _ = dialog.SetTitle(PCWSTR(title_w.as_ptr()));

        let mut filters: Vec<COMDLG_FILTERSPEC> = Vec::new();
        if let (Some(n), Some(s)) = (name_owned.as_ref(), spec_owned.as_ref()) {
            filters.push(COMDLG_FILTERSPEC {
                pszName: PCWSTR(n.as_ptr()),
                pszSpec: PCWSTR(s.as_ptr()),
            });
        }
        filters.push(COMDLG_FILTERSPEC {
            pszName: PCWSTR(all_name.as_ptr()),
            pszSpec: PCWSTR(all_spec.as_ptr()),
        });
        let _ = dialog.SetFileTypes(&filters);

        if dialog.Show(hwnd).is_err() {
            return Ok(Vec::new()); // 用户取消
        }
        let results = dialog.GetResults().map_err(|e| format!("获取结果失败: {e}"))?;
        let count = results.GetCount().unwrap_or(0);
        let mut paths = Vec::with_capacity(count as usize);
        for i in 0..count {
            let Ok(item) = results.GetItemAt(i) else { continue };
            let Ok(path_pwstr) = item.GetDisplayName(SIGDN_FILESYSPATH) else { continue };
            let mut len = 0usize;
            while *path_pwstr.0.add(len) != 0 {
                len += 1;
            }
            paths.push(String::from_utf16_lossy(std::slice::from_raw_parts(
                path_pwstr.0,
                len,
            )));
            CoTaskMemFree(Some(path_pwstr.0 as *const _));
        }
        let _ = PCWSTR::null();
        Ok(paths)
    }
}

/// 选择图片文件(可多选),返回绝对路径列表;用户取消返回空列表。
#[tauri::command]
pub fn pick_images(_app: AppHandle) -> Result<Vec<String>, String> {
    open_paths_dialog(
        "选择图片",
        Some((
            "图片文件",
            "*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.ico;*.tif;*.tiff;*.svg",
        )),
    )
}

/// 选择任意文件(二进制互传),返回绝对路径列表。
#[tauri::command]
pub fn pick_files(_app: AppHandle) -> Result<Vec<String>, String> {
    open_paths_dialog("选择要发送的文件", None)
}

/// 活跃用户进程候选(可见窗口进程,排除系统进程),供过滤进程下拉。
#[tauri::command]
pub fn list_user_processes() -> Vec<crate::platform::processes::ProcessEntry> {
    crate::platform::processes::list_user_processes()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AppInfo {
    pub version: String,
    pub os: String,
}

#[tauri::command]
pub fn app_info() -> Result<AppInfo, String> {
    Ok(AppInfo {
        version: env!("CARGO_PKG_VERSION").to_string(),
        os: format!("Windows {} 位", std::env::consts::ARCH.to_uppercase()),
    })
}

// ---- 更新检查 / 设备列表缓存 ----

/// 设备列表项(读本地缓存 devices_cache.json,SWR 首屏直出;实时列表待 Phase 3)。
#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase", default)]
pub struct DeviceInfoDto {
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Name")]
    pub name: Option<String>,
    #[serde(alias = "Platform")]
    pub platform: Option<String>,
    #[serde(alias = "Ip")]
    pub ip: Option<String>,
    #[serde(alias = "Version")]
    pub version: Option<String>,
    #[serde(alias = "Online")]
    pub online: bool,
    /// ISO-8601 字符串(C# System.Text.Json 序列化 DateTime 的格式)
    #[serde(alias = "LastSeenAt")]
    pub last_seen_at: Option<String>,
    #[serde(alias = "IsCurrent")]
    pub is_current: bool,
}

impl Default for DeviceInfoDto {
    fn default() -> Self {
        DeviceInfoDto {
            id: String::new(),
            name: None,
            platform: None,
            ip: None,
            version: None,
            online: false,
            last_seen_at: None,
            is_current: false,
        }
    }
}

#[tauri::command]
pub fn devices_cached(state: State<SharedState>) -> Result<Vec<DeviceInfoDto>, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let path = settings::devices_cache_file_path(&settings::resolve_storage_dir(
        &guard.settings.storage_dir,
    ));
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(_) => return Ok(Vec::new()), // 无缓存文件 = 空列表
    };
    if text.trim().is_empty() {
        return Ok(Vec::new());
    }
    match serde_json::from_str::<Vec<DeviceInfoDto>>(&text) {
        Ok(mut list) => {
            // 本地兜底判定"本机":服务端 IsCurrent 字段不可靠,
            // 用 settings 里的本机 device_id 对比强制标记
            let self_id = guard.settings.device_id.clone();
            for d in list.iter_mut() {
                if !d.id.is_empty() && d.id == self_id {
                    d.is_current = true;
                }
            }
            Ok(list)
        }
        Err(e) => {
            log::warn!("devices_cache.json 解析失败: {e}");
            Ok(Vec::new())
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateResultDto {
    pub success: bool,
    pub has_update: bool,
    pub current_version: String,
    pub latest_version: String,
    pub release_title: String,
    pub release_notes: String,
    pub release_url: String,
    pub download_url: String,
    pub sha256: Option<String>,
    pub file_size: Option<i64>,
    pub error_message: Option<String>,
}

/// 检查更新:按设置的来源(github / 服务器直连)。auth_token 不参与本请求。
#[tauri::command]
pub async fn check_update(state: State<'_, SharedState>) -> Result<UpdateResultDto, String> {
    // 先取设置值再发请求,不跨 await 持锁
    let (source, server_url) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.update_source.clone(),
            guard.settings.server_url.clone(),
        )
    };
    let current = env!("CARGO_PKG_VERSION").to_string();
    let r = crate::update::check(&source, &server_url, &current).await;
    crate::update::cache_result(&r);
    Ok(UpdateResultDto {
        success: r.success,
        has_update: r.has_update,
        current_version: r.current_version,
        latest_version: r.latest_version,
        release_title: r.release_title,
        release_notes: r.release_notes,
        release_url: r.release_url,
        download_url: r.download_url,
        sha256: r.sha256,
        file_size: r.file_size,
        error_message: r.error_message,
    })
}

#[tauri::command]
pub fn update_check_state() -> Option<crate::update::UpdateCheckResult> {
    crate::update::cached_result()
}

/// 内置下载更新:流式下载安装包到 %LOCALAPPDATA%/NexClip/updates,
/// 进度经 "update-download-progress" 事件推送;完成后返回落盘路径。
#[tauri::command]
pub async fn download_update(
    app: tauri::AppHandle,
    url: String,
    sha256: Option<String>,
) -> Result<String, String> {
    if url.trim().is_empty() || !url.starts_with("http") {
        return Err("下载地址无效".into());
    }
    crate::update::download_installer(app, url, sha256).await
}

/// 查询当前下载状态(前端挂载时恢复进度用)。
#[tauri::command]
pub fn update_download_state() -> Option<crate::update::DownloadProgress> {
    crate::update::download_state()
}

/// 取消进行中的下载(半成品 .part 文件会被清理)。
#[tauri::command]
pub fn cancel_update_download() {
    crate::update::cancel_download();
}

/// 启动已下载的安装包。
#[tauri::command]
pub fn run_update_installer(path: String) -> Result<(), String> {
    crate::update::run_installer(&path)
}

/// 显示并聚焦设置窗口(Rust 侧实现,避免前端 window API 的权限/时序问题)。
/// 按当前主题设置原生标题栏颜色,逻辑统一在 lib.rs::show_settings_window。
#[tauri::command]
pub fn open_settings(app: AppHandle) -> Result<(), String> {
    crate::show_settings_window(&app);
    Ok(())
}

// ---- Phase 2:剪贴板写入 / 粘贴回注 / 窗口控制 ----

/// 把历史条目写回系统剪贴板(复制)。返回 Err 时旧版行为是托盘通知,不重试。
fn payload_from_item(item: &HistoryItem, rich_text: bool) -> Result<crate::platform::writer::WritePayload, String> {
    match item.entry_type.as_str() {
        "File" => {
            let paths: Vec<String> = item.files().iter().map(|f| f.path.clone()).collect();
            if paths.is_empty() {
                return Err("文件条目无路径".into());
            }
            Ok(crate::platform::writer::WritePayload::Files(paths))
        }
        "Image" => {
            let path = item.image_path.as_deref().ok_or("图片缓存缺失")?;
            let png = std::fs::read(path).map_err(|e| format!("图片缓存读取失败: {e}"))?;
            Ok(crate::platform::writer::WritePayload::Image { png })
        }
        _ => {
            let text = item.text.clone().ok_or("文本为空")?;
            Ok(crate::platform::writer::WritePayload::Text {
                text,
                html: item.html.clone().filter(|_| rich_text),
            })
        }
    }
}

/// 复制历史条目到剪贴板(右键菜单"复制")。
#[tauri::command]
pub async fn copy_item(state: State<'_, SharedState>, id: i64) -> Result<(), String> {
    let state = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        let item = item_by_id(&state, id)?;
        let rich_text = state.lock().unwrap_or_else(|p| p.into_inner()).settings.rich_text_enabled;
        crate::platform::writer::submit(payload_from_item(&item, rich_text)?, 3000)
    }).await.map_err(|e| e.to_string())?
}

/// 按内容哈希读取最新本地条目,供"粘贴到目标"兜底使用。
fn item_by_id(state: &Arc<Mutex<AppState>>, id: i64) -> Result<HistoryItem, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    guard.history.find_by_id(id).map_err(|e| e.to_string())?
        .ok_or_else(|| "条目不存在".to_string())
}

/// 粘贴到目标应用:写剪贴板 → 隐藏弹窗 → 焦点回落 → SendInput Ctrl+V。
/// 目标在热键呼出弹窗的瞬间已记录(见 lib.rs toggle_main)。
#[tauri::command]
pub async fn paste_item(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
    id: i64,
) -> Result<(), String> {
    let target = crate::PASTE_TARGET
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone()
        .ok_or("没有记录的粘贴目标(请通过热键呼出弹窗)")?;
    if !unsafe { crate::platform::paste::target_alive(&target) } {
        return Err("目标窗口已关闭".into());
    }

    // 写剪贴板(专用线程执行阻塞调用;超时兜底)
    let state2 = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || -> Result<(), String> {
            let item = item_by_id(&state2, id)?;
            let rich_text = state2.lock().unwrap_or_else(|p| p.into_inner()).settings.rich_text_enabled;
            let payload = payload_from_item(&item, rich_text)?;
            crate::platform::writer::submit(payload, 3000)
        })
        .await.map_err(|e| e.to_string())??;

    // 隐藏弹窗让焦点自然回落(置顶模式下保持可见,不强制切前台)
    let keep_visible = crate::pinned();
    if !keep_visible {
        crate::hide_main(&app);
    }

    // 注入(按设置选择 Ctrl+V / Shift+Insert;修饰键先抬起)
    let paste_key = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.paste_key.clone()
    };
    crate::platform::paste::inject(&target, keep_visible, &paste_key)
}

/// 图钉开关(前端按钮);置顶由 Ctrl+Alt+T 或本命令设置。
#[tauri::command]
pub fn set_pinned(app: tauri::AppHandle, pinned: bool) -> Result<(), String> {
    crate::set_pinned(pinned, &app);
    Ok(())
}

/// 失焦自动隐藏(前端 onFocusChanged 且未置顶时调用)。
#[tauri::command]
pub fn hide_main(app: tauri::AppHandle) -> Result<(), String> {
    crate::hide_main(&app);
    Ok(())
}

/// 托盘/前端通用:按当前状态切换弹窗显隐。
#[tauri::command]
pub fn toggle_main(app: tauri::AppHandle) -> Result<(), String> {
    crate::toggle_main(&app);
    Ok(())
}

// ---- Phase 3:同步/配对命令(设置页同步分区使用) ----

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SyncStateDto {
    /// not_configured / connecting / connected / reconnecting / disconnected / offline
    pub status: String,
    pub is_paired: bool,
    pub server_url: String,
    pub device_name: String,
    pub device_id: String,
}

#[tauri::command]
pub fn sync_get_state(state: State<SharedState>) -> Result<SyncStateDto, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let status = guard
        .sync_status
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone();
    let s = &guard.settings;
    Ok(SyncStateDto {
        status,
        is_paired: s.is_paired,
        server_url: s.server_url.clone(),
        device_name: s.device_name.clone(),
        device_id: s.device_id.clone(),
    })
}

/// 生成一次性配对码(设置页"去配对")。
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PairingCodeDto {
    pub code: String,
    /// ISO-8601 UTC
    pub expires_at: Option<String>,
    /// 服务端给的扫码直连 payload(空则前端用 {server}/index?pairCode=code 兜底)
    pub qr_payload: Option<String>,
}

#[tauri::command]
pub async fn sync_pair_create(state: State<'_, SharedState>) -> Result<PairingCodeDto, String> {
    let (server_url, device_id, device_name, token) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
            guard.settings.auth_token.clone(),
        )
    };
    let r = crate::sync::api::create_pairing_code(&server_url, &device_id, &device_name, &token)
        .await
        .map_err(|e| e.to_string())?;
    Ok(PairingCodeDto {
        code: r.code,
        expires_at: r.expires_at,
        qr_payload: r.qr_payload,
    })
}

#[tauri::command]
pub async fn sync_pair_revoke(state: State<'_, SharedState>, code: String) -> Result<(), String> {
    let (server_url, device_id, token) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
        )
    };
    crate::sync::api::revoke_pairing_code(&server_url, &code, &device_id, &token)
        .await
        .map_err(|e| e.to_string())
}

/// 确认配对:6 位码 → 保存设备令牌 → 重连推送 + 刷新设备列表。
#[tauri::command]
pub async fn sync_pair_confirm(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
    code: String,
) -> Result<(), String> {
    let (server_url, device_id, device_name) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
        )
    };
    let (token, _status, _user) = crate::sync::api::pair(
        &server_url, &code, &device_id, &device_name, "Windows",
    )
    .await
    .map_err(|e| e.to_string())?;
    if token.trim().is_empty() {
        return Err("服务器未返回设备令牌".into());
    }
    {
        let mut guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.auth_token = token;
        guard.settings.is_paired = true;
        crate::settings::save_settings(&guard.settings);
    }
    crate::sync::engine::notify_settings_changed();
    let _ = app.emit("settings-changed", ());
    Ok(())
}

/// 连接测试(设置页服务器地址旁)。
#[tauri::command]
pub async fn sync_test_connection(state: State<'_, SharedState>) -> Result<(bool, String), String> {
    let server_url = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.server_url.clone()
    };
    crate::sync::api::test_connection(&server_url)
        .await
        .map_err(|e| e.to_string())
}

/// 发送文本给指定设备(即时互传;空目标 = 广播)。
/// 本地同时落一条手动条目(origin=2),与旧版 TransferChat 行为一致。
#[tauri::command]
pub async fn sync_send_text(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
    text: String,
    target_device_ids: Option<Vec<String>>,
) -> Result<serde_json::Value, String> {
    let text = text.trim().to_string();
    if text.is_empty() {
        return Err("内容为空".into());
    }
    let (server_url, token, device_id, device_name, targets) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.auth_token.clone(),
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
            target_device_ids.unwrap_or_default(),
        )
    };
    let entry = crate::sync::api::send_to_devices(
        &server_url, &token, &text, &device_id, &device_name, &targets,
    )
    .await
    .map_err(|e| e.to_string())?;

    // 本地落库:origin=2(本端主动同步),与旧版一致
    let now = crate::history::now_ticks();
    let hash = crate::clipboard::hash::hash_text(&text, None);
    let item = crate::history::HistoryItem {
        id: 0,
        server_id: entry.as_ref().map(|e| e.id),
        entry_type: crate::history::TYPE_TEXT.into(),
        text: Some(text.clone()),
        html: None,
        image_path: None,
        image_ref: None,
        file_paths_json: None,
        content_hash: Some(hash.clone()),
        device_id: device_id.clone(),
        device_name: Some(device_name.clone()),
        source_app_name: Some("即时互传".into()),
        source_app_path: None,
        source_app_icon: None,
        created_at_ticks: now,
        origin: crate::history::ORIGIN_MANUAL,
        starred: false,
        remark: None,
        oversized: false,
    };
    {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        // 手动互传条目绝不去重(旧版铁律):否则相同内容会合并到旧条目上,
        // 对话流丢消息,且 touch_by_hash 会把旧条目的 origin/device 改写。
        guard.history.insert(&item).map_err(|e| e.to_string())?;
    }
    if let Some(updated) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.history.find_by_hash(&hash).ok().flatten()
    } {
        let _ = app.emit("clipboard-changed", crate::commands::to_dto(updated));
    }
    Ok(serde_json::json!(entry))
}

// ---- Phase 4:智能直达 / 互传聊天 ----

/// 检测文本的智能动作(toast 卡片渲染;本地路径/文件存在性必须由后端判定)。
#[tauri::command]
pub fn smart_detect(
    state: State<SharedState>,
    text: String,
    for_toast: Option<bool>,
) -> Result<Option<crate::smart::SmartAction>, String> {
    let toggles = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let s = &guard.settings;
        crate::smart::SmartToggles {
            color: s.smart_color_enabled,
            path: s.smart_path_enabled,
            deep_link: s.smart_deep_link_enabled,
            netdisk: s.smart_netdisk_enabled,
            url: s.smart_url_enabled,
        }
    };
    Ok(crate::smart::detect(&text, toggles, for_toast.unwrap_or(false)))
}

/// 执行智能动作(open_url/open_path/locate_path/open_terminal/open_file/copy_text)。
#[tauri::command]
/// 执行直达卡片动作(命令与「打开链接」热键共用)。
pub fn exec_spec(action: crate::smart::SmartActionSpec) -> Result<(), String> {
    match action.kind.as_str() {
        "open_url" => {
            std::process::Command::new("explorer")
                .arg(&action.arg)
                .spawn()
                .map_err(|e| e.to_string())?;
            Ok(())
        }
        "open_path" => {
            std::process::Command::new("explorer")
                .arg(&action.arg)
                .spawn()
                .map_err(|e| e.to_string())?;
            Ok(())
        }
        "locate_path" => {
            // 资源管理器选中该文件:/select, 需反斜杠路径(经 explorer 参数传递,无需手动加引号)
            let arg = action.arg.replace('/', "\\");
            std::process::Command::new("explorer")
                .arg(format!("/select,{arg}"))
                .spawn()
                .map_err(|e| e.to_string())?;
            Ok(())
        }
        "open_file" => {
            std::process::Command::new("explorer")
                .arg(&action.arg)
                .spawn()
                .map_err(|e| e.to_string())?;
            Ok(())
        }
        "open_terminal" => {
            // 优先 Windows Terminal,回退 PowerShell
            if std::process::Command::new("wt.exe")
                .arg("-d")
                .arg(&action.arg)
                .spawn()
                .is_err()
            {
                std::process::Command::new("powershell.exe")
                    .arg("-NoExit")
                    .arg("-Command")
                    .arg(format!("Set-Location -LiteralPath '{}'", action.arg))
                    .spawn()
                    .map_err(|e| e.to_string())?;
            }
            Ok(())
        }
        "copy_text" => crate::platform::writer::submit(
            crate::platform::writer::WritePayload::Text { text: action.arg, html: None },
            3000,
        ),
        _ => Err(format!("未知动作: {}", action.kind)),
    }
}

#[tauri::command]
pub fn smart_exec(action: serde_json::Value) -> Result<(), String> {
    let action: crate::smart::SmartActionSpec = serde_json::from_value(action)
        .map_err(|e| format!("动作参数无效: {e}"))?;
    exec_spec(action)
}

/// 互传聊天消息:本端发送(origin=2)与远端手动推送(origin=1)合并,新→旧。
/// 返回带 `isFromSelf` 判定的 DTO(前端据此决定气泡左右,不再自行推断 origin)。
#[tauri::command]
pub fn chat_query(state: State<SharedState>, limit: Option<i64>) -> Result<Vec<ChatMessageDto>, String> {
    let limit = limit.unwrap_or(200);
    let self_id = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.device_id.clone()
    };
    // 直接在库内筛选聊天消息,避免被普通剪贴板记录挤出查询窗口。
    let rows = with_history(&state, |h| h.query_chat(limit.max(50)))?;
    let mut items: Vec<ChatMessageDto> = rows
        .into_iter()
        .filter(|r| is_chat_message(r))
        .take(limit as usize)
        .map(|r| {
            // 发送方判定以设备 ID 为准:device_id 就是指这条消息的发送设备。
            // 本机发出的互传消息(含服务端回显)device_id=本机 → 右侧;
            // 手机发来的 device_id=手机 → 左侧。设备 ID 缺失时才回退到 origin。
            let is_from_self = if !r.device_id.trim().is_empty() && !self_id.trim().is_empty() {
                r.device_id.eq_ignore_ascii_case(&self_id)
            } else {
                r.origin != crate::history::ORIGIN_REMOTE
            };
            ChatMessageDto {
                id: r.id,
                text: r.text.clone().unwrap_or_default(),
                entry_type: r.entry_type.clone(),
                image_path: r.image_path.clone(),
                origin: r.origin,
                is_manual: r.is_manual(),
                device_name: Some(if r.device_name.as_deref().unwrap_or("").is_empty() {
                    if is_from_self { "本机".into() } else { "远端设备".into() }
                } else {
                    r.device_name.clone().unwrap_or_default()
                }),
                created_at: r.created_at_millis(),
                is_from_self,
            }
        })
        .collect();
    // 聊天场景按「旧 → 新」返回:先取最新 N 条(DESC),再翻转为正序,
    // 前端渲染时新消息自然落在底部。
    items.reverse();
    Ok(items)
}

/// 是否属于互传聊天流。
///
/// 与旧版 WinUI `TransferChatViewModel` 一致:只展示 `IsManual` 的条目。
/// 普通剪贴板同步(origin=1 且无互传标记)属于剪贴板历史,不进入聊天流。
fn is_chat_message(item: &HistoryItem) -> bool {
    // 只认主动互传(origin=2 或 isManual 来源标记)。普通剪贴板同步
    // (origin=1 且无 isManual)属于剪贴板历史,不能混进聊天会话。
    (item.is_manual() || item.origin == crate::history::ORIGIN_MANUAL)
        && ((item.entry_type == "Text"
            && item.text.as_deref().map(|t| !t.trim().is_empty()).unwrap_or(false))
            || (item.entry_type == "Image" && item.image_path.is_some()))
}

/// 互传消息视图模型(只含聊天气泡需要的字段 + 明确的发送方判定)。
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ChatMessageDto {
    pub id: i64,
    pub text: String,
    /// Text | Image
    pub entry_type: String,
    pub image_path: Option<String>,
    pub origin: i32,
    pub is_manual: bool,
    pub device_name: Option<String>,
    pub created_at: i64,
    /// 是否本机发出(前端据此决定气泡左右)
    pub is_from_self: bool,
}

/// 上传文件给指定设备(互传;文件不进剪贴板历史,铁律)。
#[tauri::command]
pub async fn sync_send_file(
    app: AppHandle,
    state: State<'_, SharedState>,
    file_path: String,
    target_device_ids: Option<Vec<String>>,
) -> Result<serde_json::Value, String> {
    let targets = target_device_ids.unwrap_or_default();
    let (server_url, token, device_id, device_name) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.auth_token.clone(),
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
        )
    };
    let file = crate::sync::api::upload_file(&server_url, &token, &file_path, &device_id, &device_name, "Windows", env!("CARGO_PKG_VERSION"), &targets)
        .await
        .map_err(|e| e.to_string())?.ok_or("服务器返回的文件记录无效")?;
    let value = serde_json::to_value(&file).map_err(|e| e.to_string())?;
    state.lock().unwrap_or_else(|p| p.into_inner()).history
        .cache_transfer_file(&file.file_id, &value).map_err(|e| e.to_string())?;
    let _ = app.emit("file-transferred", &value);
    Ok(value)
}

#[tauri::command]
pub fn transfer_files_query(state: State<SharedState>) -> Result<Vec<serde_json::Value>, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let mut files = guard.history.query_transfer_files().map_err(|e| e.to_string())?;
    for file in &mut files {
        let mine = file["deviceId"].as_str().filter(|id| !id.is_empty())
            .map(|id| id.eq_ignore_ascii_case(&guard.settings.device_id)).unwrap_or(false);
        file["isFromSelf"] = mine.into();
    }
    Ok(files)
}

/// 隐藏 toast 窗口(前端倒计时结束调用;后端 6s 兜底)。
#[tauri::command]
pub fn hide_toast(app: tauri::AppHandle) -> Result<(), String> {
    use tauri::Manager;
    if let Some(w) = app.get_webview_window("toast") {
        let _ = w.hide();
    }
    Ok(())
}

/// 下载互传文件到用户下载目录(文件名取推送元数据;重名自动追加序号)。
#[tauri::command]
pub async fn download_transfer_file(
    state: State<'_, SharedState>,
    file_id: String,
    file_name: String,
) -> Result<String, String> {
    let (server_url, device_id, token) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
        )
    };
    let bytes = crate::sync::api::download_file(&server_url, &device_id, &token, &file_id)
        .await
        .map_err(|e| e.to_string())?
        .ok_or_else(|| "文件在服务端已丢失(410)".to_string())?;

    // 保存到 Downloads(重名追加序号)
    let downloads = dirs_download();
    let safe_name = file_name.replace(['\\', '/', ':', '*', '?', '"', '<', '>', '|'], "_");
    let mut final_path = downloads.join(&safe_name);
    let mut n = 1u32;
    while final_path.exists() {
        let stem = std::path::Path::new(&safe_name)
            .file_stem()
            .map(|x| x.to_string_lossy().into_owned())
            .unwrap_or_else(|| "file".into());
        let ext = std::path::Path::new(&safe_name)
            .extension()
            .map(|x| x.to_string_lossy().into_owned())
            .map(|e| format!(".{e}"))
            .unwrap_or_default();
        final_path = downloads.join(format!("{stem} ({n}){ext}"));
        n += 1;
    }
    std::fs::write(&final_path, &bytes).map_err(|e| format!("写入失败: {e}"))?;
    Ok(final_path.to_string_lossy().into_owned())
}

fn dirs_download() -> std::path::PathBuf {
    std::env::var("USERPROFILE")
        .map(|h| std::path::PathBuf::from(h).join("Downloads"))
        .unwrap_or_else(|_| std::env::temp_dir())
}

/// 移除设备(设备列表行内操作;操作后刷新缓存并广播)。
#[tauri::command]
pub async fn sync_remove_device(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
    target_device_id: String,
) -> Result<(), String> {
    let (server_url, device_id, token) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
        )
    };
    crate::sync::api::remove_device(&server_url, &device_id, &token, &target_device_id)
        .await
        .map_err(|e| e.to_string())?;
    crate::sync::engine::refresh_devices(&app, &state.inner().clone()).await;
    Ok(())
}

/// 重命名设备(设备列表行内操作;操作后刷新缓存并广播)。
#[tauri::command]
pub async fn sync_rename_device(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
    target_device_id: String,
    new_name: String,
) -> Result<(), String> {
    let new_name = new_name.trim().to_string();
    if new_name.is_empty() {
        return Err("设备名称不能为空".into());
    }
    let (server_url, device_id, token) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.device_id.clone(),
            guard.settings.auth_token.clone(),
        )
    };
    crate::sync::api::rename_device(&server_url, &device_id, &token, &target_device_id, &new_name)
        .await
        .map_err(|e| e.to_string())?;
    crate::sync::engine::refresh_devices(&app, &state.inner().clone()).await;
    Ok(())
}

/// 手动从服务端拉取设备列表(覆盖本地缓存;设置页打开时调用)。
#[tauri::command]
pub async fn sync_devices_refresh(
    app: tauri::AppHandle,
    state: State<'_, SharedState>,
) -> Result<(), String> {
    crate::sync::engine::refresh_devices(&app, state.inner()).await;
    Ok(())
}

// ==================== 截图 ====================

/// 截图选区参数(显示器本地逻辑坐标,CSS 像素)。
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScreenshotRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

/// 裁剪 + 应用调整 + 合成标注层 + 圆角,返回 RGBA 图。
fn screenshot_crop(
    monitor_id: u32,
    rect: &ScreenshotRect,
    annotation_png: Option<&[u8]>,
    adjustments: Option<&crate::screenshot::Adjustments>,
    corner_radius: u32,
) -> Result<image::RgbaImage, String> {
    let sess = crate::screenshot::session_frames_public();
    let frame = sess
        .into_iter()
        .find(|f| f.monitor_id == monitor_id)
        .ok_or_else(|| format!("未找到显示器 {monitor_id} 的冻结帧"))?;
    let phys = crate::screenshot::physical_crop_rect(&frame, rect.x, rect.y, rect.width, rect.height);
    if phys.2 == 0 || phys.3 == 0 {
        return Err("选区为空".into());
    }
    let mut img = crate::screenshot::crop_rgba(&frame, phys).ok_or("裁剪失败")?;
    if let Some(adj) = adjustments {
        crate::screenshot::apply_adjustments(&mut img, adj);
    }
    crate::screenshot::composite_annotation(&mut img, annotation_png, frame.scale_factor)?;
    let phys_radius = (corner_radius as f64 * frame.scale_factor).round() as u32;
    crate::screenshot::apply_corner_radius(&mut img, phys_radius);
    Ok(img)
}

/// 屏幕点所在窗口(悬停实时查询,O(1),比预枚举快得多)。
#[tauri::command]
pub fn window_at_point(x: i32, y: i32) -> Option<crate::screenshot::WindowRectDto> {
    crate::screenshot::window_probe::window_at_point(x, y).map(|w| {
        crate::screenshot::WindowRectDto {
            x: w.x,
            y: w.y,
            width: w.width,
            height: w.height,
            title: w.title,
            app: w.app,
        }
    })
}

/// overlay 预加载冻结帧完成回报(握手,全部就绪后统一显示)。
#[tauri::command]
pub fn screenshot_overlay_ready(revision: u64, monitor_id: u32) {
    crate::screenshot::overlay_ready(revision, monitor_id);
}

/// 前端覆盖窗启动时拉取未消费的截图会话载荷(补救首次创建窗口错过的事件)。
#[tauri::command]
pub fn screenshot_pending_start(monitor_id: u32) -> Option<crate::screenshot::CaptureStartPayload> {
    crate::screenshot::pending_start_payload(monitor_id)
}

/// 取消截图会话(Esc / ✗)。
#[tauri::command]
pub fn screenshot_cancel(app: AppHandle) -> Result<(), String> {
    crate::screenshot::cancel(&app);
    Ok(())
}

/// 选区确认 → 复制到剪贴板(经写入线程;历史由 capture 管线自动入库)。
#[tauri::command]
pub fn screenshot_copy(
    app: AppHandle,
    monitor_id: u32,
    rect: ScreenshotRect,
    annotation_png: Option<String>,
    adjustments: Option<crate::screenshot::Adjustments>,
    corner_radius: Option<u32>,
) -> Result<(), String> {
    let ann = annotation_png.as_deref().and_then(|s| {
        if s.is_empty() { None } else { Some(decode_data_url(s)) }
    }).transpose()?;
    let img = screenshot_crop(monitor_id, &rect, ann.as_deref(), adjustments.as_ref(), corner_radius.unwrap_or(0))?;
    let png = crate::screenshot::encode_png(&img)?;
    crate::platform::writer::submit(crate::platform::writer::WritePayload::Image { png }, 5000)?;
    crate::screenshot::cancel(&app);
    Ok(())
}

/// 选区确认 → 另存为 PNG(系统保存对话框,记忆最近目录)。
#[tauri::command]
pub fn screenshot_save(
    app: AppHandle,
    state: State<'_, SharedState>,
    monitor_id: u32,
    rect: ScreenshotRect,
    annotation_png: Option<String>,
    adjustments: Option<crate::screenshot::Adjustments>,
    corner_radius: Option<u32>,
) -> Result<Option<String>, String> {
    let ann = annotation_png.as_deref().and_then(|s| {
        if s.is_empty() { None } else { Some(decode_data_url(s)) }
    }).transpose()?;
    let img = screenshot_crop(monitor_id, &rect, ann.as_deref(), adjustments.as_ref(), corner_radius.unwrap_or(0))?;
    let last_dir = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.screenshot_save_dir.clone()
    };
    let Some(path) = save_png_dialog("NexClip", &last_dir)? else {
        return Ok(None); // 用户取消
    };
    image::save_buffer_with_format(
        &path,
        img.as_raw(),
        img.width(),
        img.height(),
        image::ColorType::Rgba8,
        image::ImageFormat::Png,
    )
    .map_err(|e| format!("保存失败: {e}"))?;
    // 记忆所选目录
    if let Some(dir) = std::path::Path::new(&path).parent() {
        let dir = dir.to_string_lossy().into_owned();
        let mut guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.screenshot_save_dir = dir.clone();
        let _ = settings::save_settings(&guard.settings);
    }
    crate::screenshot::cancel(&app);
    Ok(Some(path))
}

/// 选区确认 → 长截图:覆盖窗点击穿透 + 滚动拼接,结果自动入剪贴板。
#[tauri::command]
pub fn scroll_capture_start(
    app: AppHandle,
    monitor_id: u32,
    rect: ScreenshotRect,
    _annotation_png: Option<String>,
) -> Result<(), String> {
    use tauri::Manager;
    let sess = crate::screenshot::session_frames_public();
    let frame = sess
        .into_iter()
        .find(|f| f.monitor_id == monitor_id)
        .ok_or("未找到冻结帧")?;
    let phys = crate::screenshot::physical_crop_rect(&frame, rect.x, rect.y, rect.width, rect.height);
    if phys.2 == 0 || phys.3 == 0 {
        return Err("选区为空".into());
    }
    // 选区显示器本地物理坐标 → 虚拟桌面物理坐标
    let vx = frame.x + phys.0 as i32;
    let vy = frame.y + phys.1 as i32;
    // 覆盖窗点击穿透(滚动要落到下层窗口)
    if let Some(w) = app.get_webview_window(&format!("overlay-{monitor_id}")) {
        let _ = w.set_ignore_cursor_events(true);
    }
    crate::screenshot::scroll::start(app, (frame.x, frame.y), (vx, vy, phys.2, phys.3));
    Ok(())
}

/// 活屏快捷截图(热键也能触发;命令供前端/未来托盘用)。
#[tauri::command]
pub fn screenshot_quick_screen(app: AppHandle) -> Result<(), String> {
    crate::screenshot::quick_capture_screen(&app);
    Ok(())
}

/// 活动窗口快捷截图。
#[tauri::command]
pub fn screenshot_quick_window(app: AppHandle) -> Result<(), String> {
    crate::screenshot::quick_capture_window(&app);
    Ok(())
}

/// 选区确认 → 贴图:裁剪存缓存,创建置顶无边框窗口。
/// 注意:async 命令,避免同步命令在 IPC 线程里阻塞(创建窗口需主线程,同步会卡住整个 webview)。
#[tauri::command]
pub async fn screenshot_pin(
    app: AppHandle,
    monitor_id: u32,
    rect: ScreenshotRect,
    annotation_png: Option<String>,
    corner_radius: Option<u32>,
) -> Result<(), String> {
    log::info!("贴图开始: monitor={monitor_id} rect={:?}", (rect.x, rect.y, rect.width, rect.height));
    let ann = annotation_png.as_deref().and_then(|s| {
        if s.is_empty() { None } else { Some(decode_data_url(s)) }
    }).transpose()?;

    // 截图选区 → 虚拟桌面物理坐标(贴图窗口按此定位,与截取区域精确重合)
    let screen_pos = {
        let sess = crate::screenshot::session_frames_public();
        sess.iter()
            .find(|f| f.monitor_id == monitor_id)
            .map(|f| {
                let phys = crate::screenshot::physical_crop_rect(f, rect.x, rect.y, rect.width, rect.height);
                (f.x + phys.0 as i32, f.y + phys.1 as i32)
            })
            .unwrap_or((0, 0))
    };

    // 裁剪 + 编码 + 写盘:CPU/IO 密集,放到阻塞线程池
    let pin_id = uuid::Uuid::new_v4().simple().to_string();
    let pin_id_task = pin_id.clone();
    let prepared = tauri::async_runtime::spawn_blocking(move || -> Result<(u32, u32), String> {
        let img = screenshot_crop(monitor_id, &rect, ann.as_deref(), None, corner_radius.unwrap_or(0))?;
        let path = crate::screenshot::pin_path(&pin_id_task);
        let png = crate::screenshot::encode_png(&img)?;
        std::fs::write(&path, png).map_err(|e| format!("贴图缓存写入失败: {e}"))?;
        log::info!("贴图缓存已写入: {} ({}x{})", path.display(), img.width(), img.height());
        Ok((img.width(), img.height()))
    })
    .await
    .map_err(|e| format!("贴图任务失败: {e}"))??;

    // 先结束截图会话(恢复覆盖窗/键盘钩子),再创建贴图窗口
    crate::screenshot::cancel(&app);

    spawn_pin_windows(&app, &pin_id, screen_pos, prepared.0, prepared.1)
}

/// 创建贴图窗口 + 工具条(截图贴图与「剪贴板图片贴图」共用)。
/// 窗口创建必须在主线程:通过 run_on_main_thread 投递,避免阻塞 IPC。
fn spawn_pin_windows(
    app: &AppHandle,
    pin_id: &str,
    screen_pos: (i32, i32),
    px_w: u32,
    px_h: u32,
) -> Result<(), String> {
    let (w, h) = (px_w as f64, px_h as f64);
    let label = format!("pin-{pin_id}");
    let tools_label = format!("pin-tools-{pin_id}");
    // flashot 设计:工具条是独立窗口(40px 宽竖排),挂在贴图右侧,永不被贴图窗口裁剪
    const TOOLS_W: f64 = 160.0; // 40px 可见竖条 + 120px 透明区给 tooltip
    let tools_x = screen_pos.0 as f64 + w;
    let tools_y = screen_pos.1 as f64;
    let pin_id_main = pin_id.to_string();

    // 窗口创建必须在主线程:通过 run_on_main_thread 投递,避免阻塞 IPC
    let (tx, rx) = std::sync::mpsc::channel::<Result<(), String>>();
    let app_main = app.clone();
    let label_main = label.clone();
    let tools_label_main = tools_label.clone();
    let prepared = (px_w, px_h);
    app.run_on_main_thread(move || {
        let result = (|| -> Result<(), String> {
            tauri::WebviewWindowBuilder::new(
                &app_main,
                &label_main,
                tauri::WebviewUrl::App("index.html".into()),
            )
            .title("NexClip 贴图")
            .decorations(false)
            .resizable(false)
            .skip_taskbar(true)
            .always_on_top(true)
            .shadow(false)
            .transparent(true)
            .position(screen_pos.0 as f64, screen_pos.1 as f64)
            .inner_size(w, h)
            .build()
            .map(|win| {
                let _ = win.set_position(tauri::PhysicalPosition::new(screen_pos.0, screen_pos.1));
                let _ = win.set_size(tauri::PhysicalSize::new(prepared.0, prepared.1));
            })
            .map_err(|e| format!("创建贴图窗口失败: {e}"))?;

            // 工具条窗口(独立,右侧竖排 40px)
            tauri::WebviewWindowBuilder::new(
                &app_main,
                &tools_label_main,
                tauri::WebviewUrl::App("index.html".into()),
            )
            .title("NexClip 贴图工具")
            .decorations(false)
            .resizable(false)
            .skip_taskbar(true)
            .always_on_top(true)
            .shadow(false)
            .transparent(true)
            .position(tools_x, tools_y)
            .inner_size(TOOLS_W, 240.0)
            .build()
            .map(|win| {
                let sf = win.scale_factor().unwrap_or(1.0);
                let _ = win.set_position(tauri::PhysicalPosition::new(
                    (tools_x * sf) as i32,
                    (tools_y * sf) as i32,
                ));
                let _ = win.set_size(tauri::PhysicalSize::new(
                    (TOOLS_W * sf) as u32,
                    (240.0 * sf) as u32,
                ));
                // flashot:工具条常驻显示(不用悬停切换,避免鼠标移过去就消失)
                let _ = win.show();
            })
            .map_err(|e| format!("创建贴图工具条失败: {e}"))?;
            Ok(())
        })();
        if let Err(e) = &result {
            log::error!("{e}");
        } else {
            log::info!("贴图窗口已创建: {label_main} @ {:?} (工具条 {tools_label_main})", screen_pos);
        }
        let _ = tx.send(result);
    })
    .map_err(|e| format!("投递主线程失败: {e}"))?;

    // 等待主线程完成(上限 5s,防线程被杀导致永久挂起)
    let result = rx
        .recv_timeout(std::time::Duration::from_secs(5))
        .map_err(|_| "贴图窗口创建超时".to_string())?;
    let _ = pin_id_main;
    result
}

/// 把剪贴板里的图片贴到屏幕(快捷键 F3 / 托盘入口)。
///
/// 与截图贴图走同一套贴图窗口;区别只是图像来源是剪贴板。
/// 贴图位置:光标所在显示器的工作区居中(不遮挡光标附近内容)。
#[tauri::command]
pub async fn pin_from_clipboard(app: AppHandle) -> Result<(), String> {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    };
    use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;

    // 读剪贴板图片(PNG 优先,回落 CF_DIB → PNG)。读取在阻塞线程里做。
    let png = tauri::async_runtime::spawn_blocking(move || -> Result<Vec<u8>, String> {
        let read = unsafe { crate::platform::monitor::read_clipboard() };
        match read {
            Ok((_, _, _, Some(png))) if !png.is_empty() => Ok(png),
            Ok(_) => Err("剪贴板里没有图片".into()),
            Err(()) => Err("剪贴板被占用,读取失败".into()),
        }
    })
    .await
    .map_err(|e| format!("读取剪贴板任务失败: {e}"))??;

    // 决定贴图位置:光标所在显示器工作区居中,尺寸按图片原尺寸(超大时缩到 80%)
    let (origin, px_w, px_h) = {
        let cursor = unsafe {
            let mut pt = POINT::default();
            let _ = GetCursorPos(&mut pt);
            pt
        };
        let (wa, dpi_scale) = unsafe {
            let hmon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            let mut info = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(hmon, &mut info).as_bool() {
                // dpi 用工作区物理宽 / 逻辑宽近似不可得,这里直接用系统 DPI 缩放
                (info.rcWork, 1.0f64)
            } else {
                (
                    windows::Win32::Foundation::RECT { left: 0, top: 0, right: 1920, bottom: 1080 },
                    1.0f64,
                )
            }
        };

        let img = image::load_from_memory(&png).map_err(|e| format!("图片解码失败: {e}"))?;
        let (iw, ih) = (img.width() as f64, img.height() as f64);

        let work_w = (wa.right - wa.left) as f64;
        let work_h = (wa.bottom - wa.top) as f64;
        // 最大占工作区 80%,等比缩放
        let max_w = work_w * 0.8;
        let max_h = work_h * 0.8;
        let scale = (max_w / iw).min(max_h / ih).min(1.0);
        let px_w = (iw * scale).round().max(1.0) as u32;
        let px_h = (ih * scale).round().max(1.0) as u32;

        let x = wa.left + ((work_w - px_w as f64) / 2.0).round() as i32;
        let y = wa.top + ((work_h - px_h as f64) / 2.0).round() as i32;
        let _ = dpi_scale;
        ((x, y), px_w, px_h)
    };

    // 写缓存文件(贴图窗口按文件加载)
    let pin_id = uuid::Uuid::new_v4().simple().to_string();
    let pin_id_task = pin_id.clone();
    let png_task = png.clone();
    tauri::async_runtime::spawn_blocking(move || -> Result<(), String> {
        let path = crate::screenshot::pin_path(&pin_id_task);
        std::fs::write(&path, &png_task).map_err(|e| format!("贴图缓存写入失败: {e}"))?;
        Ok(())
    })
    .await
    .map_err(|e| format!("写贴图缓存失败: {e}"))??;

    log::info!("剪贴板贴图: {}x{} @ {:?}", px_w, px_h, origin);
    spawn_pin_windows(&app, &pin_id, origin, px_w, px_h)
}

/// 贴图工具条窗口可见性(随贴图悬停/移动)。
#[tauri::command]
pub fn pin_tools_show(app: AppHandle, pin_id: String, visible: bool) -> Result<(), String> {
    use tauri::Manager;
    let Some(w) = app.get_webview_window(&format!("pin-tools-{pin_id}")) else {
        return Ok(());
    };
    if visible {
        let _ = w.show();
    } else {
        let _ = w.hide();
    }
    Ok(())
}

/// 贴图图像调整:烘焙到原图(灰度/亮度/对比度/饱和度),标注层保留。
#[tauri::command]
pub fn pin_apply_adjustments(
    app: AppHandle,
    pin_id: String,
    grayscale: bool,
    brightness: f32,
    contrast: f32,
    saturation: f32,
) -> Result<(), String> {
    let path = crate::screenshot::pin_path(&pin_id);
    let base_png = std::fs::read(&path).map_err(|e| format!("读取贴图失败: {e}"))?;
    let mut img = image::load_from_memory(&base_png)
        .map_err(|e| format!("贴图解码失败: {e}"))?
        .into_rgba8();
    let adj = crate::screenshot::Adjustments { grayscale, brightness, contrast, saturation };
    crate::screenshot::apply_adjustments(&mut img, &adj);
    let png = crate::screenshot::encode_png(&img)?;
    std::fs::write(&path, png).map_err(|e| format!("贴图写回失败: {e}"))?;
    log::info!("贴图图像调整已应用: {pin_id}");
    // 通知贴图窗口刷新(绕过图片缓存)
    use tauri::Manager;
    if let Some(win) = app.get_webview_window(&format!("pin-{pin_id}")) {
        let _ = win.emit("pin:refresh", ());
    }
    Ok(())
}

/// 贴图编辑态同步:通知贴图窗口进入/退出编辑,并更新工具条按钮高亮。
#[tauri::command]
pub fn pin_edit_state(app: AppHandle, pin_id: String, editing: bool) -> Result<(), String> {
    use tauri::Manager;
    if let Some(pin) = app.get_webview_window(&format!("pin-{pin_id}")) {
        let _ = pin.emit("pin:edit", serde_json::json!({ "pinId": pin_id, "editing": editing }));
    }
    if let Some(tools) = app.get_webview_window(&format!("pin-tools-{pin_id}")) {
        let _ = tools.emit("pin:edit-state", editing);
    }
    Ok(())
}

/// 贴图编辑:把标注层(透明 PNG dataURL)单独存盘(不烘焙进原图),
/// 便于下次继续编辑;显示/复制/保存时再实时合成。
#[tauri::command]
pub fn pin_annotation_save(
    pin_id: String,
    annotation_png: Option<String>,
    annotation_json: Option<String>,
) -> Result<(), String> {
    let ann_path = crate::screenshot::pin_annotation_path(&pin_id);
    let json_path = crate::screenshot::pin_annotation_json_path(&pin_id);
    let ann = annotation_png.as_deref().and_then(|s| {
        if s.is_empty() { None } else { Some(decode_data_url(s)) }
    }).transpose()?;
    match ann {
        Some(bytes) => {
            std::fs::write(&ann_path, bytes).map_err(|e| format!("标注层写入失败: {e}"))?;
            // 对象数据(下次编辑恢复为可编辑对象)
            if let Some(js) = annotation_json.filter(|s| !s.is_empty()) {
                std::fs::write(&json_path, js).map_err(|e| format!("标注对象写入失败: {e}"))?;
            } else {
                let _ = std::fs::remove_file(&json_path);
            }
            log::info!("贴图标注已保存: {}", ann_path.display());
        }
        None => {
            let _ = std::fs::remove_file(&ann_path);
            let _ = std::fs::remove_file(&json_path);
        }
    }
    Ok(())
}

/// 读取贴图标注层(编辑时作为初始内容加载,data URL 返回)。
#[tauri::command]
pub fn pin_annotation_get(pin_id: String) -> Result<(Option<String>, Option<String>), String> {
    use base64::Engine as _;
    let png_path = crate::screenshot::pin_annotation_path(&pin_id);
    let json_path = crate::screenshot::pin_annotation_json_path(&pin_id);
    let png = std::fs::read(&png_path).ok().map(|b| {
        format!("data:image/png;base64,{}", base64::engine::general_purpose::STANDARD.encode(&b))
    });
    let json = std::fs::read_to_string(&json_path).ok().filter(|s| !s.is_empty());
    Ok((png, json))
}

/// 关闭贴图窗口(连带工具条窗口与缓存文件)。
#[tauri::command]
pub fn pin_close(app: AppHandle, pin_id: String) -> Result<(), String> {
    use tauri::Manager;
    if let Some(w) = app.get_webview_window(&format!("pin-tools-{pin_id}")) {
        let _ = w.close();
    }
    let label = format!("pin-{pin_id}");
    if let Some(w) = app.get_webview_window(&label) {
        let _ = w.close();
    }
    let _ = std::fs::remove_file(crate::screenshot::pin_path(&pin_id));
    let _ = std::fs::remove_file(crate::screenshot::pin_annotation_path(&pin_id));
    let _ = std::fs::remove_file(crate::screenshot::pin_annotation_json_path(&pin_id));
    Ok(())
}

/// 把工具条窗口对齐到贴图右侧(贴图移动/缩放后调用)。
pub fn sync_pin_tools(app: &AppHandle, pin_id: &str) {
    use tauri::Manager;
    let (Some(pin), Some(tools)) = (
        app.get_webview_window(&format!("pin-{pin_id}")),
        app.get_webview_window(&format!("pin-tools-{pin_id}")),
    ) else {
        return;
    };
    let (Ok(pos), Ok(size)) = (pin.outer_position(), pin.inner_size()) else { return };
    let sf = tools.scale_factor().unwrap_or(1.0);
    // 工具条贴在贴图右侧,顶部对齐
    let _ = tools.set_position(tauri::PhysicalPosition::new(
        pos.x + size.width as i32,
        pos.y,
    ));
    let _ = tools.set_size(tauri::PhysicalSize::new(
        (160.0 * sf) as u32,
        (240.0 * sf) as u32,
    ));
}

/// 贴图缩放(滚轮):按比例调整贴图窗口大小(等比)。
#[tauri::command]
pub fn pin_set_scale(app: AppHandle, pin_id: String, scale: f64) -> Result<(), String> {
    use tauri::Manager;
    let label = format!("pin-{pin_id}");
    let Some(w) = app.get_webview_window(&label) else {
        return Err("贴图窗口不存在".into());
    };
    let Some(size) = w.inner_size().ok() else {
        return Err("读取贴图尺寸失败".into());
    };
    let nw = ((size.width as f64 * scale) as u32).clamp(80, 8000);
    let nh = ((size.height as f64 * scale) as u32).clamp(60, 8000);
    let _ = w.set_size(tauri::PhysicalSize::new(nw, nh));
    // 工具条跟随贴图新位置(窗口变宽后贴在右侧)
    sync_pin_tools(&app, &pin_id);
    Ok(())
}

/// 贴图绝对缩放(按原始图片百分比),供工具条缩放菜单使用。
#[tauri::command]
pub fn pin_set_scale_abs(app: AppHandle, pin_id: String, percent: u32) -> Result<(), String> {
    use tauri::Manager;
    let Some(w) = app.get_webview_window(&format!("pin-{pin_id}")) else {
        return Err("贴图窗口不存在".into());
    };
    // 原始图片尺寸
    let path = crate::screenshot::pin_path(&pin_id);
    let img = image::open(&path).map_err(|e| format!("读取贴图失败: {e}"))?;
    let ratio = (percent as f64 / 100.0).clamp(0.05, 8.0);
    let nw = ((img.width() as f64 * ratio).round() as u32).clamp(20, 20000);
    let nh = ((img.height() as f64 * ratio).round() as u32).clamp(20, 20000);
    let _ = w.set_size(tauri::PhysicalSize::new(nw, nh));
    sync_pin_tools(&app, &pin_id);
    Ok(())
}

/// 系统"另存为"对话框(IFileSaveDialog),`default_stem` 为默认文件名主干,`last_dir` 记忆目录。
fn save_png_dialog(default_stem: &str, last_dir: &str) -> Result<Option<String>, String> {
    use windows::core::w;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::System::Com::{CoCreateInstance, CoTaskMemFree, CLSCTX_INPROC_SERVER};
    use windows::Win32::UI::Shell::{FileSaveDialog, IFileSaveDialog, SHCreateItemFromParsingName, SIGDN_FILESYSPATH};

    let hwnd: HWND = unsafe { windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow() };
    unsafe {
        let dialog: IFileSaveDialog =
            CoCreateInstance(&FileSaveDialog, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| format!("创建对话框失败: {e}"))?;
        let _ = dialog.SetTitle(w!("保存截图"));
        let name: Vec<u16> = format!("{default_stem}.png\0").encode_utf16().collect();
        let _ = dialog.SetFileName(windows::core::PCWSTR(name.as_ptr()));
        // 记忆目录:上一次保存位置
        if !last_dir.trim().is_empty() {
            let wide: Vec<u16> = last_dir.trim().encode_utf16().chain(std::iter::once(0)).collect();
            if let Ok(item) = SHCreateItemFromParsingName::<_, _, windows::Win32::UI::Shell::IShellItem>(
                windows::core::PCWSTR(wide.as_ptr()),
                None,
            ) {
                let _ = dialog.SetFolder(&item);
            }
        }
        if dialog.Show(hwnd).is_err() {
            return Ok(None);
        }
        let item = dialog.GetResult().map_err(|e| format!("获取结果失败: {e}"))?;
        let path_pwstr = item
            .GetDisplayName(SIGDN_FILESYSPATH)
            .map_err(|e| format!("获取路径失败: {e}"))?;
        let mut len = 0usize;
        while *path_pwstr.0.add(len) != 0 {
            len += 1;
        }
        let path = String::from_utf16_lossy(std::slice::from_raw_parts(path_pwstr.0, len));
        CoTaskMemFree(Some(path_pwstr.0 as *const _));
        Ok(Some(path))
    }
}

/// 解析 data URL(`data:image/png;base64,...`)为 PNG 字节。
fn decode_data_url(data_url: &str) -> Result<Vec<u8>, String> {
    use base64::Engine as _;
    let payload = data_url
        .split_once(',')
        .map(|(_, p)| p)
        .unwrap_or(data_url);
    base64::engine::general_purpose::STANDARD
        .decode(payload)
        .map_err(|e| format!("标注层 base64 解码失败: {e}"))
}

/// 贴图图片路径(前端 ScreenPin 拉取用)。
#[tauri::command]
pub fn pin_image_path(pin_id: String) -> Result<Option<String>, String> {
    let path = crate::screenshot::pin_path(&pin_id);
    if path.exists() {
        Ok(Some(path.to_string_lossy().into_owned()))
    } else {
        Ok(None)
    }
}

/// 贴图窗口内容复制到剪贴板。
#[tauri::command]
pub fn pin_copy(pin_id: String) -> Result<(), String> {
    // 输出 = 原图 + 标注层合成
    let img = crate::screenshot::pin_composited(&pin_id)?;
    let png = crate::screenshot::encode_png(&img)?;
    crate::platform::writer::submit(crate::platform::writer::WritePayload::Image { png }, 5000)
}

/// 贴图窗口内容另存为(系统保存对话框)。
#[tauri::command]
pub fn pin_save(pin_id: String) -> Result<Option<String>, String> {
    // 输出 = 原图 + 标注层合成
    let img = crate::screenshot::pin_composited(&pin_id)?;
    let Some(dest) = save_png_dialog("NexClip", "")? else {
        return Ok(None);
    };
    img.save_with_format(&dest, image::ImageFormat::Png)
        .map_err(|e| format!("保存失败: {e}"))?;
    Ok(Some(dest))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::history::{HistoryItem, ORIGIN_LOCAL, ORIGIN_MANUAL, ORIGIN_REMOTE};

    fn item(origin: i32, device_id: &str, text: Option<&str>) -> HistoryItem {
        HistoryItem {
            id: 1,
            server_id: Some(1),
            entry_type: "Text".into(),
            text: text.map(str::to_string),
            html: None,
            image_path: None,
            image_ref: None,
            file_paths_json: None,
            content_hash: Some("h".into()),
            device_id: device_id.into(),
            device_name: Some("Phone".into()),
            source_app_name: None,
            source_app_path: None,
            source_app_icon: None,
            created_at_ticks: 0,
            origin,
            starred: false,
            remark: None,
            oversized: false,
        }
    }

    #[test]
    fn ordinary_remote_clipboard_is_not_chat() {
        // origin=1 且无 isManual 标记 = 普通剪贴板同步,不应混进互传会话。
        assert!(!is_chat_message(&item(ORIGIN_REMOTE, "peer", Some("333826"))));
    }

    #[test]
    fn local_echo_from_self_is_not_chat() {
        assert!(!is_chat_message(&item(ORIGIN_REMOTE, "me", Some("echo"))));
    }

    #[test]
    fn local_capture_is_not_chat() {
        assert!(!is_chat_message(&item(ORIGIN_LOCAL, "me", Some("copy"))));
    }

    #[test]
    fn manual_send_is_chat() {
        assert!(is_chat_message(&item(ORIGIN_MANUAL, "me", Some("hi"))));
    }

    #[test]
    fn manual_image_is_chat() {
        let mut image = item(ORIGIN_REMOTE, "peer", Some("clipboard.png"));
        image.entry_type = "Image".into();
        image.image_path = Some("C:\\cache\\peer.png".into());
        image.source_app_name = Some("即时互传".into());
        assert!(is_chat_message(&image));
    }

    #[test]
    fn empty_text_is_not_chat() {
        assert!(!is_chat_message(&item(ORIGIN_REMOTE, "peer", Some("   "))));
    }
}
