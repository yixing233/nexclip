package clip.yixing.sync.util

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

data class UpdateInfo(
    val hasUpdate: Boolean,
    val currentVersion: String,
    val latestVersion: String,
    val releaseTitle: String,
    val releaseNotes: String,
    val releaseUrl: String,
    val downloadUrl: String?,
    val isDirectSource: Boolean = false,
    val sha256: String? = null,
    val fileSize: Long? = null
)

object UpdateChecker {
    const val SERVER_DIRECT_BASE_URL = "https://nexclip.157342.xyz/releases"

    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    suspend fun check(
        currentVersion: String,
        updateSource: Int = SyncSettings.UPDATE_SOURCE_GITHUB,
        serverUrl: String? = null
    ): Result<UpdateInfo> = withContext(Dispatchers.IO) {
        val isDirect = updateSource == SyncSettings.UPDATE_SOURCE_DIRECT

        if (isDirect) {
            val directResult = checkServerDirect(currentVersion, serverUrl)
            if (directResult.isSuccess) {
                return@withContext directResult
            }
        }

        val ghResult = checkGitHub(currentVersion, isDirect)
        if (ghResult.isSuccess) {
            return@withContext ghResult
        }

        if (!isDirect) {
            val fallbackDirect = checkServerDirect(currentVersion, serverUrl)
            if (fallbackDirect.isSuccess) {
                return@withContext fallbackDirect
            }
        }

        ghResult
    }

