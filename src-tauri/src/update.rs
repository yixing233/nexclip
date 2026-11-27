//! 更新检查。
//!
//! 移植自 C# `Services/UpdateService.cs`:双通道
//! - github:遍历 releases 列表找最新一个挂了 Tauri x64 安装包的正式发布
//!   (直接用 /releases/latest 会被只发 Android 的版本顶走);
//! - direct:读服务器 /releases/version.json 的 windowsTauri 段,缺失时回落 GitHub。
//!
//! 版本比较语义与 C# CompareVersions 一致(按 .-_ 分段,数字段比大小,否则比字符串)。

use serde::Serialize;
use serde_json::Value;

const GITHUB_RELEASES_API: &str = "https://api.github.com/repos/yixing233/nexclip/releases";
const GITHUB_RELEASES_PAGE: &str = "https://github.com/yixing233/nexclip/releases";
const USER_AGENT: &str = "NexClip-Tauri/2.0";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCheckResult {
    pub success: bool,
    pub has_update: bool,
    pub current_version: String,
    pub latest_version: String,
    pub release_title: String,
    pub release_notes: String,
    pub release_url: String,
    pub download_url: String,
    pub sha256: Option<String>,
    pub file_size: Option<i64>,
    pub error_message: Option<String>,
}

static LAST_CHECK: std::sync::Mutex<Option<UpdateCheckResult>> = std::sync::Mutex::new(None);

pub fn cache_result(result: &UpdateCheckResult) {
    *LAST_CHECK.lock().unwrap_or_else(|p| p.into_inner()) = Some(result.clone());
}

pub fn cached_result() -> Option<UpdateCheckResult> {
    LAST_CHECK.lock().unwrap_or_else(|p| p.into_inner()).clone()
}

fn failure(current: &str, err: String) -> UpdateCheckResult {
    UpdateCheckResult {
        success: false,
        has_update: false,
        current_version: current.to_string(),
        latest_version: String::new(),
        release_title: String::new(),
        release_notes: String::new(),
        release_url: String::new(),
        download_url: String::new(),
        sha256: None,
        file_size: None,
        error_message: Some(err),
    }
}

/// 从 Value 中取第一个非空字符串字段(空串/空白视为缺失,让平台段占位值自动回落)。
fn read_str(obj: Option<&Value>, keys: &[&str]) -> Option<String> {
    let obj = obj?;
    for k in keys {
        match obj.get(*k) {
            Some(Value::String(s)) if !s.trim().is_empty() => return Some(s.clone()),
            _ => continue,
        }
    }
    None
}

// ---- 内置更新下载 ----

/// 下载进度事件载荷(前端 listen "update-download-progress")。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgress {
    pub downloaded: u64,
    pub total: Option<u64>,
    /// 0-100;total 未知时为 -1
    pub percent: i32,
    pub done: bool,
    pub error: Option<String>,
}

/// 全局下载状态:None 空闲 / Some(progress) 进行中(前端可随时查询)。
static DOWNLOAD_STATE: std::sync::Mutex<Option<DownloadProgress>> = std::sync::Mutex::new(None);

/// 当前下载状态(命令查询用)。
pub fn download_state() -> Option<DownloadProgress> {
    DOWNLOAD_STATE
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .clone()
}

/// 取消标记:下载循环每块检查,置位后中止并清理半成品文件。
static DOWNLOAD_CANCEL: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub fn cancel_download() {
    DOWNLOAD_CANCEL.store(true, std::sync::atomic::Ordering::SeqCst);
}

/// 安装包落盘目录:%LOCALAPPDATA%/NexClip/updates。
fn updates_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into()))
        .join("NexClip")
        .join("updates")
}

/// 从 URL 推断文件名(取路径最后一段;空则用时间戳兜底)。
fn file_name_of(url: &str) -> String {
    let name = url
        .split(['?', '#'])
        .next()
        .unwrap_or("")
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or("");
    if name.is_empty() {
        format!("nexclip-setup-{}.exe", chrono::Utc::now().timestamp())
    } else {
        name.to_string()
    }
}

