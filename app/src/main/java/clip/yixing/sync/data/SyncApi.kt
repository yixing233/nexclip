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
    val lastSeenAt: String,
)

/** 配对结果(POST /api/pair) */
data class PairResult(val deviceId: String, val deviceToken: String)

/** 配对码(POST /api/pairing-codes) */
data class PairingCode(val code: String, val expiresAt: String)

class ApiException(message: String, val statusCode: Int? = null) : Exception(message)

/** REST 客户端:与 SyncClipboard Server 契约一致(设备同步接口免认证,无令牌) */
class SyncApi(private val serverUrl: String) {
    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    private fun builder(method: String, path: String): Request.Builder =
        Request.Builder().url(serverUrl.trimEnd('/') + path)

    private fun parseEntry(o: JSONObject) = ClipboardEntry(
        id = o.optLong("id"),
        type = o.optString("type", "Text"),
        text = if (o.isNull("text")) null else o.optString("text"),
        imageRef = if (o.isNull("imageRef")) null else o.optString("imageRef"),
        deviceId = o.optString("deviceId"),
        deviceName = if (o.isNull("deviceName")) null else o.optString("deviceName"),
        createdAt = o.optString("createdAt"),
    )

    private fun execute(req: Request): okhttp3.Response {
        val resp = client.newCall(req).execute()
        if (resp.code == 401) throw ApiException("令牌无效(401)", 401)
        if (!resp.isSuccessful) {
            // 优先透传服务端 { error: "..." } 消息(如"配对码无效或已过期")
            val body = resp.body?.string()
            val msg = try {
                JSONObject(body ?: "{}").optString("error")
                    .ifBlank { "服务器返回 " + resp.code + " " + resp.message }
            } catch (_: Exception) {
                "服务器返回 " + resp.code + " " + resp.message
            }
            throw ApiException(msg, resp.code)
        }
        return resp
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
    ): ClipboardEntry {
        val json = JSONObject().apply {
            put("type", "Text"); put("text", text)
            put("deviceId", deviceId); put("deviceName", deviceName)
            put("platform", platform); put("version", version)
        }.toString()
        val req = builder("PUT", "/api/clipboard")
            .header("Content-Type", "application/json")
            .put(json.toRequestBody("application/json".toMediaType()))
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
                    lastSeenAt = o.optString("lastSeenAt"),
                )
            }
        }
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
            )
        }
    }

    /** DELETE /api/pairing-codes/{code} → 作废配对码(关闭底部弹层后调用,码立即失效) */
    fun revokePairingCode(code: String) {
        val req = builder("DELETE", "/api/pairing-codes/" + java.net.URLEncoder.encode(code, "UTF-8")).build()
        execute(req).use { }
    }

    /** POST /api/pair → 用一次性配对码换取设备专属令牌(配对接口无需认证) */
    fun pair(pairingCode: String, deviceId: String, deviceName: String): PairResult {
        val json = JSONObject().apply {
            put("pairingCode", pairingCode)
            put("deviceId", deviceId)
            put("deviceName", deviceName)
        }.toString()
        val req = builder("POST", "/api/pair")
            .header("Content-Type", "application/json")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()
        val resp = execute(req)
        resp.use {
            val o = JSONObject(it.body?.string() ?: "{}")
            return PairResult(
                deviceId = o.optString("deviceId", deviceId),
                deviceToken = o.optString("deviceToken"),
            )
        }
    }

    /** POST /api/clipboard/image(multipart) */
    fun uploadImage(pngBytes: ByteArray, deviceId: String, deviceName: String): ClipboardEntry {
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", "clipboard.png", pngBytes.toRequestBody("image/png".toMediaType()))
            .addFormDataPart("deviceId", deviceId)
            .addFormDataPart("deviceName", deviceName)
            .addFormDataPart("platform", "Android")
            .addFormDataPart("version", android.os.Build.VERSION.RELEASE)
            .build()
        val req = builder("POST", "/api/clipboard/image").post(body).build()
        val resp = execute(req)
        resp.use { return parseEntry(JSONObject(it.body?.string() ?: "{}")) }
    }

    /** GET /api/images/{ref} */
    fun downloadImage(ref: String): ByteArray? {
        val req = builder("GET", "/api/images/" + ref.trimStart('/')).build()
        val resp = execute(req)
        resp.use { return it.body?.bytes() }
    }

    /** 连接测试 */
    fun testConnection(): Pair<Boolean, String> = try {
        val cur = getCurrent()
        true to (cur?.let { "连接成功,当前条目来自 " + (it.deviceName ?: "未知设备") } ?: "连接成功(服务器暂无内容)")
    } catch (e: ApiException) {
        false to (if (e.statusCode == 401) "令牌无效(401)" else e.message ?: "连接失败")
    } catch (e: Exception) {
        false to "连接失败: " + (e.message ?: e.javaClass.simpleName)
    }
}

/** IP 规范化:去 ::ffff: 前缀;IPv6 回环 → 127.0.0.1 */
private fun normalizeIp(ip: String?): String? {
    if (ip.isNullOrBlank()) return null
    val mapped = Regex("^::ffff:(\\d+\\.\\d+\\.\\d+\\.\\d+)$").find(ip)
    if (mapped != null) return mapped.groupValues[1]
    return if (ip == "::1") "127.0.0.1" else ip
}
