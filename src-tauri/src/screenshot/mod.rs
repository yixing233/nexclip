//! 截图会话管理（参考 flashot 架构）。
//!
//! 流程:热键触发 → xcap 截取全部显示器 → 冻结帧 PNG 存缓存 →
//! 每显示器一个透明覆盖窗(overlay-{id})渲染冻结屏 → 前端拖拽选区 →
//! 前端回报选区(显示器本地逻辑坐标) → Rust 裁剪合成标注层 → 复制/保存/贴图。
//!
//! DPI 要点:覆盖窗放置与裁剪全部使用物理像素;前端选区为逻辑坐标,
//! 乘以 scale_factor 转物理坐标(四舍五入 + clamp 到帧边界)。

pub mod scroll;
pub mod win_block;
pub mod window_probe;

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Mutex;

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindowBuilder};

use crate::settings;

/// 冻结帧:单显示器的 RGBA 截图(物理像素)。
pub struct FrozenFrame {
    pub monitor_id: u32,
    pub rgba: Vec<u8>,
    pub width: u32,
    pub height: u32,
    /// 显示器 DPI 缩放(如 1.25/1.5)
    pub scale_factor: f64,
    /// 显示器左上角在虚拟桌面坐标系的物理坐标
    pub x: i32,
    pub y: i32,
}

/// 会话状态。
struct CaptureSession {
    frames: Vec<FrozenFrame>,
    /// 各显示器冻结帧 PNG(Fast 压缩,自定义协议直出,免磁盘 IO)
    pngs: Vec<(u32, std::sync::Arc<Vec<u8>>)>,
}

/// 检测窗口 DTO(发给覆盖窗做悬停高亮/点击自动选区)。
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct WindowRectDto {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub title: String,
    pub app: String,
}

/// 发给覆盖窗的开始载荷。
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct CaptureStartPayload {
    /// 会话修订号(防旧事件串扰)
    pub revision: u64,
    pub monitor_id: u32,
    /// 冻结帧 URL(自定义协议 nexclip-frame 直出内存 PNG)
    pub frame_url: String,
    /// 冻结帧物理像素宽高
    pub frame_width: u32,
    pub frame_height: u32,
    /// 显示器物理矩形(虚拟桌面坐标系)
    pub monitor_x: i32,
    pub monitor_y: i32,
    pub monitor_width: u32,
    pub monitor_height: u32,
    pub scale_factor: f64,
    /// 可见顶层窗口矩形(虚拟桌面物理坐标)
    pub windows: Vec<WindowRectDto>,
}

/// 快速截图闪烁反馈载荷。
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct FlashPayload {
    pub monitor_id: u32,
    /// 闪烁区域(显示器本地物理坐标,None=整屏)
    pub rect: Option<(i32, i32, u32, u32)>,
    pub duration_ms: u64,
    /// 该显示器 DPI 缩放(前端物理→逻辑换算)
    pub scale_factor: f64,
}

static IN_SESSION: AtomicBool = AtomicBool::new(false);
static REVISION: AtomicU64 = AtomicU64::new(1);
static SESSION: Mutex<Option<CaptureSession>> = Mutex::new(None);
/// overlay 就绪握手:每会话待就绪的 monitor_id 集合
static PENDING_READY: Mutex<Option<Vec<u32>>> = Mutex::new(None);

pub fn in_session() -> bool {
    IN_SESSION.load(Ordering::SeqCst)
}

/// GDI 捕获的 32bpp 数据 alpha 通道未定义(通常为 0),强制不透明。
/// 不修会导致冻结屏 PNG 全透明(前端看不到冻结效果,遮罩直接叠在真实桌面上)。
pub(crate) fn force_opaque(img: &mut image::RgbaImage) {
    for p in img.pixels_mut() {
        p.0[3] = 255;
    }
}

/// xcap Monitor → 冻结帧。xcap 的 x()/y() 是虚拟桌面物理坐标。
fn capture_frames() -> Result<Vec<FrozenFrame>, String> {
    let monitors = xcap::Monitor::all().map_err(|e| format!("枚举显示器失败: {e}"))?;
    if monitors.is_empty() {
        return Err("未找到可用显示器".into());
    }
    let mut frames = Vec::new();
    for (idx, m) in monitors.iter().enumerate() {
        let mut img = m.capture_image().map_err(|e| format!("截屏失败(显示器 {idx}): {e}"))?;
        force_opaque(&mut img);
        let scale = m.scale_factor().unwrap_or(1.0) as f64;
        let (mx, my) = match (m.x(), m.y()) {
            (Ok(x), Ok(y)) => (x as i32, y as i32),
            _ => (0, 0),
        };
        let (fw, fh) = (img.width(), img.height());
        frames.push(FrozenFrame {
            monitor_id: idx as u32,
            rgba: img.into_raw(),
            width: fw,
            height: fh,
            scale_factor: scale,
            x: mx,
            y: my,
        });
    }
    Ok(frames)
}

