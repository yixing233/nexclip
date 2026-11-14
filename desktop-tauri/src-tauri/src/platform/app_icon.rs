//! 应用图标提取与缓存。
//!
//! 移植自 C# AppIconCache:从来源可执行文件提取图标 → PNG 存
//! %LOCALAPPDATA%/NexClip/app_icons/{processName}_{hash}.png
//! (哈希用 FNV-1a 32 位,文件名模式与旧版一致;旧缓存文件可直接复用)。

use std::path::PathBuf;

use windows::core::PCWSTR;
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, DeleteDC, DeleteObject, GetDIBits, GetObjectW, BITMAP,
    BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS,
};
use windows::Win32::UI::Shell::SHDefExtractIconW;
use windows::Win32::UI::WindowsAndMessaging::{DestroyIcon, GetIconInfo, HICON};

/// 获取或提取应用图标,返回本地 PNG 路径(失败返回 None)。
pub fn get_or_create(executable_path: &str) -> Option<String> {
    let path = PathBuf::from(executable_path);
    if !path.is_file() {
        return None;
    }
    let process_name = path
        .file_stem()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| "app".into());

    // FNV-1a 32 位(确定性哈希,跨进程稳定,与旧版随机哈希不同但仅影响文件名)
    let mut hash: u32 = 0x811C9DC5;
    for b in executable_path.to_lowercase().as_bytes() {
        hash ^= *b as u32;
        hash = hash.wrapping_mul(0x01000193);
    }
    let key = format!("{process_name}_{hash:08X}");

    let icon_dir = icon_dir();
    let _ = std::fs::create_dir_all(&icon_dir);
    let target = icon_dir.join(format!("{key}.png"));
    if target.is_file() {
        return Some(target.to_string_lossy().into_owned());
    }

    let png = extract_icon_png(executable_path)?;
    std::fs::write(&target, &png).ok()?;
    Some(target.to_string_lossy().into_owned())
}

fn icon_dir() -> PathBuf {
    PathBuf::from(
        std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into()),
    )
    .join("NexClip")
    .join("app_icons")
}

/// 从 exe 提取 32px 图标并转 PNG 字节。
fn extract_icon_png(executable_path: &str) -> Option<Vec<u8>> {
    let wide: Vec<u16> = executable_path
        .encode_utf16()
        .chain([0])
        .collect();
    unsafe {
        let mut hicon_large: HICON = Default::default();
        if SHDefExtractIconW(
            PCWSTR(wide.as_ptr()),
            0,
            0,
            Some(&mut hicon_large),
            None,
            32,
        )
        .is_err()
        {
            return None;
        }
        let hicon = hicon_large;

        let png = hicon_to_png(hicon);
        let _ = DestroyIcon(hicon);
        png
    }
}

/// HICON → PNG(HICON 关联的 32bpp 位图 → BGRA → RGBA)。
unsafe fn hicon_to_png(hicon: HICON) -> Option<Vec<u8>> {
    let mut info = Default::default();
    GetIconInfo(hicon, &mut info).ok()?;
    let has_color = !info.hbmColor.is_invalid();

    if !has_color {
        let _ = DeleteObject(info.hbmMask);
        return None;
    }

    // 读取位图尺寸
    let mut bm = BITMAP::default();
    if GetObjectW(
        info.hbmColor,
        std::mem::size_of::<BITMAP>() as i32,
        Some(&mut bm as *mut BITMAP as *mut core::ffi::c_void),
    ) == 0
    {
        let _ = DeleteObject(info.hbmColor);
        let _ = DeleteObject(info.hbmMask);
        return None;
    }
    let (w, h) = (bm.bmWidth as i32, bm.bmHeight as i32);
    if w <= 0 || h <= 0 {
        let _ = DeleteObject(info.hbmColor);
        let _ = DeleteObject(info.hbmMask);
        return None;
    }

    // GetDIBits → 32bpp 自顶向下 BGRA
    let hdc = CreateCompatibleDC(None);
    let mut bmi = BITMAPINFO::default();
    bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
    bmi.bmiHeader.biWidth = w;
    bmi.bmiHeader.biHeight = -h; // 负值 = 自顶向下
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = 0; // BI_RGB
    bmi.bmiHeader.biSizeImage = (w * h * 4) as u32;

    let mut pixels = vec![0u8; (w * h * 4) as usize];
    let lines = GetDIBits(
        hdc,
        info.hbmColor,
        0,
        h as u32,
        Some(pixels.as_mut_ptr() as *mut core::ffi::c_void),
        &mut bmi,
        DIB_RGB_COLORS,
    );
    let _ = DeleteDC(hdc);
    let _ = DeleteObject(info.hbmColor);
    let _ = DeleteObject(info.hbmMask);
    if lines == 0 {
        return None;
    }

    // BGRA → RGBA → PNG
    let mut img = image::RgbaImage::new(w as u32, h as u32);
    for y in 0..h as usize {
        for x in 0..w as usize {
            let idx = (y * w as usize + x) * 4;
            let b = pixels[idx];
            let g = pixels[idx + 1];
            let r = pixels[idx + 2];
            let a = pixels[idx + 3];
            img.put_pixel(x as u32, y as u32, image::Rgba([r, g, b, a]));
        }
    }
    let mut buf = std::io::Cursor::new(Vec::new());
    img.write_to(&mut buf, image::ImageFormat::Png).ok()?;
    Some(buf.into_inner())
}
