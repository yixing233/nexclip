//! REST 客户端:与旧版 ServerApi 逐端点对齐(X-Device-Id/X-Device-Token 头鉴权)。
//!
//! 错误语义(ApiException 移植):凭证类错误按响应体 code 字段区分,
//! credential_missing(请求没带凭证)不清除本地配对状态,credential_invalid /
//! credential_revoked(凭证失效/被移除)才需要重新配对。

use serde::{Deserialize, Serialize};

const TIMEOUT_SECS: u64 = 15;

/// API 错误(与旧版 ApiException 对齐)。
#[derive(Debug, Clone)]
pub struct ApiError {
    pub message: String,
    pub status: Option<u16>,
    pub code: Option<String>,
}

impl ApiError {
    pub fn is_credential_missing(&self) -> bool {
        self.code.as_deref() == Some("credential_missing")
    }
    pub fn is_credential_invalid(&self) -> bool {
        self.code.as_deref() == Some("credential_invalid")
    }
    pub fn is_credential_revoked(&self) -> bool {
        self.code.as_deref() == Some("credential_revoked") || self.status == Some(410)
    }
    /// 凭证失效/被移除(需要重新配对)。
    pub fn is_credential_dead(&self) -> bool {
        self.is_credential_invalid() || self.is_credential_revoked()
    }
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

/// 服务端条目(GET/PUT /api/clipboard;System.Text.Json Web 默认 = camelCase)。
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct ClipboardEntry {
    #[serde(alias = "Id")]
    pub id: i64,
    #[serde(alias = "Starred")]
    pub starred: bool,
    #[serde(alias = "Remark")]
    pub remark: Option<String>,
    /// Text | Image(文件条目仅存本地,不经过服务端)
    #[serde(rename = "type", alias = "Type")]
    pub entry_type: String,
    #[serde(alias = "Text")]
    pub text: Option<String>,
    #[serde(alias = "Html")]
    pub html: Option<String>,
    #[serde(alias = "ImageRef")]
    pub image_ref: Option<String>,
    #[serde(alias = "DeviceId")]
    pub device_id: String,
    #[serde(alias = "DeviceName")]
    pub device_name: Option<String>,
    #[serde(alias = "IsManual")]
    pub is_manual: bool,
    /// ISO-8601 UTC
    #[serde(alias = "CreatedAt")]
    pub created_at: Option<String>,
}

/// 设备列表项(GET /api/devices)。
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct DeviceInfo {
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Name")]
    pub name: Option<String>,
    #[serde(alias = "Platform")]
    pub platform: Option<String>,
    #[serde(alias = "Ip")]
    pub ip: Option<String>,
    #[serde(alias = "Version")]
    pub version: Option<String>,
    #[serde(alias = "Online")]
    pub online: bool,
    #[serde(alias = "LastSeenAt")]
    pub last_seen_at: Option<String>,
    #[serde(alias = "IsCurrent")]
    pub is_current: bool,
}

/// 文件互传条目(POST /api/files 返回 + FileTransferred 推送)。
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct FileTransferInfo {
    #[serde(alias = "FileId")]
    pub file_id: String,
    #[serde(alias = "FileName")]
    pub file_name: String,
    #[serde(alias = "Size")]
    pub size: i64,
    #[serde(alias = "ContentHash")]
    pub content_hash: String,
    #[serde(alias = "DeviceId")]
    pub device_id: String,
    #[serde(alias = "DeviceName")]
    pub device_name: Option<String>,
    #[serde(alias = "CreatedAt")]
    pub created_at: Option<String>,
    #[serde(alias = "DownloadUrl")]
    pub download_url: String,
}

fn endpoint(server_url: &str, path: &str) -> String {
    format!("{}/{}", server_url.trim_end_matches('/'), path.trim_start_matches('/'))
}

fn client() -> Result<reqwest::Client, ApiError> {
    reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(TIMEOUT_SECS))
        .build()
        .map_err(|e| ApiError { message: e.to_string(), status: None, code: None })
}

