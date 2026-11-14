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
    VK_CONTROL, VK_SHIFT, VK_V,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetForegroundWindow, GetGUIThreadInfo, GetWindowThreadProcessId, GUITHREADINFO,
};

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

/// 注入 Ctrl+V(Chromium/Electron 与普通应用统一;Shift+Insert 差异在设置里保留但
/// 精简版统一 Ctrl+V,与旧版"Chromium 固定 CtrlV"的结论一致)。
unsafe fn send_ctrl_v() {
    let none = windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS(0);
    let mut inputs = [
        key(VK_CONTROL, none),
        key(VK_V, none),
        key(VK_V, KEYEVENTF_KEYUP),
        key(VK_CONTROL, KEYEVENTF_KEYUP),
    ];
    SendInput(&mut inputs, std::mem::size_of::<INPUT>() as i32);
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

/// 执行注入:修饰键抬起 → 前台校验 → SendInput Ctrl+V。
/// 返回 Err 的场合调用方不要重试(旧版结论:重试会造成双粘贴)。
pub fn inject(target: &PasteTarget, keep_visible: bool) -> Result<(), String> {
    unsafe {
        if !keep_visible && !foreground_is_target(target) {
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
        send_ctrl_v();
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

    // 纯逻辑部分:等待超时行为(无窗口依赖,可在 CI 运行)
    #[test]
    fn wait_modifiers_timeout_immediate() {
        // 无真实按键按下时立即返回 true;即便按下,300ms 内也会超时返回 false 而非死等
        let start = std::time::Instant::now();
        let _ = unsafe { wait_modifiers_released(Duration::from_millis(50)) };
        assert!(start.elapsed() < Duration::from_millis(500), "等待不应死等");
    }
}
