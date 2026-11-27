//! 粘贴回注引擎。
//!
//! 移植自旧版 ClipboardWindow 的 HideAndInjectPasteAsync 核心路径
//! (裁剪到无 UI 依赖的三步原子流程,保留关键约束注释):
//! 1. 热键触发瞬间已在前台应用 → 先记录目标窗口与焦点(GetGUIThreadInfo);
//! 2. 写入剪贴板(walk writer 专用线程),隐藏 NexClip 窗口让焦点自然回落;
//! 3. AllKeysUp + 等待修饰键抬起 + 校验前台仍是目标窗口 → SendInput 原子注入 Ctrl+V。
//!
//! 旧版约束(不得违背):
//! - Chromium/Electron 固定 Ctrl+V(Shift+Insert 在部分版本不生效);
//! - 注入前必须等待修饰键松开,否则 Ctrl+V 变成修饰键组合被吞;
//! - 不要对未响应线程 AttachThreadInput(IME/TSF 死锁风险),本精简版不使用;
//! - 置顶模式(NexClip 保持可见)不强制 SetForegroundWindow,避免 TSF 候选窗问题。

use std::time::{Duration, Instant};

use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, SendInput, INPUT, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP,
    VK_CONTROL, VK_INSERT, VK_SHIFT, VK_V,
};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, GetClassNameW, GetForegroundWindow, GetGUIThreadInfo, GetWindowThreadProcessId,
    IsIconic, SendMessageTimeoutW, SetForegroundWindow, ShowWindow, GUITHREADINFO,
    SMTO_ABORTIFHUNG, SMTO_BLOCK, SW_RESTORE,
};
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};

/// 粘贴目标:热键触发瞬间记录的前台窗口与 Win32 焦点控件。
/// HWND 以 isize 存储(raw pointer 不是 Send,跨线程状态用整型)。
#[derive(Debug, Clone)]
pub struct PasteTarget {
    pub hwnd: isize,
    pub hwnd_focus: isize,
}

impl PasteTarget {
    fn hwnd(&self) -> HWND {
        HWND(self.hwnd as *mut core::ffi::c_void)
    }
    fn hwnd_focus(&self) -> HWND {
        HWND(self.hwnd_focus as *mut core::ffi::c_void)
    }
}

/// 记录当前前台窗口为粘贴目标(应在呼出弹窗的瞬间调用)。
pub fn capture_target() -> Option<PasteTarget> {
    unsafe {
        let hwnd = GetForegroundWindow();
        if hwnd.is_invalid() {
            return None;
        }
        let thread_id = GetWindowThreadProcessId(hwnd, None);
        let mut info = GUITHREADINFO {
            cbSize: std::mem::size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        let focus = if GetGUIThreadInfo(thread_id, &mut info).is_ok() && !info.hwndFocus.is_invalid() {
            info.hwndFocus
        } else {
            hwnd
        };
        Some(PasteTarget {
            hwnd: hwnd.0 as isize,
            hwnd_focus: focus.0 as isize,
        })
    }
}

/// 按设置注入粘贴键:Ctrl+V 或 Shift+Insert。
unsafe fn send_paste(paste_key: &str) {
    let none = windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS(0);
    let mut inputs = if paste_key == "ShiftInsert" {
        [
            key(VK_SHIFT, none),
            key(VK_INSERT, none),
            key(VK_INSERT, KEYEVENTF_KEYUP),
            key(VK_SHIFT, KEYEVENTF_KEYUP),
        ]
    } else {
        [
            key(VK_CONTROL, none),
            key(VK_V, none),
            key(VK_V, KEYEVENTF_KEYUP),
            key(VK_CONTROL, KEYEVENTF_KEYUP),
        ]
    };
    SendInput(&mut inputs, std::mem::size_of::<INPUT>() as i32);
}

fn resolve_paste_key<'a>(configured: &'a str, class_names: &[String]) -> &'a str {
    if class_names.iter().any(|name| {
        let name = name.to_ascii_lowercase();
        name.starts_with("chrome_") || name.starts_with("internet explorer_server")
    }) { "CtrlV" } else { configured }
}

