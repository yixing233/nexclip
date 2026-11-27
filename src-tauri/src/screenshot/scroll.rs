//! 长截图:选区固定,向下滚动目标窗口,逐帧截取并按行匹配拼接。
//!
//! 流程:覆盖窗进入滚动 HUD 模式并点击穿透 → 光标移到选区中心 →
//! 循环 [SendInput 滚轮 → 延时 → xcap 活屏截取选区 → 与上一帧行匹配求重叠 →
//! 追加新行] → 无新内容/达上限/F1 中止 → 写入剪贴板(自动入历史) → 恢复。

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use serde::Serialize;
use tauri::Emitter;

use windows::Win32::Foundation::POINT;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_MOUSE, MOUSEEVENTF_WHEEL, MOUSEINPUT,
};
use windows::Win32::UI::WindowsAndMessaging::{GetCursorPos, SetCursorPos};

use super::{end_session_internal, in_session};

/// 滚动拼接进行中(热键线程据此把 F1 转为中止)。
static SCROLLING: AtomicBool = AtomicBool::new(false);
/// F1 中止标记。
static ABORT: AtomicBool = AtomicBool::new(false);

const MAX_ROUNDS: usize = 25;
const MAX_HEIGHT: i64 = 20000;
const WHEEL_DELTA: i32 = -120 * 3; // 一次滚 3 格
const SETTLE_MS: u64 = 380;
const MIN_OVERLAP: usize = 5;

/// 进度事件载荷(发往覆盖窗 HUD)。
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct ScrollProgress {
    pub rounds: usize,
    pub height: i64,
    pub done: bool,
    pub aborted: bool,
}

/// 启动长截图(命令入口)。rect 为虚拟桌面物理坐标。
pub fn start(
    app: tauri::AppHandle,
    monitor_origin: (i32, i32),
    rect: (i32, i32, u32, u32),
) {
    if SCROLLING.swap(true, Ordering::SeqCst) {
        return;
    }
    ABORT.store(false, Ordering::SeqCst);
    std::thread::spawn(move || {
        let app = app.clone();
        let result = run(&app, monitor_origin, rect);
        SCROLLING.store(false, Ordering::SeqCst);
        ABORT.store(false, Ordering::SeqCst);
        match result {
            Ok(png) => {
                let _ = crate::platform::writer::submit(
                    crate::platform::writer::WritePayload::Image { png },
                    8000,
                );
            }
            Err(e) => log::warn!("长截图失败: {e}"),
        }
        end_session_internal(&app);
    });
}
/// 热键触发:滚动中 → 中止;否则由调用方启动新会话。
pub fn abort_if_scrolling() -> bool {
    if SCROLLING.load(Ordering::SeqCst) {
        ABORT.store(true, Ordering::SeqCst);
        true
    } else {
        false
    }
}

pub fn is_scrolling() -> bool {
    SCROLLING.load(Ordering::SeqCst)
}

fn run(
    app: &tauri::AppHandle,
    monitor_origin: (i32, i32),
    rect: (i32, i32, u32, u32),
) -> Result<Vec<u8>, String> {
    let (rx, ry, rw, rh) = rect;
    if rw == 0 || rh == 0 {
        return Err("选区为空".into());
    }

    // 光标移入选区中心(滚动事件必须落在目标窗口上)
    unsafe {
        let mut cur = POINT::default();
        let _ = GetCursorPos(&mut cur);
        let _ = SetCursorPos(rx + rw as i32 / 2, ry + rh as i32 / 2);
    }
    std::thread::sleep(Duration::from_millis(120));

    let mut stitched = capture_region(monitor_origin, rect)?;
    let _base_h = stitched.height() as i64;

    let mut prev = stitched.clone();
    for round in 0..MAX_ROUNDS {
        if ABORT.load(Ordering::SeqCst) || !in_session() {
            break;
        }
        send_wheel();
        std::thread::sleep(Duration::from_millis(SETTLE_MS));
        let next = capture_region(monitor_origin, rect)?;

        // 行匹配:prev 底部 k 行 == next 顶部 k 行(最大 k)
        let overlap = find_overlap(&prev, &next, prev.height() as usize);
        if overlap < MIN_OVERLAP {
            // 页面没滚动(到底/非滚动区):再试一次确认后退出
            if round > 0 {
                break;
            }
            prev = next;
            continue;
        }
        if overlap >= prev.height() as usize {
            // 完全相同:到底了
            break;
        }
        // 追加新内容(next 在重叠区之下的部分)
        let new_rows = next.height() as i64 - overlap as i64;
        if stitched.height() as i64 + new_rows > MAX_HEIGHT {
            break;
        }
        append_rows(&mut stitched, &next, overlap as u32);
        prev = next;

        let _ = app.emit_to(
            &format!("overlay-{}", super::monitor_of_point(rx, ry)),
            "scroll:progress",
            ScrollProgress {
                rounds: round + 1,
                height: stitched.height() as i64,
                done: false,
                aborted: false,
            },
        );
    }

    let _ = app.emit_to(
        &format!("overlay-{}", super::monitor_of_point(rx, ry)),
        "scroll:progress",
        ScrollProgress {
            rounds: 0,
            height: stitched.height() as i64,
            done: true,
            aborted: ABORT.load(Ordering::SeqCst),
        },
    );
    super::encode_png(&stitched)
}