/// 确保每显示器一个隐藏覆盖窗(运行时创建,不进 tauri.conf.json)。
fn ensure_overlays(app: &AppHandle, frames: &[FrozenFrame]) -> Result<(), String> {
    for f in frames {
        let label = format!("overlay-{}", f.monitor_id);
        if app.get_webview_window(&label).is_some() {
            continue;
        }
        // Windows:物理像素定位(逻辑值在高 DPI 下会导致窗口膨胀)
        let pos = PhysicalPosition::new(f.x, f.y);
        let size = PhysicalSize::new(f.width, f.height);
        WebviewWindowBuilder::new(
            app,
            &label,
            WebviewUrl::App("index.html".into()),
        )
        .decorations(false)
        .resizable(false)
        .maximizable(false)
        .minimizable(false)
        .skip_taskbar(true)
        .always_on_top(true)
        .focused(false)
        .shadow(false)
        .transparent(true)
        .visible(false)
        .accept_first_mouse(true)
        .position(pos.x as f64, pos.y as f64)
        .inner_size(f.width as f64, f.height as f64)
        .build()
        .map_err(|e| format!("创建覆盖窗失败({label}): {e}"))?;
        let w = app.get_webview_window(&label).ok_or("覆盖窗句柄丢失")?;
        // 物理坐标二次校准(Builder 的 position/inner_size 是逻辑值)
        let _ = w.set_position(pos);
        let _ = w.set_size(size);
    }
    Ok(())
}

/// 热键/托盘入口:启动截图会话。
/// 会话中再按:
///   - 覆盖窗可见 → 用户主动取消(系统级退路,不依赖前端)
///   - 覆盖窗不可见 → 状态卡死的残留会话,先复位再正常启动(自愈)
pub fn begin_capture(app: &AppHandle) {
    if scroll::abort_if_scrolling() {
        return;
    }
    if IN_SESSION.load(Ordering::SeqCst) {
        let visible = overlay_visible(app);
        end_session_internal(app);
        if visible {
            log::warn!("截图会话进行中,F1 触发强制取消");
            return;
        }
        // 残留状态(会话标志未复位但窗口已不可见):复位后继续启动新会话
        log::warn!("检测到残留截图会话状态,已复位并重新启动");
    }
    IN_SESSION.store(true, Ordering::SeqCst);
    // LL 键盘钩子:屏蔽 Win 键 + 全局 Esc 逃生(不依赖键盘焦点,系统级退路)
    let cb_app = app.clone();
    win_block::start_block(move || {
        let app = cb_app.clone();
        std::thread::spawn(move || end_session_internal(&app));
    });
    let app = app.clone();
    std::thread::spawn(move || {
        if let Err(e) = run_capture(&app) {
            log::error!("截图启动失败: {e}");
            end_session_internal(&app);
        }
    });
}

/// 任一覆盖窗当前是否可见。
fn overlay_visible(app: &AppHandle) -> bool {
    (0..8u32).any(|id| {
        app.get_webview_window(&format!("overlay-{id}"))
            .and_then(|w| w.is_visible().ok())
            .unwrap_or(false)
    })
}

/// 记录最后一次 emit 的载荷,供前端就绪后重取(首次创建窗口时 emit 会丢失)。
static LAST_PAYLOADS: Mutex<Vec<(u32, CaptureStartPayload)>> = Mutex::new(Vec::new());

