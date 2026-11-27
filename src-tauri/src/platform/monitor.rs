//! 剪贴板监听:AddClipboardFormatListener 消息窗口 + 150ms 去抖 + 轮询兜底。
//!
//! 移植自 C# ClipboardMonitor 的防御工程(裁剪至无 UI 依赖):
//! - WM_CLIPBOARDUPDATE 触发 → 记录序列号 → 150ms 后序列号未变才捕获(去抖);
//! - 2s 轮询兜底(GetClipboardSequenceNumber 短路,零开销检查);
//! - 防回环:跳过 writer 写入的序列号;
//! - 熔断:OpenClipboard 连续失败(剪贴板被独占)时退避;
//! - 过滤:远程控制应用 + 用户自定义进程名。

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{HANDLE, HMODULE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::DataExchange::{
    AddClipboardFormatListener, CloseClipboard, GetClipboardData, GetClipboardOwner,
    GetClipboardSequenceNumber, GetOpenClipboardWindow, IsClipboardFormatAvailable, OpenClipboard,
    RegisterClipboardFormatW,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Ole::{CF_DIB, CF_HDROP, CF_UNICODETEXT};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::Shell::{DragQueryFileW, HDROP};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, GetWindowThreadProcessId, GetMessageW,
    RegisterClassW, MSG, WINDOW_EX_STYLE, WINDOW_STYLE, WM_CLIPBOARDUPDATE,
    WNDCLASSW,
};


/// 150ms 去抖(与旧版一致:等序列号稳定再捕获)。
const DEBOUNCE_MS: u64 = 150;
/// 轮询兜底间隔(序列号短路,零开销检查)。
const POLL_MS: u64 = 2000;
/// OpenClipboard 连续失败上限,超过后进入熔断退避。
const OPEN_FAIL_CIRCUIT: usize = 6;

/// 在途捕获数上限:GetClipboardData 是跨进程调用,剪贴板 owner(如远控软件)
/// 延迟渲染挂起时会永久阻塞执行线程。卡死的线程无法安全强杀(会带着已打开的
/// 剪贴板句柄泄漏),只能限制在途数量防堆积。
const MAX_CONCURRENT_CAPTURES: usize = 3;
/// 剪贴板被持续持锁多久后判定为"被独占"并提示用户(毫秒)。
const HELD_BLOCK_THRESHOLD_MS: u128 = 10_000;
/// 看门狗检查间隔。
const WATCHDOG_INTERVAL_MS: u64 = 5_000;
/// 单次捕获超过该时长视为卡死(秒)。
const CAPTURE_STUCK_SECS: u64 = 15;

static MONITOR_TX: OnceLock<Sender<u32>> = OnceLock::new();
static LAST_CAPTURED_SEQ: AtomicU32 = AtomicU32::new(0);
/// Only timed-out work may overlap with a newer clipboard sequence.
static CAPTURES: Mutex<Vec<CaptureTask>> = Mutex::new(Vec::new());

struct CaptureTask {
    seq: u32,
    started: Instant,
    stage: &'static str,
    warned: bool,
}

fn reserve_capture(tasks: &mut Vec<CaptureTask>, seq: u32, now: Instant) -> bool {
    if tasks.len() >= MAX_CONCURRENT_CAPTURES
        || tasks.iter().any(|t| t.seq == seq || now.duration_since(t.started).as_secs() < CAPTURE_STUCK_SECS)
    {
        return false;
    }
    tasks.push(CaptureTask { seq, started: now, stage: "starting", warned: false });
    true
}

pub fn capture_stage(seq: u32, stage: &'static str) {
    if let Some(task) = CAPTURES.lock().unwrap_or_else(|p| p.into_inner()).iter_mut().find(|t| t.seq == seq) {
        task.stage = stage;
    }
}
/// 熔断标志:连续失败后暂停捕获,直到下一次序列号变化
static CIRCUIT_OPEN: AtomicBool = AtomicBool::new(false);
/// 熔断解除时刻:到期后允许探测一次。不能只靠序列号变化解除——
/// 熔断时 capture() 直接返回,读剪贴板成功路径永远走不到。
static CIRCUIT_RETRY_AT: Mutex<Option<Instant>> = Mutex::new(None);
static OPEN_FAILS: AtomicU32 = AtomicU32::new(0);
/// 当前是否有人长期持有剪贴板锁。
static HELD_SINCE: Mutex<Option<Instant>> = Mutex::new(None);
/// 本次"被独占"是否已经提示过(仅在状态翻转时通知一次)。
static HELD_ANNOUNCED: AtomicBool = AtomicBool::new(false);

/// 剪贴板健康告警(由平台层推给 UI/托盘)。
pub enum ClipboardAlert {
    /// 被某个进程持续独占。
    Blocked { holder: Option<String> },
    /// 从独占状态恢复。
    Recovered,
    /// 单次捕获卡死超过阈值(无法强杀线程,只能告警)。
    CaptureStuck { secs: u64 },
}

type AlertHook = Arc<dyn Fn(ClipboardAlert) + Send + Sync + 'static>;
static ALERT_HOOK: OnceLock<AlertHook> = OnceLock::new();

const CLASS_NAME: &[u16] = &[
    b'N' as u16, b'e' as u16, b'x' as u16, b'C' as u16, b'l' as u16, b'i' as u16, b'p' as u16,
    b'M' as u16, b'o' as u16, b'n' as u16, 0,
];

/// 启动监听线程 + 捕获工作线程。`capture(seq)` 在序列号稳定后被调用
/// (工作线程内;打开剪贴板、读取内容都在该线程,绝不占用 GUI 线程)。
pub fn start<F, A>(capture: F, alert: A) -> Result<(), String>
where
    F: Fn(u32) + Send + Sync + 'static,
    A: Fn(ClipboardAlert) + Send + Sync + 'static,
{
    let _ = ALERT_HOOK.set(Arc::new(alert));
    LAST_CAPTURED_SEQ
        .store(unsafe { GetClipboardSequenceNumber() }, Ordering::SeqCst);
    let (tx, rx) = std::sync::mpsc::channel::<u32>();
    let _ = MONITOR_TX.set(tx);
    let capture = std::sync::Arc::new(capture);

    std::thread::Builder::new()
        .name("clipboard-monitor".into())
        .spawn(move || unsafe { run_listener_thread() })
        .map_err(|e| e.to_string())?;

    let capture2 = capture.clone();
    std::thread::Builder::new()
        .name("clipboard-capture".into())
        .spawn(move || run_capture_worker(rx, capture2))
        .map_err(|e| e.to_string())?;

    // 轮询兜底:ContentChanged 在某些宿主上会丢消息
    let _capture3 = capture;
    std::thread::Builder::new()
        .name("clipboard-poll".into())
        .spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_millis(POLL_MS));
            let seq = unsafe { GetClipboardSequenceNumber() };
            if seq != LAST_CAPTURED_SEQ.load(Ordering::SeqCst) {
                if let Some(tx) = MONITOR_TX.get() {
                    let _ = tx.send(seq);
                }
            }
        })
        .map_err(|e| e.to_string())?;

    // 看门狗:独立后台线程。即使捕获线程/UI 线程被剪贴板调用挂住,
    // 这里仍能检测到并提示"谁在占用剪贴板"。
    std::thread::Builder::new()
        .name("clipboard-watchdog".into())
        .spawn(move || loop {
            std::thread::sleep(Duration::from_millis(WATCHDOG_INTERVAL_MS));
            run_watchdog_tick();
        })
        .map_err(|e| e.to_string())?;
    Ok(())
}

