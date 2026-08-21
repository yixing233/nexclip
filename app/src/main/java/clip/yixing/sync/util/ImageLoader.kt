package clip.yixing.sync.util

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.util.Base64
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.core.content.FileProvider
import clip.yixing.sync.data.SyncApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream

object ImageLoader {

    private val memCache = object : LruCache<String, ImageBitmap>(30 * 1024 * 1024) {
        override fun sizeOf(key: String, value: ImageBitmap): Int {
            return value.width * value.height * 4
        }
    }

    private fun getCacheDir(context: Context): File {
        val dir = File(context.cacheDir, "clip_images")
        if (!dir.exists()) dir.mkdirs()
        return dir
    }

    private fun getCachedFile(context: Context, key: String): File {
        val cleanName = key.replace(Regex("[^a-zA-Z0-9_.-]"), "_") + ".png"
        return File(getCacheDir(context), cleanName)
    }

    fun saveBytesToDisk(context: Context, key: String, bytes: ByteArray) {
        runCatching {
            val file = getCachedFile(context, key)
            FileOutputStream(file).use { it.write(bytes) }
        }
    }

    fun saveDroppedImage(context: Context, uri: Uri): String? {
        return runCatching {
            val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return null
            val key = "drop_${System.currentTimeMillis()}_${bytes.size}"
            saveBytesToDisk(context, key, bytes)
            key
        }.getOrNull()
    }

    suspend fun getImageBytes(context: Context, imageRef: String?, rawText: String?): ByteArray? = withContext(Dispatchers.IO) {
        if (!rawText.isNullOrBlank() && rawText.startsWith("data:image/")) {
            val base64Index = rawText.indexOf("base64,")
            if (base64Index != -1) {
                return@withContext runCatching {
                    Base64.decode(rawText.substring(base64Index + 7), Base64.DEFAULT)
                }.getOrNull()
            }
        }

        val key = imageRef?.takeIf { it.isNotBlank() } ?: return@withContext null
        val file = getCachedFile(context, key)
        if (file.exists() && file.length() > 0) {
            return@withContext runCatching { file.readBytes() }.getOrNull()
        }

        // 从远端服务器下载
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isNotBlank() && SyncSettings.isPaired(context)) {
            val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
            val bytes = runCatching { api.downloadImage(key) }.getOrNull()
            if (bytes != null && bytes.isNotEmpty()) {
                saveBytesToDisk(context, key, bytes)
                return@withContext bytes
            }
        }

        null
    }

    suspend fun loadImageBitmap(context: Context, imageRef: String?, rawText: String?): ImageBitmap? = withContext(Dispatchers.IO) {
        val key = imageRef?.takeIf { it.isNotBlank() } ?: (if (!rawText.isNullOrBlank() && rawText.startsWith("data:image/")) "data_base64_${rawText.hashCode()}" else null)
        if (key != null) {
            val cached = memCache.get(key)
            if (cached != null) return@withContext cached
        }

        val bytes = getImageBytes(context, imageRef, rawText) ?: return@withContext null
        val bitmap = runCatching {
            // 解码并限制最大分辨率以保护内存
            val opts = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)

            var sampleSize = 1
            val maxDimension = 2048
            while (opts.outWidth / sampleSize > maxDimension || opts.outHeight / sampleSize > maxDimension) {
                sampleSize *= 2
            }

            val decodeOpts = BitmapFactory.Options().apply { inSampleSize = sampleSize }
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size, decodeOpts)
        }.getOrNull() ?: return@withContext null

        val imageBitmap = bitmap.asImageBitmap()
        if (key != null) {
            memCache.put(key, imageBitmap)
        }
        imageBitmap
    }

    /** 保存图片到系统相册 */
    suspend fun saveToGallery(context: Context, imageRef: String?, rawText: String?): Boolean = withContext(Dispatchers.IO) {
        val bytes = getImageBytes(context, imageRef, rawText) ?: return@withContext false
        runCatching {
            val filename = "NexClip_${System.currentTimeMillis()}.png"
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                val values = ContentValues().apply {
                    put(MediaStore.Images.Media.DISPLAY_NAME, filename)
                    put(MediaStore.Images.Media.MIME_TYPE, "image/png")
                    put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/NexClip")
                    put(MediaStore.Images.Media.IS_PENDING, 1)
                }
                val resolver = context.contentResolver
                val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return@withContext false
                resolver.openOutputStream(uri)?.use { it.write(bytes) }
                values.clear()
                values.put(MediaStore.Images.Media.IS_PENDING, 0)
                resolver.update(uri, values, null, null)
                true
            } else {
                val picturesDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_PICTURES)
                val targetDir = File(picturesDir, "NexClip").apply { if (!exists()) mkdirs() }
                val targetFile = File(targetDir, filename)
                FileOutputStream(targetFile).use { it.write(bytes) }
                // 触发媒体库扫描
                context.sendBroadcast(Intent(Intent.ACTION_MEDIA_SCANNER_SCAN_FILE, Uri.fromFile(targetFile)))
                true
            }
        }.getOrDefault(false)
    }

    /** 复制图片到剪贴板 */
    suspend fun copyImageToClipboard(context: Context, imageRef: String?, rawText: String?): Boolean = withContext(Dispatchers.IO) {
        val bytes = getImageBytes(context, imageRef, rawText) ?: return@withContext false
        val key = imageRef?.takeIf { it.isNotBlank() } ?: "temp_clip_${System.currentTimeMillis()}"
        val file = getCachedFile(context, key)
        if (!file.exists()) {
            saveBytesToDisk(context, key, bytes)
        }
        val uri = runCatching {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }.getOrNull() ?: return@withContext false

        withContext(Dispatchers.Main) {
            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            val clip = ClipData.newUri(context.contentResolver, "SyncClipboard Image", uri)
            cm.setPrimaryClip(clip)
        }
        true
    }

    /** 系统分享图片 */
    suspend fun shareImage(context: Context, imageRef: String?, rawText: String?): Boolean = withContext(Dispatchers.IO) {
        val bytes = getImageBytes(context, imageRef, rawText) ?: return@withContext false
        val key = imageRef?.takeIf { it.isNotBlank() } ?: "share_${System.currentTimeMillis()}"
        val file = getCachedFile(context, key)
        if (!file.exists()) {
            saveBytesToDisk(context, key, bytes)
        }
        val uri = runCatching {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }.getOrNull() ?: return@withContext false

        withContext(Dispatchers.Main) {
            val intent = Intent(Intent.ACTION_SEND).apply {
                type = "image/png"
                putExtra(Intent.EXTRA_STREAM, uri)
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(Intent.createChooser(intent, "分享图片").apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            })
        }
        true
    }
}