fn run_capture(app: &AppHandle) -> Result<(), String> {
    let t0 = std::time::Instant::now();
    let frames = capture_frames()?;
    log::info!("[截图计时] 截屏: {:?} ({} 显示器)", t0.elapsed(), frames.len());
    let revision = REVISION.fetch_add(1, Ordering::SeqCst) + 1;
    // 悬停命中改为前端实时查询 window_at_point(WindowFromPoint,O(1)),
    // 因此这里不再预枚举窗口/元素 —— 截图路径只剩「截屏 + PNG 编码」。
    ensure_overlays(app, &frames)?;

    // 冻结帧 Fast PNG 编码(内存直出,免磁盘 IO)
    let t2 = std::time::Instant::now();
    let mut pngs: Vec<(u32, std::sync::Arc<Vec<u8>>)> = Vec::new();
    let mut pending = Vec::new();
    for f in &frames {
        let img = image::RgbaImage::from_raw(f.width, f.height, f.rgba.clone())
            .ok_or("冻结帧数据长度异常")?;
        let png = std::sync::Arc::new(encode_png(&img)?);
        pngs.push((f.monitor_id, png));
        pending.push(f.monitor_id);
    }
    log::info!("[截图计时] PNG 编码: {:?}", t2.elapsed());

    // 关键顺序:就绪集合与会话必须在 emit 之前建立。
    // 否则前端秒回 screenshot_overlay_ready 时会因集合尚未创建而丢信号,
    // 导致等满超时、本次截图被误杀(用户感知为"快捷键失效")。
    {
        let mut p = PENDING_READY.lock().unwrap_or_else(|p| p.into_inner());
        *p = Some(pending.clone());
    }
    {
        let mut sess = SESSION.lock().unwrap_or_else(|p| p.into_inner());
        *sess = Some(CaptureSession { frames, pngs });
    }
    clear_last_payloads();

    // 逐显示器 emit 开始载荷
    for f in session_frames() {
        let payload = CaptureStartPayload {
            revision,
            monitor_id: f.monitor_id,
            frame_url: frame_url(f.monitor_id),
            frame_width: f.width,
            frame_height: f.height,
            monitor_x: f.x,
            monitor_y: f.y,
            monitor_width: f.width,
            monitor_height: f.height,
            scale_factor: f.scale_factor,
            windows: Vec::new(), // 悬停改用 window_at_point 实时查询,不再预枚举
        };
        app.emit_to(
            &format!("overlay-{}", f.monitor_id),
            "screenshot:start",
            payload.clone(),
        )
        .map_err(|e| format!("emit screenshot:start 失败: {e}"))?;
        LAST_PAYLOADS
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .push((f.monitor_id, payload));
    }

    // 等待各覆盖窗预加载完成。
    // 超时兜底 2.5s(4K 帧解码 + 前端 canvas 像素缓存冷启动可能需 1-2s);
    // 仍超时说明前端确实没起来,取消会话 —— 绝不让无交互层的全屏窗显示(会吞系统输入)。
    let deadline = std::time::Instant::now() + std::time::Duration::from_millis(2500);
    loop {
        let done = PENDING_READY
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .as_ref()
            .map(|v| v.is_empty())
            .unwrap_or(true);
        if done {
            break;
        }
        if std::time::Instant::now() >= deadline {
            log::warn!("截图覆盖窗就绪超时(2.5s),取消本次会话(避免全屏窗口吞输入)");
            end_session_internal(app);
            return Ok(());
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }

    // 显示全部覆盖窗 + 抢焦点重试(Windows 前台锁定可能拒绝单次 set_focus,
    // 焦点拿不到时键盘 Esc/Enter 会失效,故重试至 500ms)
    for f in session_frames() {
        if let Some(w) = app.get_webview_window(&format!("overlay-{}", f.monitor_id)) {
            let _ = w.set_position(PhysicalPosition::new(f.x, f.y));
            let _ = w.set_size(PhysicalSize::new(f.width, f.height));
            let _ = w.show();
        }
    }
    if let Some(w) = app.get_webview_window("overlay-0").or_else(|| {
        (0..8u32).find_map(|id| app.get_webview_window(&format!("overlay-{id}")))
    }) {
        for _ in 0..8 {
            if w.is_focused().unwrap_or(false) {
                break;
            }
            let _ = w.set_focus();
            std::thread::sleep(std::time::Duration::from_millis(60));
        }
    }
    log::info!("[截图计时] 总计(到显示): {:?}", t0.elapsed());
    app.emit("screenshot:revealed", revision).ok();
    Ok(())
}

/// 取当前会话帧快照。
fn session_frames() -> Vec<FrozenFrame> {
    SESSION
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .as_ref()
        .map(|s| s.frames.clone())
        .unwrap_or_default()
}

/// 供命令层取冻结帧(会话结束前调用)。
pub fn session_frames_public() -> Vec<FrozenFrame> {
    session_frames()
}

/// 贴图缓存路径:%LOCALAPPDATA%/NexClip/screenshot/pins/pin-<id>.png。
pub fn pin_path(pin_id: &str) -> std::path::PathBuf {
    let dir = screenshot_cache_dir_of().join("pins");
    let _ = std::fs::create_dir_all(&dir);
    dir.join(format!("pin-{pin_id}.png"))
}

/// 贴图标注层路径(透明 PNG;与原图分离存储,支持反复编辑)。
pub fn pin_annotation_path(pin_id: &str) -> std::path::PathBuf {
    let dir = screenshot_cache_dir_of().join("pins");
    let _ = std::fs::create_dir_all(&dir);
    dir.join(format!("pin-{pin_id}.ann.png"))
}

/// 贴图标注对象 JSON(恢复为可编辑对象)。
pub fn pin_annotation_json_path(pin_id: &str) -> std::path::PathBuf {
    let dir = screenshot_cache_dir_of().join("pins");
    let _ = std::fs::create_dir_all(&dir);
    dir.join(format!("pin-{pin_id}.ann.json"))
}

/// 合成贴图输出:原图 + 标注层(标注层不存在则仅原图)。
pub fn pin_composited(pin_id: &str) -> Result<image::RgbaImage, String> {
    let base_png = std::fs::read(pin_path(pin_id)).map_err(|e| format!("读取贴图失败: {e}"))?;
    let mut base = image::load_from_memory(&base_png)
        .map_err(|e| format!("贴图解码失败: {e}"))?
        .into_rgba8();
    let ann_path = pin_annotation_path(pin_id);
    if ann_path.exists() {
        if let Ok(ann_bytes) = std::fs::read(&ann_path) {
            composite_annotation(&mut base, Some(&ann_bytes), 1.0)?;
        }
    }
    Ok(base)
}

fn screenshot_cache_dir_of() -> std::path::PathBuf {
    settings::resolve_storage_dir("").join("screenshot")
}
impl Clone for FrozenFrame {
    fn clone(&self) -> Self {
        FrozenFrame {
            monitor_id: self.monitor_id,
            rgba: self.rgba.clone(),
            width: self.width,
            height: self.height,
            scale_factor: self.scale_factor,
            x: self.x,
            y: self.y,
        }
    }
}

/// overlay 就绪回报(前端预加载冻结帧后调用)。
pub fn overlay_ready(revision: u64, monitor_id: u32) {
    let rev = REVISION.load(Ordering::SeqCst);
    if revision != rev {
        return; // 旧会话事件,忽略
    }
    let mut p = PENDING_READY.lock().unwrap_or_else(|p| p.into_inner());
    if let Some(v) = p.as_mut() {
        v.retain(|&id| id != monitor_id);
    }
}

/// 前端启动时拉取未消费的 screenshot:start 载荷(覆盖首次创建窗口错过 emit 的问题)。
pub fn pending_start_payload(monitor_id: u32) -> Option<CaptureStartPayload> {
    LAST_PAYLOADS
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .iter()
        .find(|(id, _)| *id == monitor_id)
        .map(|(_, p)| p.clone())
}

fn clear_last_payloads() {
    LAST_PAYLOADS.lock().unwrap_or_else(|p| p.into_inner()).clear();
}

/// 取消截图会话:隐藏全部覆盖窗。
pub fn cancel(app: &AppHandle) {
    end_session_internal(app);
}

/// 结束会话(scroll.rs 也调用)。
pub fn end_session_internal(app: &AppHandle) {
    IN_SESSION.store(false, Ordering::SeqCst);
    win_block::stop_block();
    let rev = REVISION.load(Ordering::SeqCst);
    for id in 0..8u32 {
        if let Some(w) = app.get_webview_window(&format!("overlay-{id}")) {
            let _ = w.set_ignore_cursor_events(false);
            let _ = w.hide();
        }
    }
    app.emit("screenshot:end", rev).ok();
    *SESSION.lock().unwrap_or_else(|p| p.into_inner()) = None;
    *PENDING_READY.lock().unwrap_or_else(|p| p.into_inner()) = None;
    clear_last_payloads();
}

/// 点所在显示器 id(供长截图事件路由)。
pub fn monitor_of_point(x: i32, y: i32) -> u32 {
    for f in session_frames() {
        if x >= f.x && x < f.x + f.width as i32 && y >= f.y && y < f.y + f.height as i32 {
            return f.monitor_id;
        }
    }
    0
}

// ==================== 快捷截图(无选区 UI) ====================

/// 光标所在点(虚拟桌面物理坐标)。
fn cursor_pos() -> (i32, i32) {
    unsafe {
        let mut p = windows::Win32::Foundation::POINT::default();
        let _ = windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut p);
        (p.x, p.y)
    }
}