fn notify_alert(alert: ClipboardAlert) {
    if let Some(hook) = ALERT_HOOK.get() {
        let hook = hook.clone();
        // Notification delivery may wait on application state; keep the watchdog independent.
        let _ = std::thread::Builder::new().name("clipboard-alert".into()).spawn(move || hook(alert));
    }
}

/// 看门狗单次检查:
/// Probe lock availability and report each overdue capture with its current stage.
fn run_watchdog_tick() {
    // ---- 剪贴板持锁检测 ----
    // 用"试开一次"判断是否被独占(与旧版 TryProbeClipboard 一致)。
    // 注意不能只看 GetOpenClipboardWindow:无窗口进程(控制台/服务)持锁时它返回 NULL。
    let held = unsafe {
        if OpenClipboard(None).is_ok() {
            let _ = CloseClipboard();
            false
        } else {
            true
        }
    };
    if held {
        let mut since = HELD_SINCE.lock().unwrap_or_else(|p| p.into_inner());
        if since.is_none() {
            log::info!("剪贴板暂时不可打开(可能被其他程序占用),开始计时");
        }
        let started = *since.get_or_insert_with(Instant::now);
        let held_ms = started.elapsed().as_millis();
        drop(since);
        if held_ms >= HELD_BLOCK_THRESHOLD_MS && !HELD_ANNOUNCED.swap(true, Ordering::SeqCst) {
            // 打开熔断,让 capture() 暂时跳过跨进程读取;3 秒后自动探测恢复。
            CIRCUIT_OPEN.store(true, Ordering::SeqCst);
            *CIRCUIT_RETRY_AT
                .lock()
                .unwrap_or_else(|p| p.into_inner()) = Some(Instant::now() + Duration::from_secs(3));
            let holder = unsafe { clipboard_owner_name() };
            log::warn!(
                "剪贴板被持续独占 {held_ms}ms,暂停捕获;持锁者={}",
                holder.as_deref().unwrap_or("未知进程")
            );
            notify_alert(ClipboardAlert::Blocked { holder });
        }
    } else {
        let was_held = HELD_SINCE
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .take()
            .is_some();
        if HELD_ANNOUNCED.swap(false, Ordering::SeqCst) {
            log::info!("剪贴板已恢复可用,重新开始监听");
            notify_alert(ClipboardAlert::Recovered);
        } else if was_held {
            log::debug!("剪贴板短暂争用已解除");
        }
    }

    // ---- 捕获卡死检测 ----
    let alerts: Vec<_> = {
        let mut tasks = CAPTURES.lock().unwrap_or_else(|p| p.into_inner());
        tasks.iter_mut().filter_map(|task| {
            let secs = task.started.elapsed().as_secs();
            if secs < CAPTURE_STUCK_SECS || task.warned { return None; }
            task.warned = true;
            Some((secs, task.stage))
        }).collect()
    };
    for (secs, stage) in alerts {
            let holder = unsafe { clipboard_owner_name() };
            log::warn!(
                "剪贴板看门狗触发: 阶段={stage}, 持续={secs}s, 持锁者={}",
                holder.as_deref().unwrap_or("无")
            );
            notify_alert(ClipboardAlert::CaptureStuck {
                secs,
            });
    }
}

