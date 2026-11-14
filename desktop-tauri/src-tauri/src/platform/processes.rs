//! 活跃用户进程枚举:供设置页"自定义过滤进程"下拉候选。
//!
//! 过滤策略:
//! 1. 只保留至少拥有一个可见顶级窗口的进程(用户实际在交互的);
//! 2. 排除系统/服务类进程(名称黑名单 + 系统目录下的进程)。
//!
//! 每个候选附带应用友好名(exe 版本信息 FileDescription,退化到产品名/文件名)
//! 与应用图标(复用 app_icon.rs 提取 + 缓存)。

use std::collections::BTreeSet;

use windows::core::PWSTR;
use windows::Win32::Foundation::{BOOL, HWND, LPARAM};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
    TH32CS_SNAPPROCESS,
};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetWindowThreadProcessId, IsWindowVisible, GetWindowTextLengthW,
};

/// 系统进程黑名单(不区分大小写,不含 .exe 也匹配)。
const SYSTEM_PROCESSES: &[&str] = &[
    "system", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
    "dwm", "fontdrvhost", "conhost", "wudfhost", "spoolsv", "taskhostw", "explorerframe",
    "sihost", "ctfmon", "searchhost", "startmenuexperiencehost", "textinputhost", "shellexperiencehost",
    "runtimebroker", "applicationframehost", "lockapp", "logonui", "audiodg", "dialogic",
    "memcompression", "registry", "securityhealthservice", "securityhealthsystray", "wermgr",
    "werfault", "dllhost", "cmd", "openconsole", "windowsterminal",
    "msmpeng", "nissrv", "scheduled", "smartscreen", "explorer",
];

/// 判定是否系统/后台进程:黑名单命中,或可执行文件位于 Windows 目录。
fn is_system_process(name: &str, exe_path: Option<&str>) -> bool {
    let lower = name.to_ascii_lowercase();
    let stem = lower.strip_suffix(".exe").unwrap_or(&lower);
    if SYSTEM_PROCESSES.iter().any(|s| *s == stem) {
        return true;
    }
    if let Some(p) = exe_path {
        let pl = p.to_ascii_lowercase();
        // 系统目录下的进程(svchost 类宿主已进黑名单)
        if pl.starts_with("c:\\windows\\") {
            return true;
        }
    }
    false
}

/// PID → 可执行文件完整路径(失败返回 None,权限不足/系统进程常见)。
fn pid_exe_path(pid: u32) -> Option<String> {
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 1024];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(
            process,
            PROCESS_NAME_WIN32,
            PWSTR(buf.as_mut_ptr()),
            &mut len,
        )
        .is_ok();
        let _ = windows::Win32::Foundation::CloseHandle(process);
        if ok && len > 0 {
            Some(String::from_utf16_lossy(&buf[..len as usize]))
        } else {
            None
        }
    }
}

/// 从 exe 版本信息取友好显示名:FileDescription → ProductName(各语言页)。
fn friendly_name(exe_path: &str) -> Option<String> {
    use windows::Win32::Storage::FileSystem::{
        GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW,
    };
    use windows::core::PCWSTR;

    let wide: Vec<u16> = exe_path.encode_utf16().chain([0]).collect();
    unsafe {
        let mut handle = 0u32;
        let size = GetFileVersionInfoSizeW(PCWSTR(wide.as_ptr()), Some(&mut handle));
        if size == 0 {
            return None;
        }
        let mut data = vec![0u8; size as usize];
        if GetFileVersionInfoW(
            PCWSTR(wide.as_ptr()),
            0,
            size,
            data.as_mut_ptr() as *mut core::ffi::c_void,
        )
        .is_err()
        {
            return None;
        }

        let query_str = |key_w: Vec<u16>| -> Option<String> {
            let mut key = key_w;
            key.push(0);
            let mut ptr = std::ptr::null_mut();
            let mut len = 0u32;
            if VerQueryValueW(
                data.as_ptr() as *const core::ffi::c_void,
                PCWSTR(key.as_ptr()),
                &mut ptr,
                &mut len,
            )
            .as_bool()
            {
                let slice = std::slice::from_raw_parts(ptr as *const u16, len as usize);
                let s = String::from_utf16_lossy(
                    &slice[..slice.iter().position(|&c| c == 0).unwrap_or(slice.len())],
                );
                let trimmed = s.trim().to_string();
                (!trimmed.is_empty()).then_some(trimmed)
            } else {
                None
            }
        };

        // 翻译表第一个语言页:\VarFileInfo\Translation → (langID, codepage)
        let sub = {
            let mut tkey: Vec<u16> = "\\VarFileInfo\\Translation".encode_utf16().collect();
            let mut ptr = std::ptr::null_mut();
            let mut len = 0u32;
            if VerQueryValueW(
                data.as_ptr() as *const core::ffi::c_void,
                PCWSTR(tkey.as_mut_ptr()),
                &mut ptr,
                &mut len,
            )
            .as_bool() && len >= 4
            {
                let words = std::slice::from_raw_parts(ptr as *const u16, 2);
                format!("\\StringFileInfo\\{:04X}{:04X}", words[0], words[1])
            } else {
                "\\StringFileInfo\\040904B0".to_string() // 兜底:美式英语 Unicode
            }
        };

        let try_keys = [
            format!("{sub}\\FileDescription"),
            format!("{sub}\\ProductName"),
            "\\StringFileInfo\\040904B0\\FileDescription".to_string(),
            "\\StringFileInfo\\040904B0\\ProductName".to_string(),
        ];
        try_keys.iter().find_map(|k| query_str(k.encode_utf16().collect()))
    }
}