/// 前台窗口物理矩形(DWM 扩展边界优先)。
fn foreground_window_rect() -> Option<RectI> {
    unsafe {
        let hwnd = windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow();
        if hwnd.is_invalid() {
            return None;
        }
        use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS};
        let mut rect = windows::Win32::Foundation::RECT::default();
        let ok = DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            &mut rect as *mut _ as *mut _,
            std::mem::size_of::<windows::Win32::Foundation::RECT>() as u32,
        )
        .is_ok();
        if !ok {
            let _ = windows::Win32::UI::WindowsAndMessaging::GetWindowRect(hwnd, &mut rect);
        }
        Some(RectI {
            x: rect.left,
            y: rect.top,
            w: (rect.right - rect.left).max(0) as u32,
            h: (rect.bottom - rect.top).max(0) as u32,
        })
    }
}

pub struct RectI {
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
}

/// 活屏截取:截光标所在显示器全屏 → 复制 → 闪烁反馈。
pub fn quick_capture_screen(app: &AppHandle) {
    let (cx, cy) = cursor_pos();
    let monitors = match xcap::Monitor::all() {
        Ok(v) => v,
        Err(e) => return log::error!("快捷截屏枚举显示器失败: {e}"),
    };
    let m = match monitors.iter().find(|m| {
        let (mx, my) = (m.x().unwrap_or(0), m.y().unwrap_or(0));
        let (mw, mh) = (m.width().unwrap_or(0), m.height().unwrap_or(0));
        cx >= mx && cx < mx + mw as i32 && cy >= my && cy < my + mh as i32
    }) {
        Some(m) => m,
        None => return log::error!("未找到光标所在显示器"),
    };
    let img = match m.capture_image() {
        Ok(mut i) => { force_opaque(&mut i); i }
        Err(e) => return log::error!("快捷截屏失败: {e}"),
    };
    let mid = monitor_at(cx, cy);
    match encode_png(&img) {
        Ok(png) => {
            let _ = crate::platform::writer::submit(
                crate::platform::writer::WritePayload::Image { png },
                5000,
            );
        }
        Err(e) => return log::error!("快捷截屏编码失败: {e}"),
    }
    flash_overlay(app, mid, None);
}

