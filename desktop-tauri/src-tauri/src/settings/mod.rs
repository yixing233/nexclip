//! 设置存储:JSON + DPAPI。
//!
//! 移植自 C# `Services/SettingsStore.cs`。文件路径与 JSON 字段名(PascalCase)与旧版
//! 完全兼容:AuthToken 仍以 DPAPI(CurrentUser)加密落盘,旧版写入的加密令牌可直接解出。

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

use serde::{Deserialize, Serialize};

const PASTE_KEY_VERSION: i64 = 1;

// ---- 设置项(默认值与旧版一致) ----

#[derive(Debug, Clone)]
pub struct Settings {
    pub server_url: String,
    /// 解密后的设备令牌(仅在内存中明文存在;落盘时 DPAPI 加密)
    pub auth_token: String,
    pub is_paired: bool,
    pub device_id: String,
    pub device_name: String,
    /// 剪贴板窗口呼出
    pub hotkey: String,
    /// 设置窗口打开
    pub hotkey_settings: String,
    /// 打开复制的链接
    pub hotkey_open_url: String,
    /// 剪贴板窗口置顶开关
    pub hotkey_topmost: String,
    /// light | dark | system
    pub theme_mode: String,
    /// 窗口背景材质:Mica | MicaAlt | Acrylic
    pub backdrop_mode: String,
    /// 亚克力背景不透明度(0~1)
    pub backdrop_tint_opacity: f64,
    pub boot_start_enabled: bool,
    pub start_minimized: bool,
    pub close_to_tray: bool,
    /// 启动时自动检查更新
    pub auto_check_update: bool,
    /// github | direct
    pub update_source: String,
    pub monitor_enabled: bool,
    /// 自动监听时忽略远程控制/远程桌面应用写入的剪贴板内容
    pub app_filter_enabled: bool,
    /// 用户自定义的过滤进程名或可执行文件名
    pub custom_filtered_processes: Vec<String>,
    pub auto_paste: bool,
    pub notify_enabled: bool,
    /// 复制内容时显示右下角直达卡片(总开关)
    pub copy_direct_enabled: bool,
    /// 智能识别:颜色代码微预览与格式转换
    pub smart_color_enabled: bool,
    /// 智能识别:本地文件与路径直达
    pub smart_path_enabled: bool,
    /// 智能识别:GitHub 仓库深度识别
    pub smart_deep_link_enabled: bool,
    /// 智能识别:网盘链接与提取码识别
    pub smart_netdisk_enabled: bool,
    /// 智能识别:通用网页链接直达
    pub smart_url_enabled: bool,
    pub max_history: i64,
    /// 重新打开剪贴板时保持关闭前的位置(默认关闭,回到顶部)
    pub remember_scroll_position: bool,
    /// 富文本(HTML)支持:捕获并写回带格式内容
    pub rich_text_enabled: bool,
    /// center=屏幕中心 | cursor=跟随鼠标
    pub window_position_mode: String,
    /// 历史保留天数上限(0=不限)
    pub retention_days: i64,
    /// 普通应用粘贴键:CtrlV | ShiftInsert;Chromium/Electron 固定使用 CtrlV
    pub paste_key: String,
    /// 数据储存目录(空=默认 %LOCALAPPDATA%/NexClip)
    pub storage_dir: String,
    /// 剪贴板窗口尺寸记忆(0=未记忆)
    pub window_width: f64,
    pub window_height: f64,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            server_url: "http://127.0.0.1:5033".into(),
            auth_token: String::new(),
            is_paired: false,
            device_id: String::new(),
            device_name: String::new(),
            hotkey: "Alt+V".into(),
            hotkey_settings: "Alt+X".into(),
            hotkey_open_url: "Ctrl+Alt+O".into(),
            hotkey_topmost: "Ctrl+Alt+T".into(),
            theme_mode: "system".into(),
            backdrop_mode: "Mica".into(),
            backdrop_tint_opacity: 0.85,
            boot_start_enabled: true,
            start_minimized: true,
            close_to_tray: true,
            auto_check_update: true,
            update_source: "github".into(),
            monitor_enabled: true,
            app_filter_enabled: true,
            custom_filtered_processes: Vec::new(),
            auto_paste: true,
            notify_enabled: true,
            copy_direct_enabled: true,
            smart_color_enabled: true,
            smart_path_enabled: true,
            smart_deep_link_enabled: true,
            smart_netdisk_enabled: true,
            smart_url_enabled: true,
            max_history: 200,
            remember_scroll_position: false,
            rich_text_enabled: true,
            window_position_mode: "cursor".into(),
            retention_days: 0,
            paste_key: "CtrlV".into(),
            storage_dir: String::new(),
            window_width: 0.0,
            window_height: 0.0,
        }
    }
}

