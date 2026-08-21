package clip.yixing.sync.ui.scan

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
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
     * 通过 ML Kit 解析 Bitmap 中的二维码
     */
    suspend fun decodeFromBitmap(bitmap: Bitmap): String? = suspendCancellableCoroutine { cont ->
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
    }
}
