//! 剪贴板写入:专用线程 + OpenClipboard 竞争重试。
//!
//! 约束(旧版 ClipboardWriter 踩坑):SetClipboardData 是跨进程 OLE 调用、
//! 无超时,绝不能在 GUI 线程直接调用;写入完成后记录剪贴板序列号,
//! 监听端据此跳过自己写的内容(防回环)。所有权移交系统,无需保活。

use std::sync::mpsc::{self, Sender};
use std::sync::{Mutex, OnceLock};

use windows::Win32::Foundation::HANDLE;
use windows::Win32::System::DataExchange::{
    CloseClipboard, EmptyClipboard, GetClipboardSequenceNumber, OpenClipboard, RegisterClipboardFormatW,
    SetClipboardData,
};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::CF_UNICODETEXT;
use windows::core::PCWSTR;

/// 写入载荷(与 HistoryItem 内容一一对应)。
#[derive(Debug, Clone)]
pub enum WritePayload {
    /// 文本 + 可选 HTML 片段(CF_HTML)
    Text { text: String, html: Option<String> },
    /// 文件/文件夹路径(CF_HDROP,复制语义)
    Files(Vec<String>),
    /// PNG 字节(同时写 PNG 与 CF_DIB 两种格式,最大化兼容)
    Image { png: Vec<u8> },
}

struct Job {
    payload: WritePayload,
    reply: mpsc::Sender<Result<(), String>>,
}

static WRITER_TX: OnceLock<Sender<Job>> = OnceLock::new();

/// 自己最后一次写入后的剪贴板序列号(监听端防回环)。
pub static LAST_SELF_SEQ: Mutex<u32> = Mutex::new(0);

/// 启动写入线程。
pub fn start() {
    let (tx, rx) = mpsc::channel::<Job>();
    let _ = WRITER_TX.set(tx);
    std::thread::Builder::new()
        .name("clipboard-writer".into())
        .spawn(move || {
            for job in rx {
                let result = unsafe { write(&job.payload) };
                // 记录序列号(成功或失败都刷新一次读数,失败也可能动了剪贴板)
                unsafe {
                    let seq = GetClipboardSequenceNumber();
                    *LAST_SELF_SEQ.lock().unwrap_or_else(|p| p.into_inner()) = seq;
                }
                let _ = job.reply.send(result);
            }
        })
        .expect("剪贴板写入线程启动失败");
}

/// 提交一次写入,阻塞等待结果(带超时兜底,防止剪贴板被独占时调用方卡死)。
pub fn submit(payload: WritePayload, timeout_ms: u64) -> Result<(), String> {
    let tx = WRITER_TX.get().ok_or("写入线程未启动")?;
    let (reply_tx, reply_rx) = mpsc::channel();
    tx.send(Job { payload, reply: reply_tx })
        .map_err(|_| "写入线程已退出")?;
    match reply_rx.recv_timeout(std::time::Duration::from_millis(timeout_ms)) {
        Ok(r) => r,
        Err(_) => Err("写入超时(剪贴板可能被其他程序独占)".into()),
    }
}

unsafe fn write(payload: &WritePayload) -> Result<(), String> {
    // OpenClipboard 竞争重试:其他程序可能正持有剪贴板
    let mut opened = false;
    for _ in 0..10 {
        if OpenClipboard(None).is_ok() {
            opened = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(15));
    }
    if !opened {
        return Err("无法打开剪贴板(被其他程序独占)".into());
    }

    let result = write_locked(payload);
    let _ = CloseClipboard();
    result
}