impl Settings {
    /// 补齐默认值(首次运行生成设备标识)。
    pub fn ensure_defaults(&mut self) {
        if self.device_id.trim().is_empty() {
            self.device_id = uuid::Uuid::new_v4().simple().to_string();
        }
        if self.device_name.trim().is_empty() {
            self.device_name = machine_name();
        }
        if self.server_url.trim().is_empty() {
            self.server_url = "http://127.0.0.1:5033".into();
        }
    }
}

fn machine_name() -> String {
    std::env::var("COMPUTERNAME")
        .or_else(|_| std::env::var("HOSTNAME"))
        .unwrap_or_else(|_| "未知设备".into())
}

/// %APPDATA% 目录(与 C# Environment.SpecialFolder.ApplicationData 一致)。
fn roaming_dir() -> PathBuf {
    PathBuf::from(
        std::env::var("APPDATA")
            .expect("APPDATA 环境变量缺失,非 Windows 环境不支持"),
    )
}

/// %LOCALAPPDATA% 目录。
fn local_appdata_dir() -> PathBuf {
    PathBuf::from(
        std::env::var("LOCALAPPDATA")
            .expect("LOCALAPPDATA 环境变量缺失,非 Windows 环境不支持"),
    )
}

/// Roaming 配置目录:%APPDATA%/NexClip。
pub fn settings_dir() -> PathBuf {
    roaming_dir().join("NexClip")
}

/// 默认数据目录:%LOCALAPPDATA%/NexClip。
pub fn default_storage_dir() -> PathBuf {
    local_appdata_dir().join("NexClip")
}

/// 迁移旧版目录标记,保证整个进程只尝试一次。
static MIGRATION_DONE: AtomicBool = AtomicBool::new(false);

/// 旧数据目录迁移:%APPDATA%/SyncClipboard → %APPDATA%/NexClip,
/// %LOCALAPPDATA%/SyncClipboard → %LOCALAPPDATA%/NexClip。
pub fn try_migrate_legacy_directories() {
    if MIGRATION_DONE.swap(true, Ordering::SeqCst) {
        return;
    }
    let legacy_roaming = roaming_dir().join("SyncClipboard");
    let dir = settings_dir();
    if legacy_roaming.is_dir() && !dir.exists() {
        if let Err(e) = std::fs::rename(&legacy_roaming, &dir) {
            log::warn!("迁移旧配置目录失败: {e}");
        }
    }
    let legacy_local = local_appdata_dir().join("SyncClipboard");
    let target = default_storage_dir();
    if legacy_local.is_dir() && !target.exists() {
        if let Err(e) = std::fs::rename(&legacy_local, &target) {
            log::warn!("迁移旧数据目录失败: {e}");
        }
    }
}