/// 活动窗口截取:前台窗口矩形 → 所在显示器截屏 → 裁剪 → 复制 → 闪烁反馈。
pub fn quick_capture_window(app: &AppHandle) {
    let Some(rect) = foreground_window_rect() else {
        return log::error!("快捷截窗:未获取前台窗口");
    };
    let (cx, cy) = (rect.x + rect.w as i32 / 2, rect.y + rect.h as i32 / 2);
    let monitors = match xcap::Monitor::all() {
        Ok(v) => v,
        Err(e) => return log::error!("快捷截窗枚举显示器失败: {e}"),
    };
    let m = match monitors.iter().find(|m| {
        let (mx, my) = (m.x().unwrap_or(0), m.y().unwrap_or(0));
        let (mw, mh) = (m.width().unwrap_or(0), m.height().unwrap_or(0));
        cx >= mx && cx < mx + mw as i32 && cy >= my && cy < my + mh as i32
    }) {
        Some(m) => m,
        None => return log::error!("快捷截窗:窗口不在任何显示器上"),
    };
    let img = match m.capture_image() {
        Ok(mut i) => { force_opaque(&mut i); i }
        Err(e) => return log::error!("快捷截窗失败: {e}"),
    };
    let (mx, my) = (m.x().unwrap_or(0), m.y().unwrap_or(0));
    let (mw, mh) = (img.width(), img.height());
    // 窗口矩形 → 显示器本地物理坐标,clamp
    let x0 = (rect.x - mx).clamp(0, mw.saturating_sub(1) as i32) as u32;
    let y0 = (rect.y - my).clamp(0, mh.saturating_sub(1) as i32) as u32;
    let x1 = (rect.x + rect.w as i32 - mx).clamp(0, mw as i32) as u32;
    let y1 = (rect.y + rect.h as i32 - my).clamp(0, mh as i32) as u32;
    let (w, h) = (x1.saturating_sub(x0), y1.saturating_sub(y0));
    if w == 0 || h == 0 {
        return log::error!("快捷截窗:窗口区域无效");
    }
    let mut cropped = image::RgbaImage::new(w, h);
    for row in 0..h {
        let src_start = ((y0 + row) as usize * mw as usize + x0 as usize) * 4;
        let src_end = src_start + w as usize * 4;
        let dst_start = row as usize * w as usize * 4;
        cropped.as_mut()[dst_start..dst_start + w as usize * 4]
            .copy_from_slice(&img.as_raw()[src_start..src_end]);
    }
    match encode_png(&cropped) {
        Ok(png) => {
            let _ = crate::platform::writer::submit(
                crate::platform::writer::WritePayload::Image { png },
                5000,
            );
        }
        Err(e) => return log::error!("快捷截窗编码失败: {e}"),
    }
    let mid = monitor_at(cx, cy);
    let local = (x0 as i32, y0 as i32, w, h);
    flash_overlay(app, mid, Some(local));
}