unsafe fn target_paste_key<'a>(target: &PasteTarget, configured: &'a str) -> &'a str {
    let names: Vec<_> = [target.hwnd(), target.hwnd_focus()].into_iter().map(|hwnd| {
        let mut buf = [0u16; 256];
        let len = GetClassNameW(hwnd, &mut buf) as usize;
        String::from_utf16_lossy(&buf[..len])
    }).collect();
    resolve_paste_key(configured, &names)
}

unsafe fn key(vk: windows::Win32::UI::Input::KeyboardAndMouse::VIRTUAL_KEY, flags: windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: windows::Win32::UI::Input::KeyboardAndMouse::INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk,
                wScan: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

/// 等待修饰键全部抬起(最多 300ms,5ms 轮询,与旧版 WaitToGiveUp 一致)。
/// 游戏按住 WASD/Shift 时直接注入 Ctrl+V 会被吞,必须先松开。
pub unsafe fn wait_modifiers_released(timeout: Duration) -> bool {
    use windows::Win32::UI::Input::KeyboardAndMouse::VK_MENU;
    let deadline = Instant::now() + timeout;
    loop {
        let ctrl = (GetAsyncKeyState(VK_CONTROL.0 as i32) as u16) & 0x8000 != 0;
        let alt = (GetAsyncKeyState(VK_MENU.0 as i32) as u16) & 0x8000 != 0;
        let shift = (GetAsyncKeyState(VK_SHIFT.0 as i32) as u16) & 0x8000 != 0;
        if !ctrl && !alt && !shift {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        std::thread::sleep(Duration::from_millis(5));
    }
}

/// 校验前台窗口仍为目标(用户在弹窗期间切走了就不注入)。
pub unsafe fn foreground_is_target(target: &PasteTarget) -> bool {
    let fg = GetForegroundWindow();
    if fg == target.hwnd() {
        return true;
    }
    // 同根窗口(部分应用点击后前台窗口对象变化但属同一顶层)
    fg == target.hwnd_focus()
}

/// 判断指定线程是否仍在处理消息(带 150ms 超时 + SMTO_ABORTIFHUNG 的空消息探测)。
/// 线程无响应时立即返回 false —— 这是安全 AttachThreadInput 的前置条件。
unsafe fn is_thread_responsive(thread_id: u32) -> bool {
    if thread_id == 0 {
        return false;
    }
    let mut info = GUITHREADINFO {
        cbSize: std::mem::size_of::<GUITHREADINFO>() as u32,
        ..Default::default()
    };
    if GetGUIThreadInfo(thread_id, &mut info).is_err() {
        return false;
    }
    // 空闲线程(GUI 结构里没有任何活动窗口/焦点)通常是已退出或不可交互,不应附加
    let probe = if !info.hwndActive.is_invalid() {
        info.hwndActive
    } else if !info.hwndFocus.is_invalid() {
        info.hwndFocus
    } else {
        info.hwndCaret
    };
    if probe.is_invalid() {
        return false;
    }
    let mut result = 0usize;
    SendMessageTimeoutW(
        probe,
        0, // WM_NULL
        None,
        None,
        SMTO_ABORTIFHUNG | SMTO_BLOCK,
        150,
        Some(&mut result),
    )
    .0 != 0
}

/// 显式激活目标窗口(单次 try 版本)。
///
/// AttachThreadInput 会把本线程输入队列挂到目标线程:目标一旦不泵消息,本线程
/// 也不再泵消息 —— 而消息泵是本进程响应别人粘贴请求的唯一通道。所以附加前必须
/// 先确认目标线程还活着;无响应就放弃附加,只做裸 SetForegroundWindow
/// (成功率低一些,但绝不会把调用方拖死)。
unsafe fn activate_window(hwnd: HWND) -> bool {
    if hwnd.is_invalid() {
        return false;
    }
    if IsIconic(hwnd).as_bool() {
        let _ = ShowWindow(hwnd, SW_RESTORE);
    }
    let foreground = GetForegroundWindow();
    let foreground_thread = if !foreground.is_invalid() {
        GetWindowThreadProcessId(foreground, None)
    } else {
        0
    };
    let current_thread = GetCurrentThreadId();
    let can_attach = foreground_thread != 0
        && foreground_thread != current_thread
        && is_thread_responsive(foreground_thread);
    let attached = can_attach && AttachThreadInput(current_thread, foreground_thread, true).as_bool();
    let activated = SetForegroundWindow(hwnd).as_bool();
    if activated {
        let _ = BringWindowToTop(hwnd);
    }
    if attached {
        let _ = AttachThreadInput(current_thread, foreground_thread, false);
    }
    activated
}

/// 等待目标成为前台并要求稳定 150ms(旧版 WaitForForegroundAsync 语义)。
unsafe fn wait_for_foreground(target: &PasteTarget, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    let mut stable_since: Option<Instant> = None;
    while Instant::now() < deadline {
        if foreground_is_target(target) {
            let since = *stable_since.get_or_insert_with(Instant::now);
            if since.elapsed() >= Duration::from_millis(150) {
                return true;
            }
        } else {
            stable_since = None;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    false
}

/// 先等自然恢复(250ms),失败才显式激活再等(700ms)。
unsafe fn wait_and_activate(target: &PasteTarget, timeout: Duration) -> bool {
    if wait_for_foreground(target, Duration::from_millis(250)) {
        return true;
    }
    activate_window(target.hwnd());
    wait_for_foreground(target, timeout)
}

/// 执行注入:修饰键抬起 → 前台校验 → SendInput Ctrl+V。
/// 返回 Err 的场合调用方不要重试(旧版结论:重试会造成双粘贴)。
pub fn inject(target: &PasteTarget, keep_visible: bool, paste_key: &str) -> Result<(), String> {
    unsafe {
        if keep_visible {
            // 置顶模式:窗口全程可见、不隐藏(隐藏会造成闪烁)。但**必须**让目标
            // 重新成为前台,否则 SendInput 打进的是 NexClip 自己的窗口(焦点在此),
            // 表现为"置顶时双击粘贴毫无反应"。
            // 这里不做 UIA 强制回焦(会把目标 TSF 焦点文档与 Win32 焦点解耦,
            // 导致输入法候选栏飞到屏幕左上角,见旧版 8a9c65f 的结论)。
            if !wait_and_activate(target, Duration::from_millis(700)) {
                return Err("置顶模式:目标窗口未成为前台,放弃注入".into());
            }
        } else if !foreground_is_target(target) {
            // 等待焦点回落(窗口隐藏后系统自动恢复,最多 500ms)
            let deadline = Instant::now() + Duration::from_millis(500);
            while Instant::now() < deadline {
                if foreground_is_target(target) {
                    break;
                }
                std::thread::sleep(Duration::from_millis(15));
            }
            if !foreground_is_target(target) {
                return Err("前台已切走,放弃注入".into());
            }
        }
        // 注入前清一次残留按键
        super::hotkey::all_keys_up();
        // 等待修饰键抬起(游戏中按住 WASD 时会超时,超时后仍注入一次,与旧版行为一致)
        let _ = wait_modifiers_released(Duration::from_millis(300));
        send_paste(target_paste_key(target, paste_key));
        Ok(())
    }
}

/// 目标窗口是否仍然存活(用户可能关掉了原窗口)。
pub unsafe fn target_alive(target: &PasteTarget) -> bool {
    let hwnd = target.hwnd();
    if hwnd.is_invalid() {
        return false;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    pid != 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chromium_overrides_shift_insert() {
        assert_eq!(resolve_paste_key("ShiftInsert", &["Chrome_WidgetWin_1".into()]), "CtrlV");
        assert_eq!(resolve_paste_key("ShiftInsert", &["Chrome_RenderWidgetHostHWND".into()]), "CtrlV");
        assert_eq!(resolve_paste_key("ShiftInsert", &["Notepad".into()]), "ShiftInsert");
    }

    // 纯逻辑部分:等待超时行为(无窗口依赖,可在 CI 运行)
    #[test]
    fn wait_modifiers_timeout_immediate() {
        // 无真实按键按下时立即返回 true;即便按下,300ms 内也会超时返回 false 而非死等
        let start = std::time::Instant::now();
        let _ = unsafe { wait_modifiers_released(Duration::from_millis(50)) };
        assert!(start.elapsed() < Duration::from_millis(500), "等待不应死等");
    }
}