/// 磁盘 JSON 结构(设备凭证已加密)。字段名与旧版 C# SettingsDto 逐一对齐。
#[derive(Serialize, Deserialize, Default)]
#[serde(default)]
pub struct SettingsDto {
    #[serde(rename = "ServerUrl")]
    server_url: Option<String>,
    #[serde(rename = "AuthTokenEncrypted")]
    auth_token_encrypted: Option<String>,
    #[serde(rename = "IsPaired")]
    is_paired: Option<bool>,
    #[serde(rename = "DeviceId")]
    device_id: Option<String>,
    #[serde(rename = "DeviceName")]
    device_name: Option<String>,
    #[serde(rename = "Hotkey")]
    hotkey: Option<String>,
    #[serde(rename = "HotkeySettings")]
    hotkey_settings: Option<String>,
    #[serde(rename = "HotkeyOpenUrl")]
    hotkey_open_url: Option<String>,
    #[serde(rename = "HotkeyTopmost")]
    hotkey_topmost: Option<String>,
    #[serde(rename = "ThemeMode")]
    theme_mode: Option<String>,
    #[serde(rename = "BackdropMode")]
    backdrop_mode: Option<String>,
    #[serde(rename = "BackdropTintOpacity")]
    backdrop_tint_opacity: Option<f64>,
    #[serde(rename = "BootStartEnabled")]
    boot_start_enabled: Option<bool>,
    #[serde(rename = "StartMinimized")]
    start_minimized: Option<bool>,
    #[serde(rename = "CloseToTray")]
    close_to_tray: Option<bool>,
    #[serde(rename = "AutoCheckUpdate")]
    auto_check_update: Option<bool>,
    #[serde(rename = "UpdateSource")]
    update_source: Option<String>,
    #[serde(rename = "MonitorEnabled")]
    monitor_enabled: Option<bool>,
    #[serde(rename = "AppFilterEnabled")]
    app_filter_enabled: Option<bool>,
    #[serde(rename = "CustomFilteredProcesses")]
    custom_filtered_processes: Option<Vec<String>>,
    #[serde(rename = "AutoPaste")]
    auto_paste: Option<bool>,
    #[serde(rename = "NotifyEnabled")]
    notify_enabled: Option<bool>,
    #[serde(rename = "CopyDirectEnabled")]
    copy_direct_enabled: Option<bool>,
    #[serde(rename = "SmartColorEnabled")]
    smart_color_enabled: Option<bool>,
    #[serde(rename = "SmartPathEnabled")]
    smart_path_enabled: Option<bool>,
    #[serde(rename = "SmartDeepLinkEnabled")]
    smart_deep_link_enabled: Option<bool>,
    #[serde(rename = "SmartNetDiskEnabled")]
    smart_netdisk_enabled: Option<bool>,
    #[serde(rename = "SmartUrlEnabled")]
    smart_url_enabled: Option<bool>,
    #[serde(rename = "MaxHistory")]
    max_history: Option<i64>,
    #[serde(rename = "RememberScrollPosition")]
    remember_scroll_position: Option<bool>,
    #[serde(rename = "RichTextEnabled")]
    rich_text_enabled: Option<bool>,
    #[serde(rename = "WindowPositionMode")]
    window_position_mode: Option<String>,
    #[serde(rename = "RetentionDays")]
    retention_days: Option<i64>,
    #[serde(rename = "PasteKeyVersion")]
    paste_key_version: Option<i64>,
    #[serde(rename = "PasteKey")]
    paste_key: Option<String>,
    #[serde(rename = "StorageDir")]
    storage_dir: Option<String>,
    #[serde(rename = "WindowWidth")]
    window_width: Option<f64>,
    #[serde(rename = "WindowHeight")]
    window_height: Option<f64>,
}