/// 解析错误响应 → (给用户看的描述, 机器可读 code)。
/// 401 的文案与 code 分离:调用方需要 code 区分"缺少凭证"与"凭证无效"。
async fn read_error_detail(resp: reqwest::Response) -> ApiError {
    let status = resp.status().as_u16();
    let body = resp.text().await.unwrap_or_default();
    let mut server_error = None;
    let mut code = None;
    if !body.trim().is_empty() {
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(&body) {
            if let Some(e) = v.get("error").and_then(|x| x.as_str()) {
                if !e.trim().is_empty() {
                    server_error = Some(e.to_string());
                }
            }
            if let Some(c) = v.get("code").and_then(|x| x.as_str()) {
                code = Some(c.to_string());
            }
        }
    }
    let message = if let Some(e) = server_error {
        format!("服务器拒绝请求:{e}")
    } else if status == 401 {
        "服务器拒绝请求:设备未配对、配对已失效或设备已被移除,请重新完成配对。".into()
    } else {
        let status_message = match status {
            400 => "请求参数无效",
            403 => "没有权限执行此操作",
            404 => "服务器接口不存在,请确认服务端版本",
            409 => "请求与服务器当前状态冲突",
            410 => "设备已被移除,请重新完成配对",
            413 => "请求内容超过服务器允许的大小",
            429 => "请求过于频繁,请稍后重试",
            s if s >= 500 => "服务器内部发生错误",
            s => return ApiError {
                message: format!("服务器返回 {s}"),
                status: Some(status),
                code,
            },
        };
        format!("服务器请求失败:{status_message}。")
    };
    ApiError { message, status: Some(status), code }
}

/// 带设备鉴权的请求执行(统一错误转换)。
async fn send(
    req: reqwest::RequestBuilder,
    device_id: &str,
    token: &str,
) -> Result<reqwest::Response, ApiError> {
    send_allowing(req, device_id, token, &[]).await
}

async fn send_allowing(
    req: reqwest::RequestBuilder, device_id: &str, token: &str, allowed: &[u16],
) -> Result<reqwest::Response, ApiError> {
    let req = if !device_id.trim().is_empty() && !token.trim().is_empty() {
        req.header("X-Device-Id", device_id).header("X-Device-Token", token)
    } else {
        req
    };
    let resp = req.send().await.map_err(|e| ApiError {
        message: describe_transport_error(&e),
        status: None,
        code: None,
    })?;
    if resp.status().is_success() || allowed.contains(&resp.status().as_u16()) {
        Ok(resp)
    } else {
        Err(read_error_detail(resp).await)
    }
}

async fn send_json<T: serde::de::DeserializeOwned>(
    req: reqwest::RequestBuilder,
    device_id: &str,
    token: &str,
) -> Result<T, ApiError> {
    let resp = send(req, device_id, token).await?;
    resp.json::<T>().await.map_err(|e| ApiError {
        message: format!("服务器返回的数据格式无效:{e}"),
        status: None,
        code: None,
    })
}

/// 把 reqwest 传输错误转成用户可读文案(与旧版 DescribeException 对齐)。
fn describe_transport_error(e: &reqwest::Error) -> String {
    if e.is_timeout() {
        return "服务器响应超时,请确认服务器正在运行,并检查当前网络连接。".into();
    }
    if e.is_connect() {
        if e.to_string().to_lowercase().contains("dns") {
            return "无法找到服务器主机,请检查服务器地址和 DNS 设置。".into();
        }
        return "无法连接到服务器,请检查服务器地址、端口、网络和防火墙设置。".into();
    }
    if e.is_decode() {
        return "服务器返回的数据格式无效,请确认服务端版本与客户端兼容。".into();
    }
    format!("请求失败:{e}")
}

