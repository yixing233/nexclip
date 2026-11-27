//! 可见顶层窗口枚举(供截图覆盖窗悬停高亮与点击自动选区)。
//!
//! 过滤:可见、非最小化、根窗口、非 DWM cloaked;
//! 矩形优先 DWMWA_EXTENDED_FRAME_BOUNDS(比 GetWindowRect 更贴合视觉边界)。

use std::collections::HashMap;
use std::sync::Mutex;

use windows::Win32::Foundation::{BOOL, HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::{OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetAncestor, GetClassNameW, GetWindowTextW, GetWindowThreadProcessId,
    GetWindowRect, IsIconic, IsWindowVisible, WindowFromPoint, GA_ROOT,
};

/// 检测到的窗口矩形(虚拟桌面物理坐标)。
#[derive(Debug, Clone)]
pub struct WindowRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub title: String,
    pub app: String,
}

/// pid → 进程名缓存(避免同一进程多窗口重复 OpenProcess;每次枚举复用)
static PROC_NAME_CACHE: Mutex<Option<HashMap<u32, String>>> = Mutex::new(None);

fn cached_process_name(pid: u32) -> String {
    let mut guard = PROC_NAME_CACHE.lock().unwrap_or_else(|p| p.into_inner());
    let map = guard.get_or_insert_with(HashMap::new);
    if let Some(name) = map.get(&pid) {
        return name.clone();
    }
    let name = process_name_by_pid(pid);
    map.insert(pid, name.clone());
    name
}

/// 清空缓存(每次截图会话开始时调用,避免进程重启后陈旧)
pub fn clear_process_cache() {
    *PROC_NAME_CACHE.lock().unwrap_or_else(|p| p.into_inner()) = None;
}

/// 屏幕点所在窗口(实时 O(1) 查询,替代预枚举,悬停用)。
pub fn window_at_point(x: i32, y: i32) -> Option<WindowRect> {
    unsafe {
        let hwnd = WindowFromPoint(POINT { x, y });
        if hwnd.is_invalid() {
            return None;
        }
        let root = GetAncestor(hwnd, GA_ROOT);
        let target = if root.is_invalid() { hwnd } else { root };
        // 桌面壳过滤(Progman/WorkerW)
        let mut cls = [0u16; 64];
        let cn = GetClassNameW(target, &mut cls);
        let class = String::from_utf16_lossy(&cls[..cn.min(64) as usize]);
        if matches!(class.as_str(), "Progman" | "WorkerW") {
            return None;
        }
        if let Some((_, rect, title)) = collect(target) {
            let mut pid = 0u32;
            GetWindowThreadProcessId(target, Some(&mut pid));
            return Some(WindowRect {
                x: rect.left,
                y: rect.top,
                width: rect.right - rect.left,
                height: rect.bottom - rect.top,
                title,
                app: cached_process_name(pid),
            });
        }
        None
    }
}

pub fn visible_windows() -> Vec<WindowRect> {
    let mut out: Vec<WindowRect> = Vec::new();
    let mut result: Vec<(HWND, RECT, String)> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(enum_proc), LPARAM(&mut result as *mut _ as isize));
    }
    for (hwnd, rect, title) in result {
        if title.is_empty() {
            continue;
        }
        let mut pid = 0u32;
        unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid)); }
        out.push(WindowRect {
            x: rect.left,
            y: rect.top,
            width: rect.right - rect.left,
            height: rect.bottom - rect.top,
            title,
            app: cached_process_name(pid),
        });
    }
    // 保持 EnumWindows 的 Z 序(最前窗口在前):前端命中取第一个,
    // 悬停小窗口时优先命中精确窗口而非覆盖它的大窗口。
    out
}

/// EnumWindows 回调:收集 (hwnd, rect, title) 三元组,过滤后置阶段处理。
unsafe fn collect(hwnd: HWND) -> Option<(HWND, RECT, String)> {
    if !IsWindowVisible(hwnd).as_bool() || IsIconic(hwnd).as_bool() {
        return None;
    }
    // 根窗口(排除 WS_CHILD)
    let root = GetAncestor(hwnd, GA_ROOT);
    if root != hwnd {
        return None;
    }
    // 桌面壳窗口过滤:Progman/WorkerW 是"桌面"本身,不排除会让悬停高亮铺满全屏
    let mut cls = [0u16; 64];
    let cn = GetClassNameW(hwnd, &mut cls);
    let class = String::from_utf16_lossy(&cls[..cn.min(64) as usize]);
    if matches!(class.as_str(), "Progman" | "WorkerW") {
        return None;
    }
    // DWM cloaked(UWP 挂起窗口等)过滤
    let mut cloaked: u32 = 0;
    if DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut u32 as *mut _, 4).is_ok()
        && cloaked != 0
    {
        return None;
    }
    // 矩形:优先 DWM 扩展边界
    let mut rect = RECT::default();
    let got_dwm = DwmGetWindowAttribute(
        hwnd,
        DWMWA_EXTENDED_FRAME_BOUNDS,
        &mut rect as *mut RECT as *mut _,
        std::mem::size_of::<RECT>() as u32,
    )
    .is_ok();
    if !got_dwm {
        let _ = GetWindowRect(hwnd, &mut rect);
    }
    let w = rect.right - rect.left;
    let h = rect.bottom - rect.top;
    if w < 40 || h < 40 {
        return None;
    }
    // 标题
    let mut buf = [0u16; 256];
    let n = GetWindowTextW(hwnd, &mut buf);
    let title = String::from_utf16_lossy(&buf[..n.min(256) as usize]);
    Some((hwnd, rect, title))
}

unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
    let list = &mut *(lparam.0 as *mut Vec<(HWND, RECT, String)>);
    if let Some(t) = collect(hwnd) {
        list.push(t);
    }
    BOOL(1)
}

/// 进程友好名(QueryFullProcessImageNameW 文件名部分)。
fn process_name_by_pid(pid: u32) -> String {
    unsafe {
        if pid == 0 {
            return String::new();
        }
        let Ok(handle) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) else {
            return String::new();
        };
        let mut buf = [0u16; 1024];
        let mut len: u32 = 1024;
        let name = if windows::Win32::System::Threading::QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            windows::core::PWSTR(buf.as_mut_ptr()),
            &mut len,
        )
        .is_ok()
        {
            let full = String::from_utf16_lossy(&buf[..len as usize]);
            full.rsplit(['\\', '/']).next().unwrap_or("").to_string()
        } else {
            String::new()
        };
        let _ = windows::Win32::Foundation::CloseHandle(handle);
        name
    }
}

/// 模块句柄(钩子等模块级资源用)。
pub fn module_handle() -> windows::Win32::Foundation::HINSTANCE {
    unsafe { GetModuleHandleW(None).unwrap_or_default().into() }
}
