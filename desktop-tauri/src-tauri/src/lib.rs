pub mod capture;
pub mod clipboard;
pub mod commands;
pub mod history;
pub mod platform;
pub mod settings;
pub mod smart;
pub mod sync;
pub mod update;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use tauri::{AppHandle, Emitter, Manager, PhysicalPosition};

/// 应用级共享状态:历史库 + 设置。命令层整锁访问(数据量小,锁竞争可忽略)。
pub struct AppState {
    pub settings: settings::Settings,
    pub history: history::HistoryStore,
    /// 同步引擎实时状态(not_configured/connecting/connected/reconnecting/disconnected/offline)
    pub sync_status: Mutex<String>,
}

/// 置顶状态(前端图钉 / Ctrl+Alt+T 共用)。
static PINNED: AtomicBool = AtomicBool::new(false);

/// 粘贴目标:热键呼出弹窗瞬间记录的前台窗口(粘贴回注的注入目标)。
pub static PASTE_TARGET: Mutex<Option<platform::paste::PasteTarget>> = Mutex::new(None);

/// 当前显示中的直达卡片主操作(卡片隐藏时清空)。
/// 供「打开复制的链接」热键优先执行卡片动作(与旧版 OpenCopiedUrl 语义一致)。
pub static LAST_SMART_ACTION: Mutex<Option<smart::SmartActionSpec>> = Mutex::new(None);

fn pinned() -> bool {
    PINNED.load(Ordering::SeqCst)
}

pub fn set_pinned(pinned: bool, app: &AppHandle) {
    PINNED.store(pinned, Ordering::SeqCst);
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.set_always_on_top(pinned);
    }
    let _ = app.emit("pinned-changed", pinned);
}

/// 把主窗口移动到光标所在显示器的工作区内(多显示器 + 边缘钳制)。
fn position_main_at_cursor(app: &AppHandle) {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    };
    unsafe {
        let mut pt = POINT { x: 0, y: 0 };
        let _ = windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut pt);
        let hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(hmon, &mut info).as_bool() {
            if let Some(w) = app.get_webview_window("main") {
                let size = w.outer_size().unwrap_or_default();
                let wa = &info.rcWork;
                // 光标处优先,边缘钳制在工作区内
                let mut x = pt.x - (size.width as i32) / 2;
                let mut y = pt.y - (size.height as i32) / 2;
                x = x.clamp(wa.left, (wa.right - size.width as i32).max(wa.left));
                y = y.clamp(wa.top, (wa.bottom - size.height as i32).max(wa.top));
                let _ = w.set_position(PhysicalPosition::new(x, y));
            }
        }
    }
}

fn show_main_at_cursor(app: &AppHandle) {
    position_main_at_cursor(app);
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.show();
        let _ = w.set_focus();
    }
}

pub fn hide_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.hide();
    }
}

/// 智能直达 toast:检测 → 推送内容到 toast 窗口 → 右下角显示(不抢焦点),6s 后自动隐藏。
pub fn show_smart_toast(app: &AppHandle, state: &Arc<Mutex<AppState>>, text: &str) {
    let (copy_direct, toggles) = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        let s = &guard.settings;
        let t = crate::smart::SmartToggles {
            color: s.smart_color_enabled,
            path: s.smart_path_enabled,
            deep_link: s.smart_deep_link_enabled,
            netdisk: s.smart_netdisk_enabled,
            url: s.smart_url_enabled,
        };
        (s.copy_direct_enabled, t)
    };
    if !copy_direct {
        return;
    }
    let Some(action) = crate::smart::detect(text, toggles, true) else {
        return;
    };
    let Some(w) = app.get_webview_window("toast") else {
        return;
    };
    // 主显示器工作区右下角(与旧版 LinkToastWindow 一致)
    unsafe {
        use windows::Win32::Foundation::POINT;
        use windows::Win32::Graphics::Gdi::{
            GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTOPRIMARY,
        };
        let hmon = MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY);
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if GetMonitorInfoW(hmon, &mut info).as_bool() {
            let size = w.outer_size().unwrap_or_default();
            let wa = &info.rcWork;
            let x = wa.right - size.width as i32 - 12;
            let y = wa.bottom - size.height as i32 - 12;
            let _ = w.set_position(PhysicalPosition::new(x, y));
        }
    }
    let _ = app.emit_to("toast", "smart-action", &action);
    *LAST_SMART_ACTION.lock().unwrap_or_else(|p| p.into_inner()) = Some(action.primary.clone());
    let _ = w.show();
    // 6s 自动隐藏(倒计时由前端渲染,后端兜底);隐藏时清空卡片动作记录
    let hide_app = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_secs(6));
        if let Some(w) = hide_app.get_webview_window("toast") {
            let _ = w.hide();
        }
        *LAST_SMART_ACTION.lock().unwrap_or_else(|p| p.into_inner()) = None;
    });
}

