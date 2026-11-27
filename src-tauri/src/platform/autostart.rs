//! 开机自启动:写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
//!
//! 与旧版 StartupService 保持一致:值名 `NexClip`,命令为
//! `"<exe>" --autostart`(带引号,兼容含空格的安装路径)。

use windows::core::w;
use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS};
use windows::Win32::System::Registry::{
    RegCloseKey, RegDeleteValueW, RegOpenKeyExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER,
    KEY_SET_VALUE, REG_SZ,
};

const RUN_KEY: windows::core::PCWSTR =
    w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const VALUE_NAME: windows::core::PCWSTR = w!("NexClip");

/// 应用开机自启状态。失败返回错误字符串,调用方决定是否只记日志。
pub fn apply(enabled: bool) -> Result<(), String> {
    let command = if enabled {
        let exe = std::env::current_exe().map_err(|e| format!("获取程序路径失败: {e}"))?;
        Some(format!("\"{}\" --autostart", exe.display()))
    } else { None };
    let mut key = HKEY::default();
    let status = unsafe {
        RegOpenKeyExW(
            HKEY_CURRENT_USER,
            RUN_KEY,
            0,
            KEY_SET_VALUE,
            &mut key,
        )
    };
    if status != ERROR_SUCCESS {
        return Err(format!("打开启动项注册表失败: {status:?}"));
    }

    let result = if let Some(command) = command {
        let wide: Vec<u16> = command.encode_utf16().chain(std::iter::once(0)).collect();
        let bytes = unsafe {
            std::slice::from_raw_parts(wide.as_ptr().cast::<u8>(), wide.len() * 2)
        };
        let r = unsafe { RegSetValueExW(key, VALUE_NAME, 0, REG_SZ, Some(bytes)) };
        if r == ERROR_SUCCESS {
            Ok(())
        } else {
            Err(format!("写入开机自启失败: {r:?}"))
        }
    } else {
        let r = unsafe { RegDeleteValueW(key, VALUE_NAME) };
        if r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND {
            Ok(())
        } else {
            Err(format!("移除开机自启失败: {r:?}"))
        }
    };
    unsafe {
        let _ = RegCloseKey(key);
    }
    result
}