fn monitor_at(x: i32, y: i32) -> u32 {
    let monitors = xcap::Monitor::all().unwrap_or_default();
    for (idx, m) in monitors.iter().enumerate() {
        let (mx, my) = (m.x().unwrap_or(0), m.y().unwrap_or(0));
        let (mw, mh) = (m.width().unwrap_or(0), m.height().unwrap_or(0));
        if x >= mx && x < mx + mw as i32 && y >= my && y < my + mh as i32 {
            return idx as u32;
        }
    }
    0
}

/// 闪烁反馈:覆盖窗显示蓝色边框 420ms(不进会话,快速截图专用)。
fn flash_overlay(app: &AppHandle, monitor_id: u32, rect: Option<(i32, i32, u32, u32)>) {
    let label = format!("overlay-{monitor_id}");
    // 无覆盖窗时先确保存在(快捷截图也可能在会话外)
    let ok = if app.get_webview_window(&label).is_some() {
        true
    } else {
        ensure_overlays(app, &[monitor_frame(monitor_id)]).is_ok()
    };
    if !ok {
        return;
    }
    let scale = monitor_frame_scale(monitor_id);
    let payload = FlashPayload { monitor_id, rect, duration_ms: 420, scale_factor: scale };
    let _ = app.emit_to(&label, "screenshot:flash", payload);
    if let Some(w) = app.get_webview_window(&label) {
        let _ = w.set_ignore_cursor_events(true);
        let _ = w.show();
        let app2 = app.clone();
        let label2 = label.clone();
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_millis(520));
            if !in_session() {
                if let Some(w) = app2.get_webview_window(&label2) {
                    let _ = w.hide();
                    let _ = w.set_ignore_cursor_events(false);
                }
            }
        });
    }
}

/// 单显示器帧(快捷截屏用,不进会话)。
fn monitor_frame(monitor_id: u32) -> FrozenFrame {
    let monitors = xcap::Monitor::all().unwrap_or_default();
    let m = monitors.get(monitor_id as usize);
    if let Some(m) = m {
        if let Ok(img) = m.capture_image() {
            return FrozenFrame {
                monitor_id,
                rgba: Vec::new(),
                width: img.width(),
                height: img.height(),
                scale_factor: m.scale_factor().unwrap_or(1.0) as f64,
                x: m.x().unwrap_or(0),
                y: m.y().unwrap_or(0),
            };
        }
    }
    FrozenFrame {
        monitor_id,
        rgba: Vec::new(),
        width: 1920,
        height: 1080,
        scale_factor: 1.0,
        x: 0,
        y: 0,
    }
}

/// 单显示器 DPI 缩放。
fn monitor_frame_scale(monitor_id: u32) -> f64 {
    let monitors = xcap::Monitor::all().unwrap_or_default();
    monitors
        .get(monitor_id as usize)
        .and_then(|m| m.scale_factor().ok())
        .map(|s| s as f64)
        .unwrap_or(1.0)
}

/// 逻辑选区(显示器本地 CSS 像素) → 物理像素矩形(已 clamp 到帧边界)。
pub fn physical_crop_rect(
    frame: &FrozenFrame,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
) -> (u32, u32, u32, u32) {
    let sx = (x * frame.scale_factor).round().max(0.0) as i64;
    let sy = (y * frame.scale_factor).round().max(0.0) as i64;
    let sw = (width * frame.scale_factor).round() as i64;
    let sh = (height * frame.scale_factor).round() as i64;
    let fw = frame.width as i64;
    let fh = frame.height as i64;
    let x0 = sx.clamp(0, fw.saturating_sub(1));
    let y0 = sy.clamp(0, fh.saturating_sub(1));
    let x1 = (sx + sw).clamp(0, fw);
    let y1 = (sy + sh).clamp(0, fh);
    (
        x0 as u32,
        y0 as u32,
        (x1 - x0).max(0) as u32,
        (y1 - y0).max(0) as u32,
    )
}