pub fn toggle_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let visible = w.is_visible().unwrap_or(false);
        if visible {
            hide_main(app);
        } else {
            // 记录粘贴目标:此刻的前台就是用户正在使用的应用
            let target = platform::paste::capture_target();
            *PASTE_TARGET.lock().unwrap_or_else(|p| p.into_inner()) = target;
            show_main_at_cursor(app);
        }
    }
}

/// 系统通知(受设置 notifyEnabled 控制):同步事件与错误。
/// Windows 上走 tauri-plugin-notification(Toast 通知,不抢焦点)。
pub fn notify(app: &AppHandle, state: &Arc<Mutex<AppState>>, title: &str, body: &str) {
    let enabled = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        guard.settings.notify_enabled
    };
    if !enabled {
        return;
    }
    use tauri_plugin_notification::NotificationExt;
    let _ = app
        .notification()
        .builder()
        .title(title)
        .body(body)
        .show();
}

/// 显示设置窗口:按当前主题设置原生标题栏颜色(深色模式不再白条)。
/// 托盘菜单 / Alt+X 热键 / 弹窗齿轮三条路径统一走这里。
pub(crate) fn show_settings_window(app: &AppHandle) {
    use tauri::Theme;
    let dark = {
        let state = app.state::<Arc<Mutex<AppState>>>();
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        match guard.settings.theme_mode.as_str() {
            "dark" => true,
            "light" => false,
            _ => sys_dark_mode(),
        }
    };
    if let Some(w) = app.get_webview_window("settings") {
        let _ = w.set_theme(if dark { Some(Theme::Dark) } else { Some(Theme::Light) });
        let _ = w.show();
        let _ = w.unminimize();
        let _ = w.set_focus();
    }
}

/// 读系统"应用深色模式"(HKCU\...\Themes\Personalize\AppsUseLightTheme)。
fn sys_dark_mode() -> bool {
    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::{
        RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_CURRENT_USER, KEY_QUERY_VALUE,
        REG_NONE,
    };
    use windows::core::w;
    unsafe {
        let mut hkey = HKEY::default();
        if RegOpenKeyExW(
            HKEY_CURRENT_USER,
            w!("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"),
            0,
            KEY_QUERY_VALUE,
            &mut hkey,
        ) != ERROR_SUCCESS
        {
            return false;
        }
        let mut val: u32 = 1;
        let mut cb = std::mem::size_of::<u32>() as u32;
        let mut reg_type = REG_NONE;
        let r = RegQueryValueExW(
            hkey,
            w!("AppsUseLightTheme"),
            None,
            Some(&mut reg_type),
            Some(&mut val as *mut u32 as *mut u8),
            Some(&mut cb),
        );
        let _ = RegCloseKey(hkey);
        r == ERROR_SUCCESS && val == 0
    }
}

/// 从设置构建热键集合(解析失败回退旧版默认键)。
pub(crate) fn build_hotkey_specs(s: &settings::Settings) -> Vec<platform::hotkey::HotkeySpec> {    use platform::hotkey::{parse_hotkey, HotkeyAction, HotkeySpec};
    let defaults = [
        (HotkeyAction::Main, s.hotkey.clone(), "Alt+V"),
        (HotkeyAction::Settings, s.hotkey_settings.clone(), "Alt+X"),
        (HotkeyAction::OpenUrl, s.hotkey_open_url.clone(), "Ctrl+Alt+O"),
        (HotkeyAction::Topmost, s.hotkey_topmost.clone(), "Ctrl+Alt+T"),
    ];
    defaults
        .into_iter()
        .map(|(action, value, fallback)| {
            let parsed = parse_hotkey(&value).or_else(|| {
                log::warn!("热键 {value:?} 解析失败,回退默认 {fallback}");
                parse_hotkey(fallback)
            });
            match parsed {
                Some((modifiers, vk)) => HotkeySpec { action, modifiers, vk },
                None => HotkeySpec { action, modifiers: 0, vk: 0 },
            }
        })
        .collect()
}

