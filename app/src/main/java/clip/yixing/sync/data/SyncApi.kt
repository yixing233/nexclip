package clip.yixing.sync.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class ClipboardEntry(
    val id: Long,
    val type: String,
    val text: String?,
    val imageRef: String?,
    val deviceId: String,
    val deviceName: String?,
    val createdAt: String,
    val isManual: Boolean = false,
) {
    val isImage: Boolean get() = type == "Image"
}

/** 服务端登记的设备(online = LastSeenAt 在服务端阈值内) */
data class DeviceInfo(
    val id: String,
    val name: String,
    val platform: String,
    val ip: String?,
    val version: String?,
    val online: Boolean,
    val userId: String?,
    val bound: Boolean,
    val lastSeenAt: String,
)

/** 配对结果(POST /api/pair → approved + 一次性设备凭证) */
data class PairStatus(val status: String, val deviceToken: String? = null)

/**
 * 文件互传条目(GET /api/files、POST /api/files 返回的元数据)。
 *
 * 与 ClipboardEntry 的关键区别:文件不在剪贴板历史里,接收端收到
 * FileTransferred 推送后应展示为「文件」而非写入剪贴板/自动粘贴。
 */
data class FileTransferInfo(
    val fileId: String,
    val fileName: String,
    val size: Long,
    val contentHash: String,
    val deviceId: String,
    val deviceName: String?,
    val createdAt: String,
    val downloadUrl: String,
)

/** 按扩展名猜 MIME(上传分片用;服务端只存字节,不依赖该值) */
internal fun mimeTypeOf(fileName: String): String {
    val ext = fileName.substringAfterLast('.', "").lowercase()
    return when (ext) {
        "png" -> "image/png"; "jpg", "jpeg" -> "image/jpeg"; "gif" -> "image/gif"
        "webp" -> "image/webp"; "pdf" -> "application/pdf"
        "zip" -> "application/zip"; "7z" -> "application/x-7z-compressed"
        "mp3" -> "audio/mpeg"; "mp4" -> "video/mp4"
        "txt", "md", "log" -> "text/plain"
        "doc", "docx" -> "application/msword"
        "xls", "xlsx" -> "application/vnd.ms-excel"
        else -> "application/octet-stream"
    }
}


/** 6 位纯数字配对码(POST /api/pairing-codes → { code, expiresAt, userId }) */
data class PairingCode(val code: String, val expiresAt: String, val userId: String, val deviceToken: String? = null, val qrPayload: String? = null)

/**
 * 服务端返回的错误。
 *
 * [code] 是服务端给出的机器可读错误码(响应体的 code 字段),可能为 null(老服务端)。
 * 需要它的原因:凭证类错误的状态码有歧义 ——
 *   "缺少凭证"(客户端没带) 与 "凭证无效"(真的失效) 都是 401;
 *   而 403 表示"上传设备身份与凭证不匹配",与凭证有效性无关。
 * 只看状态码就清空本地配对,会把仍然有效的凭证误删,用户被迫重新配对。
 */
class ApiException(
    message: String,
    val statusCode: Int? = null,
    val code: String? = null,
) : Exception(message) {
    /** 服务端"缺少设备凭证":不是凭证失效,不应清除本地状态。 */
    val isCredentialMissing: Boolean get() = code == "credential_missing"
    /** 服务端"设备凭证无效":需要用户重新配对。 */
    val isCredentialInvalid: Boolean get() = code == "credential_invalid"
    /** 服务端"设备已被移除":需要用户重新配对。 */
    val isCredentialRevoked: Boolean get() = code == "credential_revoked"
    /** 服务端"上传设备身份与凭证不匹配"(403):凭证本身有效,属参数问题。 */
    val isDeviceIdentityMismatch: Boolean get() = code == "device_identity_mismatch"
}