unsafe fn run_listener_thread() {
    let hinstance: HMODULE = GetModuleHandleW(None).unwrap_or_default().into();
    let class_name = PCWSTR(CLASS_NAME.as_ptr());
    let wc = WNDCLASSW {
        lpfnWndProc: Some(wnd_proc),
        hInstance: hinstance.into(),
        lpszClassName: class_name,
        ..Default::default()
    };
    RegisterClassW(&wc);
    let hwnd = CreateWindowExW(
        WINDOW_EX_STYLE::default(),
        class_name,
        PCWSTR::null(),
        WINDOW_STYLE::default(),
        0,
        0,
        0,
        0,
        None,
        None,
        hinstance,
        None,
    )
    .unwrap_or_default();
    if hwnd.is_invalid() {
        log::error!("剪贴板监听消息窗口创建失败");
        return;
    }
    if let Err(e) = AddClipboardFormatListener(hwnd) {
        log::error!("AddClipboardFormatListener 失败: {e}");
        return;
    }
    let mut msg = MSG::default();
    while GetMessageW(&mut msg, HWND::default(), 0, 0).as_bool() {
        let _ = windows::Win32::UI::WindowsAndMessaging::TranslateMessage(&msg);
        windows::Win32::UI::WindowsAndMessaging::DispatchMessageW(&msg);
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if msg == WM_CLIPBOARDUPDATE {
        let seq = GetClipboardSequenceNumber();
        if let Some(tx) = MONITOR_TX.get() {
            let _ = tx.send(seq);
        }
        return LRESULT(0);
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
}

fn run_capture_worker(
    rx: std::sync::mpsc::Receiver<u32>,
    capture: std::sync::Arc<impl Fn(u32) + Send + Sync + 'static>,
) {
    loop {
        let seq = match rx.recv() {
            Ok(s) => s,
            Err(_) => return,
        };
        if seq == LAST_CAPTURED_SEQ.load(Ordering::SeqCst) {
            continue;
        }
        // 去抖:等待 150ms 后序列号未变才捕获(连续复制时只取最后一次)
        std::thread::sleep(std::time::Duration::from_millis(DEBOUNCE_MS));
        if unsafe { GetClipboardSequenceNumber() } != seq {
            continue;
        }
        // 捕获放短命线程执行:GetClipboardData 可能被挂起的延迟渲染 owner
        // (远控软件)永久卡死,主循环绝不能被拖死。卡死线程留在内核等 owner
        // 唤醒(强杀会泄漏已打开的剪贴板句柄),并发超限时跳过本次序列号。
        if !reserve_capture(&mut CAPTURES.lock().unwrap_or_else(|p| p.into_inner()), seq, Instant::now()) {
            continue;
        }
        let shot = capture.clone();
        let spawn_result = std::thread::Builder::new()
            .name("clipboard-capture-shot".into())
            .spawn(move || {
                // RAII:即便 capture() panic 也保证计数归还。否则计数只增不减,
                // 累积到上限后所有捕获都被跳过 —— 表现为「复制再也不进剪贴板」。
                struct ActiveGuard(u32);
                impl Drop for ActiveGuard {
                    fn drop(&mut self) {
                        CAPTURES.lock().unwrap_or_else(|p| p.into_inner()).retain(|t| t.seq != self.0);
                    }
                }
                let _guard = ActiveGuard(seq);
                // 捕获线程内单独 catch:panic 不得让监听静默停摆
                if std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| shot(seq))).is_err() {
                    log::error!("剪贴板捕获线程 panic(序列号 {seq}),已忽略本次内容");
                }
            });
        if spawn_result.is_err() {
            CAPTURES.lock().unwrap_or_else(|p| p.into_inner()).retain(|t| t.seq != seq);
            log::error!("剪贴板捕获线程创建失败,跳过序列号 {seq}");
        }
    }
}

