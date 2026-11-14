//! Tauri IPC 命令层:前端 ↔ Rust 数据层的唯一入口。
//!
//! 序列化为 camelCase(serde rename),前端 TS 类型与之对应。

use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

use crate::clipboard::files_meta::ClipboardFileInfo;
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
    pub files: Vec<ClipboardFileInfo>,
    pub device_id: String,
    pub device_name: Option<String>,
    pub source_app_name: Option<String>,
    pub source_app_path: Option<String>,
    pub source_app_icon: Option<String>,
    /// UTC 毫秒时间戳
    pub created_at: i64,
    pub origin: i32,
    pub starred: bool,
    pub remark: Option<String>,
    pub is_manual: bool,
}

pub(crate) fn to_dto(item: HistoryItem) -> HistoryItemDto {
    HistoryItemDto {
        id: item.id,
        server_id: item.server_id,
        entry_type: item.entry_type.clone(),
        text: item.text.clone(),
        html: item.html.clone(),
        image_path: item.image_path.clone(),
        image_ref: item.image_ref.clone(),
        files: item.files(),
        device_id: item.device_id.clone(),
        device_name: item.device_name.clone(),
        source_app_name: item.source_app_name.clone(),
        source_app_path: item.source_app_path.clone(),
        source_app_icon: item.source_app_icon.clone(),
        created_at: item.created_at_millis(),
        origin: item.origin,
        starred: item.starred,
        remark: item.remark.clone(),
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
    Ok(items.into_iter().map(to_dto).collect())
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
    pub theme_mode: String,
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
        theme_mode: s.theme_mode.clone(),
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
    pub theme_mode: Option<String>,
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
            theme_mode: None,
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
    if !path.contains("NexClip\\updates") {
        return Err("路径不在更新缓存目录,拒绝执行".into());
    }
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
fn payload_from_item(item: &HistoryItem) -> Result<crate::platform::writer::WritePayload, String> {
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
                html: item.html.clone(),
            })
        }
    }
}

/// 复制历史条目到剪贴板(右键菜单"复制")。
#[tauri::command]
pub fn copy_item(state: State<SharedState>, id: i64) -> Result<(), String> {
    let item = item_by_id(state.inner(), id)?;
    let payload = payload_from_item(&item)?;
    crate::platform::writer::submit(payload, 3000)
}

/// 按内容哈希读取最新本地条目,供"粘贴到目标"兜底使用。
fn item_by_id(state: &Arc<Mutex<AppState>>, id: i64) -> Result<HistoryItem, String> {
    let guard = state.lock().unwrap_or_else(|p| p.into_inner());
    let rows = guard
        .history
        .query(None, None, false, false, 500, 0)
        .map_err(|e| e.to_string())?;
    rows.into_iter()
        .find(|r| r.id == id)
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
    let result = std::thread::Builder::new()
        .name("paste-write".into())
        .spawn(move || -> Result<(), String> {
            let item = item_by_id(&state2, id)?;
            let payload = payload_from_item(&item)?;
            crate::platform::writer::submit(payload, 3000)
        })
        .map_err(|e| e.to_string())?
        .join()
        .map_err(|_| "写入线程崩溃")?;
    result?;

    // 隐藏弹窗让焦点自然回落(置顶模式下保持可见,不强制切前台)
    let keep_visible = crate::pinned();
    if !keep_visible {
        crate::hide_main(&app);
    }

    // 注入(Chromium 与普通应用统一 Ctrl+V;修饰键先抬起)
    crate::platform::paste::inject(&target, keep_visible)
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
    };
    {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let touched = guard
            .history
            .touch_by_hash(&hash, None, &device_id, Some(&device_name), now, None, None, None)
            .unwrap_or(false);
        if !touched {
            guard.history.insert(&item).map_err(|e| e.to_string())?;
        }
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
#[tauri::command]
pub fn chat_query(state: State<SharedState>, limit: Option<i64>) -> Result<Vec<HistoryItemDto>, String> {
    let limit = limit.unwrap_or(200);
    let rows = with_history(&state, |h| h.query(None, None, false, false, limit.max(50), 0))?;
    let items: Vec<HistoryItemDto> = rows
        .into_iter()
        .filter(|r| {
            (r.is_manual() || r.origin == crate::history::ORIGIN_MANUAL)
                && r.entry_type == "Text"
                && r.text.as_deref().map(|t| !t.trim().is_empty()).unwrap_or(false)
        })
        .take(limit as usize)
        .map(to_dto)
        .collect();
    Ok(items)
}

/// 上传文件给指定设备(互传;文件不进剪贴板历史,铁律)。
#[tauri::command]
pub async fn sync_send_file(
    state: State<'_, SharedState>,
    file_path: String,
    target_device_ids: Option<Vec<String>>,
) -> Result<serde_json::Value, String> {
    let _targets = target_device_ids.unwrap_or_default();
    let (server_url, token, device_id, device_name) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        (
            guard.settings.server_url.clone(),
            guard.settings.auth_token.clone(),
            guard.settings.device_id.clone(),
            guard.settings.device_name.clone(),
        )
    };
    crate::sync::api::upload_file(&server_url, &token, &file_path, &device_id, &device_name, "Windows", env!("CARGO_PKG_VERSION"))
        .await
        .map(|v| serde_json::json!(v))
        .map_err(|e| e.to_string())
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
