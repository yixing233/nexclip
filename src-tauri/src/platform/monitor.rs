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
use std::sync::OnceLock;

use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{HANDLE, HMODULE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::DataExchange::{
    AddClipboardFormatListener, CloseClipboard, GetClipboardData, GetClipboardOwner,
    GetClipboardSequenceNumber, IsClipboardFormatAvailable, OpenClipboard,
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

static MONITOR_TX: OnceLock<Sender<u32>> = OnceLock::new();
static LAST_CAPTURED_SEQ: AtomicU32 = AtomicU32::new(0);
/// 熔断标志:连续失败后暂停捕获,直到下一次序列号变化
static CIRCUIT_OPEN: AtomicBool = AtomicBool::new(false);
static OPEN_FAILS: AtomicU32 = AtomicU32::new(0);

const CLASS_NAME: &[u16] = &[
    b'N' as u16, b'e' as u16, b'x' as u16, b'C' as u16, b'l' as u16, b'i' as u16, b'p' as u16,
    b'M' as u16, b'o' as u16, b'n' as u16, 0,
];

/// 启动监听线程 + 捕获工作线程。`capture(seq)` 在序列号稳定后被调用
/// (工作线程内;打开剪贴板、读取内容都在该线程,绝不占用 GUI 线程)。
pub fn start<F>(capture: F) -> Result<(), String>
where
    F: Fn(u32) + Send + Sync + 'static,
{
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
    Ok(())
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

fn run_capture_worker(rx: std::sync::mpsc::Receiver<u32>, capture: std::sync::Arc<impl Fn(u32)>) {
    let mut last_scheduled = 0u32;
    loop {
        let seq = match rx.recv() {
            Ok(s) => s,
            Err(_) => return,
        };
        if seq == last_scheduled || seq == LAST_CAPTURED_SEQ.load(Ordering::SeqCst) {
            continue;
        }
        last_scheduled = seq;
        // 去抖:等待 150ms 后序列号未变才捕获(连续复制时只取最后一次)
        std::thread::sleep(std::time::Duration::from_millis(DEBOUNCE_MS));
        if unsafe { GetClipboardSequenceNumber() } != seq {
            continue;
        }
        LAST_CAPTURED_SEQ.store(seq, Ordering::SeqCst);
        capture(seq);
    }
}

// ---- 捕获实现(工作线程内调用) ----

/// 从剪贴板读取全部内容(调用线程必须允许阻塞;失败返回 Err,熔断计数由调用方处理)。
///
/// 返回:(文本, HTML 片段, 文件路径列表, PNG 字节)
pub unsafe fn read_clipboard() -> Result<(Option<String>, Option<String>, Vec<String>, Option<Vec<u8>>), ()> {
    let mut opened = false;
    for _ in 0..OPEN_FAIL_CIRCUIT {
        if OpenClipboard(None).is_ok() {
            opened = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    if !opened {
        return Err(());
    }
    let result = read_locked();
    let _ = CloseClipboard();
    result
}

unsafe fn read_locked() -> Result<(Option<String>, Option<String>, Vec<String>, Option<Vec<u8>>), ()> {
    // 1. 文件(CF_HDROP):仅元数据
    if IsClipboardFormatAvailable(CF_HDROP.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_HDROP.0 as u32) {
            let files = read_hdrop(HANDLE(handle.0));
            if !files.is_empty() {
                return Ok((None, None, files, None));
            }
        }
    }
    // 2. 图片:PNG 注册格式优先,回落 CF_DIB
    let png_fmt = register_png_format();
    if IsClipboardFormatAvailable(png_fmt).is_ok() {
        if let Ok(handle) = GetClipboardData(png_fmt) {
            if let Some(png) = handle_bytes(handle) {
                return Ok((None, None, Vec::new(), Some(png)));
            }
        }
    }
    if IsClipboardFormatAvailable(CF_DIB.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_DIB.0 as u32) {
            if let Some(dib) = handle_bytes(handle) {
                if let Ok(png) = super::writer::dib_to_png(&dib) {
                    return Ok((None, None, Vec::new(), Some(png)));
                }
            }
        }
    }
    // 3. 文本 + HTML
    if IsClipboardFormatAvailable(CF_UNICODETEXT.0 as u32).is_ok() {
        if let Ok(handle) = GetClipboardData(CF_UNICODETEXT.0 as u32) {
            if let Some(bytes) = handle_bytes(handle) {
                let utf16: Vec<u16> = bytes
                    .chunks_exact(2)
                    .map(|c| u16::from_le_bytes([c[0], c[1]]))
                    .collect();
                let text = String::from_utf16_lossy(&utf16);
                let text = text.trim_end_matches('\0').to_string();
                if !text.is_empty() {
                    let html = if IsClipboardFormatAvailable(html_format_id()).is_ok() {
                        GetClipboardData(html_format_id())
                            .ok()
                            .and_then(|h| handle_bytes(h))
                            .and_then(|bytes| {
                                String::from_utf16_lossy(
                                    &bytes
                                        .chunks_exact(2)
                                        .map(|c| u16::from_le_bytes([c[0], c[1]]))
                                        .collect::<Vec<u16>>(),
                                )
                                .strip_suffix('\0')
                                .map(String::from)
                                .or_else(|| {
                                    Some(String::from_utf16_lossy(
                                        &bytes
                                            .chunks_exact(2)
                                            .map(|c| u16::from_le_bytes([c[0], c[1]]))
                                            .collect::<Vec<u16>>(),
                                    ))
                                })
                            })
                            .and_then(|raw| crate::clipboard::html_format::get_static_fragment(&raw))
                    } else {
                        None
                    };
                    return Ok((Some(text), html, Vec::new(), None));
                }
            }
        }
    }
    Err(())
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
            let len = DragQueryFileW(hdrop, i, None) as usize;
            if len == 0 {
                continue;
            }
            let mut buf = vec![0u16; len];
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

/// 熔断记账:OpenClipboard 失败 → 累加,连续超限暂停捕获;成功 → 归零。
pub fn note_open_result(ok: bool) {
    if ok {
        OPEN_FAILS.store(0, Ordering::SeqCst);
        CIRCUIT_OPEN.store(false, Ordering::SeqCst);
    } else {
        let n = OPEN_FAILS.fetch_add(1, Ordering::SeqCst) + 1;
        if n >= OPEN_FAIL_CIRCUIT as u32 {
            CIRCUIT_OPEN.store(true, Ordering::SeqCst);
            log::warn!("剪贴板连续打开失败 {n} 次,暂停捕获直到内容变化(可能被远程工具独占)");
        }
    }
}

/// 熔断状态下是否跳过本次捕获。
pub fn circuit_open() -> bool {
    CIRCUIT_OPEN.load(Ordering::SeqCst)
}

#[cfg(test)]
mod tests {
    use super::*;

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
}