unsafe fn write_locked(payload: &WritePayload) -> Result<(), String> {
    match payload {
        WritePayload::Text { text, html } => {
            if EmptyClipboard().is_err() {
                return Err("EmptyClipboard 失败".into());
            }
            // CF_UNICODETEXT:UTF-16 + 终止符
            let mut utf16: Vec<u16> = text.encode_utf16().collect();
            utf16.push(0);
            let bytes: Vec<u8> = utf16.iter().flat_map(|c| c.to_le_bytes()).collect();
            set_data(CF_UNICODETEXT.0 as u32, &bytes)?;
            // CF_HTML("HTML Format")
            if let Some(fragment) = html {
                if !fragment.is_empty() {
                    let cf = register_format("HTML Format");
                    let cf_html = crate::clipboard::html_format::create_html_format(fragment);
                    set_data(cf, cf_html.as_bytes())?;
                }
            }
            Ok(())
        }
        WritePayload::Files(paths) => {
            if EmptyClipboard().is_err() {
                return Err("EmptyClipboard 失败".into());
            }
            write_hdrop(paths)
        }
        WritePayload::Image { png } => {
            if EmptyClipboard().is_err() {
                return Err("EmptyClipboard 失败".into());
            }
            // PNG 注册格式(浏览器/截图工具原生支持)
            let png_fmt = register_format("PNG");
            if set_data(png_fmt, png).is_err() {
                log::debug!("PNG 格式写入失败,仅保留 DIB");
            }
            // CF_DIB:PNG 解码 → BGRA 自底向上(大多数传统应用只认 DIB)
            match png_to_dib(png) {
                Ok(dib) => {
                    const CF_DIB: u32 = 8;
                    set_data(CF_DIB, &dib)?;
                }
                Err(e) => log::warn!("PNG→DIB 转换失败: {e}"),
            }
            Ok(())
        }
    }
}

/// 把字节块放入剪贴板(GlobalAlloc + SetClipboardData,所有权移交系统)。
unsafe fn set_data(format: u32, bytes: &[u8]) -> Result<(), String> {
    let hglobal = GlobalAlloc(GMEM_MOVEABLE, bytes.len().max(1))
        .map_err(|e| format!("GlobalAlloc 失败: {e}"))?;
    let ptr = GlobalLock(hglobal);
    if ptr.is_null() {
        let _ = windows::Win32::Foundation::GlobalFree(hglobal);
        return Err("GlobalLock 失败".into());
    }
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr as *mut u8, bytes.len());
    let _ = GlobalUnlock(hglobal);
    if SetClipboardData(format, HANDLE(hglobal.0)).is_err() {
        let _ = windows::Win32::Foundation::GlobalFree(hglobal);
        return Err("SetClipboardData 失败".into());
    }
    Ok(())
}

unsafe fn register_format(name: &str) -> u32 {
    let wide: Vec<u16> = name.encode_utf16().chain([0]).collect();
    RegisterClipboardFormatW(PCWSTR(wide.as_ptr()))
}

/// CF_HDROP:DROPFILES 头 + 双 \0 结尾的宽字符路径列表(复制语义引用)。
unsafe fn write_hdrop(paths: &[String]) -> Result<(), String> {
    // DROPFILES: pFiles(u32) + pt(8) + fNC(4) + fWide(4) = 20 字节
    let mut buf: Vec<u8> = Vec::with_capacity(20 + 512);
    buf.extend_from_slice(&20u32.to_le_bytes()); // pFiles offset
    buf.extend_from_slice(&0i32.to_le_bytes()); // pt.x
    buf.extend_from_slice(&0i32.to_le_bytes()); // pt.y
    buf.extend_from_slice(&0u32.to_le_bytes()); // fNC
    buf.extend_from_slice(&1u32.to_le_bytes()); // fWide = true
    for path in paths {
        for u in path.encode_utf16() {
            buf.extend_from_slice(&u.to_le_bytes());
        }
        buf.extend_from_slice(&0u16.to_le_bytes());
    }
    buf.extend_from_slice(&0u16.to_le_bytes()); // 双 \0 结束
    set_data(15 /* CF_HDROP */, &buf)
}