/// MIME 猜测(注册表查值待后续;扩展名回退表与旧版一致)。
pub fn guess_content_type(file_name: &str) -> &'static str {
    let ext = file_name.rsplit('.').next().unwrap_or("").to_lowercase();
    match ext.as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "pdf" => "application/pdf",
        "zip" => "application/zip",
        "txt" | "md" | "log" => "text/plain",
        "mp3" => "audio/mpeg",
        "mp4" => "video/mp4",
        _ => "application/octet-stream",
    }
}

// ---- 剪贴板同步 ----

/// PUT /api/clipboard:上传文本(html 非空时一并上传富文本片段)。
/// 内容未变化(服务端返回 unchanged)返回 None。
pub async fn put_text(
    server_url: &str, token: &str, text: &str,
    device_id: &str, device_name: &str,
    platform: &str, version: &str, is_manual: bool, html: Option<&str>,
) -> Result<Option<ClipboardEntry>, ApiError> {
    let payload = serde_json::json!({
        "type": "Text", "text": text, "html": html,
        "deviceId": device_id, "deviceName": device_name,
        "platform": platform, "version": version, "isManual": is_manual,
    });
    let json: serde_json::Value = send_json(
        client()?.put(endpoint(server_url, "/api/clipboard")).json(&payload),
        device_id, token,
    ).await?;
    if json.get("unchanged").and_then(|u| u.as_bool()) == Some(true) {
        return Ok(None);
    }
    Ok(serde_json::from_value(json).ok())
}

/// POST /api/clipboard/send:发送文本给指定的一批目标设备(即时互传)。
pub async fn send_to_devices(
    server_url: &str, token: &str, text: &str,
    device_id: &str, device_name: &str, target_device_ids: &[String],
) -> Result<Option<ClipboardEntry>, ApiError> {
    let payload = serde_json::json!({
        "text": text, "deviceId": device_id, "deviceName": device_name,
        "deviceIds": target_device_ids, "isManual": true,
    });
    let json: serde_json::Value = send_json(
        client()?.post(endpoint(server_url, "/api/clipboard/send")).json(&payload),
        device_id, token,
    ).await?;
    Ok(serde_json::from_value(json).ok())
}

/// POST /api/clipboard/image:上传图片(multipart)。
pub async fn upload_image(
    server_url: &str, token: &str, png_bytes: Vec<u8>,
    device_id: &str, device_name: &str,
    platform: &str, version: &str, is_manual: bool,
) -> Result<Option<ClipboardEntry>, ApiError> {
    if png_bytes.len() > crate::capture::MAX_IMAGE_BYTES {
        return Err(ApiError { message: "图片超过 10MB 同步上限".into(), status: Some(413), code: None });
    }
    let part = reqwest::multipart::Part::bytes(png_bytes)
        .file_name("clipboard.png")
        .mime_str("image/png")
        .map_err(|e| ApiError { message: e.to_string(), status: None, code: None })?;
    let mut form = reqwest::multipart::Form::new()
        .part("file", part)
        .text("deviceId", device_id.to_string())
        .text("deviceName", device_name.to_string());
    if is_manual {
        form = form.text("isManual", "true");
    }
    if !platform.trim().is_empty() {
        form = form.text("platform", platform.to_string());
    }
    if !version.trim().is_empty() {
        form = form.text("version", version.to_string());
    }
    let json: serde_json::Value = send_json(
        client()?.post(endpoint(server_url, "/api/clipboard/image")).multipart(form),
        device_id, token,
    ).await?;
    Ok(serde_json::from_value(json).ok())
}

/// GET /api/clipboard/history:远端历史(不含图片字节)。
pub async fn get_remote_history(
    server_url: &str, device_id: &str, token: &str,
    offset: i64, limit: i64, query: Option<&str>,
) -> Result<Vec<ClipboardEntry>, ApiError> {
    let mut path = format!("/api/clipboard/history?offset={offset}&limit={limit}");
    if let Some(q) = query.map(str::trim).filter(|q| !q.is_empty()) {
        path += &format!("&q={}", urlencode(q));
    }
    let json: serde_json::Value = send_json(
        client()?.get(endpoint(server_url, &path)),
        device_id, token,
    ).await?;
    Ok(json
        .get("items")
        .and_then(|i| i.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|el| serde_json::from_value::<ClipboardEntry>(el.clone()).ok())
                .collect()
        })
        .unwrap_or_default())
}

