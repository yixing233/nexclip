package clip.yixing.sync.ui.scan

import androidx.camera.core.ImageProxy
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.GlobalHistogramBinarizer
import com.google.zxing.common.HybridBinarizer
import java.nio.ByteBuffer
import java.util.EnumMap

/**
 * 纯离线、零 GMS 依赖的 ZXing 极速二维码解码引擎
 */
object ZxingQrDecoder {

    private val HINTS: Map<DecodeHintType, Any> = EnumMap<DecodeHintType, Any>(DecodeHintType::class.java).apply {
        put(DecodeHintType.POSSIBLE_FORMATS, listOf(BarcodeFormat.QR_CODE))
        put(DecodeHintType.CHARACTER_SET, "utf-8")
        put(DecodeHintType.TRY_HARDER, java.lang.Boolean.TRUE)
    }

    /**
     * 从 ImageProxy（YUV_420_888）亮度平面中实时解码二维码
     */
    fun decodeImageProxy(image: ImageProxy): String? {
        return try {
            val planes = image.planes
            if (planes.isEmpty()) return null

            val yPlane = planes[0]
            val buffer: ByteBuffer = yPlane.buffer.duplicate()
            val remaining = buffer.remaining()
            if (remaining == 0) return null

            val bytes = ByteArray(remaining)
            buffer.get(bytes)

            val width = image.width
            val height = image.height
            val rowStride = yPlane.rowStride
            val pixelStride = yPlane.pixelStride

            // 提取紧凑的 Y 灰度数组
            val data = if (rowStride == width && pixelStride == 1) {
                bytes
            } else {
                val compact = ByteArray(width * height)
                for (row in 0 until height) {
                    val srcPos = row * rowStride
                    val dstPos = row * width
                    val copyLen = minOf(width, bytes.size - srcPos)
                    if (copyLen > 0 && dstPos + copyLen <= compact.size) {
                        System.arraycopy(bytes, srcPos, compact, dstPos, copyLen)
                    }
                }
                compact
            }

            val rotation = image.imageInfo.rotationDegrees

            // 1. 尝试当前相机旋转角度
            val res1 = decodeRotatedYuv(data, width, height, rotation)
            if (res1 != null) return res1

            // 2. 备用尝试 0 度原图
            if (rotation != 0) {
                val res0 = decodeRotatedYuv(data, width, height, 0)
                if (res0 != null) return res0
            }

            null
        } catch (e: Exception) {
            null
        }
    }

    private fun decodeRotatedYuv(data: ByteArray, width: Int, height: Int, rotation: Int): String? {
        val (rotatedData, rotW, rotH) = when (rotation) {
            90 -> rotate90(data, width, height)
            180 -> rotate180(data, width, height)
            270 -> rotate270(data, width, height)
            else -> Triple(data, width, height)
        }

        val source = PlanarYUVLuminanceSource(
            rotatedData,
            rotW,
            rotH,
            0,
            0,
            rotW,
            rotH,
            false
        )

        val reader = MultiFormatReader().apply { setHints(HINTS) }

        // A. 尝试 HybridBinarizer (常规)
        try {
            val bitmap = BinaryBitmap(HybridBinarizer(source))
            val res = reader.decodeWithState(bitmap)
            if (!res.text.isNullOrBlank()) return res.text
        } catch (_: Exception) {
            reader.reset()
        }

        // B. 尝试 GlobalHistogramBinarizer (低对比度/反光)
        try {
            val bitmap = BinaryBitmap(GlobalHistogramBinarizer(source))
            val res = reader.decodeWithState(bitmap)
            if (!res.text.isNullOrBlank()) return res.text
        } catch (_: Exception) {
            reader.reset()
        }

        // C. 尝试反转颜色 (黑底白码)
        try {
            val bitmap = BinaryBitmap(HybridBinarizer(source.invert()))
            val res = reader.decodeWithState(bitmap)
            if (!res.text.isNullOrBlank()) return res.text
        } catch (_: Exception) {
            reader.reset()
        }

        return null
    }

    private fun rotate90(data: ByteArray, width: Int, height: Int): Triple<ByteArray, Int, Int> {
        val rotated = ByteArray(data.size)
        var i = 0
        for (x in 0 until width) {
            for (y in height - 1 downTo 0) {
                val idx = y * width + x
                if (idx < data.size && i < rotated.size) {
                    rotated[i++] = data[idx]
                }
            }
        }
        return Triple(rotated, height, width)
    }

    private fun rotate180(data: ByteArray, width: Int, height: Int): Triple<ByteArray, Int, Int> {
        val rotated = ByteArray(data.size)
        val size = data.size
        for (i in 0 until size) {
            rotated[size - 1 - i] = data[i]
        }
        return Triple(rotated, width, height)
    }

    private fun rotate270(data: ByteArray, width: Int, height: Int): Triple<ByteArray, Int, Int> {
        val rotated = ByteArray(data.size)
        var i = 0
        for (x in width - 1 downTo 0) {
            for (y in 0 until height) {
                val idx = y * width + x
                if (idx < data.size && i < rotated.size) {
                    rotated[i++] = data[idx]
                }
            }
        }
        return Triple(rotated, height, width)
    }
}