/// 流式下载安装包 → updates 目录 → SHA256 校验(可选)→ 返回落盘路径。
/// 进度通过 "update-download-progress" 事件推给前端,同时写入 DOWNLOAD_STATE。
pub async fn download_installer(
    app: tauri::AppHandle,
    url: String,
    sha256: Option<String>,
) -> Result<String, String> {
    use futures_util::StreamExt;
    use sha2::Digest;
    use tauri::Emitter;

    // 已有完成的同文件(SHA256 命中)直接复用;无校验值时存在即视为上次完成
    let dir = updates_dir();
    let _ = std::fs::create_dir_all(&dir);
    let target = dir.join(file_name_of(&url));
    if target.is_file() {
        let reuse = match &sha256 {
            Some(expect) => verify_sha256(&target, expect),
            None => true,
        };
        if reuse {
            let p = target.to_string_lossy().into_owned();
            let _ = app.emit(
                "update-download-progress",
                DownloadProgress { downloaded: 0, total: None, percent: 100, done: true, error: None },
            );
            return Ok(p);
        }
        // 校验不过:视为损坏缓存,删除后重新下载
        let _ = std::fs::remove_file(&target);
    }

    DOWNLOAD_CANCEL.store(false, std::sync::atomic::Ordering::SeqCst);
    {
        let mut st = DOWNLOAD_STATE.lock().unwrap_or_else(|p| p.into_inner());
        *st = Some(DownloadProgress { downloaded: 0, total: None, percent: 0, done: false, error: None });
    }

    let result = async {
        let http = reqwest::Client::builder()
            .user_agent(USER_AGENT)
            .build()
            .map_err(|e| format!("HTTP 客户端创建失败: {e}"))?;
        let resp = http
            .get(&url)
            .timeout(std::time::Duration::from_secs(600))
            .send()
            .await
            .map_err(|e| format!("连接失败: {e}"))?;
        if !resp.status().is_success() {
            return Err(format!("服务器返回 {}", resp.status()));
        }
        let total = resp.content_length();

        let tmp = target.with_extension("part");
        let mut file = std::fs::File::create(&tmp).map_err(|e| format!("创建文件失败: {e}"))?;
        let mut hasher = sha2::Sha256::new();
        let mut downloaded: u64 = 0;
        let mut last_emit = std::time::Instant::now();

        let mut stream = resp.bytes_stream();
        while let Some(chunk) = stream.next().await {
            if DOWNLOAD_CANCEL.load(std::sync::atomic::Ordering::SeqCst) {
                drop(file);
                let _ = std::fs::remove_file(&tmp);
                return Err("__cancelled__".into());
            }
            let chunk = chunk.map_err(|e| format!("下载中断: {e}"))?;
            hasher.update(&chunk);
            std::io::Write::write_all(&mut file, &chunk).map_err(|e| format!("写入失败: {e}"))?;
            downloaded += chunk.len() as u64;

            // 100ms 节流的进度推送
            if last_emit.elapsed() >= std::time::Duration::from_millis(100) {
                last_emit = std::time::Instant::now();
                let percent = total
                    .map(|t| if t > 0 { (downloaded as f64 / t as f64 * 100.0) as i32 } else { -1 })
                    .unwrap_or(-1);
                let prog = DownloadProgress { downloaded, total, percent, done: false, error: None };
                {
                    let mut st = DOWNLOAD_STATE.lock().unwrap_or_else(|p| p.into_inner());
                    *st = Some(prog.clone());
                }
                let _ = app.emit("update-download-progress", &prog);
            }
        }
        std::io::Write::flush(&mut file).ok();
        drop(file);

        // 校验
        if let Some(expect) = &sha256 {
            let actual = format!("{:x}", hasher.finalize());
            if !actual.eq_ignore_ascii_case(expect) {
                let _ = std::fs::remove_file(&tmp);
                return Err(format!("SHA256 校验失败:期望 {expect},实际 {actual}"));
            }
        }
        std::fs::rename(&tmp, &target).map_err(|e| format!("落盘失败: {e}"))?;

        Ok(target.to_string_lossy().into_owned())
    }
    .await;

    match result {
        Ok(path) => {
            let prog = DownloadProgress { downloaded: 0, total: None, percent: 100, done: true, error: None };
            {
                let mut st = DOWNLOAD_STATE.lock().unwrap_or_else(|p| p.into_inner());
                *st = Some(prog.clone());
            }
            let _ = app.emit("update-download-progress", &prog);
            Ok(path)
        }
        Err(e) => {
            let msg = if e == "__cancelled__" { "已取消".to_string() } else { e.clone() };
            let prog = DownloadProgress { downloaded: 0, total: None, percent: 0, done: true, error: Some(msg) };
            {
                let mut st = DOWNLOAD_STATE.lock().unwrap_or_else(|p| p.into_inner());
                *st = Some(prog.clone());
            }
            let _ = app.emit("update-download-progress", &prog);
            Err(e)
        }
    }
}

