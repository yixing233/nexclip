package clip.yixing.sync.ui.scan

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.util.EnumMap
import kotlin.coroutines.resume

object BitmapQrDecoder {

    /**
     * 从 Content Uri 读取图片并解码二维码
     */
    suspend fun decodeFromUri(context: Context, uri: Uri): String? = withContext(Dispatchers.IO) {
        val bitmap = runCatching {
            context.contentResolver.openInputStream(uri)?.use { stream ->
                BitmapFactory.decodeStream(stream)
            }
        }.getOrNull() ?: return@withContext null

        decodeFromBitmap(bitmap)
    }

    /**
     * 通过 ZXing (离线优先) 与 ML Kit 解析 Bitmap 中的二维码
     */
    suspend fun decodeFromBitmap(bitmap: Bitmap): String? = withContext(Dispatchers.Default) {
        // 1. ZXing 离线解析
        val zxingRes = decodeWithZxing(bitmap)
        if (!zxingRes.isNullOrBlank()) {
            return@withContext zxingRes
        }

        // 2. ML Kit 备用解析
        decodeWithMlKit(bitmap)
    }

    private fun decodeWithZxing(bitmap: Bitmap): String? {
        return try {
            val width = bitmap.width
            val height = bitmap.height
            val pixels = IntArray(width * height)
            bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
            val source = RGBLuminanceSource(width, height, pixels)
            val binaryBitmap = BinaryBitmap(HybridBinarizer(source))
            val hints = EnumMap<DecodeHintType, Any>(DecodeHintType::class.java).apply {
                put(DecodeHintType.POSSIBLE_FORMATS, listOf(BarcodeFormat.QR_CODE))
                put(DecodeHintType.CHARACTER_SET, "utf-8")
                put(DecodeHintType.TRY_HARDER, java.lang.Boolean.TRUE)
            }
            val reader = MultiFormatReader().apply { setHints(hints) }
            reader.decodeWithState(binaryBitmap)?.text
        } catch (_: Exception) {
            null
        }
    }

    private suspend fun decodeWithMlKit(bitmap: Bitmap): String? = suspendCancellableCoroutine { cont ->
        try {
            val image = InputImage.fromBitmap(bitmap, 0)
            val scanner = BarcodeScanning.getClient()
            scanner.process(image)
                .addOnSuccessListener { barcodes ->
                    val qr = barcodes.firstOrNull { it.format == Barcode.FORMAT_QR_CODE }
                        ?: barcodes.firstOrNull()
                    cont.resume(qr?.rawValue)
                }
                .addOnFailureListener {
                    cont.resume(null)
                }
        } catch (_: Exception) {
            cont.resume(null)
        }
    }
}