/// PUT /api/clipboard/{id}:收藏/备注局部更新(sendRemark 才提交 remark,避免清掉别端写的备注)。
pub async fn update_entry_metadata(
    server_url: &str, device_id: &str, token: &str,
    id: i64, starred: Option<bool>, remark: Option<Option<&str>>,
) -> Result<(), ApiError> {
    let mut payload = serde_json::Map::new();
    if let Some(st) = starred {
        payload.insert("starred".into(), serde_json::Value::Bool(st));
    }
    if let Some(r) = remark {
        payload.insert("remark".into(), r.map(|v| serde_json::Value::String(v.to_string())).unwrap_or(serde_json::Value::Null));
    }
    send(
        client()?.put(endpoint(server_url, &format!("/api/clipboard/{id}"))).json(&payload),
        device_id, token,
    ).await?;
    Ok(())
}

/// GET /api/images/{ref}:下载图片字节(404 → None)。
pub async fn download_image(
    server_url: &str, device_id: &str, token: &str, image_ref: &str,
) -> Result<Option<Vec<u8>>, ApiError> {
    let resp = send_allowing(
        client()?.get(endpoint(server_url, &format!("/api/images/{}", image_ref.trim_start_matches('/')))),
        device_id, token, &[404],
    ).await?;
    let status = resp.status().as_u16();
    if status == 404 || status == 204 {
        return Ok(None);
    }
    resp.bytes().await.map(|b| Some(b.to_vec())).map_err(|e| ApiError {
        message: format!("图片下载失败: {e}"),
        status: Some(status),
        code: None,
    })
}

// ---- 文件互传 ----

/// POST /api/files:上传任意文件(独立于剪贴板历史)。
pub async fn upload_file(
    server_url: &str, token: &str, file_path: &str,
    device_id: &str, device_name: &str, platform: &str, version: &str,
    target_device_ids: &[String],
) -> Result<Option<FileTransferInfo>, ApiError> {
    let file_name = std::path::Path::new(file_path)
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| file_path.to_string());
    let bytes = std::fs::read(file_path).map_err(|e| ApiError {
        message: format!("读取文件失败: {e}"),
        status: None,
        code: None,
    })?;
    let part = reqwest::multipart::Part::bytes(bytes)
        .file_name(file_name.clone())
        .mime_str(guess_content_type(&file_name))
        .map_err(|e| ApiError { message: e.to_string(), status: None, code: None })?;
    let mut form = reqwest::multipart::Form::new()
        .part("file", part)
        .text("deviceId", device_id.to_string())
        .text("deviceName", device_name.to_string())
        .text("deviceIds", target_device_ids.join(","));
    if !platform.trim().is_empty() {
        form = form.text("platform", platform.to_string());
    }
    if !version.trim().is_empty() {
        form = form.text("version", version.to_string());
    }
    let json: serde_json::Value = send_json(
        client()?.post(endpoint(server_url, "/api/files")).multipart(form),
        device_id, token,
    ).await?;
    Ok(serde_json::from_value(json).ok())
}

/// GET /api/files/{id}/download:下载文件内容(404/410/204 → None,服务端文件已丢失)。
pub async fn download_file(
    server_url: &str, device_id: &str, token: &str, file_id: &str,
) -> Result<Option<Vec<u8>>, ApiError> {
    let resp = send_allowing(
        client()?.get(endpoint(server_url, &format!("/api/files/{file_id}/download"))),
        device_id, token, &[404, 410],
    ).await?;
    let status = resp.status().as_u16();
    if status == 404 || status == 410 || status == 204 {
        return Ok(None);
    }
    resp.bytes().await.map(|b| Some(b.to_vec())).map_err(|e| ApiError {
        message: format!("文件下载失败: {e}"),
        status: Some(status),
        code: None,
    })
}