/// 校验文件 SHA256(小文件一次性读入;安装包 <200MB 可接受)。
fn verify_sha256(path: &std::path::Path, expect: &str) -> bool {
    use sha2::Digest;
    let Ok(bytes) = std::fs::read(path) else { return false };
    let actual = format!("{:x}", sha2::Sha256::digest(&bytes));
    actual.eq_ignore_ascii_case(expect)
}

/// 启动安装包(独立进程;调用方可随后自行退出应用)。
pub fn run_installer(path: &str) -> Result<(), String> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let path = std::path::Path::new(path).canonicalize().map_err(|e| e.to_string())?;
    let directory = updates_dir().canonicalize().map_err(|e| e.to_string())?;
    if path.parent() != Some(directory.as_path()) || !path.is_file()
        || !path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("exe")) {
        return Err("安装包不在更新缓存目录".into());
    }
    std::process::Command::new(path)
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("启动安装程序失败: {e}"))
}

/// 版本比较:与 C# CompareVersions 一致。>0 表示 v1 更新。
pub fn compare_versions(v1: &str, v2: &str) -> i32 {
    if v1.eq_ignore_ascii_case(v2) {
        return 0;
    }
    let p1: Vec<&str> = v1.split(['.', '-', '_']).filter(|s| !s.is_empty()).collect();
    let p2: Vec<&str> = v2.split(['.', '-', '_']).filter(|s| !s.is_empty()).collect();
    for i in 0..p1.len().max(p2.len()) {
        let a = p1.get(i).copied().unwrap_or("");
        let b = p2.get(i).copied().unwrap_or("");
        match (a.parse::<i64>(), b.parse::<i64>()) {
            (Ok(x), Ok(y)) => {
                if x != y {
                    return if x > y { 1 } else { -1 };
                }
            }
            _ => {
                let (la, lb) = (a.to_lowercase(), b.to_lowercase());
                if la != lb {
                    return if la > lb { 1 } else { -1 };
                }
            }
        }
    }
    0
}

/// 从 GitHub Release 正文中提取 Windows 段更新日志。
/// 约定以二级标题 `## Windows` 分段,只显示对应段落,找不到时回落到全文。
fn read_platform_release_notes(body: &str) -> String {
    let Some(pos) = body.find("## Windows") else {
        return body.trim().to_string();
    };
    let rest = &body[pos + "## Windows".len()..];
    // 跳过本行剩余内容(标题行)
    let rest = rest.split_once('\n').map(|(_, r)| r).unwrap_or(rest);
    let end = rest.find("\n## ").unwrap_or(rest.len());
    rest[..end].trim().to_string()
}