fn dto_to_settings(dto: SettingsDto, mut s: Settings) -> Settings {
    s.server_url = dto.server_url.unwrap_or(s.server_url);
    let has_token = dto
        .auth_token_encrypted
        .as_deref()
        .is_some_and(|t| !t.is_empty());
    s.auth_token = match dto.auth_token_encrypted {
        Some(enc) if !enc.is_empty() => dpapi::decrypt(&enc).unwrap_or_default(),
        _ => String::new(),
    };
    // 迁移兼容:旧版用 AuthToken 存在与否表示已配对
    s.is_paired = dto.is_paired.unwrap_or(has_token);
    s.device_id = dto.device_id.unwrap_or(s.device_id);
    s.device_name = dto.device_name.unwrap_or(s.device_name);
    s.hotkey = dto.hotkey.unwrap_or(s.hotkey);
    s.hotkey_settings = dto.hotkey_settings.unwrap_or(s.hotkey_settings);
    s.hotkey_open_url = dto.hotkey_open_url.unwrap_or(s.hotkey_open_url);
    s.hotkey_topmost = dto.hotkey_topmost.unwrap_or(s.hotkey_topmost);
    s.theme_mode = dto.theme_mode.unwrap_or(s.theme_mode);
    s.backdrop_mode = dto.backdrop_mode.unwrap_or(s.backdrop_mode);
    s.backdrop_tint_opacity = dto.backdrop_tint_opacity.unwrap_or(s.backdrop_tint_opacity);
    s.boot_start_enabled = dto.boot_start_enabled.unwrap_or(s.boot_start_enabled);
    s.start_minimized = dto.start_minimized.unwrap_or(s.start_minimized);
    s.close_to_tray = dto.close_to_tray.unwrap_or(s.close_to_tray);
    s.auto_check_update = dto.auto_check_update.unwrap_or(s.auto_check_update);
    s.update_source = dto.update_source.unwrap_or(s.update_source);
    s.monitor_enabled = dto.monitor_enabled.unwrap_or(s.monitor_enabled);
    s.app_filter_enabled = dto.app_filter_enabled.unwrap_or(s.app_filter_enabled);
    s.custom_filtered_processes = {
        let raw: Vec<String> = dto
            .custom_filtered_processes
            .unwrap_or_default()
            .into_iter()
            .filter(|x| !x.trim().is_empty())
            .map(|x| x.trim().to_string())
            .collect();
        // 去重(忽略大小写,与 C# Distinct(OrdinalIgnoreCase) 一致)
        let mut out: Vec<String> = Vec::new();
        let mut seen: Vec<String> = Vec::new();
        for x in raw {
            let lower = x.to_lowercase();
            if !seen.contains(&lower) {
                seen.push(lower);
                out.push(x);
            }
        }
        out
    };
    s.auto_paste = dto.auto_paste.unwrap_or(s.auto_paste);
    s.notify_enabled = dto.notify_enabled.unwrap_or(s.notify_enabled);
    s.copy_direct_enabled = dto.copy_direct_enabled.unwrap_or(s.copy_direct_enabled);
    s.smart_color_enabled = dto.smart_color_enabled.unwrap_or(s.smart_color_enabled);
    s.smart_path_enabled = dto.smart_path_enabled.unwrap_or(s.smart_path_enabled);
    s.smart_deep_link_enabled = dto.smart_deep_link_enabled.unwrap_or(s.smart_deep_link_enabled);
    s.smart_netdisk_enabled = dto.smart_netdisk_enabled.unwrap_or(s.smart_netdisk_enabled);
    s.smart_url_enabled = dto.smart_url_enabled.unwrap_or(s.smart_url_enabled);
    s.max_history = dto.max_history.unwrap_or(s.max_history);
    s.remember_scroll_position = dto.remember_scroll_position.unwrap_or(s.remember_scroll_position);
    s.rich_text_enabled = dto.rich_text_enabled.unwrap_or(s.rich_text_enabled);
    s.window_position_mode = dto.window_position_mode.unwrap_or(s.window_position_mode);
    s.retention_days = dto.retention_days.unwrap_or(s.retention_days);
    // PasteKey 版本迁移:低于当前版本强制回 CtrlV
    if (dto.paste_key_version.unwrap_or(0)) < PASTE_KEY_VERSION {
        s.paste_key = "CtrlV".into();
    } else {
        s.paste_key = dto.paste_key.unwrap_or(s.paste_key);
    }
    s.storage_dir = dto.storage_dir.unwrap_or(s.storage_dir);
    s.window_width = dto.window_width.unwrap_or(s.window_width);
    s.window_height = dto.window_height.unwrap_or(s.window_height);
    s
}

/// 从磁盘 DTO 构造设置(兼容验证等外部入口使用)。
pub fn settings_from_dto(dto: SettingsDto) -> Settings {
    dto_to_settings(dto, Settings::default())
}