fn show_target_before_main(app: &AppHandle) {
    let target = platform::paste::capture_target();
    *PASTE_TARGET.lock().unwrap_or_else(|p| p.into_inner()) = target;
    show_main_at_cursor(app);
}

/// 启动全部 Phase 2 平台组件(热键/监听/写入线程/托盘)。
fn start_platform(app: &AppHandle, state: Arc<Mutex<AppState>>) {
    // 剪贴板写入线程
    platform::writer::start();

    // 同步引擎(REST + WS 推送;未配对时静默待机)
    sync::engine::start(app.clone(), state.clone());

    // 剪贴板监听(防环/去抖/过滤)
    let cap_state = state.clone();
    let cap_app = app.clone();
    if let Err(e) = platform::monitor::start(move |seq| {
        capture::capture(&cap_app, &cap_state, seq);
    }) {
        log::error!("剪贴板监听启动失败: {e}");
    }

    // 全局热键
    let hot_app = app.clone();
    let specs = {
        let guard = state.lock().unwrap_or_else(|p| p.into_inner());
        build_hotkey_specs(&guard.settings)
    };
    if let Err(e) = platform::hotkey::start(specs, move |action| {
        match action {
            platform::hotkey::HotkeyAction::Main => toggle_main(&hot_app),
            platform::hotkey::HotkeyAction::Settings => {
                show_settings_window(&hot_app);
            }
            platform::hotkey::HotkeyAction::OpenUrl => {
                // 直达卡片优先:卡片显示中 → 执行卡片主操作(与旧版 OpenCopiedUrl 一致);
                // 否则回退:打开剪贴板里最近一条链接
                let card_visible = hot_app
                    .get_webview_window("toast")
                    .and_then(|w| w.is_visible().ok())
                    .unwrap_or(false);
                let card_action = if card_visible {
                    LAST_SMART_ACTION
                        .lock()
                        .unwrap_or_else(|p| p.into_inner())
                        .clone()
                } else {
                    None
                };
                if let Some(spec) = card_action {
                    if let Err(e) = crate::commands::exec_spec(spec) {
                        log::warn!("直达卡片热键执行失败: {e}");
                    }
                    if let Some(w) = hot_app.get_webview_window("toast") {
                        let _ = w.hide();
                    }
                    *LAST_SMART_ACTION.lock().unwrap_or_else(|p| p.into_inner()) = None;
                    return;
                }
                // 打开最近一条链接
                let url = {
                    let guard = hot_app.state::<Arc<Mutex<AppState>>>();
                    let guard = guard.lock().unwrap_or_else(|p| p.into_inner());
                    guard
                        .history
                        .query(None, None, false, true, 5, 0)
                        .ok()
                        .and_then(|rows| {
                            rows.into_iter()
                                .find(|r| r.text.as_deref().is_some_and(|t| t.starts_with("http")))
                        })
                        .and_then(|r| r.text)
                };
                if let Some(url) = url {
                    let _ = std::process::Command::new("explorer").arg(&url).spawn();
                }
            }
            platform::hotkey::HotkeyAction::Topmost => {
                set_pinned(!pinned(), &hot_app);
            }
        }
    }) {
        log::error!("全局热键启动失败: {e}");
    }

    // 托盘
    setup_tray(app);
}

fn setup_tray(app: &AppHandle) {
    use tauri::menu::{Menu, MenuItem};
    use tauri::tray::{TrayIconBuilder, TrayIconEvent};

    let show = MenuItem::with_id(app, "show", "显示剪贴板", true, None::<&str>);
    let settings_item = MenuItem::with_id(app, "settings", "设置", true, None::<&str>);
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>);
    let (Ok(show), Ok(settings_item), Ok(quit)) = (show, settings_item, quit) else {
        log::error!("托盘菜单创建失败");
        return;
    };
    let Ok(menu) = Menu::with_items(app, &[&show, &settings_item, &quit]) else {
        log::error!("托盘菜单构建失败");
        return;
    };
    let mut builder = TrayIconBuilder::with_id("nexclip-tray")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .tooltip("NexClip - 剪贴板同步")
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_target_before_main(app),
            "settings" => show_settings_window(app),
            "quit" => app.exit(0),
            _ => {}
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: tauri::tray::MouseButton::Left,
                ..
            } = event
            {
                show_target_before_main(tray.app_handle());
            }
        })
        .build(app)
        .map(|_| ())
        .unwrap_or_else(|e| log::error!("托盘创建失败: {e}"));
}


