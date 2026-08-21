package clip.yixing.sync.ui.scan

import androidx.camera.core.ImageProxy
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.nio.ByteBuffer
import java.util.EnumMap

/**
 * 纯离线、零 GMS 依赖的 ZXing 极速二维码解码引擎
 */
object ZxingQrDecoder {

    /**
     * 从 ImageProxy（YUV_420_888）亮度平面中实时解码二维码
     */
    fun decodeImageProxy(image: ImageProxy): String? {
        return try {
            val planes = image.planes
            if (planes.isEmpty()) return null

            val yPlane = planes[0]
            val buffer: ByteBuffer = yPlane.buffer
            val remaining = buffer.remaining()
            if (remaining == 0) return null

            val bytes = ByteArray(remaining)
            buffer.get(bytes)

            val width = image.width
            val height = image.height
            val rowStride = yPlane.rowStride
            val pixelStride = yPlane.pixelStride

            // 紧凑排布 Y 亮度数据（去除行填充）
            val data = if (rowStride == width && pixelStride == 1) {
                bytes
            } else {
                val compact = ByteArray(width * height)
                for (row in 0 until height) {
                    val srcPos = row * rowStride
                    val dstPos = row * width
                    if (srcPos + width <= bytes.size && dstPos + width <= compact.size) {
                        System.arraycopy(bytes, srcPos, compact, dstPos, width)
                    }
                }
                compact
            }

            // 根据相机旋转角度旋转灰度图像
            val rotation = image.imageInfo.rotationDegrees
            val (rotatedData, rotatedWidth, rotatedHeight) = when (rotation) {
                90 -> rotate90(data, width, height)
                180 -> rotate180(data, width, height)
                270 -> rotate270(data, width, height)
                else -> Triple(data, width, height)
            }

            tryDecodeYuv(rotatedData, rotatedWidth, rotatedHeight)
        } catch (_: Exception) {
            null
        }
    }

    private fun tryDecodeYuv(data: ByteArray, width: Int, height: Int): String? {
        return try {
            val source = PlanarYUVLuminanceSource(
                data,
                width,
                height,
                0,
                0,
                width,
                height,
                false
            )
            val binaryBitmap = BinaryBitmap(HybridBinarizer(source))
            val hints = EnumMap<DecodeHintType, Any>(DecodeHintType::class.java).apply {
                put(DecodeHintType.POSSIBLE_FORMATS, listOf(BarcodeFormat.QR_CODE))
                put(DecodeHintType.CHARACTER_SET, "utf-8")
            }
            val reader = MultiFormatReader().apply { setHints(hints) }
            reader.decodeWithState(binaryBitmap)?.text
        } catch (_: Exception) {
            null
        }
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