/// 标记序列号已成功处理(含按规则主动忽略的内容)。
pub fn mark_captured(seq: u32) {
    if seq != 0 && unsafe { GetClipboardSequenceNumber() } == seq {
        LAST_CAPTURED_SEQ.store(seq, Ordering::SeqCst);
    }
}

// ---- 捕获实现(工作线程内调用) ----

/// 从剪贴板读取全部内容(调用线程必须允许阻塞;失败返回 Err,熔断计数由调用方处理)。
///
/// 返回:(文本, HTML 片段, 文件路径列表, PNG 字节)
pub unsafe fn read_clipboard() -> Result<(Option<String>, Option<String>, Vec<String>, Option<Vec<u8>>), ()> {
    read_clipboard_with_status().0
}

/// 读取剪贴板,同时区分「打开失败」和「打开成功但没有可用格式」。
/// 只有前者应计入熔断;空内容/未知格式是正常状态,不能连续六次就暂停监听。
pub unsafe fn read_clipboard_with_status(
) -> (
    Result<(Option<String>, Option<String>, Vec<String>, Option<Vec<u8>>), ()>,
    bool,
) {
    let mut opened = false;
    for _ in 0..OPEN_FAIL_CIRCUIT {
        if OpenClipboard(None).is_ok() {
            opened = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    if !opened {
        return (Err(()), false);
    }
    let result = read_locked();
    let _ = CloseClipboard();
    (result.decode(), true)
}

#[derive(Default)]
struct ClipboardSnapshot {
    files: Vec<String>,
    png: Option<Vec<u8>>,
    dib: Option<Vec<u8>>,
    text: Option<Vec<u8>>,
    html: Option<Vec<u8>>,
}

impl ClipboardSnapshot {
    fn decode(self) -> Result<(Option<String>, Option<String>, Vec<String>, Option<Vec<u8>>), ()> {
        if !self.files.is_empty() { return Ok((None, None, self.files, None)); }
        if let Some(png) = self.png {
            if image::load_from_memory(&png).is_ok() {
                return Ok((None, None, Vec::new(), Some(png)));
            }
            log::warn!("剪贴板 PNG 数据损坏,尝试 DIB 格式");
        }
        if let Some(dib) = self.dib {
            if let Ok(png) = super::writer::dib_to_png(&dib) {
                return Ok((None, None, Vec::new(), Some(png)));
            }
        }
        if let Some(bytes) = self.text {
            let utf16: Vec<u16> = bytes.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
            let text = String::from_utf16_lossy(&utf16).trim_end_matches('\0').to_string();
            if !text.is_empty() {
                let html = self.html.and_then(|bytes| String::from_utf8(bytes).ok())
                    .and_then(|raw| crate::clipboard::html_format::get_static_fragment(raw.trim_end_matches('\0')));
                return Ok((Some(text), html, Vec::new(), None));
            }
        }
        Err(())
    }
}

unsafe fn read_locked() -> ClipboardSnapshot {
    let mut snapshot = ClipboardSnapshot::default();
    // 1. 文件(CF_HDROP):仅元数据
    if IsClipboardFormatAvailable(CF_HDROP.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_HDROP.0 as u32) {
            let files = read_hdrop(HANDLE(handle.0));
            if !files.is_empty() {
                snapshot.files = files;
                return snapshot;
            }
        }
    }
    // 2. 图片:PNG 注册格式优先,回落 CF_DIB
    let png_fmt = register_png_format();
    if IsClipboardFormatAvailable(png_fmt).is_ok() {
        if let Ok(handle) = GetClipboardData(png_fmt) {
            snapshot.png = handle_bytes(handle);
        }
    }
    if IsClipboardFormatAvailable(CF_DIB.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_DIB.0 as u32) {
            snapshot.dib = handle_bytes(handle);
        }
    }
    // 3. 文本 + HTML
    if IsClipboardFormatAvailable(CF_UNICODETEXT.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_UNICODETEXT.0 as u32) {
            snapshot.text = handle_bytes(handle);
            if IsClipboardFormatAvailable(html_format_id()).is_ok() {
                snapshot.html = GetClipboardData(html_format_id()).ok().and_then(|h| handle_bytes(h));
            }
        }
    }
    snapshot
}

