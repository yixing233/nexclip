//! 全局热键:消息窗口线程 + RegisterHotKey,设置变更后可重注册。
//!
//! 与旧版 HotKeyService 一致:消息窗口(message-only)接收 WM_HOTKEY。
//! 线程模型:RegisterHotKey 必须在接收消息的线程上调用,
//! 因此重注册通过 WM_APP_RELOAD 投递回热键线程执行,不跨线程拿 HWND。

use std::sync::{Mutex, OnceLock};

use windows::core::PCWSTR;
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    RegisterHotKey, SendInput, UnregisterHotKey, INPUT, INPUT_KEYBOARD, KEYBDINPUT,
    KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, HOT_KEY_MODIFIERS, VK_CONTROL, VK_MENU, VK_SHIFT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, PostMessageW, RegisterClassW,
    TranslateMessage, MSG, WINDOW_EX_STYLE, WINDOW_STYLE, WM_APP, WM_HOTKEY,
    WNDCLASSW,
};

/// 热键动作(与旧版四个热键一一对应)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HotkeyAction {
    /// 呼出/隐藏剪贴板弹窗(Alt+V)
    Main,
    /// 打开设置(Alt+X)
    Settings,
    /// 打开最近复制的链接(Ctrl+Alt+O)
    OpenUrl,
    /// 置顶开关(Ctrl+Alt+T)
    Topmost,
    /// 截图(F1,可在设置中修改)
    Screenshot,
    /// 活屏快捷截图(Ctrl+Shift+F)
    ScreenshotScreen,
    /// 活动窗口快捷截图(Ctrl+Shift+W)
    ScreenshotWindow,
    /// 把剪贴板里的图片贴到屏幕(F3)
    PinFromClipboard,
}

/// 单个热键:修饰键 + 虚拟键码。
#[derive(Debug, Clone)]
pub struct HotkeySpec {
    pub action: HotkeyAction,
    /// MOD_ALT 0x1 / MOD_CONTROL 0x2 / MOD_SHIFT 0x4 / MOD_WIN 0x8
    pub modifiers: u32,
    pub vk: u32,
}

/// 解析 "Ctrl+Alt+V" 形式的热键串,返回 (修饰键, 虚拟键码)。
/// 解析失败返回 None(调用方回退默认键并告警)。
pub fn parse_hotkey(s: &str) -> Option<(u32, u32)> {
    const MOD_ALT: u32 = 0x0001;
    const MOD_CONTROL: u32 = 0x0002;
    const MOD_SHIFT: u32 = 0x0004;
    const MOD_WIN: u32 = 0x0008;

    let mut modifiers = 0u32;
    let mut vk: Option<u32> = None;
    for part in s.split('+') {
        let p = part.trim();
        if p.is_empty() {
            continue;
        }
        let upper = p.to_ascii_uppercase();
        match upper.as_str() {
            "CTRL" | "CONTROL" => modifiers |= MOD_CONTROL,
            "ALT" => modifiers |= MOD_ALT,
            "SHIFT" => modifiers |= MOD_SHIFT,
            "WIN" | "SUPER" | "META" => modifiers |= MOD_WIN,
            _ => {
                let code = if let Some(rest) = upper.strip_prefix('F') {
                    // F 后必须跟数字才是功能键(F1-F24);裸 "F" 是字母 F
                    if rest.is_empty() {
                        0x46 // VK_F(字母 F)
                    } else {
                        let n: u32 = rest.parse().ok()?;
                        if !(1..=24).contains(&n) {
                            return None;
                        }
                        0x70 + n - 1 // F1-F24
                    }
                } else if upper.len() == 1 {
                    let c = upper.chars().next().unwrap();
                    if c.is_ascii_digit() {
                        0x30 + c.to_digit(10)? // 0-9
                    } else if c.is_ascii_alphabetic() {
                        'A' as u32 + (c as u32 - 'A' as u32) // A-Z
                    } else {
                        return None;
                    }
                } else {
                    return None;
                };
                if vk.is_some() {
                    return None; // 出现两个非修饰键
                }
                vk = Some(code);
            }
        }
    }
    let vk = vk?;
    Some((modifiers, vk))
}

/// 热键 id:按动作枚举值(稳定,重注册后不变)。
fn spec_id(spec: &HotkeySpec) -> i32 {
    match spec.action {
        HotkeyAction::Main => 1,
        HotkeyAction::Settings => 2,
        HotkeyAction::OpenUrl => 3,
        HotkeyAction::Topmost => 4,
        HotkeyAction::Screenshot => 5,
        HotkeyAction::ScreenshotScreen => 6,
        HotkeyAction::ScreenshotWindow => 7,
        HotkeyAction::PinFromClipboard => 8,
    }
}

static SPECS: Mutex<Vec<HotkeySpec>> = Mutex::new(Vec::new());
static HANDLER: OnceLock<Box<dyn Fn(HotkeyAction) + Send + Sync>> = OnceLock::new();
static HOTKEY_HWND: OnceLock<usize> = OnceLock::new();
/// 待重注册的热键集合(reload → 消息循环线程取出执行)
static PENDING_RELOAD: Mutex<Option<Vec<HotkeySpec>>> = Mutex::new(None);
const WM_APP_RELOAD: u32 = WM_APP + 1;

const CLASS_NAME: &[u16] = &[
    b'N' as u16, b'e' as u16, b'x' as u16, b'C' as u16, b'l' as u16, b'i' as u16, b'p' as u16,
    b'H' as u16, b'K' as u16, 0,
];

