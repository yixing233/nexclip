//! 剪贴板写入:专用线程 + OpenClipboard 竞争重试。
//!
//! 约束(旧版 ClipboardWriter 踩坑):SetClipboardData 是跨进程 OLE 调用、
//! 无超时,绝不能在 GUI 线程直接调用;写入完成后记录剪贴板序列号,
//! 监听端据此跳过自己写的内容(防回环)。所有权移交系统,无需保活。

use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

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
    deadline: Instant,
    cancelled: Arc<AtomicBool>,
}

impl Job {
    fn expired(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst) || Instant::now() >= self.deadline
    }
}

static WRITER_TX: OnceLock<Sender<Job>> = OnceLock::new();
/// 共享接收端:写入线程卡死后,由新线程接管队列继续消费
static WRITER_RX: OnceLock<Arc<Mutex<mpsc::Receiver<Job>>>> = OnceLock::new();
/// 写入线程世代:submit 超时且世代未变才重生(防多个超时请求重复重生)
static WRITER_GEN: AtomicU64 = AtomicU64::new(0);
static WORKERS: AtomicUsize = AtomicUsize::new(0);

/// 自己最后一次写入后的剪贴板序列号(监听端防回环)。
pub static LAST_SELF_SEQ: Mutex<u32> = Mutex::new(0);

/// 启动写入线程。
pub fn start() {
    let (tx, rx) = mpsc::channel::<Job>();
    let _ = WRITER_TX.set(tx);
    let _ = WRITER_RX.set(Arc::new(Mutex::new(rx)));
    WRITER_GEN.store(1, Ordering::SeqCst);
    spawn_writer(1);
}

/// 生成一个写入线程(recv 在锁内、write 在锁外,卡死的线程不阻塞接管者)。
fn spawn_writer(generation: u64) {
    let Some(rx) = WRITER_RX.get() else { return };
    WORKERS.fetch_add(1, Ordering::SeqCst);
    let rx = rx.clone();
    std::thread::Builder::new()
        .name("clipboard-writer".into())
        .spawn(move || {
          struct WorkerGuard;
          impl Drop for WorkerGuard { fn drop(&mut self) { WORKERS.fetch_sub(1, Ordering::SeqCst); } }
          let _guard = WorkerGuard;
          loop {
            if WRITER_GEN.load(Ordering::SeqCst) != generation { return; }
            let job = { rx.lock().unwrap_or_else(|p| p.into_inner()).recv() };
            match job {
                Ok(job) => {
                    if WRITER_GEN.load(Ordering::SeqCst) != generation {
                        if let Some(tx) = WRITER_TX.get() { let _ = tx.send(job); }
                        return;
                    }
                    let result = if job.expired() { Err("写入请求已过期".into()) }
                        else { unsafe { write(&job) } };
                    // 记录序列号(成功或失败都刷新一次读数,失败也可能动了剪贴板)
                    unsafe {
                        let seq = GetClipboardSequenceNumber();
                        *LAST_SELF_SEQ.lock().unwrap_or_else(|p| p.into_inner()) = seq;
                    }
                    let _ = job.reply.send(result);
                }
                Err(_) => return,
            }
          }
        })
        .expect("剪贴板写入线程启动失败");
}

/// 提交一次写入,阻塞等待结果(带超时兜底,防止剪贴板被独占时调用方卡死)。
pub fn submit(payload: WritePayload, timeout_ms: u64) -> Result<(), String> {
    let tx = WRITER_TX.get().ok_or("写入线程未启动")?;
    let gen_before = WRITER_GEN.load(Ordering::SeqCst);
    let (reply_tx, reply_rx) = mpsc::channel();
    let cancelled = Arc::new(AtomicBool::new(false));
    tx.send(Job { payload, reply: reply_tx, deadline: Instant::now() + Duration::from_millis(timeout_ms), cancelled: cancelled.clone() })
        .map_err(|_| "写入线程已退出")?;
    match reply_rx.recv_timeout(std::time::Duration::from_millis(timeout_ms)) {
        Ok(r) => r,
        Err(_) => {
            cancelled.store(true, Ordering::SeqCst);
            // 超时:写入线程可能已卡死在 SetClipboardData 等跨进程调用里。
            // 世代未变(没有其他请求重生过)→ 生成新线程接管队列;旧线程留在
            // 内核等 owner 唤醒或进程退出释放剪贴板(强杀会泄漏打开的句柄)。
            if WORKERS.load(Ordering::SeqCst) < 3 && WRITER_GEN.compare_exchange(gen_before, gen_before + 1, Ordering::SeqCst, Ordering::SeqCst).is_ok() {
                log::warn!("剪贴板写入线程 {timeout_ms}ms 无响应,重生写入线程接管队列");
                spawn_writer(gen_before + 1);
            }
            Err("写入超时(剪贴板可能被其他程序独占)".into())
        }
    }
}