unsafe fn handle_bytes(handle: HANDLE) -> Option<Vec<u8>> {
    use windows::Win32::Foundation::HGLOBAL;
    use windows::Win32::System::Memory::{GlobalLock, GlobalSize, GlobalUnlock};
    if handle.0.is_null() {
        return None;
    }
    let hglobal = HGLOBAL(handle.0);
    let size = GlobalSize(hglobal);
    if size == 0 {
        return None;
    }
    let ptr = GlobalLock(hglobal);
    if ptr.is_null() {
        return None;
    }
    let bytes = std::slice::from_raw_parts(ptr as *const u8, size);
    let out = bytes.to_vec();
    let _ = GlobalUnlock(hglobal);
    Some(out)
}

fn read_hdrop(handle: HANDLE) -> Vec<String> {
    let mut out = Vec::new();
    unsafe {
        let hdrop = HDROP(handle.0);
        let count = DragQueryFileW(hdrop, u32::MAX, None);
        for i in 0..count {
            if out.len() >= crate::clipboard::files_meta::MAX_FILES {
                log::info!("剪贴板文件数超过上限,仅记录前 {} 项", crate::clipboard::files_meta::MAX_FILES);
                break;
            }
            // len 是不含结尾 '\0' 的字符数。
            // 注意:windows crate 把 slice.len() 直接当作 Win32 的 cch 参数传入,
            // 而 cch 指的是「缓冲区能容纳的字符数(含结尾 '\0')」。若按 len 分配,
            // 缓冲区比需要少一个 u16,Win32 只能写入 len-1 个字符 ——
            // 表现为路径最后一个字符被吃掉(tsconfig.json → tsconfig.jso)。
            let len = DragQueryFileW(hdrop, i, None) as usize;
            if len == 0 {
                continue;
            }
            let mut buf = vec![0u16; len + 1];
            let n = DragQueryFileW(hdrop, i, Some(&mut buf)) as usize;
            if n > 0 {
                out.push(String::from_utf16_lossy(&buf[..n]));
            }
        }
    }
    out
}

