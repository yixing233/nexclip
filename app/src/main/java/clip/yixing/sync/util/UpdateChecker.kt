package clip.yixing.sync.util

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class UpdateInfo(
    val hasUpdate: Boolean,
    val currentVersion: String,
    val latestVersion: String,
    val releaseTitle: String,
    val releaseNotes: String,
    val releaseUrl: String,
    val downloadUrl: String?
)

object UpdateChecker {
    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    suspend fun check(currentVersion: String): Result<UpdateInfo> = withContext(Dispatchers.IO) {
        runCatching {
            val request = Request.Builder()
                .url("https://api.github.com/repos/yixing233/nexclip/releases/latest")
                .header("User-Agent", "NexClip-Android")
                .header("Accept", "application/vnd.github.v3+json")
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                throw Exception("HTTP ${response.code}")
            }

            val bodyStr = response.body?.string() ?: throw Exception("返回内容为空")
            val json = JSONObject(bodyStr)

            val tagName = json.optString("tag_name", "").trim()
            val cleanLatest = tagName.removePrefix("v").removePrefix("V").trim()
            val cleanCurrent = currentVersion.removePrefix("v").removePrefix("V").trim()

            val title = json.optString("name", "")
            val body = json.optString("body", "")
            val htmlUrl = json.optString("html_url", "https://github.com/yixing233/nexclip/releases")

            var downloadUrl: String? = null
            val assets = json.optJSONArray("assets")
            if (assets != null) {
                for (i in 0 until assets.length()) {
                    val asset = assets.optJSONObject(i) ?: continue
                    val name = asset.optString("name", "")
                    if (name.endsWith(".apk", ignoreCase = true)) {
                        downloadUrl = asset.optString("browser_download_url")
                        break
                    }
                }
            }

            val hasUpdate = compareVersions(cleanLatest, cleanCurrent) > 0

            UpdateInfo(
                hasUpdate = hasUpdate,
                currentVersion = currentVersion,
                latestVersion = cleanLatest,
                releaseTitle = title,
                releaseNotes = body,
                releaseUrl = htmlUrl,
                downloadUrl = downloadUrl
            )
        }
    }

    fun compareVersions(v1: String, v2: String): Int {
        if (v1.equals(v2, ignoreCase = true)) return 0
        val parts1 = v1.split('.', '-', '_').filter { it.isNotEmpty() }
        val parts2 = v2.split('.', '-', '_').filter { it.isNotEmpty() }
        val maxLen = maxOf(parts1.size, parts2.size)

        for (i in 0 until maxLen) {
            val p1 = parts1.getOrNull(i) ?: ""
            val p2 = parts2.getOrNull(i) ?: ""
            val num1 = p1.toLongOrNull()
            val num2 = p2.toLongOrNull()

            if (num1 != null && num2 != null) {
                if (num1 != num2) return num1.compareTo(num2)
            } else {
                val cmp = p1.compareTo(p2, ignoreCase = true)
                if (cmp != 0) return cmp
            }
        }
        return 0
    }
}
