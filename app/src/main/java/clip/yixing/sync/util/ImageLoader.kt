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
import clip.yixing.sync.service.ClipboardMonitorService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream

object ImageLoader {

    /**
     * 服务端允许的单张图片上限(与服务端 config.json 的 maxImageSizeBytes 一致)。
     * 这里留 20% 余量给 multipart 封装开销。
     */
    const val SERVER_IMAGE_LIMIT_BYTES = 10L * 1024 * 1024
    private const val UPLOAD_TARGET_BYTES = (SERVER_IMAGE_LIMIT_BYTES * 0.8).toLong()

    /**
     * 尺寸缩小序列(相对原图的比例)。
     *
     * 必须"先降质量、再降尺寸":截图与照片经 JPEG q=90 编码后通常已远小于 10MB,
     * 直接按尺寸缩会把长截图压成细条(实测原实现把 1080x12000 压成 368x4096,宽只剩 34%),
     * 那是明显的画质损失,而体积本可以靠质量参数解决。
     */
    private val DOWNSCALE_SCALES = floatArrayOf(1.0f, 0.75f, 0.5f, 0.35f, 0.25f, 0.15f)

    /** JPEG 质量递减序列。 */
    private val JPEG_QUALITIES = intArrayOf(90, 85, 80, 70, 60, 50)

    /**
     * 总像素上限(约 24MP)。
     *
     * 刻意【不设单边硬上限】:长截图(如 1080x30000)的价值恰恰在于"长",
     * 按单边 4096 钳制会把宽度从 1080 压到 147(只剩 14%),是明显的画质损失。
     * 体积由 JPEG 质量参数控制,尺寸只需约束总像素以免解码 OOM。
     */
    private const val MAX_PIXELS = 24_000_000L

    /** 压缩结果。 */
    data class CompressedImage(
        val bytes: ByteArray,
        /** 原始尺寸。 */
        val originalWidth: Int,
        val originalHeight: Int,
        /** 实际编码后的尺寸(可能被缩小)。 */
        val width: Int,
        val height: Int,
        /** 是否为了满足体积限制而做了降级。 */
        val degraded: Boolean,
        /** 编码格式,"image/jpeg" 或 "image/png"。 */
        val mimeType: String,
    )

    /**
     * 把剪贴板图片压缩到服务端可接受的体积内。
     *
     * 背景:此前 Android 端直接把原图 readBytes() 上传,判据是 30MB,
     * 而服务端只收 10MB —— 手机长截图/相机原图很容易超过,
     * 服务端返回 413 后又被 catch {} 静默吞掉,用户完全无感知。
     *
     * 策略(顺序很重要):
     * 1. 原图已足够小:原样上传,不解码不重编码(省 CPU、不损失画质)。
     * 2. 否则【先在原尺寸上降质量】:截图与照片经 JPEG q=90 通常已远小于 10MB,
     *    这一步能保住绝大多数图片的原始分辨率(实测 1080x12000 长截图 q=90 约 0.4MB)。
     * 3. 仍超限才【按比例缩小尺寸】,每一档同样从 q=90 开始试。
     *    先降质量再降尺寸可避免"长截图被压成细条"这种明显画质损失。
     * 4. 透明图优先保持 PNG;PNG 超限时才退回 JPEG(牺牲透明度换体积)。
     *
     * 注意 EXIF:BitmapFactory 解码后方向信息会丢失。这里用 ExifInterface 读取并旋转,
     * 避免上传后图片被其他设备旋转 90 度。
     *
     * @return 压缩结果;无法解码(损坏文件)或最小档仍超限时返回 null,由调用方提示用户。
     */
    fun compressForUpload(context: Context, raw: ByteArray, uri: Uri? = null): CompressedImage? {
        if (raw.isEmpty()) return null

        // 1) 读尺寸,判断是否需要处理
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(raw, 0, raw.size, bounds)
        val origW = bounds.outWidth
        val origH = bounds.outHeight
        if (origW <= 0 || origH <= 0) return null

        val hasAlpha = bounds.outMimeType?.contains("png", ignoreCase = true) == true
        val withinSize = raw.size <= UPLOAD_TARGET_BYTES
        val withinPixels = origW.toLong() * origH <= MAX_PIXELS

        if (withinSize && withinPixels) {
            return CompressedImage(
                bytes = raw,
                originalWidth = origW, originalHeight = origH,
                width = origW, height = origH,
                degraded = false,
                mimeType = bounds.outMimeType ?: "image/png",
            )
        }

        // 2) 按比例档位尝试:每档先原尺寸降质量,再考虑该档的尺寸
        for (scale in DOWNSCALE_SCALES) {
            // 优先保留原尺寸:体积靠下面的质量参数调节。
            // 只有总像素超过内存上限时才等比缩小。
            val (targetW, targetH) = clampToLimits(
                maxOf(1, (origW * scale).toInt()),
                maxOf(1, (origH * scale).toInt()),
            )

            val bitmap = decodeScaled(raw, targetW, targetH) ?: continue
            val oriented = applyExifRotation(context, bitmap, uri, raw)

            // 2a) 透明图先试 PNG(无损保留透明度)
            if (hasAlpha) {
                val png = encode(oriented, Bitmap.CompressFormat.PNG, 100)
                if (png != null && png.size <= UPLOAD_TARGET_BYTES) {
                    val w = oriented.width
                    val h = oriented.height
                    if (oriented !== bitmap) oriented.recycle()
                    bitmap.recycle()
                    return CompressedImage(png, origW, origH, w, h, true, "image/png")
                }
            }

            // 2b) JPEG 按质量递减
            for (q in JPEG_QUALITIES) {
                val jpeg = encode(oriented, Bitmap.CompressFormat.JPEG, q)
                if (jpeg != null && jpeg.size <= UPLOAD_TARGET_BYTES) {
                    val w = oriented.width
                    val h = oriented.height
                    if (oriented !== bitmap) oriented.recycle()
                    bitmap.recycle()
                    return CompressedImage(jpeg, origW, origH, w, h, true, "image/jpeg")
                }
            }

            if (oriented !== bitmap) oriented.recycle()
            bitmap.recycle()
        }

        // 3) 最小档 + 最低质量仍超限
        return null
    }