unsafe fn register_png_format() -> u32 {
    let wide: Vec<u16> = "PNG\0".encode_utf16().collect();
    RegisterClipboardFormatW(PCWSTR(wide.as_ptr()))
}

unsafe fn html_format_id() -> u32 {
    let wide: Vec<u16> = "HTML Format\0".encode_utf16().collect();
    RegisterClipboardFormatW(PCWSTR(wide.as_ptr()))
}

// ---- 来源应用(简化版 SourceAppDetector;图标缓存待后续) ----

/// 剪贴板所有者进程的可执行路径(GetClipboardOwner → pid → 模块路径)。
pub unsafe fn owner_process_path() -> Option<String> {
    let hwnd = GetClipboardOwner().ok()?;
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == 0 {
        return None;
    }
    let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
    let mut buf = [0u16; 1024];
    let mut len = buf.len() as u32;
    let ok = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
    let _ = windows::Win32::Foundation::CloseHandle(process);
    if ok && len > 0 {
        Some(String::from_utf16_lossy(&buf[..len as usize]))
    } else {
        None
    }
}

/// 远程控制应用基础名单(与旧版 ClipboardAppFilter 内置名单一致)。
pub const BUILTIN_FILTERED_PROCESSES: &[&str] = &[
    "anydesk", "teamviewer", "rustdesk", "todesk", "sunloginclient", "向日葵",
    "mstsc", "mstscax", "parsec", "gameviewer", "ultraviewer", "splashtop",
];

/// 来源应用是否应被过滤(忽略其写入的剪贴板内容)。
pub fn should_filter_process(process_path: Option<&str>, custom: &[String]) -> bool {
    let Some(path) = process_path else {
        return false;
    };
    let file_name = path.rsplit(['\\', '/']).next().unwrap_or(path)
        .trim_end_matches(".exe")
        .to_lowercase();
    if BUILTIN_FILTERED_PROCESSES.iter().any(|p| file_name.contains(p)) {
        return true;
    }
    custom.iter().any(|c| {
        let c = c.trim().trim_end_matches(".exe").to_lowercase();
        !c.is_empty() && (file_name.contains(&c) || path.to_lowercase().contains(&c))
    })
}

/// 熔断判定:冷却期结束后放行一次探测;成功归零,失败重新进入冷却。
pub fn circuit_open() -> bool {
    if !CIRCUIT_OPEN.load(Ordering::SeqCst) {
        return false;
    }
    let retry_at = *CIRCUIT_RETRY_AT
        .lock()
        .unwrap_or_else(|p| p.into_inner());
    if retry_at.is_some_and(|t| Instant::now() >= t) {
        CIRCUIT_OPEN.store(false, Ordering::SeqCst);
        return false;
    }
    true
}