/// 极简文件日志器:追加写 %LOCALAPPDATA%/NexClip/logs/nexclip.log(5MB 轮转)。
struct FileLogger {
    file: Mutex<Option<std::fs::File>>,
    path: std::path::PathBuf,
}

impl log::Log for FileLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Info
    }
    fn log(&self, record: &log::Record) {
        let mut guard = self.file.lock().unwrap_or_else(|p| p.into_inner());
        // 轮转:超过 5MB 换 .old
        if let Ok(meta) = std::fs::metadata(&self.path) {
            if meta.len() > 5 * 1024 * 1024 {
                let old = self.path.with_extension("old.log");
                let _ = std::fs::rename(&self.path, old);
                *guard = std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(&self.path)
                    .ok();
            }
        }
        if guard.is_none() {
            *guard = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&self.path)
                .ok();
        }
        let ts = chrono::Utc::now().format("%Y-%m-%d %H:%M:%S");
        let line = format!("[{ts}] [{}] {}: {}\n", record.level(), record.target(), record.args());
        if let Some(f) = guard.as_mut() {
            let _ = std::io::Write::write_all(f, line.as_bytes());
        }
    }
    fn flush(&self) {}
}

/// 初始化文件日志(启动最早一步;失败则静默降级为无日志)。
fn init_file_logger() {
    let dir = std::path::PathBuf::from(
        std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into()),
    )
    .join("NexClip")
    .join("logs");
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join("nexclip.log");
    let file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&path)
        .ok();
    let _ = log::set_boxed_logger(Box::new(FileLogger {
        file: Mutex::new(file),
        path,
    }));
    log::set_max_level(log::LevelFilter::Info);
}

pub fn run() {
    init_file_logger();
    let settings = settings::load_settings();
    let storage_dir = settings::resolve_storage_dir(&settings.storage_dir);
    let history = match history::HistoryStore::open(&storage_dir) {
        Ok(h) => h,
        Err(e) => {
            log::error!("打开历史库失败: {e}");
            panic!("无法打开历史库: {e}");
        }
    };
    history.set_max_entries(settings.max_history);
    if settings.retention_days > 0 {
        if let Err(e) = history.prune_older_than(settings.retention_days) {
            log::warn!("按保留天数清理历史失败: {e}");
        }
    }

    log::info!("NexClip(Tauri)启动,版本 {}", env!("CARGO_PKG_VERSION"));
    let state = Arc::new(Mutex::new(AppState {
        settings,
        history,
        sync_status: Mutex::new("not_configured".into()),
    }));
    let setup_state = state.clone();

    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            // 第二实例启动:唤起主窗口
            show_main_at_cursor(app);
        }))
        .plugin(tauri_plugin_notification::init())
        .manage(state)
        .setup(move |app| {
            let handle = app.handle().clone();
            start_platform(&handle, setup_state.clone());
            // 后台回填:为缺图标的存量条目提取来源应用图标(完成后通知前端刷新)
            let back_app = handle.clone();
            let back_state = setup_state;
            tauri::async_runtime::spawn(async move {
                let count = {
                    let guard = back_state.lock().unwrap_or_else(|p| p.into_inner());
                    guard
                        .history
                        .backfill_source_icons(crate::platform::app_icon::get_or_create)
                        .unwrap_or(0)
                };
                if count > 0 {
                    log::info!("已为 {count} 条存量历史回填来源图标");
                    let _ = back_app.emit("history-refresh", ());
                }
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::history_query,
            commands::history_count,
            commands::history_delete,
            commands::history_toggle_star,
            commands::history_update_remark,
            commands::history_update_text,
            commands::history_clear,
            commands::history_stats,
            commands::settings_get,
            commands::settings_set,
            commands::open_in_explorer,
            commands::pick_folder,
            commands::list_user_processes,
            commands::app_info,
            commands::devices_cached,
            commands::check_update,
            commands::download_update,
            commands::update_download_state,
            commands::cancel_update_download,
            commands::run_update_installer,
            commands::open_settings,
            commands::copy_item,
            commands::paste_item,
            commands::set_pinned,
            commands::hide_main,
            commands::toggle_main,
            commands::sync_get_state,
            commands::sync_pair_create,
            commands::sync_pair_confirm,
            commands::sync_pair_revoke,
            commands::sync_test_connection,
            commands::sync_send_text,
            commands::sync_send_file,
            commands::download_transfer_file,
            commands::sync_remove_device,
            commands::sync_rename_device,
            commands::sync_devices_refresh,
            commands::smart_detect,
            commands::smart_exec,
            commands::chat_query,
            commands::hide_toast,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