fn settings_to_dto(s: &Settings) -> SettingsDto {
    SettingsDto {
        server_url: Some(s.server_url.clone()),
        auth_token_encrypted: if s.auth_token.is_empty() {
            None
        } else {
            dpapi::encrypt(&s.auth_token).ok()
        },
        is_paired: Some(s.is_paired),
        device_id: Some(s.device_id.clone()),
        device_name: Some(s.device_name.clone()),
        hotkey: Some(s.hotkey.clone()),
        hotkey_settings: Some(s.hotkey_settings.clone()),
        hotkey_open_url: Some(s.hotkey_open_url.clone()),
        hotkey_topmost: Some(s.hotkey_topmost.clone()),
        theme_mode: Some(s.theme_mode.clone()),
        backdrop_mode: Some(s.backdrop_mode.clone()),
        backdrop_tint_opacity: Some(s.backdrop_tint_opacity),
        boot_start_enabled: Some(s.boot_start_enabled),
        start_minimized: Some(s.start_minimized),
        close_to_tray: Some(s.close_to_tray),
        auto_check_update: Some(s.auto_check_update),
        update_source: Some(s.update_source.clone()),
        monitor_enabled: Some(s.monitor_enabled),
        app_filter_enabled: Some(s.app_filter_enabled),
        custom_filtered_processes: Some(s.custom_filtered_processes.clone()),
        auto_paste: Some(s.auto_paste),
        notify_enabled: Some(s.notify_enabled),
        copy_direct_enabled: Some(s.copy_direct_enabled),
        smart_color_enabled: Some(s.smart_color_enabled),
        smart_path_enabled: Some(s.smart_path_enabled),
        smart_deep_link_enabled: Some(s.smart_deep_link_enabled),
        smart_netdisk_enabled: Some(s.smart_netdisk_enabled),
        smart_url_enabled: Some(s.smart_url_enabled),
        max_history: Some(s.max_history),
        remember_scroll_position: Some(s.remember_scroll_position),
        rich_text_enabled: Some(s.rich_text_enabled),
        window_position_mode: Some(s.window_position_mode.clone()),
        retention_days: Some(s.retention_days),
        paste_key_version: Some(PASTE_KEY_VERSION),
        paste_key: Some(s.paste_key.clone()),
        storage_dir: Some(s.storage_dir.clone()),
        window_width: Some(s.window_width),
        window_height: Some(s.window_height),
    }
}

pub fn settings_file_path() -> PathBuf {
    settings_dir().join("settings.json")
}

/// 实际使用的数据目录:设置项优先,为空则用默认目录。
pub fn resolve_storage_dir(storage_dir_setting: &str) -> PathBuf {
    if storage_dir_setting.trim().is_empty() {
        default_storage_dir()
    } else {
        PathBuf::from(storage_dir_setting.trim())
    }
}

/// 加载设置(文件缺失或损坏时回退默认值)。
pub fn load_settings() -> Settings {
    try_migrate_legacy_directories();
    let path = settings_file_path();
    let mut s = Settings::default();
    if path.is_file() {
        match std::fs::read_to_string(&path) {
            Ok(text) => match serde_json::from_str::<SettingsDto>(&text) {
                Ok(dto) => {
                    s = dto_to_settings(dto, s);
                }
                Err(e) => log::error!("设置 JSON 解析失败,使用默认值: {e}"),
            },
            Err(e) => log::error!("设置读取失败,使用默认值: {e}"),
        }
    }
    s.ensure_defaults();
    s
}

/// 保存设置(缩进 JSON,与旧版 WriteIndented 一致)。
pub fn save_settings(s: &Settings) {
    let mut s = s.clone();
    s.ensure_defaults();
    let dto = settings_to_dto(&s);
    let path = settings_file_path();
    if let Err(e) = std::fs::create_dir_all(path.parent().unwrap()) {
        log::error!("设置目录创建失败: {e}");
        return;
    }
    match serde_json::to_string_pretty(&dto) {
        Ok(text) => {
            // 直接覆写并 flush,确保内容落盘
            if let Ok(mut f) = std::fs::File::create(&path) {
                if let Err(e) = f.write_all(text.as_bytes()) {
                    log::error!("设置保存失败: {e}");
                }
            }
        }
        Err(e) => log::error!("设置序列化失败: {e}"),
    }
}

// ---- 设备列表本地缓存(%LOCALAPPDATA%/NexClip/devices_cache.json,SWR 策略首屏直出) ----