// ---- 配对 ----

/// 配对码生成结果(POST /api/pairing-codes)。
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct PairingCodeResult {
    #[serde(alias = "Code")]
    pub code: String,
    /// ISO-8601 UTC
    #[serde(alias = "ExpiresAt")]
    pub expires_at: Option<String>,
    #[serde(alias = "QrPayload")]
    pub qr_payload: Option<String>,
    #[serde(alias = "UserId")]
    pub user_id: Option<String>,
}

/// POST /api/pairing-codes:生成一次性配对码(一码一设备,10 分钟)。
pub async fn create_pairing_code(
    server_url: &str, device_id: &str, device_name: &str, device_token: &str,
) -> Result<PairingCodeResult, ApiError> {
    let payload = serde_json::json!({ "deviceId": device_id, "deviceName": device_name });
    send_json(
        client()?.post(endpoint(server_url, "/api/pairing-codes")).json(&payload),
        device_id, device_token,
    ).await
}

/// DELETE /api/pairing-codes/{code}:作废配对码(204/404 静默成功)。
pub async fn revoke_pairing_code(
    server_url: &str, code: &str, device_id: &str, device_token: &str,
) -> Result<(), ApiError> {
    let resp = send(
        client()?.delete(endpoint(
            server_url,
            &format!("/api/pairing-codes/{}", urlencode(code)),
        )),
        device_id,
        device_token,
    ).await?;
    let status = resp.status().as_u16();
    if status == 204 || status == 404 {
        return Ok(());
    }
    Err(read_error_detail(resp).await)
}

/// POST /api/pair:6 位码配对,返回 (deviceToken, status, userId)。
pub async fn pair(
    server_url: &str, code: &str, device_id: &str, device_name: &str, platform: &str,
) -> Result<(String, String, String), ApiError> {
    let payload = serde_json::json!({
        "code": code.trim(), "deviceId": device_id,
        "deviceName": device_name, "platform": platform,
    });
    let json: serde_json::Value = send_json(
        client()?.post(endpoint(server_url, "/api/pair")).json(&payload),
        "", "",
    ).await?;
    Ok((
        json.get("deviceToken").and_then(|v| v.as_str()).unwrap_or("").to_string(),
        json.get("status").and_then(|v| v.as_str()).unwrap_or("approved").to_string(),
        json.get("userId").and_then(|v| v.as_str()).unwrap_or("").to_string(),
    ))
}

/// GET /api/devices:设备列表(鉴权失败返回空列表,与旧版一致)。
pub async fn get_devices(
    server_url: &str, device_id: &str, token: &str,
) -> Result<Vec<DeviceInfo>, ApiError> {
    let url = format!(
        "{}/api/devices?deviceId={}",
        server_url.trim_end_matches('/'),
        urlencode(device_id)
    );
    match send_json::<Vec<DeviceInfo>>(client()?.get(url), device_id, token).await {
        Ok(list) => Ok(list),
        Err(e) if e.status == Some(401) || e.status == Some(410) => Ok(Vec::new()),
        Err(e) => Err(e),
    }
}

/// DELETE /api/devices/{id}:移除/注销指定设备。
pub async fn remove_device(
    server_url: &str, actor_device_id: &str, token: &str, target_device_id: &str,
) -> Result<(), ApiError> {
    send(
        client()?.delete(endpoint(
            server_url,
            &format!("/api/devices/{}", urlencode(target_device_id)),
        )),
        actor_device_id,
        token,
    )
    .await?;
    Ok(())
}