fn http_client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .user_agent(USER_AGENT)
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .map_err(|e| e.to_string())
}

/// 服务器直连通道:GET {origin}/releases/version.json
async fn check_direct(origin: &str, current: &str) -> UpdateCheckResult {
    let client = match http_client() {
        Ok(c) => c,
        Err(e) => return failure(current, e),
    };
    let url = format!("{origin}/releases/version.json");
    let resp = match client.get(&url).send().await {
        Ok(r) => r,
        Err(e) => return failure(current, format!("网络请求失败: {e}")),
    };
    if !resp.status().is_success() {
        return failure(current, format!("HTTP {}", resp.status().as_u16()));
    }
    let root: Value = match resp.json().await {
        Ok(v) => v,
        Err(e) => return failure(current, format!("version.json 解析失败: {e}")),
    };

    direct_release_result(&root, origin, current)
        .unwrap_or_else(|| failure(current, "直连服务器尚未提供 Tauri 更新通道".into()))
}

fn is_tauri_installer(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    name.starts_with("nexclip_tauri_") && name.ends_with("_x64-setup.exe")
}

fn valid_sha256(value: &str) -> Option<String> {
    let value = value.strip_prefix("sha256:").unwrap_or(value);
    (value.len() == 64 && value.bytes().all(|ch| ch.is_ascii_hexdigit())).then(|| value.to_ascii_lowercase())
}

fn direct_release_result(root: &Value, origin: &str, current: &str) -> Option<UpdateCheckResult> {
    // Keep the WinUI calendar-version channel separate from Tauri's SemVer channel.
    let platform = root.get("windowsTauri").filter(|v| v.is_object())?;
    let tag = read_str(Some(platform), &["tag_name", "version"])?;
    let clean_latest = tag.trim_start_matches(['v', 'V']).trim().to_string();
    let clean_current = current.trim_start_matches(['v', 'V']).trim().to_string();

    let title = read_str(Some(platform), &["name"])
        .unwrap_or_else(|| format!("NexClip v{clean_latest}"));
    let body = read_str(Some(platform), &["body"])
        .unwrap_or_default();
    let html_url = read_str(Some(platform), &["html_url"])
        .unwrap_or_else(|| GITHUB_RELEASES_PAGE.to_string());

    let base = format!("{origin}/releases");
    let download_url = read_str(Some(platform), &["url"])
        .or_else(|| {
            read_str(Some(platform), &["filename"]).map(|f| format!("{base}/{f}"))
        })?;
    if !is_tauri_installer(&file_name_of(&download_url)) { return None; }
    let sha256 = read_str(Some(platform), &["sha256"]).and_then(|value| valid_sha256(&value));
    let file_size = platform
        .get("size")
        .and_then(Value::as_i64);

    Some(UpdateCheckResult {
        success: true,
        has_update: compare_versions(&clean_latest, &clean_current) > 0,
        current_version: current.to_string(),
        latest_version: clean_latest,
        release_title: title,
        release_notes: read_platform_release_notes(&body),
        release_url: html_url,
        download_url,
        sha256,
        file_size,
        error_message: None,
    })
}

fn github_release_result(release: &Value, current: &str) -> Option<UpdateCheckResult> {
    if release.get("draft").and_then(Value::as_bool) == Some(true)
        || release.get("prerelease").and_then(Value::as_bool) == Some(true) { return None; }
    let installer = release.get("assets")?.as_array()?.iter().find(|asset|
        asset.get("name").and_then(Value::as_str).is_some_and(is_tauri_installer))?;
    let tag = read_str(Some(release), &["tag_name"])?;
    let latest = tag.trim_start_matches(['v', 'V']).trim();
    let clean_current = current.trim_start_matches(['v', 'V']).trim();
    let body = read_str(Some(release), &["body"]).unwrap_or_default();
    Some(UpdateCheckResult {
        success: true,
        has_update: compare_versions(latest, clean_current) > 0,
        current_version: current.to_string(),
        latest_version: latest.to_string(),
        release_title: read_str(Some(release), &["name"]).unwrap_or_else(|| format!("NexClip Tauri v{latest}")),
        release_notes: read_platform_release_notes(&body),
        release_url: read_str(Some(release), &["html_url"]).unwrap_or_else(|| GITHUB_RELEASES_PAGE.into()),
        download_url: read_str(Some(installer), &["browser_download_url"])?,
        sha256: read_str(Some(installer), &["digest"]).and_then(|value| valid_sha256(&value)),
        file_size: installer.get("size").and_then(Value::as_i64),
        error_message: None,
    })
}