pub fn devices_cache_file_path(storage_dir: &Path) -> PathBuf {
    storage_dir.join("devices_cache.json")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dto_field_names_match_legacy_csharp() {
        // 旧版 C# 序列化产物必须能无损读入
        let legacy = r#"{
          "ServerUrl": "https://example.com",
          "AuthTokenEncrypted": null,
          "IsPaired": true,
          "DeviceId": "abc123",
          "DeviceName": "MyPC",
          "Hotkey": "Alt+Q",
          "MaxHistory": 500,
          "CustomFilteredProcesses": ["AnyDesk", "anydesk", "ToDesk "],
          "PasteKeyVersion": 1,
          "PasteKey": "ShiftInsert",
          "StorageDir": ""
        }"#;
        let dto: SettingsDto = serde_json::from_str(legacy).unwrap();
        let s = dto_to_settings(dto, Settings::default());
        assert_eq!(s.server_url, "https://example.com");
        assert!(s.is_paired);
        assert_eq!(s.device_id, "abc123");
        assert_eq!(s.hotkey, "Alt+Q");
        assert_eq!(s.max_history, 500);
        // 自定义过滤:去空白、去重(忽略大小写)
        assert_eq!(s.custom_filtered_processes, vec!["AnyDesk", "ToDesk"]);
        assert_eq!(s.paste_key, "ShiftInsert");
    }

    #[test]
    fn paste_key_version_migration_forces_ctrlv() {
        let legacy = r#"{"PasteKeyVersion": 0, "PasteKey": "ShiftInsert"}"#;
        let dto: SettingsDto = serde_json::from_str(legacy).unwrap();
        let s = dto_to_settings(dto, Settings::default());
        assert_eq!(s.paste_key, "CtrlV");
    }

    #[test]
    fn missing_fields_fall_back_to_defaults() {
        let dto: SettingsDto = serde_json::from_str("{}").unwrap();
        let s = dto_to_settings(dto, Settings::default());
        assert_eq!(s.server_url, "http://127.0.0.1:5033");
        assert_eq!(s.hotkey, "Alt+V");
        assert_eq!(s.max_history, 200);
        assert!(!s.is_paired, "无令牌且无 IsPaired 标记 → 未配对");
    }

    #[test]
    fn roundtrip_dto_without_dpapi() {
        // DPAPI 不可用时(测试环境无窗口站等)token 保留为空,其余字段不丢
        let mut s = Settings::default();
        s.auth_token = String::new(); // 空令牌不加密
        s.custom_filtered_processes = vec!["mstsc".into()];
        s.max_history = 42;
        let dto = settings_to_dto(&s);
        let json = serde_json::to_string(&dto).unwrap();
        let back: SettingsDto = serde_json::from_str(&json).unwrap();
        let s2 = dto_to_settings(back, Settings::default());
        assert_eq!(s2.max_history, 42);
        assert_eq!(s2.custom_filtered_processes, vec!["mstsc"]);
    }

    #[test]
    fn storage_dir_resolution() {
        assert_eq!(resolve_storage_dir(""), default_storage_dir());
        assert_eq!(resolve_storage_dir("  "), default_storage_dir());
        assert_eq!(
            resolve_storage_dir("D:\\NexData"),
            PathBuf::from("D:\\NexData")
        );
    }
}

/// DPAPI(CurrentUser)加密,输出 base64(与 C# ProtectedData.Protect 兼容)。
pub mod dpapi {
    pub fn encrypt(plain: &str) -> Result<String, String> {
        super::dpapi_impl::protect(plain.as_bytes())
            .map(|b| super::dpapi_impl::b64_encode(&b))
    }

    pub fn decrypt(encrypted_base64: &str) -> Result<String, String> {
        let bytes = super::dpapi_impl::b64_decode(encrypted_base64)?;
        let plain = super::dpapi_impl::unprotect(&bytes)?;
        String::from_utf8(plain).map_err(|e| e.to_string())
    }
}

