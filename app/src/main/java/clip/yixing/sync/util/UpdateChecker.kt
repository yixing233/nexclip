package clip.yixing.sync.util

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
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

    private const val GITHUB_RELEASES_API = "https://api.github.com/repos/yixing233/nexclip/releases"
    private const val DEFAULT_RELEASES_PAGE = "https://github.com/yixing233/nexclip/releases"

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

            // android 段优先，缺字段才回落顶层：顶层版本号是两端共用的，
            // 只发布 Windows 的版本不应该把 Android 也顶成「有新版本」。
            val platform = json.optJSONObject("android")

            val tagName = readString(platform, json, "tag_name", "version") ?: ""
            val cleanLatest = tagName.removePrefix("v").removePrefix("V").trim()
            val cleanCurrent = currentVersion.removePrefix("v").removePrefix("V").trim()

            val title = readString(platform, json, "name") ?: "NexClip v$cleanLatest"
            val body = readString(platform, json, "body") ?: ""
            val htmlUrl = readString(platform, json, "html_url") ?: DEFAULT_RELEASES_PAGE

            var downloadUrl: String? = null
            var sha256: String? = null
            var fileSize: Long? = null
            if (platform != null) {
                downloadUrl = readString(platform, "url")
                    ?: readString(platform, "filename")?.let { "$baseUrl/$it" }
                sha256 = readString(platform, "sha256")
                fileSize = platform.optLong("size", 0L).takeIf { it > 0 }
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

    /**
     * GitHub 通道。先在发布列表里找「最新一个挂了 APK 的发布」，而不是直接用 /releases/latest：
     * 后者是两端共用的单一指针，只发 Windows 的版本会把它顶走，
     * Android 照它比版本号就会提示一个根本没有 APK 可下的新版本。
     */
    private fun checkGitHub(currentVersion: String, useDirectDownload: Boolean): Result<UpdateInfo> {
        return runCatching {
            val list = runCatching { JSONArray(readGitHubJson("$GITHUB_RELEASES_API?per_page=20")) }.getOrNull()
            if (list != null) {
                for (i in 0 until list.length()) {
                    val release = list.optJSONObject(i) ?: continue
                    if (release.optBoolean("draft", false)) continue
                    if (findApkAsset(release) == null) continue
                    return@runCatching buildGitHubInfo(release, currentVersion, useDirectDownload)
                }
            }

            // 列表不可用（限流/网络/返回结构变化）就退回旧路径，行为与改动前一致
            buildGitHubInfo(
                JSONObject(readGitHubJson("$GITHUB_RELEASES_API/latest")),
                currentVersion,
                useDirectDownload
            )
        }
    }

    private fun readGitHubJson(url: String): String {
        val request = Request.Builder()
            .url(url)
            .header("User-Agent", "NexClip-Android")
            .header("Accept", "application/vnd.github.v3+json")
            .build()

        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                throw Exception("HTTP ${response.code}")
            }
            return response.body?.string() ?: throw Exception("返回内容为空")
        }
    }

    /** 取出 release 里的 APK 资产；没有就返回 null，调用方靠它跳过「只发 Windows」的版本。 */
    private fun findApkAsset(release: JSONObject): JSONObject? {
        val assets = release.optJSONArray("assets") ?: return null
        for (i in 0 until assets.length()) {
            val asset = assets.optJSONObject(i) ?: continue
            if (asset.optString("name", "").endsWith(".apk", ignoreCase = true)) {
                return asset
            }
        }
        return null
    }

    private fun buildGitHubInfo(
        release: JSONObject,
        currentVersion: String,
        useDirectDownload: Boolean
    ): UpdateInfo {
        val tagName = readString(release, "tag_name") ?: ""
        val cleanLatest = tagName.removePrefix("v").removePrefix("V").trim()
        val cleanCurrent = currentVersion.removePrefix("v").removePrefix("V").trim()

        val asset = findApkAsset(release)
        val assetFileName = asset?.let { readString(it, "name") }
        var downloadUrl = asset?.let { readString(it, "browser_download_url") }
        val fileSize = asset?.optLong("size", 0L)?.takeIf { it > 0 }

        if (useDirectDownload && !assetFileName.isNullOrBlank()) {
            downloadUrl = "$SERVER_DIRECT_BASE_URL/$assetFileName"
        }

        return UpdateInfo(
            hasUpdate = compareVersions(cleanLatest, cleanCurrent) > 0,
            currentVersion = currentVersion,
            latestVersion = cleanLatest,
            releaseTitle = readString(release, "name") ?: "",
            releaseNotes = readString(release, "body") ?: "",
            releaseUrl = readString(release, "html_url") ?: DEFAULT_RELEASES_PAGE,
            downloadUrl = downloadUrl,
            isDirectSource = useDirectDownload,
            sha256 = null,
            fileSize = fileSize
        )
    }

    /** 取非空字符串字段；空串与空白视为缺失，好让平台段里的占位值自动回落。 */
    private fun readString(json: JSONObject?, name: String): String? =
        json?.optString(name)?.trim()?.takeIf { it.isNotEmpty() }

    /** 先在平台段里按顺序找，再在顶层按同样顺序找。 */
    private fun readString(platform: JSONObject?, root: JSONObject, vararg names: String): String? {
        names.forEach { name -> readString(platform, name)?.let { return it } }
        names.forEach { name -> readString(root, name)?.let { return it } }
        return null
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
            if (!tempApk.renameTo(finalApk)) {
                tempApk.copyTo(finalApk, overwrite = true)
                tempApk.delete()
            }
            if (!finalApk.exists() || finalApk.length() <= 0L) {
                throw Exception("安装包写入失败，请检查存储空间后重试。")
            }
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
     * 是否已获得「安装未知应用」授权。Android 8.0+ 未授权时系统安装器会直接拒绝安装请求。
     */
    fun canRequestPackageInstalls(context: Context): Boolean =
        runCatching { context.packageManager.canRequestPackageInstalls() }.getOrDefault(false)

    /**
     * 跳转到本应用的「安装未知应用」授权页；失败时回退到应用详情页。
     */
    fun openInstallPermissionSettings(context: Context): Boolean {
        val intent = Intent(
            Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
            Uri.parse("package:${context.packageName}")
        ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return runCatching { context.startActivity(intent); true }.getOrElse {
            runCatching {
                context.startActivity(
                    Intent(
                        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.parse("package:${context.packageName}")
                    ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                )
                true
            }.getOrDefault(false)
        }
    }

    /**
     * 唤起系统安装器执行 APK 覆盖安装。
     *
     * 失败原因会包装成可读文案返回，常见于：未授予「安装未知应用」权限、
     * 安装包被清理、FileProvider 未声明对应目录。
     */
    fun installApk(context: Context, apkFile: File): Result<Unit> = runCatching {
        if (!apkFile.exists() || apkFile.length() <= 0L) {
            throw IllegalStateException("安装包不存在或已损坏，请重新下载")
        }
        if (!canRequestPackageInstalls(context)) {
            throw SecurityException("请先允许 NexClip「安装未知应用」")
        }

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
        // 部分 ROM 的安装器不读取 Intent 上的临时授权，显式对目标包再授一次
        context.packageManager
            .queryIntentActivities(installIntent, PackageManager.MATCH_DEFAULT_ONLY)
            .forEach { resolveInfo ->
                runCatching {
                    context.grantUriPermission(
                        resolveInfo.activityInfo.packageName,
                        uri,
                        Intent.FLAG_GRANT_READ_URI_PERMISSION
                    )
                }
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