    /** 仅在总像素超过内存上限时等比缩小,不施加单边硬上限(见 MAX_PIXELS 注释)。 */
    private fun clampToLimits(w: Int, h: Int): Pair<Int, Int> {
        val pixels = w.toLong() * h
        if (pixels <= MAX_PIXELS) return w to h
        val k = Math.sqrt(MAX_PIXELS.toDouble() / pixels)
        return maxOf(1, (w * k).toInt()) to maxOf(1, (h * k).toInt())
    }

    private fun decodeScaled(raw: ByteArray, targetW: Int, targetH: Int): Bitmap? = try {
        val opts = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(raw, 0, raw.size, opts)
        var sample = 1
        while (opts.outWidth / (sample * 2) >= targetW && opts.outHeight / (sample * 2) >= targetH) {
            sample *= 2
        }
        val decodeOpts = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }
        val decoded = BitmapFactory.decodeByteArray(raw, 0, raw.size, decodeOpts) ?: return null
        if (decoded.width == targetW && decoded.height == targetH) {
            decoded
        } else {
            Bitmap.createScaledBitmap(decoded, targetW, targetH, true).also {
                if (it !== decoded) decoded.recycle()
            }
        }
    } catch (e: Exception) {
        null
    } catch (e: OutOfMemoryError) {
        null
    }

    /**
     * 按 EXIF 方向旋转;读不到方向或方向正常时原样返回。
     *
     * 优先复用传入的 Uri(避免重复解码);没有 Uri 时直接从字节流读 EXIF ——
     * 之前这里返回 null 就放弃了,导致"从字节数组来的图片"方向信息全部丢失,
     * 手机竖拍的照片上传后在其他设备上是横的。
     */
    private fun applyExifRotation(context: Context, bitmap: Bitmap, uri: Uri?, raw: ByteArray): Bitmap {
        val orientation = try {
            val stream: java.io.InputStream? = if (uri != null) {
                context.contentResolver.openInputStream(uri)
            } else {
                java.io.ByteArrayInputStream(raw)
            }
            stream?.use {
                android.media.ExifInterface(it).getAttributeInt(
                    android.media.ExifInterface.TAG_ORIENTATION,
                    android.media.ExifInterface.ORIENTATION_NORMAL,
                )
            } ?: android.media.ExifInterface.ORIENTATION_NORMAL
        } catch (e: Exception) {
            android.media.ExifInterface.ORIENTATION_NORMAL
        }

        val matrix = android.graphics.Matrix()
        when (orientation) {
            android.media.ExifInterface.ORIENTATION_ROTATE_90 -> matrix.postRotate(90f)
            android.media.ExifInterface.ORIENTATION_ROTATE_180 -> matrix.postRotate(180f)
            android.media.ExifInterface.ORIENTATION_ROTATE_270 -> matrix.postRotate(270f)
            android.media.ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> matrix.postScale(-1f, 1f)
            android.media.ExifInterface.ORIENTATION_FLIP_VERTICAL -> matrix.postScale(1f, -1f)
            else -> return bitmap
        }
        return try {
            Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, matrix, true)
        } catch (e: Exception) {
            bitmap
        }
    }

    private fun encode(bitmap: Bitmap, format: Bitmap.CompressFormat, quality: Int): ByteArray? = try {
        java.io.ByteArrayOutputStream().use { out ->
            bitmap.compress(format, quality, out)
            out.toByteArray()
        }
    } catch (e: Exception) {
        null
    } catch (e: OutOfMemoryError) {
        null
    }

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
            val clip = ClipData.newUri(context.contentResolver, "NexClip Image", uri)
            ClipboardMonitorService.copyToClipboardInternal(
                context = context,
                clipData = clip,
                imageRef = imageRef
            )
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

    /** 使用其他应用打开图片 (ACTION_VIEW 弹起应用选择器) */
    suspend fun openImageWithOtherApp(context: Context, imageRef: String?, rawText: String?): Boolean = withContext(Dispatchers.IO) {
        val bytes = getImageBytes(context, imageRef, rawText) ?: return@withContext false
        val key = imageRef?.takeIf { it.isNotBlank() } ?: "view_${System.currentTimeMillis()}"
        val file = getCachedFile(context, key)
        if (!file.exists()) {
            saveBytesToDisk(context, key, bytes)
        }
        val uri = runCatching {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }.getOrNull() ?: return@withContext false

        withContext(Dispatchers.Main) {
            val intent = Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(uri, "image/*")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            try {
                val chooser = Intent.createChooser(intent, "使用其他应用打开图片").apply {
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                }
                context.startActivity(chooser)
            } catch (_: Exception) {
                try {
                    context.startActivity(intent)
                } catch (_: Exception) {
                    return@withContext false
                }
            }
        }
        true
    }
}