/// 行级 RGBA 裁剪拷贝。
pub fn crop_rgba(frame: &FrozenFrame, rect: (u32, u32, u32, u32)) -> Option<image::RgbaImage> {
    let (x, y, w, h) = rect;
    if w == 0 || h == 0 {
        return None;
    }
    let mut out = image::RgbaImage::new(w, h);
    for row in 0..h {
        let src_start = ((y + row) as usize * frame.width as usize + x as usize) * 4;
        let src_end = src_start + w as usize * 4;
        let dst_start = row as usize * w as usize * 4;
        out.as_mut()[dst_start..dst_start + w as usize * 4]
            .copy_from_slice(&frame.rgba[src_start..src_end]);
    }
    Some(out)
}

/// 合成标注层(透明 PNG 字节,选区相对坐标,物理像素)到裁剪结果上。
pub fn composite_annotation(
    base: &mut image::RgbaImage,
    annotation_png: Option<&[u8]>,
    scale_factor: f64,
) -> Result<(), String> {
    let Some(png) = annotation_png else { return Ok(()) };
    if png.is_empty() {
        return Ok(());
    }
    let ann_img = image::load_from_memory(png).map_err(|e| format!("标注层解码失败: {e}"))?;
    // 标注层为物理像素(前端在选区 canvas 上按 scale_factor 绘制),尺寸不符时缩放对齐
    let (bw, bh) = (base.width(), base.height());
    let ann_rgba = if ann_img.width() != bw || ann_img.height() != bh {
        ann_img.resize_exact(bw, bh, image::imageops::FilterType::Triangle).into_rgba8()
    } else {
        ann_img.into_rgba8()
    };
    let _ = scale_factor;
    image::imageops::overlay(base, &ann_rgba, 0, 0);
    Ok(())
}

/// 图像调整参数(与 flashot 对齐;-100..100,0=不变)。
#[derive(serde::Deserialize, Clone, Copy, Default)]
#[serde(rename_all = "camelCase")]
pub struct Adjustments {
    pub grayscale: bool,
    pub brightness: f32,
    pub contrast: f32,
    pub saturation: f32,
}

/// 应用图像调整(灰度 → 亮度 → 对比度 → 饱和度)。
pub fn apply_adjustments(img: &mut image::RgbaImage, adj: &Adjustments) {
    // 灰度
    if adj.grayscale {
        for p in img.pixels_mut() {
            let l = 0.299 * p[0] as f32 + 0.587 * p[1] as f32 + 0.114 * p[2] as f32;
            p[0] = l.clamp(0.0, 255.0) as u8;
            p[1] = l.clamp(0.0, 255.0) as u8;
            p[2] = l.clamp(0.0, 255.0) as u8;
        }
    }
    // 亮度:通道加偏移
    let b = adj.brightness * 2.55;
    if b.abs() > 0.01 {
        for p in img.pixels_mut() {
            p[0] = ((p[0] as f32 + b).round() as i32).clamp(0, 255) as u8;
            p[1] = ((p[1] as f32 + b).round() as i32).clamp(0, 255) as u8;
            p[2] = ((p[2] as f32 + b).round() as i32).clamp(0, 255) as u8;
        }
    }
    // 对比度:围绕 128 缩放
    let c = 1.0 + adj.contrast / 100.0;
    if (c - 1.0).abs() > 0.01 {
        for p in img.pixels_mut() {
            p[0] = (((p[0] as f32 - 128.0) * c + 128.0).round() as i32).clamp(0, 255) as u8;
            p[1] = (((p[1] as f32 - 128.0) * c + 128.0).round() as i32).clamp(0, 255) as u8;
            p[2] = (((p[2] as f32 - 128.0) * c + 128.0).round() as i32).clamp(0, 255) as u8;
        }
    }
    // 饱和度:与亮度按比例混合(s=0 → 灰)
    let s = 1.0 + adj.saturation / 100.0;
    if (s - 1.0).abs() > 0.01 {
        for p in img.pixels_mut() {
            let l = 0.299 * p[0] as f32 + 0.587 * p[1] as f32 + 0.114 * p[2] as f32;
            p[0] = ((l + (p[0] as f32 - l) * s).round() as i32).clamp(0, 255) as u8;
            p[1] = ((l + (p[1] as f32 - l) * s).round() as i32).clamp(0, 255) as u8;
            p[2] = ((l + (p[2] as f32 - l) * s).round() as i32).clamp(0, 255) as u8;
        }
    }
}