/// PNG → CF_DIB(BITMAPINFOHEADER + BGRA 自底向上)。
fn png_to_dib(png: &[u8]) -> Result<Vec<u8>, String> {
    let img = image::load_from_memory(png).map_err(|e| e.to_string())?;
    let rgba = img.to_rgba8();
    let (w, h) = (rgba.width() as i32, rgba.height() as i32);
    // BITMAPINFOHEADER(40 字节) + 像素自底向上 BGRA
    let mut out = Vec::with_capacity(40 + rgba.len());
    out.extend_from_slice(&40u32.to_le_bytes());
    out.extend_from_slice(&w.to_le_bytes());
    out.extend_from_slice(&h.to_le_bytes()); // 正值 = 自底向上
    out.extend_from_slice(&1u16.to_le_bytes()); // planes
    out.extend_from_slice(&32u16.to_le_bytes()); // bpp
    out.extend_from_slice(&0u32.to_le_bytes()); // BI_RGB
    out.extend_from_slice(&((w * h * 4) as u32).to_le_bytes()); // 图像大小
    out.extend_from_slice(&0i32.to_le_bytes()); // XPPM
    out.extend_from_slice(&0i32.to_le_bytes()); // YPPM
    out.extend_from_slice(&0u32.to_le_bytes()); // 颜色数
    out.extend_from_slice(&0u32.to_le_bytes()); // 重要颜色
    let raw = rgba.as_raw();
    for y in (0..h as usize).rev() {
        for x in 0..w as usize {
            let idx = y * w as usize * 4 + x * 4;
            out.push(raw[idx + 2]); // B
            out.push(raw[idx + 1]); // G
            out.push(raw[idx]);     // R
            out.push(raw[idx + 3]); // A
        }
    }
    Ok(out)
}