/// GitHub channel: stable Tauri x64 installers only, independent of WinUI releases.
async fn check_github(current: &str) -> UpdateCheckResult {
    let client = match http_client() {
        Ok(c) => c,
        Err(e) => return failure(current, e),
    };
    let list: Option<Value> = async {
        let resp = client
            .get(format!("{GITHUB_RELEASES_API}?per_page=20"))
            .header("Accept", "application/vnd.github.v3+json")
            .send()
            .await
            .ok()?;
        let resp = resp.error_for_status().ok()?;
        resp.json::<Value>().await.ok()
    }
    .await;
    if let Some(Value::Array(releases)) = list {
        for release in &releases {
            if let Some(result) = github_release_result(release, current) { return result; }
        }
        return failure(current, "发布列表中尚无 Tauri 正式版".into());
    }

    let latest: Option<Value> = async {
        let resp = client
            .get(format!("{GITHUB_RELEASES_API}/latest"))
            .header("Accept", "application/vnd.github.v3+json")
            .send()
            .await
            .ok()?;
        let resp = resp.error_for_status().ok()?;
        resp.json::<Value>().await.ok()
    }
    .await;
    match latest {
        Some(release) => {
            github_release_result(&release, current)
                .unwrap_or_else(|| failure(current, "最新发布未包含 Tauri 正式版安装包".into()))
        }
        None => failure(current, "GitHub 请求失败(限流或网络不可用)".into()),
    }
}

