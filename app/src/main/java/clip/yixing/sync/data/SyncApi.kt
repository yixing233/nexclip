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

class ApiException(message: String, val statusCode: Int? = null) : Exception(message)

/** REST 客户端:与 SyncClipboard Server 契约一致(Bearer token) */
class SyncApi(private val serverUrl: String, private val token: String) {
    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .build()

    private fun builder(method: String, path: String): Request.Builder {
        val b = Request.Builder().url(serverUrl.trimEnd('/') + path)
        if (token.isNotBlank()) b.header("Authorization", "Bearer " + token)
        return b
    }

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
        if (!resp.isSuccessful) throw ApiException("服务器返回 " + resp.code + " " + resp.message, resp.code)
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

    /** PUT /api/clipboard → 上传文本,返回条目(unchanged 时仍是当前条目) */
    fun putText(text: String, deviceId: String, deviceName: String): ClipboardEntry {
        val json = JSONObject().apply {
            put("type", "Text"); put("text", text)
            put("deviceId", deviceId); put("deviceName", deviceName)
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

    /** POST /api/clipboard/image(multipart) */
    fun uploadImage(pngBytes: ByteArray, deviceId: String, deviceName: String): ClipboardEntry {
        val body = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", "clipboard.png", pngBytes.toRequestBody("image/png".toMediaType()))
            .addFormDataPart("deviceId", deviceId)
            .addFormDataPart("deviceName", deviceName)
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
