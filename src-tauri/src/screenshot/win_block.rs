//! 截图会话期间屏蔽 Win 键(低级键盘钩子),防止选区中按 Win 弹出开始菜单抢焦点。
//!
//! LL 钩子要求安装线程有消息泵,因此专用线程安装 + GetMessage 循环;
//! 停止时向该线程 PostThreadMessage(WM_QUIT)。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

use windows::Win32::Foundation::{BOOL, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{GetCurrentProcessId, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::VK_LWIN;
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetForegroundWindow, GetMessageW, GetWindowTextLengthW, PostThreadMessageW,
    SetWindowsHookExW, UnhookWindowsHookEx, HHOOK, HOOKPROC, KBDLLHOOKSTRUCT, MSG,
    WH_KEYBOARD_LL, WM_KEYDOWN, WM_SYSKEYDOWN, WM_QUIT,
};

static ACTIVE: AtomicBool = AtomicBool::new(false);
/// HHOOK 非线程安全,以 usize 存储句柄。
static HOOK: std::sync::Mutex<usize> = std::sync::Mutex::new(0);
static THREAD_ID: std::sync::Mutex<u32> = std::sync::Mutex::new(0);
/// 会话中 Esc 逃生回调(LL 钩子线程调用,实现方须自行开线程防阻塞钩子)。
static CANCEL_CB: OnceLock<Box<dyn Fn() + Send + Sync>> = OnceLock::new();

/// 会话期间安装 Win 键屏蔽钩子 + 全局 Esc 逃生(已激活则忽略)。
pub fn start_block(cancel: impl Fn() + Send + Sync + 'static) {
    if ACTIVE.swap(true, Ordering::SeqCst) {
        return;
    }
    let _ = CANCEL_CB.set(Box::new(cancel));
    std::thread::spawn(|| unsafe {
        let hinst = GetModuleHandleW(None).unwrap_or_default();
        let hookproc: HOOKPROC = Some(hook_proc);
        let hook = SetWindowsHookExW(WH_KEYBOARD_LL, hookproc, hinst, 0);
        match hook {
            Ok(h) => {
                *HOOK.lock().unwrap_or_else(|p| p.into_inner()) = h.0 as usize;
                *THREAD_ID.lock().unwrap_or_else(|p| p.into_inner()) = GetCurrentThreadId();
                let mut msg = MSG::default();
                while GetMessageW(&mut msg, None, 0, 0).as_bool() {}
            }
            Err(e) => {
                log::error!("Win 键屏蔽钩子安装失败: {e}");
                ACTIVE.store(false, Ordering::SeqCst);
            }
        }
    });
}

/// 会话结束卸载钩子。
pub fn stop_block() {
    if !ACTIVE.swap(false, Ordering::SeqCst) {
        return;
    }
    unsafe {
        let handle = *HOOK.lock().unwrap_or_else(|p| p.into_inner());
        if handle != 0 {
            let _ = UnhookWindowsHookEx(HHOOK(handle as *mut _));
        }
        *HOOK.lock().unwrap_or_else(|p| p.into_inner()) = 0;
        let tid = *THREAD_ID.lock().unwrap_or_else(|p| p.into_inner());
        if tid != 0 {
            let _ = PostThreadMessageW(tid, WM_QUIT, WPARAM(0), LPARAM(0));
        }
        *THREAD_ID.lock().unwrap_or_else(|p| p.into_inner()) = 0;
    }
}

/// 前台窗口是否属于本进程且为截图覆盖窗(title 为空)。
/// 覆盖窗内 Esc 由前端处理(回选择态),不吞;否则 Esc 作为全局逃生。
unsafe fn foreground_is_overlay() -> bool {
    let hwnd = GetForegroundWindow();
    if hwnd.is_invalid() {
        return false;
    }
    let mut pid: u32 = 0;
    windows::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId(hwnd, Some(&mut pid));
    if pid != GetCurrentProcessId() {
        return false;
    }
    GetWindowTextLengthW(hwnd) == 0
}

unsafe extern "system" fn hook_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code >= 0 && ACTIVE.load(Ordering::SeqCst) {
        let msg_code = wparam.0 as u32;
        if msg_code == WM_KEYDOWN || msg_code == WM_SYSKEYDOWN {
            let kb = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
            // 屏蔽左右 Win 键按下(VK_LWIN=0x5B / VK_RWIN=0x5C)
            if kb.vkCode == VK_LWIN.0 as u32 || kb.vkCode == VK_LWIN.0 as u32 + 1 {
                return LRESULT(1);
            }
            // 全局 Esc 逃生:焦点不在覆盖窗(无法被前端处理)时强制取消会话,
            // 不依赖键盘焦点 —— 这是"截图无法退出"的系统级最后退路。
            if kb.vkCode == 0x1B && !foreground_is_overlay() {
                if let Some(cb) = CANCEL_CB.get() {
                    std::thread::spawn(cb); // 钩子线程内严禁阻塞,取消异步执行
                }
                return LRESULT(1); // 吞掉 Esc,不传给原应用
            }
        }
    }
    CallNextHookEx(None, code, wparam, lparam)
}

// 引用 BOOL 避免 unused import(保留类型显式性)
#[allow(dead_code)]
const _: BOOL = BOOL(1);