/// 供命令层调用:按来源检查更新。
pub async fn check(update_source: &str, server_url: &str, current: &str) -> UpdateCheckResult {
    match update_source {
        "direct" => {
            // 服务器根地址(去掉路径),与 C# ServerDirectBaseUrl 一致
            let origin = if let Some(idx) = server_url.find("://") {
                let rest = &server_url[idx + 3..];
                let end = rest.find('/').unwrap_or(rest.len());
                format!("{}{}", &server_url[..idx + 3], &rest[..end])
            } else {
                server_url.to_string()
            };
            let result = check_direct(&origin, current).await;
            if result.success { result } else { check_github(current).await }
        }
        _ => check_github(current).await,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_compare_numeric_segments() {
        assert_eq!(compare_versions("2026.11.14", "2026.11.13"), 1);
        assert_eq!(compare_versions("2.0.0", "2.0.0"), 0);
        assert_eq!(compare_versions("V2.10.0", "v2.9.1"), 1);
        assert_eq!(compare_versions("1.0", "1.0.1"), -1);
        // 数字段 vs 字符串段:走字符串比较
        assert_eq!(compare_versions("20261112.01", "20261112.01"), 0);
        // C# 语义:"dev" 段与缺失段("")比较,"dev" 更大
        assert_eq!(compare_versions("1.0-dev", "1.0"), 1);
        assert_eq!(compare_versions("1.0", "1.0-dev"), -1);
    }

    #[test]
    fn platform_notes_extraction() {
        let body = "## Windows\n- 修复粘贴\n- 新增设置页\n\n## Android\n- 其他改动";
        assert_eq!(
            read_platform_release_notes(body),
            "- 修复粘贴\n- 新增设置页"
        );
        assert_eq!(read_platform_release_notes("全文更新日志"), "全文更新日志");
        assert_eq!(read_platform_release_notes(""), "");
    }

    #[test]
    fn read_str_skips_blank() {
        let v: Value = serde_json::from_str(r#"{"a":"", "b":"ok"}"#).unwrap();
        assert_eq!(read_str(Some(&v), &["a", "b"]).unwrap(), "ok");
    }

    fn tauri_release() -> Value {
        serde_json::json!({
            "tag_name": "v2.1.0",
            "name": "NexClip Tauri v2.1.0",
            "draft": false,
            "prerelease": false,
            "body": "## Windows\nTauri changes\n\n## Android\nOther changes",
            "assets": [{
                "name": "NexClip_Tauri_2.1.0_x64-setup.exe",
                "browser_download_url": "https://example.com/NexClip_Tauri_2.1.0_x64-setup.exe",
                "digest": format!("sha256:{}", "AB".repeat(32)),
                "size": 1234
            }]
        })
    }

    #[test]
    fn github_tauri_release_preserves_download_integrity() {
        let release = tauri_release();
        let result = github_release_result(&release, "2.0.0").unwrap();
        assert!(result.success && result.has_update);
        assert_eq!(result.latest_version, "2.1.0");
        assert_eq!(result.release_notes, "Tauri changes");
        assert_eq!(result.sha256, Some("ab".repeat(32)));
        assert_eq!(result.file_size, Some(1234));
        assert!(!github_release_result(&release, "2.1.0").unwrap().has_update);
        assert!(!github_release_result(&release, "2.2.0").unwrap().has_update);
    }

    #[test]
    fn github_ignores_other_editions_and_unpublished_releases() {
        for name in ["NexClip_20261112.01_x64-setup.exe", "NexClip_Tauri_2.1.0_arm64-setup.exe", "NexClip.apk"] {
            let mut release = tauri_release();
            release["assets"][0]["name"] = Value::String(name.into());
            assert!(github_release_result(&release, "2.0.0").is_none());
        }
        for field in ["draft", "prerelease"] {
            let mut release = tauri_release();
            release[field] = Value::Bool(true);
            assert!(github_release_result(&release, "2.0.0").is_none());
        }
    }

    #[test]
    fn direct_requires_tauri_manifest_and_installer() {
        let legacy = serde_json::json!({
            "version": "20261112.01", "filename": "NexClip-Setup.exe",
            "windows": {"version": "20261112.01", "filename": "NexClip-Setup.exe"}
        });
        assert!(direct_release_result(&legacy, "https://example.com", "2.0.0").is_none());
        let mut manifest = legacy;
        manifest["windowsTauri"] = serde_json::json!({
            "version": "v2.1.0", "filename": "NexClip_Tauri_2.1.0_x64-setup.exe",
            "sha256": "CD".repeat(32), "size": 5678
        });
        let result = direct_release_result(&manifest, "https://example.com", "2.0.0").unwrap();
        assert!(result.success && result.has_update);
        assert_eq!(result.download_url, "https://example.com/releases/NexClip_Tauri_2.1.0_x64-setup.exe");
        assert_eq!(result.sha256, Some("cd".repeat(32)));
        assert_eq!(result.file_size, Some(5678));
        manifest["windowsTauri"]["filename"] = Value::String("NexClip-Setup.exe".into());
        assert!(direct_release_result(&manifest, "https://example.com", "2.0.0").is_none());
    }

    #[test]
    fn sha256_accepts_only_complete_hex_digests() {
        assert_eq!(valid_sha256(&"AB".repeat(32)), Some("ab".repeat(32)));
        assert_eq!(valid_sha256(&format!("sha256:{}", "ab".repeat(32))), Some("ab".repeat(32)));
        for value in ["", "sha256:", "not-a-hash", &"g".repeat(64), &"a".repeat(63)] {
            assert_eq!(valid_sha256(value), None);
        }
    }
}