/// 活屏截取(非冻结帧):定位包含该矩形中心点的显示器,截取后裁剪。
fn capture_region(
    monitor_origin: (i32, i32),
    rect: (i32, i32, u32, u32),
) -> Result<image::RgbaImage, String> {
    let (rx, ry, rw, rh) = rect;
    let monitors = xcap::Monitor::all().map_err(|e| format!("枚举显示器失败: {e}"))?;
    let cx = rx + rw as i32 / 2;
    let cy = ry + rh as i32 / 2;
    let m = monitors
        .into_iter()
        .find(|m| {
            let (mx, my) = (m.x().unwrap_or(0), m.y().unwrap_or(0));
            let (mw, mh) = (m.width().unwrap_or(0), m.height().unwrap_or(0));
            cx >= mx && cx < mx + mw as i32 && cy >= my && cy < my + mh as i32
        })
        .ok_or("未找到选区所在显示器")?;
    let mut img = m.capture_image().map_err(|e| format!("截屏失败: {e}"))?;
    super::force_opaque(&mut img);
    // 虚拟桌面坐标 → 显示器本地坐标
    let local_x = (rx - monitor_origin.0).max(0);
    let local_y = (ry - monitor_origin.1).max(0);
    let x = local_x.min(img.width().saturating_sub(1) as i32) as u32;
    let y = local_y.min(img.height().saturating_sub(1) as i32) as u32;
    let w = rw.min(img.width().saturating_sub(x));
    let h = rh.min(img.height().saturating_sub(y));
    crop_rows(&img, x, y, w, h)
}

fn crop_rows(img: &image::RgbaImage, x: u32, y: u32, w: u32, h: u32) -> Result<image::RgbaImage, String> {
    let mut out = image::RgbaImage::new(w.max(1), h.max(1));
    for row in 0..h {
        let src_start = ((y + row) as usize * img.width() as usize + x as usize) * 4;
        let src_end = src_start + w as usize * 4;
        let dst_start = row as usize * w as usize * 4;
        out.as_mut()[dst_start..dst_start + w as usize * 4]
            .copy_from_slice(&img.as_raw()[src_start..src_end]);
    }
    Ok(out)
}

fn append_rows(dst: &mut image::RgbaImage, src: &image::RgbaImage, skip_rows: u32) {
    let w = dst.width();
    let new_h = src.height() - skip_rows;
    let old_h = dst.height();
    let mut combined = image::RgbaImage::new(w, old_h + new_h);
    // 旧内容
    for row in 0..old_h {
        let src_start = (row as usize) * w as usize * 4;
        let dst_start = (row as usize) * w as usize * 4;
        combined.as_mut()[dst_start..dst_start + w as usize * 4]
            .copy_from_slice(&dst.as_raw()[src_start..src_start + w as usize * 4]);
    }
    // 新内容(跳过重叠区)
    for row in 0..new_h {
        let src_start = ((skip_rows + row) as usize * src.width() as usize) * 4;
        let src_end = src_start + w as usize * 4;
        let dst_start = ((old_h + row) as usize * w as usize) * 4;
        combined.as_mut()[dst_start..dst_start + w as usize * 4]
            .copy_from_slice(&src.as_raw()[src_start..src_end]);
    }
    *dst = combined;
}

/// 行均值灰度差:返回最大 k 使 prev 底部 k 行与 next 顶部 k 行匹配。
fn find_overlap(prev: &image::RgbaImage, next: &image::RgbaImage, max_k: usize) -> usize {
    let ph = prev.height() as usize;
    let nh = next.height() as usize;
    let max_k = max_k.min(ph).min(nh).min(1200); // 单屏重叠上限,控制搜索成本
    let prev_rows = row_signatures(prev);
    let next_rows = row_signatures(next);
    for k in (MIN_OVERLAP..=max_k).rev() {
        let mut ok = true;
        for i in 0..k {
            let pi = ph - k + i;
            if diff(&prev_rows[pi], &next_rows[i]) > 10.0 {
                ok = false;
                break;
            }
        }
        if ok {
            return k;
        }
    }
    0
}

/// 每行采样均值 [r, g, b](隔 4 像素采样,加速)。
fn row_signatures(img: &image::RgbaImage) -> Vec<[f32; 3]> {
    let w = img.width() as usize;
    let h = img.height() as usize;
    let raw = img.as_raw();
    let mut out = Vec::with_capacity(h);
    for y in 0..h {
        let mut acc = [0f32; 3];
        let mut n = 0;
        let base = y * w * 4;
        let mut x = 0;
        while x < w {
            let i = base + x * 4;
            acc[0] += raw[i] as f32;
            acc[1] += raw[i + 1] as f32;
            acc[2] += raw[i + 2] as f32;
            n += 1;
            x += 4;
        }
        if n > 0 {
            acc[0] /= n as f32;
            acc[1] /= n as f32;
            acc[2] /= n as f32;
        }
        out.push(acc);
    }
    out
}

fn diff(a: &[f32; 3], b: &[f32; 3]) -> f32 {
    (a[0] - b[0]).abs() + (a[1] - b[1]).abs() + (a[2] - b[2]).abs()
}

/// 向光标位置发送滚轮事件(向下 3 格)。
fn send_wheel() {
    let input = INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: windows::Win32::UI::Input::KeyboardAndMouse::INPUT_0 {
            mi: MOUSEINPUT {
                dx: 0,
                dy: 0,
                mouseData: WHEEL_DELTA as u32,
                dwFlags: MOUSEEVENTF_WHEEL,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    unsafe {
        SendInput(&[input], std::mem::size_of::<INPUT>() as i32);
    }
}