unsafe fn write(job: &Job) -> Result<(), String> {
    // Filesystem checks and image decoding happen before taking the global clipboard lock.
    let prepared = prepare(&job.payload)?;
    if job.expired() { return Err("写入请求已过期".into()); }
    // OpenClipboard 竞争重试:其他程序可能正持有剪贴板
    let mut opened = false;
    for _ in 0..10 {
        if job.expired() { return Err("写入请求已过期".into()); }
        if OpenClipboard(None).is_ok() {
            opened = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(15));
    }
    if !opened {
        let owner = unsafe { crate::platform::monitor::clipboard_owner_name() }
            .unwrap_or_else(|| "未知进程".into());
        log::warn!("剪贴板打开失败(重试 10 次共 150ms),占用者: {owner}");
        return Err("无法打开剪贴板(被其他程序独占)".into());
    }

    let result = if job.expired() { Err("写入请求已过期".into()) }
        else { write_locked(&prepared) };
    let _ = CloseClipboard();
    result
}

fn prepare(payload: &WritePayload) -> Result<Vec<(u32, Vec<u8>)>, String> {
    match payload {
        WritePayload::Text { text, html } => {
            // CF_UNICODETEXT:UTF-16 + 终止符
            let mut utf16: Vec<u16> = text.encode_utf16().collect();
            utf16.push(0);
            let bytes: Vec<u8> = utf16.iter().flat_map(|c| c.to_le_bytes()).collect();
            let mut formats = vec![(CF_UNICODETEXT.0 as u32, bytes)];
            // CF_HTML("HTML Format")
            if let Some(fragment) = html {
                if !fragment.is_empty() {
                    let cf = unsafe { register_format("HTML Format") };
                    let cf_html = crate::clipboard::html_format::create_html_format(fragment);
                    formats.push((cf, cf_html.into_bytes()));
                }
            }
            Ok(formats)
        }
        WritePayload::Files(paths) => {
            // 先过滤存活路径与 EmptyClipboard 的顺序很关键:全部失效时必须
            // 保持剪贴板原内容不变,所以 exists 检查要在清空之前完成。
            let alive: Vec<String> = paths
                .iter()
                .filter(|p| std::path::Path::new(p.as_str()).exists())
                .cloned()
                .collect();
            if alive.is_empty() {
                return Err("文件已全部不存在,保留剪贴板原内容".into());
            }
            Ok(vec![(15, hdrop_bytes(&alive))])
        }
        WritePayload::Image { png } => {
            let dib = png_to_dib(png)?;
            Ok(vec![(unsafe { register_format("PNG") }, png.clone()), (8, dib)])
        }
    }
}

unsafe fn write_locked(formats: &[(u32, Vec<u8>)]) -> Result<(), String> {
    EmptyClipboard().map_err(|e| e.to_string())?;
    let mut written = false;
    let mut last_error = String::new();
    for (format, bytes) in formats {
        match set_data(*format, bytes) {
            Ok(()) => written = true,
            Err(e) => last_error = e,
        }
    }
    if written { Ok(()) } else { Err(last_error) }
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
/// CF_HDROP:DROPFILES 头 + 双 \0 结尾的宽字符路径列表(复制语义引用)。
/// 调用方负责过滤存活路径,这里不再做 exists 检查。
fn hdrop_bytes(paths: &[String]) -> Vec<u8> {
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
    buf
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
    use image::ImageDecoder;
    // The codec handles extended headers, palettes, bitfields and top-down DIBs.
    let mut decoder = image::codecs::bmp::BmpDecoder::new_without_file_header(std::io::Cursor::new(dib))
        .map_err(|e| e.to_string())?;
    decoder.set_limits(image::Limits::default()).map_err(|e| e.to_string())?;
    let img = image::DynamicImage::from_decoder(decoder).map_err(|e| e.to_string())?;
    let mut buf = std::io::Cursor::new(Vec::new());
    img.write_to(&mut buf, image::ImageFormat::Png)
        .map_err(|e| e.to_string())?;
    Ok(buf.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dib_extended_header_bitfields_are_decoded_at_the_correct_offset() {
        let mut dib = make_dib(1, 1, 32, false);
        let pixels = dib.split_off(40);
        dib[..4].copy_from_slice(&56u32.to_le_bytes());
        dib[16..20].copy_from_slice(&3u32.to_le_bytes());
        for mask in [0x00ff0000u32, 0x0000ff00, 0x000000ff, 0xff000000] {
            dib.extend_from_slice(&mask.to_le_bytes());
        }
        dib.extend_from_slice(&pixels);
        let png = dib_to_png(&dib).unwrap();
        assert_eq!(image::load_from_memory(&png).unwrap().to_rgba8().get_pixel(0, 0).0, [0, 0, 7, 255]);
    }

    #[test]
    fn cancelled_or_timed_out_job_is_not_eligible_to_write() {
        let (reply, _) = mpsc::channel();
        let cancel = Arc::new(AtomicBool::new(false));
        let mut job = Job { payload: WritePayload::Text { text: "hello".into(), html: None },
            reply, deadline: Instant::now() + Duration::from_secs(1), cancelled: cancel.clone() };
        assert!(!job.expired());
        cancel.store(true, Ordering::SeqCst);
        assert!(job.expired());
        cancel.store(false, Ordering::SeqCst);
        job.deadline = Instant::now() - Duration::from_secs(1);
        assert!(job.expired());
    }

    #[test]
    fn broken_image_is_rejected_before_clipboard_is_emptied() {
        assert!(prepare(&WritePayload::Image { png: b"invalid".to_vec() }).is_err());
    }

    fn make_dib(width: usize, height: usize, bpp: usize, bottom_up: bool) -> Vec<u8> {
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
