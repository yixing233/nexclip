package clip.yixing.sync.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
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

/** 6 位纯数字配对码(POST /api/pairing-codes → { code, expiresAt, userId }) */
data class PairingCode(val code: String, val expiresAt: String, val userId: String, val deviceToken: String? = null, val qrPayload: String? = null)

class ApiException(message: String, val statusCode: Int? = null) : Exception(message)

/** REST 客户端:设备同步接口使用 X-Device-Id/X-Device-Token 凭证。 */
class SyncApi(
    private val serverUrl: String,
    private val authDeviceId: String = "",
    private val authDeviceToken: String = "",
) {
    private val client = OkHttpClient.Builder()
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

    private fun execute(req: Request): okhttp3.Response = try {
        val resp = client.newCall(req).execute()
        if (!resp.isSuccessful) {
            val body = resp.body?.string()
            val serverMsg = try {
                val o = JSONObject(body ?: "{}")
                o.optString("error").takeIf { it.isNotBlank() }
            } catch (_: Exception) {
                null
            }
            val finalMsg = serverMsg ?: httpStatusText(resp.code)
            throw ApiException(finalMsg, resp.code)
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
    fun uploadImage(pngBytes: ByteArray, deviceId: String, deviceName: String, isManual: Boolean = false): ClipboardEntry {
        val builder = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", "clipboard.png", pngBytes.toRequestBody("image/png".toMediaType()))
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