/// 启动热键线程并注册初始热键集合。`handler` 在热键触发时被调用(热键线程内)。
pub fn start<F>(specs: Vec<HotkeySpec>, handler: F) -> Result<(), String>
where
    F: Fn(HotkeyAction) + Send + Sync + 'static,
{
    let _ = HANDLER.set(Box::new(handler));
    std::thread::Builder::new()
        .name("hotkey".into())
        .spawn(move || unsafe { run_thread(specs) })
        .map_err(|e| e.to_string())?;
    Ok(())
}

unsafe fn run_thread(specs: Vec<HotkeySpec>) {
    let hinstance: HINSTANCE = GetModuleHandleW(None).unwrap_or_default().into();
    let class_name = PCWSTR(CLASS_NAME.as_ptr());

    let wc = WNDCLASSW {
        lpfnWndProc: Some(wnd_proc),
        hInstance: hinstance,
        lpszClassName: class_name,
        ..Default::default()
    };
    RegisterClassW(&wc);

    // 消息窗口:不显示、不进任务栏,仅接收 WM_HOTKEY
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
        log::error!("热键消息窗口创建失败");
        return;
    }

    register_all(hwnd, &specs);
    let _ = HOTKEY_HWND.set(hwnd.0 as usize);

    // 阻塞消息循环
    let mut msg = MSG::default();
    while GetMessageW(&mut msg, HWND::default(), 0, 0).as_bool() {
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
}

unsafe fn register_all(hwnd: HWND, specs: &[HotkeySpec]) {
    // 先注销全部再注册(设置页可能改了同一个键)
    for id in 1..=4 {
        let _ = UnregisterHotKey(hwnd, id);
    }
    let mut guard = SPECS.lock().unwrap_or_else(|p| p.into_inner());
    *guard = Vec::new();
    for spec in specs {
        let id = spec_id(spec);
        const MOD_NOREPEAT: HOT_KEY_MODIFIERS = HOT_KEY_MODIFIERS(0x4000);
        match RegisterHotKey(hwnd, id, HOT_KEY_MODIFIERS(spec.modifiers) | MOD_NOREPEAT, spec.vk) {
            Ok(()) => {
                log::info!("热键注册成功: {:?}", spec.action);
                guard.push(spec.clone());
            }
            Err(e) => log::warn!("热键注册失败({:?}): {e}", spec.action),
        }
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if msg == WM_APP_RELOAD {
        if let Some(new_specs) = PENDING_RELOAD.lock().unwrap_or_else(|p| p.into_inner()).take() {
            register_all(hwnd, &new_specs);
        }
        return LRESULT(0);
    }
    if msg == WM_HOTKEY {
        let id = wparam.0 as i32;
        let action = SPECS
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .iter()
            .find(|s| spec_id(s) == id)
            .map(|s| s.action);
        if let (Some(action), Some(handler)) = (action, HANDLER.get()) {
            handler(action);
        }
        return LRESULT(0);
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
}

/// 设置变更后重注册热键(向热键线程投递 WM_APP_RELOAD;注册必须在该线程执行)。
pub fn reload(specs: Vec<HotkeySpec>) {
    *PENDING_RELOAD.lock().unwrap_or_else(|p| p.into_inner()) = Some(specs);
    if let Some(&hwnd) = HOTKEY_HWND.get() {
        unsafe {
            let _ = PostMessageW(
                HWND(hwnd as *mut core::ffi::c_void),
                WM_APP_RELOAD,
                WPARAM(0),
                LPARAM(0),
            );
        }
    }
}

/// 全部修饰键抬起(粘贴前调用,避免 Ctrl+V 与按住的修饰键冲突)。
pub unsafe fn all_keys_up() {
    let mut inputs = [
        key_input(VK_CONTROL, KEYEVENTF_KEYUP),
        key_input(VK_MENU, KEYEVENTF_KEYUP),
        key_input(VK_SHIFT, KEYEVENTF_KEYUP),
    ];
    SendInput(&mut inputs, std::mem::size_of::<INPUT>() as i32);
}

unsafe fn key_input(vk: windows::Win32::UI::Input::KeyboardAndMouse::VIRTUAL_KEY, flags: KEYBD_EVENT_FLAGS) -> INPUT {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_basic() {
        assert_eq!(parse_hotkey("Alt+V"), Some((0x0001, 'V' as u32)));
        assert_eq!(parse_hotkey("Ctrl+Alt+O"), Some((0x0003, 'O' as u32)));
        assert_eq!(parse_hotkey("Ctrl+Shift+T"), Some((0x0006, 'T' as u32)));
        assert_eq!(parse_hotkey("Win+D"), Some((0x0008, 'D' as u32)));
        assert_eq!(parse_hotkey("F5"), Some((0, 0x74)));
        assert_eq!(parse_hotkey("Ctrl+F12"), Some((0x0002, 0x7B)));
        assert_eq!(parse_hotkey("Alt+0"), Some((0x0001, 0x30)));
        // 大小写不敏感
        assert_eq!(parse_hotkey("alt+v"), parse_hotkey("ALT+V"));
    }

    #[test]
    fn parse_rejects_garbage() {
        assert_eq!(parse_hotkey(""), None);
        assert_eq!(parse_hotkey("Alt"), None);
        assert_eq!(parse_hotkey("Ctrl+Alt"), None);
        assert_eq!(parse_hotkey("Ctrl+AB"), None);
        assert_eq!(parse_hotkey("Alt+V+X"), None);
        assert_eq!(parse_hotkey("Alt+F99"), None);
        assert_eq!(parse_hotkey("Ctrl+中"), None);
    }
}