/** REST 客户端:设备同步接口使用 X-Device-Id/X-Device-Token 凭证。 */
class SyncApi(
    private val serverUrl: String,
    private val authDeviceId: String = "",
    private val authDeviceToken: String = "",
) {
    private val client = OkHttpClient.Builder()
        .proxy(java.net.Proxy.NO_PROXY)
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    private fun builder(method: String, path: String): Request.Builder =
        Request.Builder().url(serverUrl.trimEnd('/') + path).apply {
            if (authDeviceId.isNotBlank() && authDeviceToken.isNotBlank()) {
                header("X-Device-Id", authDeviceId)
                header("X-Device-Token", authDeviceToken)
            }
        }

    private fun parseEntry(o: JSONObject) = ClipboardEntry(
        id = o.optLong("id"),
        type = o.optString("type", "Text"),
        text = if (o.isNull("text")) null else o.optString("text"),
        imageRef = if (o.isNull("imageRef")) null else o.optString("imageRef"),
        deviceId = o.optString("deviceId"),
        deviceName = if (o.isNull("deviceName")) null else o.optString("deviceName"),
        createdAt = o.optString("createdAt"),
        isManual = o.optBoolean("isManual", false),
    )

    private fun parseFileTransferInfo(o: JSONObject?) = o?.let {
        FileTransferInfo(
            fileId = it.getString("fileId"),
            fileName = it.getString("fileName"),
            size = it.optLong("size"),
            contentHash = it.optString("contentHash"),
            deviceId = it.optString("deviceId"),
            deviceName = if (it.isNull("deviceName")) null else it.optString("deviceName"),
            createdAt = it.optString("createdAt"),
            downloadUrl = it.optString("downloadUrl"),
        )
    }

    private fun execute(req: Request): okhttp3.Response = try {
        val resp = client.newCall(req).execute()
        if (!resp.isSuccessful) {
            val body = resp.body?.string()
            var serverMsg: String? = null
            var errorCode: String? = null
            try {
                val o = JSONObject(body ?: "{}")
                serverMsg = o.optString("error").takeIf { it.isNotBlank() }
                errorCode = o.optString("code").takeIf { it.isNotBlank() }
            } catch (_: Exception) {
                // 响应体不是 JSON:保持两个字段为 null
            }
            val finalMsg = serverMsg ?: httpStatusText(resp.code)
            throw ApiException(finalMsg, resp.code, errorCode)
        }
        resp
    } catch (e: ApiException) {
        throw e
    } catch (e: java.io.IOException) {
        throw ApiException(networkErrorText(e))
    }

    /** HTTP 状态码 → 中文提示 */
    private fun httpStatusText(code: Int): String = when (code) {
        400 -> "请求无效(400),请检查输入"
        401 -> "设备凭证无效或已失效(401),请重新配对"
        403 -> "没有权限(403)"
        404 -> "接口不存在(404),请检查服务器版本"
        409 -> "请求冲突(409)"
        410 -> "设备已被移除(410),请重新配对"
        429 -> "操作过于频繁(429),请稍后再试"
        500 -> "服务器内部错误(500)"
        502 -> "网关错误(502)"
        503 -> "服务暂不可用(503)"
        else -> "服务器返回错误(" + code + ")"
    }

    /** 网络异常 → 中文提示(不暴露英文底层消息) */
    private fun networkErrorText(e: java.io.IOException): String = when (e) {
        is java.net.UnknownHostException -> "无法连接服务器:地址无法解析,请检查服务器地址"
        is java.net.ConnectException -> "无法连接服务器:连接被拒绝,请确认服务端已启动"
        is java.net.SocketTimeoutException -> "连接超时,请检查网络或服务器地址"
        is javax.net.ssl.SSLException -> "安全连接失败,请检查服务器是否使用 HTTPS"
        else -> "网络错误,无法连接服务器"
    }

    /** GET /api/clipboard → 当前条目或 null(204) */
    fun getCurrent(): ClipboardEntry? {
        val resp = execute(builder("GET", "/api/clipboard").build())
        resp.use {
            if (it.code == 204) return null
            return parseEntry(JSONObject(it.body?.string() ?: "{}"))
        }
    }

    /** PUT /api/clipboard → 上传文本,返回条目(unchanged 时仍是当前条目);platform/version 用于服务端设备登记 */
    fun putText(
        text: String,
        deviceId: String,
        deviceName: String,
        platform: String = "Android",
        version: String = android.os.Build.VERSION.RELEASE,
        isManual: Boolean = false,
    ): ClipboardEntry {
        val json = JSONObject().apply {
            put("type", "Text"); put("text", text)
            put("deviceId", deviceId); put("deviceName", deviceName)
            put("platform", platform); put("version", version)
            put("isManual", isManual)
        }.toString()
        val req = builder("PUT", "/api/clipboard")
            .header("Content-Type", "application/json")
            .put(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use { return parseEntry(JSONObject(it.body?.string() ?: "{}")) }
    }

    /** POST /api/clipboard/send → 发送给指定目标设备 */
    fun sendToDevices(
        text: String,
        deviceId: String,
        deviceName: String,
        targetDeviceIds: List<String>,
    ): ClipboardEntry {
        val json = JSONObject().apply {
            put("text", text)
            put("deviceId", deviceId)
            put("deviceName", deviceName)
            put("deviceIds", JSONArray(targetDeviceIds))
            put("isManual", true)
        }.toString()
        val req = builder("POST", "/api/clipboard/send")
            .header("Content-Type", "application/json")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use { return parseEntry(JSONObject(it.body?.string() ?: "{}")) }
    }

    /** GET /api/clipboard/history?offset&limit */
    fun getHistory(offset: Int = 0, limit: Int = 20): Pair<List<ClipboardEntry>, Int> {
        val resp = execute(builder("GET", "/api/clipboard/history?offset=" + offset + "&limit=" + limit).build())
        resp.use {
            val o = JSONObject(it.body?.string() ?: "{}")
            val arr = o.optJSONArray("items") ?: JSONArray()
            val items = (0 until arr.length()).map { i -> parseEntry(arr.getJSONObject(i)) }
            return items to o.optInt("total", items.size)
        }
    }

    /** GET /api/devices → 设备列表(服务端按 LastSeenAt 计算 online) */
    fun getDevices(): List<DeviceInfo> {
        val resp = execute(builder("GET", "/api/devices").build())
        resp.use {
            val arr = JSONArray(it.body?.string() ?: "[]")
            return (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                DeviceInfo(
                    id = o.optString("id"),
                    name = o.optString("name", "未知设备"),
                    platform = o.optString("platform", "Unknown"),
                    ip = normalizeIp(if (o.isNull("ip")) null else o.optString("ip")),
                    version = if (o.isNull("version")) null else o.optString("version"),
                    online = o.optBoolean("online"),
                    userId = if (o.isNull("userId")) null else o.optString("userId"),
                    bound = o.optBoolean("bound", false),
                    lastSeenAt = o.optString("lastSeenAt"),
                )
            }
        }
    }

    /** PUT /api/devices/{id} → 重命名设备 */
    fun updateDeviceName(deviceId: String, name: String): Boolean {
        val json = JSONObject().apply { put("name", name) }.toString()
        val req = builder("PUT", "/api/devices/" + java.net.URLEncoder.encode(deviceId, "UTF-8"))
            .header("Content-Type", "application/json")
            .put(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use { return it.isSuccessful }
    }

    /** DELETE /api/devices/{id} → 移除设备 */
    fun deleteDevice(deviceId: String): Boolean {
        val req = builder("DELETE", "/api/devices/" + java.net.URLEncoder.encode(deviceId, "UTF-8")).build()
        val resp = execute(req)
        resp.use { return it.isSuccessful }
    }

    /** POST /api/pairing-codes → 生成一次性配对码(使用当前设备令牌,已配对即可生成) */
    /** 生成一次性配对码:免认证;携带本机设备信息,服务端同步登记生成方设备 */
    fun createPairingCode(deviceId: String, deviceName: String): PairingCode {
        val json = JSONObject().apply {
            put("deviceId", deviceId)
            put("deviceName", deviceName)
        }.toString()
        val req = builder("POST", "/api/pairing-codes")
            .header("Content-Type", "application/json")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use {
            val o = JSONObject(it.body?.string() ?: "{}")
            return PairingCode(
                code = o.optString("code"),
                expiresAt = o.optString("expiresAt"),
                userId = o.optString("userId"),
                deviceToken = o.optString("deviceToken").takeIf { it.isNotBlank() },
            )
        }
    }

    /** DELETE /api/pairing-codes/{code} → 作废配对码(关闭底部弹层后调用,码立即失效) */
    fun revokePairingCode(code: String) {
        runCatching {
            val req = builder("DELETE", "/api/pairing-codes/" + java.net.URLEncoder.encode(code, "UTF-8")).build()
            execute(req).use { }
        }
    }

    /** POST /api/pair → 6 位纯数字验证码 / 扫码单向即入配对 (无需二次确认) */
    fun pair(code: String, deviceId: String, deviceName: String, platform: String = "Android"): PairStatus {
        val json = JSONObject().apply {
            put("code", code.trim())
            put("deviceId", deviceId)
            put("deviceName", deviceName)
            put("platform", platform)
        }.toString()
        val req = builder("POST", "/api/pair")
            .header("Content-Type", "application/json")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use {
            val o = JSONObject(it.body?.string() ?: "{}")
            return PairStatus(
                status = o.optString("status", "approved"),
                deviceToken = o.optString("deviceToken").takeIf { it.isNotBlank() }
            )
        }
    }

    /** 别名 */
    fun pairDirect(code: String, deviceId: String, deviceName: String, platform: String = "Android"): PairStatus
        = pair(code, deviceId, deviceName, platform)

    /** POST /api/clipboard/image(multipart) */
    /**
     * 上传图片。
     *
     * [mimeType] 决定 multipart 的 Content-Type 与文件名后缀 —— 服务端按扩展名选择
     * 存储文件名与响应 Content-Type,所以压缩成 JPEG 后必须传 "image/jpeg",
     * 否则会被当成 png 存下来,下载端拿到错误的类型。
     */
    fun uploadImage(
        bytes: ByteArray,
        deviceId: String,
        deviceName: String,
        isManual: Boolean = false,
        mimeType: String = "image/png",
    ): ClipboardEntry {
        val isJpeg = mimeType.contains("jpeg", ignoreCase = true)
        val fileName = if (isJpeg) "clipboard.jpg" else "clipboard.png"
        val builder = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", fileName, bytes.toRequestBody(mimeType.toMediaType()))
            .addFormDataPart("deviceId", deviceId)
            .addFormDataPart("deviceName", deviceName)
            .addFormDataPart("platform", "Android")
            .addFormDataPart("version", android.os.Build.VERSION.RELEASE)
        if (isManual) {
            builder.addFormDataPart("isManual", "true")
        }
        val req = builder("POST", "/api/clipboard/image").post(builder.build()).build()
        val resp = execute(req)
        resp.use { return parseEntry(JSONObject(it.body?.string() ?: "{}")) }
    }

    /** GET /api/images/{ref} */
    fun downloadImage(ref: String): ByteArray? {
        val req = builder("GET", "/api/images/" + ref.trimStart('/')).build()
        val resp = execute(req)
        resp.use { return it.body?.bytes() }
    }

    // ---------- 文件互传(/api/files, 2026-11-13 服务端新增) ----------

    /**
     * 上传任意文件(带进度回调 + 定向推送 + 长超时)。
     *
     * 与 uploadImage 的差异:文件走独立的 Files 表,不会进入剪贴板历史,
     * 各端收到的是 FileTransferred 推送而非 ClipboardUpdated。
     *
     * @param targetDeviceIds 目标设备 id;空集合 = 全员广播(含网页会话)
     * @param onProgress (已发送字节, 总字节) —— 流式写出时按块回调,UI 据此算速率
     */
    fun uploadFile(
        file: java.io.File,
        deviceId: String,
        deviceName: String,
        targetDeviceIds: Set<String> = emptySet(),
        onProgress: ((sent: Long, total: Long) -> Unit)? = null,
    ): FileTransferInfo {
        val mime = mimeTypeOf(file.name)
        val fileBody = object : okhttp3.RequestBody() {
            override fun contentType(): okhttp3.MediaType? = mime.toMediaType()
            override fun contentLength(): Long = file.length()
            override fun writeTo(sink: okio.BufferedSink) {
                val total = file.length()
                var sent = 0L
                file.inputStream().use { input ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        sink.write(buf, 0, n)
                        sent += n
                        onProgress?.invoke(sent, total)
                    }
                }
            }
        }
        val builder = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", file.name, fileBody)
            .addFormDataPart("deviceId", deviceId)
            .addFormDataPart("deviceName", deviceName)
            .addFormDataPart("platform", "Android")
            .addFormDataPart("version", android.os.Build.VERSION.RELEASE)
        if (targetDeviceIds.isNotEmpty()) {
            builder.addFormDataPart("deviceIds", targetDeviceIds.joinToString(","))
        }
        val req = builder("POST", "/api/files").post(builder.build()).build()
        // 上传可能远超 30s readTimeout:大文件在 Wi-Fi 上 2MB/s 也需要分钟级
        val longClient = client.newBuilder()
            .readTimeout(120, TimeUnit.SECONDS)
            .writeTimeout(120, TimeUnit.SECONDS)
            .callTimeout(300, TimeUnit.SECONDS)
            .build()
        val resp = longClient.newCall(req).execute()
        resp.use {
            val json = JSONObject(it.body?.string() ?: "{}")
            return FileTransferInfo(
                fileId = json.getString("fileId"),
                fileName = json.getString("fileName"),
                size = json.getLong("size"),
                contentHash = json.optString("contentHash"),
                deviceId = json.optString("deviceId"),
                deviceName = json.optString("deviceName"),
                createdAt = json.optString("createdAt"),
                downloadUrl = json.optString("downloadUrl"),
            )
        }
    }

    /** 列出文件互传记录(新→旧),返回 (条目, 总数) */
    fun listFiles(offset: Int = 0, limit: Int = 50): Pair<List<FileTransferInfo>, Int> {
        val req = builder("GET", "/api/files?offset=$offset&limit=$limit").build()
        val resp = execute(req)
        resp.use {
            val json = JSONObject(it.body?.string() ?: "{}")
            val items = json.optJSONArray("items") ?: return Pair(emptyList(), 0)
            val list = (0 until items.length()).mapNotNull { i ->
                parseFileTransferInfo(items.optJSONObject(i) ?: return@mapNotNull null)
            }
            return Pair(list, json.optInt("total", list.size))
        }
    }

    /** 下载文件内容(调用方负责写盘;文件名用元数据里的 fileName) */
    fun downloadFile(fileId: String): ByteArray? {
        val req = builder("GET", "/api/files/$fileId/download").build()
        val resp = execute(req)
        resp.use { return it.body?.bytes() }
    }

    /** 删除文件(服务端会广播 FileDeleted 给同组其它端) */
    fun deleteFile(fileId: String): Boolean {
        val req = builder("DELETE", "/api/files/$fileId").build()
        val resp = execute(req)
        resp.use { return it.isSuccessful }
    }

    /** 确认制传输:本端接受发送请求(仅目标设备可调) */
    fun acceptFile(fileId: String): Boolean {
        val req = builder("POST", "/api/files/$fileId/accept")
            .header("Content-Type", "application/json")
            .post("{}".toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use { return it.isSuccessful }
    }

    /** 确认制传输:本端拒绝发送请求(服务端立即删除文件) */
    fun rejectFile(fileId: String): Boolean {
        val req = builder("POST", "/api/files/$fileId/reject")
            .header("Content-Type", "application/json")
            .post("{}".toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use { return it.isSuccessful }
    }

    /** 服务连通性与网络延迟测试 (GET /api/health) */
    fun testConnection(): Pair<Boolean, String> {
        val start = System.currentTimeMillis()
        return try {
            val req = Request.Builder()
                .url(serverUrl.trimEnd('/') + "/api/health")
                .get()
                .build()
            client.newCall(req).execute().use { resp ->
                val elapsed = System.currentTimeMillis() - start
                if (resp.isSuccessful) {
                    val body = resp.body?.string()
                    val ver = try {
                        JSONObject(body ?: "{}").optString("version").takeIf { it.isNotBlank() }
                    } catch (_: Exception) {
                        null
                    }
                    val verText = if (ver != null) " (v$ver)" else ""
                    true to "连接成功！服务器响应正常，延迟 ${elapsed}ms$verText"
                } else if (resp.code == 404 || resp.code == 401 || resp.code == 204) {
                    true to "连接成功！服务器已响应，延迟 ${elapsed}ms"
                } else {
                    false to "服务器返回异常状态码: ${resp.code} ${resp.message}"
                }
            }
        } catch (e: java.io.IOException) {
            false to networkErrorText(e)
        } catch (e: Exception) {
            false to "连接失败: ${e.message ?: e.javaClass.simpleName}"
        }
    }
}

/** IP 规范化:去 ::ffff: 前缀;IPv6 回环 → 127.0.0.1 */
private fun normalizeIp(ip: String?): String? {
    if (ip.isNullOrBlank()) return null
    val mapped = Regex("^::ffff:(\\d+\\.\\d+\\.\\d+\\.\\d+)$").find(ip)
    if (mapped != null) return mapped.groupValues[1]
    return if (ip == "::1") "127.0.0.1" else ip
}