/// 熔断记账:OpenClipboard 失败 → 累加,连续超限暂停捕获;成功 → 归零。
pub fn note_open_result(ok: bool) {
    if ok {
        OPEN_FAILS.store(0, Ordering::SeqCst);
        CIRCUIT_OPEN.store(false, Ordering::SeqCst);
        *CIRCUIT_RETRY_AT
            .lock()
            .unwrap_or_else(|p| p.into_inner()) = None;
    } else {
        let n = OPEN_FAILS.fetch_add(1, Ordering::SeqCst) + 1;
        if n >= OPEN_FAIL_CIRCUIT as u32 {
            CIRCUIT_OPEN.store(true, Ordering::SeqCst);
            *CIRCUIT_RETRY_AT
                .lock()
                .unwrap_or_else(|p| p.into_inner()) =
                Some(Instant::now() + Duration::from_secs(3));
            let owner = unsafe { clipboard_owner_name() }
                .unwrap_or_else(|| "未知进程".into());
            log::warn!("剪贴板连续读取失败 {n} 次,暂停捕获 3 秒后自动重试;占用者: {owner}");
        }
    }
}

/// 查询当前持有剪贴板(OpenClipboard 后未 Close)的窗口所属进程路径。
/// 仅诊断用:OpenClipboard 失败时定位是哪个程序独占。
pub unsafe fn clipboard_owner_name() -> Option<String> {
    let hwnd = GetOpenClipboardWindow().ok()?;
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid == 0 {
        return Some("PID 未知".into());
    }
    let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
    let mut buf = [0u16; 1024];
    let mut len = buf.len() as u32;
    let ok = QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
    let _ = windows::Win32::Foundation::CloseHandle(process);
    if ok && len > 0 {
        Some(String::from_utf16_lossy(&buf[..len as usize]))
    } else {
        Some(format!("PID {pid}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overdue_capture_does_not_block_a_new_sequence() {
        let now = Instant::now();
        let mut tasks = Vec::new();
        assert!(reserve_capture(&mut tasks, 1, now));
        assert!(!reserve_capture(&mut tasks, 2, now + Duration::from_secs(1)));
        assert!(!reserve_capture(&mut tasks, 1, now + Duration::from_secs(16)));
        assert!(reserve_capture(&mut tasks, 2, now + Duration::from_secs(16)));
        tasks.retain(|task| task.seq != 1);
        assert_eq!(tasks[0].seq, 2);
        assert!(!reserve_capture(&mut tasks, 3, now + Duration::from_secs(17)));
        assert!(reserve_capture(&mut tasks, 3, now + Duration::from_secs(32)));
        assert!(reserve_capture(&mut tasks, 4, now + Duration::from_secs(48)));
        assert!(!reserve_capture(&mut tasks, 5, now + Duration::from_secs(64)));
    }

    #[test]
    fn filter_builtin() {
        assert!(should_filter_process(Some(r"C:\Program Files\AnyDesk\AnyDesk.exe"), &[]));
        assert!(should_filter_process(Some(r"C:\Windows\system32\mstsc.exe"), &[]));
        assert!(should_filter_process(Some("C:\\安装\\向日葵\\SunloginClient.exe"), &[]));
        assert!(!should_filter_process(Some(r"C:\Windows\notepad.exe"), &[]));
        assert!(!should_filter_process(None, &["AnyDesk".into()]));
    }

    #[test]
    fn filter_custom() {
        assert!(should_filter_process(
            Some(r"D:\Tools\mygame.exe"),
            &["MyGame".into()]
        ));
        assert!(should_filter_process(
            Some(r"D:\Tools\foo.exe"),
            &["foo.exe".into()]
        ));
        assert!(!should_filter_process(Some(r"D:\Tools\bar.exe"), &["mygame".into()]));
    }

    #[test]
    fn filter_matches_substring_in_path() {
        assert!(should_filter_process(Some(r"C:\x\Parcecp\parsec.exe"), &[]));
    }

    #[test]
    fn circuit_reopens_after_cooldown() {
        // 冷却结束后必须放行探测,否则 capture() 永远走不到 note_open_result(true)。
        for _ in 0..OPEN_FAIL_CIRCUIT {
            note_open_result(false);
        }
        assert!(circuit_open());
        *CIRCUIT_RETRY_AT
            .lock()
            .unwrap_or_else(|p| p.into_inner()) = Some(Instant::now());
        assert!(!circuit_open());
        note_open_result(true);
        assert!(!circuit_open());
        assert_eq!(OPEN_FAILS.load(Ordering::SeqCst), 0);
    }
}