/// PUT /api/devices/{id}:修改指定设备名称。
pub async fn rename_device(
    server_url: &str, actor_device_id: &str, token: &str,
    target_device_id: &str, new_name: &str,
) -> Result<(), ApiError> {
    let payload = serde_json::json!({ "name": new_name.trim() });
    send(
        client()?.put(endpoint(
            server_url,
            &format!("/api/devices/{}", urlencode(target_device_id)),
        )).json(&payload),
        actor_device_id,
        token,
    )
    .await?;
    Ok(())
}

/// GET /api/health:连接测试 → (成功?, 描述含延迟)。
pub async fn test_connection(server_url: &str) -> Result<(bool, String), ApiError> {
    if server_url.trim().is_empty() {
        return Ok((false, "请先输入服务器地址。".into()));
    }
    let start = std::time::Instant::now();
    let resp = client()?.get(endpoint(server_url, "/api/health")).send().await;
    let elapsed = start.elapsed().as_millis();
    match resp {
        Ok(r) => {
            let status = r.status().as_u16();
            if r.status().is_success() {
                Ok((true, format!("连接成功!服务器响应正常(延迟 {elapsed}ms)")))
            } else if status == 404 || status == 401 || status == 204 {
                Ok((true, format!("连接成功!服务器已响应(延迟 {elapsed}ms)")))
            } else {
                Ok((false, format!("服务器返回异常状态码:{status}")))
            }
        }
        Err(e) => Ok((false, describe_transport_error(&e))),
    }
}

fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mime_guess() {
        assert_eq!(guess_content_type("a.png"), "image/png");
        assert_eq!(guess_content_type("b.JPEG"), "image/jpeg");
        assert_eq!(guess_content_type("noext"), "application/octet-stream");
        assert_eq!(guess_content_type("x.unknown"), "application/octet-stream");
    }

    #[test]
    fn endpoint_join() {
        assert_eq!(endpoint("http://x:5033/", "/api/health"), "http://x:5033/api/health");
        assert_eq!(endpoint("http://x:5033", "api/health"), "http://x:5033/api/health");
    }

    #[test]
    fn error_code_semantics() {
        let missing = ApiError { message: String::new(), status: Some(401), code: Some("credential_missing".into()) };
        let invalid = ApiError { message: String::new(), status: Some(401), code: Some("credential_invalid".into()) };
        let revoked = ApiError { message: String::new(), status: Some(410), code: None };
        // 缺少凭证 ≠ 凭证失效:不能清本地配对(2026-10-23 线上教训)
        assert!(missing.is_credential_missing() && !missing.is_credential_dead());
        assert!(invalid.is_credential_dead());
        assert!(revoked.is_credential_dead());
    }

    #[test]
    fn clipboard_entry_accepts_pascal_case_payload() {
        // 服务端/旧客户端可能返回 PascalCase;若字段被默忽略,
        // IsManual 会变成 false,互传消息随后被 chat_query 过滤掉。
        let v = serde_json::json!({
            "Id": 12357,
            "Type": "Text",
            "Text": "333826",
            "DeviceId": "e93f1cf5-89cf-49b7-b9d1-4a0c072cab0f",
            "DeviceName": "小米 14",
            "IsManual": true,
            "CreatedAt": "2026-11-18T16:31:39Z"
        });
        let entry: ClipboardEntry = serde_json::from_value(v).unwrap();
        assert_eq!(entry.id, 12357);
        assert_eq!(entry.entry_type, "Text");
        assert_eq!(entry.text.as_deref(), Some("333826"));
        assert_eq!(entry.device_name.as_deref(), Some("小米 14"));
        assert!(entry.is_manual);
    }

    #[test]
    fn clipboard_entry_accepts_camel_case_payload() {
        let v = serde_json::json!({
            "id": 1,
            "type": "Text",
            "text": "hello",
            "deviceId": "peer",
            "deviceName": "Phone",
            "isManual": true
        });
        let entry: ClipboardEntry = serde_json::from_value(v).unwrap();
        assert_eq!(entry.id, 1);
        assert!(entry.is_manual);
        assert_eq!(entry.device_id, "peer");
    }
}