/// 枚举有可见窗口的用户进程(排除系统进程),按友好名排序。
/// name 为可执行文件名(含 .exe,与过滤匹配语义一致)。
pub fn list_user_processes() -> Vec<ProcessEntry> {
    // 1) 收集有可见、有标题窗口的 PID
    let mut pids: BTreeSet<u32> = BTreeSet::new();
    unsafe {
        let _ = EnumWindows(
            Some(enum_proc),
            LPARAM(&mut pids as *mut BTreeSet<u32> as isize),
        );
    }
    unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let pids = &mut *(lparam.0 as *mut BTreeSet<u32>);
        if IsWindowVisible(hwnd).as_bool() && GetWindowTextLengthW(hwnd) > 0 {
            let mut pid = 0u32;
            GetWindowThreadProcessId(hwnd, Some(&mut pid));
            if pid != 0 {
                pids.insert(pid);
            }
        }
        true.into()
    }

    // 2) 快照匹配 PID → 进程名/路径 → 友好名 + 图标
    let mut out: Vec<ProcessEntry> = Vec::new();
    let mut seen: BTreeSet<String> = BTreeSet::new();
    unsafe {
        let snap = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            Ok(s) => s,
            Err(_) => return out,
        };
        let mut entry = PROCESSENTRY32W {
            dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        if Process32FirstW(snap, &mut entry).is_ok() {
            loop {
                let pid = entry.th32ProcessID;
                if pids.contains(&pid) {
                    let name = String::from_utf16_lossy(
                        &entry.szExeFile[..entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(0)],
                    );
                    let exe_path = pid_exe_path(pid);
                    // 系统判定带路径(Windows 目录下 = 系统进程)
                    if !is_system_process(&name, exe_path.as_deref())
                        && seen.insert(name.clone())
                    {
                        let stem = name.to_ascii_lowercase()
                            .strip_suffix(".exe")
                            .unwrap_or(&name.to_ascii_lowercase())
                            .to_string();
                        let display = exe_path
                            .as_deref()
                            .and_then(friendly_name)
                            .unwrap_or_else(|| stem.clone());
                        let icon = exe_path
                            .as_deref()
                            .and_then(crate::platform::app_icon::get_or_create);
                        out.push(ProcessEntry { name, stem, display, icon });
                    }
                }
                if Process32NextW(snap, &mut entry).is_err() {
                    break;
                }
            }
        }
        let _ = windows::Win32::Foundation::CloseHandle(snap);
    }
    out.sort_by(|a, b| {
        a.display
            .to_lowercase()
            .cmp(&b.display.to_lowercase())
            .then_with(|| a.stem.cmp(&b.stem))
    });
    out
}

/// 进程候选条目:name 含 .exe(与过滤匹配语义一致),display 为友好名,icon 为 PNG 路径。
#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessEntry {
    pub name: String,
    pub stem: String,
    pub display: String,
    pub icon: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::is_system_process;

    #[test]
    fn system_blacklist() {
        assert!(is_system_process("svchost.exe", None));
        assert!(is_system_process("System", None));
        assert!(is_system_process("dwm.exe", None));
        assert!(!is_system_process("chrome.exe", None));
        assert!(!is_system_process("WeChat.exe", None));
    }

    #[test]
    fn windows_dir_is_system() {
        assert!(is_system_process("whatever.exe", Some(r"C:\Windows\System32\abc.exe")));
        assert!(!is_system_process("notepad_alt.exe", Some(r"D:\Tools\notepad_alt.exe")));
    }
}