    private fun checkServerDirect(currentVersion: String, customServerUrl: String?): Result<UpdateInfo> {
        return runCatching {
            val baseUrl = if (!customServerUrl.isNullOrBlank() && customServerUrl.startsWith("http")) {
                val uri = java.net.URI(customServerUrl.trim())
                "${uri.scheme}://${uri.authority}/releases"
            } else {
                SERVER_DIRECT_BASE_URL
            }

            val request = Request.Builder()
                .url("$baseUrl/version.json")
                .header("User-Agent", "NexClip-Android")
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                throw Exception("HTTP ${response.code}")
            }

            val bodyStr = response.body?.string() ?: throw Exception("返回内容为空")
            val json = JSONObject(bodyStr)

            val tagName = json.optString("tag_name", "").ifEmpty { json.optString("version", "") }.trim()
            val cleanLatest = tagName.removePrefix("v").removePrefix("V").trim()
            val cleanCurrent = currentVersion.removePrefix("v").removePrefix("V").trim()

            val title = json.optString("name", "NexClip v$cleanLatest")
            val body = json.optString("body", "")
            val htmlUrl = json.optString("html_url", "https://github.com/yixing233/nexclip/releases")

            var downloadUrl: String? = null
            var sha256: String? = null
            var fileSize: Long? = null
            val androidObj = json.optJSONObject("android")
            if (androidObj != null) {
                downloadUrl = androidObj.optString("url", "").ifEmpty {
                    val fn = androidObj.optString("filename", "")
                    if (fn.isNotBlank()) "$baseUrl/$fn" else null
                }
                sha256 = androidObj.optString("sha256", "").takeIf { it.isNotBlank() }
                fileSize = androidObj.optLong("size", 0L).takeIf { it > 0 }
            }
            if (downloadUrl.isNullOrBlank()) {
                downloadUrl = "$baseUrl/NexClip_v${cleanLatest}_Android.apk"
            }

            val hasUpdate = compareVersions(cleanLatest, cleanCurrent) > 0

            UpdateInfo(
                hasUpdate = hasUpdate,
                currentVersion = currentVersion,
                latestVersion = cleanLatest,
                releaseTitle = title,
                releaseNotes = body,
                releaseUrl = htmlUrl,
                downloadUrl = downloadUrl,
                isDirectSource = true,
                sha256 = sha256,
                fileSize = fileSize
            )
        }
    }

    private fun checkGitHub(currentVersion: String, useDirectDownload: Boolean): Result<UpdateInfo> {
        return runCatching {
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
            var assetFileName: String? = null
            var fileSize: Long? = null
            val assets = json.optJSONArray("assets")
            if (assets != null) {
                for (i in 0 until assets.length()) {
                    val asset = assets.optJSONObject(i) ?: continue
                    val name = asset.optString("name", "")
                    if (name.endsWith(".apk", ignoreCase = true)) {
                        downloadUrl = asset.optString("browser_download_url")
                        assetFileName = name
                        fileSize = asset.optLong("size", 0L).takeIf { it > 0 }
                        break
                    }
                }
            }

            if (useDirectDownload && !assetFileName.isNullOrBlank()) {
                downloadUrl = "$SERVER_DIRECT_BASE_URL/$assetFileName"
            }

            val hasUpdate = compareVersions(cleanLatest, cleanCurrent) > 0

            UpdateInfo(
                hasUpdate = hasUpdate,
                currentVersion = currentVersion,
                latestVersion = cleanLatest,
                releaseTitle = title,
                releaseNotes = body,
                releaseUrl = htmlUrl,
                downloadUrl = downloadUrl,
                isDirectSource = useDirectDownload,
                sha256 = null,
                fileSize = fileSize
            )
        }
    }

    /**
     * 协程流式分块下载 APK，并在下载完成后校验 SHA256。
     */
    suspend fun downloadApk(
        context: Context,
        downloadUrl: String,
        latestVersion: String,
        expectedSha256: String? = null,
        onProgress: (bytesRead: Long, totalBytes: Long, percentage: Float, speed: String) -> Unit
    ): Result<File> = withContext(Dispatchers.IO) {
        runCatching {
            val updateDir = File(context.getExternalFilesDir(null) ?: context.filesDir, "updates")
            if (!updateDir.exists()) updateDir.mkdirs()
            val finalApk = File(updateDir, "NexClip_v${latestVersion}_Android.apk")
            val tempApk = File(updateDir, "NexClip_v${latestVersion}_Android.apk.download")

            // 1. 如果已有匹配文件则直接返回
            if (finalApk.exists()) {
                if (expectedSha256.isNullOrBlank() || verifySha256(finalApk, expectedSha256)) {
                    val len = finalApk.length()
                    onProgress(len, len, 100f, "")
                    return@runCatching finalApk
                } else {
                    finalApk.delete()
                }
            }

            if (tempApk.exists()) tempApk.delete()

            val request = Request.Builder()
                .url(downloadUrl)
                .header("User-Agent", "NexClip-Android")
                .build()

            val response = client.newCall(request).execute()
            if (!response.isSuccessful) {
                throw Exception("下载失败: HTTP ${response.code}")
            }

            val responseBody = response.body ?: throw Exception("响应体为空")
            val totalBytes = responseBody.contentLength()
            val inputStream = responseBody.byteStream()
            val outputStream = FileOutputStream(tempApk)

            val buffer = ByteArray(64 * 1024)
            var bytesReadTotal = 0L
            var lastReportTime = System.currentTimeMillis()
            var lastReportBytes = 0L

            inputStream.use { input ->
                outputStream.use { output ->
                    while (true) {
                        val read = input.read(buffer)
                        if (read == -1) break
                        output.write(buffer, 0, read)
                        bytesReadTotal += read

                        val now = System.currentTimeMillis()
                        if (now - lastReportTime >= 250 || (totalBytes > 0 && bytesReadTotal == totalBytes)) {
                            val timeDiffSec = (now - lastReportTime) / 1000.0
                            val bytesDiff = bytesReadTotal - lastReportBytes
                            val speedBytesPerSec = if (timeDiffSec > 0) bytesDiff / timeDiffSec else 0.0
                            val pct = if (totalBytes > 0) (bytesReadTotal.toFloat() / totalBytes * 100f).coerceIn(0f, 100f) else 0f

                            val speedText = formatSpeed(speedBytesPerSec)
                            withContext(Dispatchers.Main) {
                                onProgress(bytesReadTotal, totalBytes, pct, speedText)
                            }
                            lastReportTime = now
                            lastReportBytes = bytesReadTotal
                        }
                    }
                    output.flush()
                }
            }

            // 2. 校验 SHA256 (若提供)
            if (!expectedSha256.isNullOrBlank()) {
                if (!verifySha256(tempApk, expectedSha256)) {
                    tempApk.delete()
                    throw Exception("安装包 SHA256 校验失败，文件可能在传输中损坏，请重试。")
                }
            }

            // 3. 重命名为正式文件
            if (finalApk.exists()) finalApk.delete()
            tempApk.renameTo(finalApk)
            finalApk
        }
    }

    fun verifySha256(file: File, expectedSha256: String): Boolean {
        return runCatching {
            val digest = MessageDigest.getInstance("SHA-256")
            file.inputStream().use { input ->
                val buffer = ByteArray(8192)
                var read: Int
                while (input.read(buffer).also { read = it } != -1) {
                    digest.update(buffer, 0, read)
                }
            }
            val hex = digest.digest().joinToString("") { "%02x".format(it) }
            hex.equals(expectedSha256.trim(), ignoreCase = true)
        }.getOrDefault(false)
    }

    fun formatSpeed(bytesPerSec: Double): String {
        return when {
            bytesPerSec < 1024 -> "%.0f B/s".format(bytesPerSec)
            bytesPerSec < 1024 * 1024 -> "%.1f KB/s".format(bytesPerSec / 1024.0)
            else -> "%.2f MB/s".format(bytesPerSec / (1024.0 * 1024.0))
        }
    }

    fun formatBytes(bytes: Long): String {
        return when {
            bytes < 0 -> "未知大小"
            bytes < 1024 -> "$bytes B"
            bytes < 1024 * 1024 -> "%.1f KB".format(bytes / 1024.0)
            bytes < 1024 * 1024 * 1024 -> "%.2f MB".format(bytes / (1024.0 * 1024.0))
            else -> "%.2f GB".format(bytes / (1024.0 * 1024.0 * 1024.0))
        }
    }

    /**
     * 唤起系统安装器执行 APK 覆盖安装。
     */
    fun installApk(context: Context, apkFile: File) {
        val uri = FileProvider.getUriForFile(
            context,
            "${context.packageName}.fileprovider",
            apkFile
        )
        val installIntent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(installIntent)
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