/// 实际 DPAPI 与 base64 实现。
mod dpapi_impl {
    use windows::Win32::Foundation::{LocalFree, HLOCAL};
    use windows::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };

    pub fn b64_encode(data: &[u8]) -> String {
        const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
        for chunk in data.chunks(3) {
            let b0 = chunk[0] as u32;
            let b1 = *chunk.get(1).unwrap_or(&0) as u32;
            let b2 = *chunk.get(2).unwrap_or(&0) as u32;
            let n = (b0 << 16) | (b1 << 8) | b2;
            out.push(TABLE[(n >> 18) as usize & 63] as char);
            out.push(TABLE[(n >> 12) as usize & 63] as char);
            out.push(if chunk.len() > 1 { TABLE[(n >> 6) as usize & 63] as char } else { '=' });
            out.push(if chunk.len() > 2 { TABLE[n as usize & 63] as char } else { '=' });
        }
        out
    }

    pub fn b64_decode(s: &str) -> Result<Vec<u8>, String> {
        fn val(c: u8) -> Result<u32, String> {
            match c {
                b'A'..=b'Z' => Ok((c - b'A') as u32),
                b'a'..=b'z' => Ok((c - b'a' + 26) as u32),
                b'0'..=b'9' => Ok((c - b'0' + 52) as u32),
                b'+' | b'-' => Ok(62),
                b'/' | b'_' => Ok(63),
                b'=' => Ok(0),
                _ => Err(format!("非法 base64 字符: {}", c as char)),
            }
        }
        let filtered: Vec<u8> = s
            .bytes()
            .filter(|b| !b.is_ascii_whitespace())
            .collect();
        let mut out = Vec::with_capacity(filtered.len() / 4 * 3);
        for chunk in filtered.chunks(4) {
            if chunk.len() < 2 {
                return Err("base64 长度不足".into());
            }
            let n = (val(chunk[0])? << 18) | (val(chunk[1])? << 12)
                | (if chunk.len() > 2 { val(chunk[2])? } else { 0 } << 6)
                | (if chunk.len() > 3 { val(chunk[3])? } else { 0 });
            out.push((n >> 16) as u8);
            if chunk.len() > 2 {
                out.push((n >> 8) as u8);
            }
            if chunk.len() > 3 {
                out.push(n as u8);
            }
        }
        Ok(out)
    }

    fn blob(data: &[u8]) -> CRYPT_INTEGER_BLOB {
        CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_ptr() as *mut u8,
        }
    }

    fn take_and_free(out: CRYPT_INTEGER_BLOB) -> Vec<u8> {
        let slice = unsafe { std::slice::from_raw_parts(out.pbData, out.cbData as usize) };
        let result = slice.to_vec();
        unsafe {
            let _ = LocalFree(HLOCAL(out.pbData as *mut _));
        }
        result
    }

    pub fn protect(data: &[u8]) -> Result<Vec<u8>, String> {
        unsafe {
            let input = blob(data);
            let mut output = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
            CryptProtectData(
                &input,
                None,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
            .map_err(|e| format!("CryptProtectData 失败: {e}"))?;
            Ok(take_and_free(output))
        }
    }

    pub fn unprotect(data: &[u8]) -> Result<Vec<u8>, String> {
        unsafe {
            let input = blob(data);
            let mut output = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
            CryptUnprotectData(
                &input,
                None,
                None,
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
            .map_err(|e| format!("CryptUnprotectData 失败: {e}"))?;
            Ok(take_and_free(output))
        }
    }
}

#[cfg(test)]
mod dpapi_tests {
    use super::dpapi_impl::{b64_decode, b64_encode};

    #[test]
    fn base64_rfc4648_vectors() {
        assert_eq!(b64_encode(b""), "");
        assert_eq!(b64_encode(b"f"), "Zg==");
        assert_eq!(b64_encode(b"fo"), "Zm8=");
        assert_eq!(b64_encode(b"foo"), "Zm9v");
        assert_eq!(b64_encode(b"foob"), "Zm9vYg==");
        assert_eq!(b64_encode(b"fooba"), "Zm9vYmE=");
        assert_eq!(b64_encode(b"foobar"), "Zm9vYmFy");
        assert_eq!(b64_decode("Zm9vYmFy").unwrap(), b"foobar");
        // 解码容忍空白
        assert_eq!(b64_decode("Zm9v\nYm Fy").unwrap(), b"foobar");
    }
}