/// 反向:DIB → PNG 字节(监听端读取 CF_DIB 用)。
pub fn dib_to_png(dib: &[u8]) -> Result<Vec<u8>, String> {
    if dib.len() < 40 {
        return Err("DIB 头不完整".into());
    }
    let w = i32::from_le_bytes([dib[4], dib[5], dib[6], dib[7]]);
    let h = i32::from_le_bytes([dib[8], dib[9], dib[10], dib[11]]);
    let bpp = u16::from_le_bytes([dib[14], dib[15]]);
    if w <= 0 || h == 0 || (bpp != 32 && bpp != 24) {
        return Err(format!("不支持的 DIB({w}x{h}@{bpp}bpp)"));
    }
    let bottom_up = h > 0;
    let height = h.unsigned_abs() as usize;
    let width = w as usize;
    let row_stride = ((width * bpp as usize + 31) / 32) * 4; // 行按 4 字节对齐
    let pixels_offset = 40;
    if dib.len() < pixels_offset + row_stride * height {
        return Err("DIB 数据不完整".into());
    }
    let mut img = image::RgbaImage::new(width as u32, height as u32);
    for y in 0..height {
        let src_y = if bottom_up { height - 1 - y } else { y };
        let row = &dib[pixels_offset + src_y * row_stride..][..row_stride];
        for x in 0..width {
            let px = if bpp == 32 {
                [row[x * 4 + 2], row[x * 4 + 1], row[x * 4], row[x * 4 + 3]]
            } else {
                let mut p = [row[x * 3 + 2], row[x * 3 + 1], row[x * 3], 255u8];
                if x * 3 + 3 > row.len() {
                    p = [0, 0, 0, 255];
                }
                p
            };
            img.put_pixel(x as u32, y as u32, image::Rgba(px));
        }
    }
    let mut buf = std::io::Cursor::new(Vec::new());
    img.write_to(&mut buf, image::ImageFormat::Png)
        .map_err(|e| e.to_string())?;
    Ok(buf.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_dib(width: usize, height: usize, bpp: usize, bottom_up: bool) -> Vec<u8> {
        let row = ((width * bpp + 31) / 32) * 4;
        let mut d = Vec::new();
        d.extend_from_slice(&40u32.to_le_bytes());
        d.extend_from_slice(&(width as i32).to_le_bytes());
        d.extend_from_slice(&(if bottom_up { height as i32 } else { -(height as i32) }).to_le_bytes());
        d.extend_from_slice(&1u16.to_le_bytes());
        d.extend_from_slice(&(bpp as u16).to_le_bytes());
        d.extend_from_slice(&0u32.to_le_bytes());
        d.extend_from_slice(&((width * height * 4) as u32).to_le_bytes());
        d.extend_from_slice(&0i32.to_le_bytes());
        d.extend_from_slice(&0i32.to_le_bytes());
        d.extend_from_slice(&0u32.to_le_bytes());
        d.extend_from_slice(&0u32.to_le_bytes());
        // 像素:第 y 行 → R = y, G = x%256, B = 7
        for yy in 0..height {
            let row_y = if bottom_up { height - 1 - yy } else { yy };
            for x in 0..width {
                if bpp == 32 {
                    d.push(7); // B
                    d.push((x % 256) as u8); // G
                    d.push(row_y as u8); // R
                    d.push(255); // A
                } else {
                    d.push(7);
                    d.push((x % 256) as u8);
                    d.push(row_y as u8);
                }
                while d.len() % 4 != 0 && x == width - 1 {
                    d.push(0); // 行尾对齐填充
                }
            }
        }
        d
    }

    #[test]
    fn dib_to_png_roundtrip_bottom_up() {
        let dib = make_dib(4, 3, 32, true);
        let png = dib_to_png(&dib).expect("转换失败");
        let img = image::load_from_memory(&png).expect("PNG 无效");
        let rgba = img.to_rgba8();
        assert_eq!(rgba.dimensions(), (4, 3));
        // bottom-up:DIB 第 yy 行存的是显示第 height-1-yy 行
        // png 第 0 行 ← DIB 第 2 行 → 显示第 0 行,R=0
        assert_eq!(rgba.get_pixel(0, 0).0, [0, 0, 7, 255]);
        // png 第 2 行 ← DIB 第 0 行 → 显示第 2 行,R=2,G=x%256
        assert_eq!(rgba.get_pixel(3, 2).0, [2, 3, 7, 255]);
    }

    #[test]
    fn dib_to_png_top_down() {
        let dib = make_dib(4, 3, 32, false);
        let png = dib_to_png(&dib).expect("转换失败");
        let img = image::load_from_memory(&png).expect("PNG 无效");
        let rgba = img.to_rgba8();
        // top-down:第 0 行 = DIB 第 0 行,R = 0
        assert_eq!(rgba.get_pixel(0, 0).0, [0, 0, 7, 255]);
    }

    #[test]
    fn dib_rejects_garbage() {
        assert!(dib_to_png(&[]).is_err());
        assert!(dib_to_png(&[0u8; 10]).is_err());
        assert!(dib_to_png(&make_dib(2, 2, 8, true)).is_err(), "8bpp 不支持");
    }

    #[test]
    fn hdrop_layout() {
        // DROPFILES 头 20 字节 + 宽路径 + 双 \0
        let mut buf: Vec<u8> = Vec::new();
        buf.extend_from_slice(&20u32.to_le_bytes());
        buf.extend_from_slice(&0i32.to_le_bytes());
        buf.extend_from_slice(&0i32.to_le_bytes());
        buf.extend_from_slice(&0u32.to_le_bytes());
        buf.extend_from_slice(&1u32.to_le_bytes());
        for u in "C:\\a.txt".encode_utf16() {
            buf.extend_from_slice(&u.to_le_bytes());
        }
        buf.extend_from_slice(&0u16.to_le_bytes());
        buf.extend_from_slice(&0u16.to_le_bytes());
        assert_eq!(u32::from_le_bytes(buf[0..4].try_into().unwrap()), 20);
        assert_eq!(u32::from_le_bytes(buf[16..20].try_into().unwrap()), 1); // fWide
        // 尾部双 \0
        let n = buf.len();
        assert_eq!(&buf[n - 4..], &[0, 0, 0, 0]);
    }
}