/// 圆角:四角 alpha 渐变裁切(抗锯齿,1px 边缘羽化)。
pub fn apply_corner_radius(img: &mut image::RgbaImage, radius: u32) {
    if radius == 0 {
        return;
    }
    let (w, h) = (img.width(), img.height());
    let r = radius.min(w / 2).min(h / 2);
    if r == 0 {
        return;
    }
    for y in 0..h {
        for x in 0..w {
            // 每个角最多遍历 r×r 区域
            let in_corner = (x < r && y < r)
                || (x >= w - r && y < r)
                || (x < r && y >= h - r)
                || (x >= w - r && y >= h - r);
            if !in_corner {
                continue;
            }
            // 角内圆心
            let (cx, cy) = if x < r && y < r {
                (r, r)
            } else if x >= w - r && y < r {
                (w - r, r)
            } else if x < r && y >= h - r {
                (r, h - r)
            } else {
                (w - r, h - r)
            };
            let dx = (x as f32 + 0.5 - cx as f32).abs();
            let dy = (y as f32 + 0.5 - cy as f32).abs();
            let dist = (dx * dx + dy * dy).sqrt();
            let p = img.get_pixel_mut(x, y);
            if dist > r as f32 + 0.5 {
                p.0[3] = 0;
            } else if dist > r as f32 - 0.5 {
                // 边缘羽化
                let t = (r as f32 + 0.5 - dist).clamp(0.0, 1.0);
                p.0[3] = (p.0[3] as f32 * t) as u8;
            }
        }
    }
}

/// 保存/贴图共用的编码 PNG(Fast 压缩,4K 提速一个数量级)。
pub fn encode_png(img: &image::RgbaImage) -> Result<Vec<u8>, String> {
    use image::codecs::png::{CompressionType, FilterType, PngEncoder};
    use image::ImageEncoder;
    let mut buf = Vec::new();
    let enc = PngEncoder::new_with_quality(&mut buf, CompressionType::Fast, FilterType::Sub);
    enc.write_image(img.as_raw(), img.width(), img.height(), image::ExtendedColorType::Rgba8)
        .map_err(|e| format!("PNG 编码失败: {e}"))?;
    Ok(buf)
}

// ==================== 自定义协议帧供给与启动预创建 ====================

/// 冻结帧 URL(Windows 下 webview 请求 http://nexclip-frame.localhost/{id})。
pub fn frame_url(monitor_id: u32) -> String {
    format!("http://nexclip-frame.localhost/{monitor_id}")
}

/// 贴图 URL。
pub fn pin_url(pin_id: &str) -> String {
    format!("http://nexclip-frame.localhost/p/{pin_id}")
}

/// 自定义协议处理器入口:路径 "/{monitor_id}" → 会话冻结帧;"/p/{pin_id}" → 贴图合成图。
pub fn serve_frame_request(path: &str) -> Result<Vec<u8>, String> {
    let path = path.replace("%2F", "/");
    let path = path.trim_start_matches('/');
    if let Some(pin_id) = path.strip_prefix("p/") {
        // 贴图输出 = 原图 + 标注层合成
        let img = pin_composited(pin_id)?;
        return encode_png(&img);
    }
    let id: u32 = path
        .split('/')
        .next()
        .unwrap_or("")
        .parse()
        .map_err(|_| "无效帧编号".to_string())?;
    let sess = SESSION.lock().unwrap_or_else(|p| p.into_inner());
    if let Some(s) = sess.as_ref() {
        if let Some((_, png)) = s.pngs.iter().find(|(mid, _)| *mid == id) {
            return Ok(png.as_ref().clone());
        }
    }
    Err("会话不存在或帧已过期".into())
}

/// 应用启动时预创建覆盖窗(消除首次触发的 webview 冷启动延迟)。
pub fn preload_overlays(app: &AppHandle) {
    let app = app.clone();
    std::thread::spawn(move || {
        match monitor_geometries() {
            Ok(frames) => {
                if let Err(e) = ensure_overlays(&app, &frames) {
                    log::warn!("覆盖窗预创建失败: {e}");
                } else {
                    log::info!("截图覆盖窗预创建完成({} 个显示器)", frames.len());
                }
            }
            Err(e) => log::warn!("覆盖窗预创建跳过: {e}"),
        }
    });
}

/// 仅显示器几何信息(不截屏)。
fn monitor_geometries() -> Result<Vec<FrozenFrame>, String> {
    let monitors = xcap::Monitor::all().map_err(|e| format!("枚举显示器失败: {e}"))?;
    Ok(monitors
        .iter()
        .enumerate()
        .map(|(idx, m)| FrozenFrame {
            monitor_id: idx as u32,
            rgba: Vec::new(),
            width: m.width().unwrap_or(1920) as u32,
            height: m.height().unwrap_or(1080) as u32,
            scale_factor: m.scale_factor().unwrap_or(1.0) as f64,
            x: m.x().unwrap_or(0),
            y: m.y().unwrap_or(0),
        })
        .collect())
}
