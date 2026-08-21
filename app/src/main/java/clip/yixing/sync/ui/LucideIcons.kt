package clip.yixing.sync.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

/**
 * Android 端 Lucide 原生矢量图标集定义 (24x24, 线宽 2.0)
 */
object LucideIcons {

    /** Lucide Trash-2 垃圾桶图标 */
    val Trash2: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Trash2",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            // M3 6h18
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 6f)
                lineTo(21f, 6f)
            }
            // M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(19f, 6f)
                lineTo(19f, 20f)
                curveTo(19f, 21.1f, 18.1f, 22f, 17f, 22f)
                lineTo(7f, 22f)
                curveTo(5.9f, 22f, 5f, 21.1f, 5f, 20f)
                lineTo(5f, 6f)
            }
            // M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(8f, 6f)
                lineTo(8f, 4f)
                curveTo(8f, 2.9f, 8.9f, 2f, 10f, 2f)
                lineTo(14f, 2f)
                curveTo(15.1f, 2f, 16f, 2.9f, 16f, 4f)
                lineTo(16f, 6f)
            }
            // M10 11v6
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(10f, 11f)
                lineTo(10f, 17f)
            }
            // M14 11v6
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(14f, 11f)
                lineTo(14f, 17f)
            }
        }.build()
    }

    /** Lucide Copy 复制图标 */
    val Copy: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Copy",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            // rect x="9" y="9" width="13" height="13" rx="2" ry="2"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(11f, 9f)
                lineTo(20f, 9f)
                curveTo(21.1f, 9f, 22f, 9.9f, 22f, 11f)
                lineTo(22f, 20f)
                curveTo(22f, 21.1f, 21.1f, 22f, 20f, 22f)
                lineTo(11f, 22f)
                curveTo(9.9f, 22f, 9f, 21.1f, 9f, 20f)
                lineTo(9f, 11f)
                curveTo(9f, 9.9f, 9.9f, 9f, 11f, 9f)
                close()
            }
            // path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(5f, 15f)
                lineTo(4f, 15f)
                curveTo(2.9f, 15f, 2f, 14.1f, 2f, 13f)
                lineTo(2f, 4f)
                curveTo(2f, 2.9f, 2.9f, 2f, 4f, 2f)
                lineTo(13f, 2f)
                curveTo(14.1f, 2f, 15f, 2.9f, 15f, 4f)
                lineTo(15f, 5f)
            }
        }.build()
    }

    /** Lucide Refresh-cw 刷新图标 */
    val RefreshCw: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.RefreshCw",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 12f)
                curveTo(3f, 7.03f, 7.03f, 3f, 12f, 3f)
                curveTo(14.49f, 3f, 16.74f, 4.01f, 18.74f, 5.74f)
                lineTo(21f, 8f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(21f, 3f)
                lineTo(21f, 8f)
                lineTo(16f, 8f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(21f, 12f)
                curveTo(21f, 16.97f, 16.97f, 21f, 12f, 21f)
                curveTo(9.51f, 21f, 7.26f, 19.99f, 5.26f, 18.26f)
                lineTo(3f, 16f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 21f)
                lineTo(3f, 16f)
                lineTo(8f, 16f)
            }
        }.build()
    }

    /** Lucide Smartphone 手机图标 */
    val Smartphone: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Smartphone",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(7f, 2f)
                lineTo(17f, 2f)
                curveTo(18.1f, 2f, 19f, 2.9f, 19f, 4f)
                lineTo(19f, 20f)
                curveTo(19f, 21.1f, 18.1f, 22f, 17f, 22f)
                lineTo(7f, 22f)
                curveTo(5.9f, 22f, 5f, 21.1f, 5f, 20f)
                lineTo(5f, 4f)
                curveTo(5f, 2.9f, 5.9f, 2f, 7f, 2f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 18f)
                lineTo(12.01f, 18f)
            }
        }.build()
    }

    /** Lucide Laptop 笔记本图标 */
    val Laptop: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Laptop",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(20f, 16f)
                lineTo(20f, 7f)
                curveTo(20f, 5.9f, 19.1f, 5f, 18f, 5f)
                lineTo(6f, 5f)
                curveTo(4.9f, 5f, 4f, 5.9f, 4f, 7f)
                lineTo(4f, 16f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(2f, 19f)
                lineTo(22f, 19f)
            }
        }.build()
    }

    /** Lucide Monitor 桌面台式电脑图标 */
    val Monitor: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Monitor",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(4f, 3f)
                lineTo(20f, 3f)
                curveTo(21.1f, 3f, 22f, 3.9f, 22f, 5f)
                lineTo(22f, 15f)
                curveTo(22f, 16.1f, 21.1f, 17f, 20f, 17f)
                lineTo(4f, 17f)
                curveTo(2.9f, 17f, 2f, 16.1f, 2f, 15f)
                lineTo(2f, 5f)
                curveTo(2f, 3.9f, 2.9f, 3f, 4f, 3f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(8f, 21f)
                lineTo(16f, 21f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 17f)
                lineTo(12f, 21f)
            }
        }.build()
    }

    /** Lucide Globe 网页/地球图标 */
    val Globe: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Globe",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 22f)
                curveTo(17.52f, 22f, 22f, 17.52f, 22f, 12f)
                curveTo(22f, 6.48f, 17.52f, 2f, 12f, 2f)
                curveTo(6.48f, 2f, 2f, 6.48f, 2f, 12f)
                curveTo(2f, 17.52f, 6.48f, 22f, 12f, 22f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(2f, 12f)
                lineTo(22f, 12f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 2f)
                curveTo(14.5f, 5f, 16f, 8.5f, 16f, 12f)
                curveTo(16f, 15.5f, 14.5f, 19f, 12f, 22f)
                curveTo(9.5f, 19f, 8f, 15.5f, 8f, 12f)
                curveTo(8f, 8.5f, 9.5f, 5f, 12f, 2f)
                close()
            }
        }.build()
    }

    /** Lucide ClipboardCheck 剪贴板勾选图标 */
    val ClipboardCheck: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ClipboardCheck",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(9f, 5f)
                lineTo(15f, 5f)
                curveTo(16.1f, 5f, 17f, 5.9f, 17f, 7f)
                lineTo(17f, 7f)
                curveTo(17f, 8.1f, 16.1f, 9f, 15f, 9f)
                lineTo(9f, 9f)
                curveTo(7.9f, 9f, 7f, 8.1f, 7f, 7f)
                lineTo(7f, 7f)
                curveTo(7f, 5.9f, 7.9f, 5f, 9f, 5f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(16f, 4f)
                lineTo(18f, 4f)
                curveTo(19.1f, 4f, 20f, 4.9f, 20f, 6f)
                lineTo(20f, 20f)
                curveTo(20f, 21.1f, 19.1f, 22f, 18f, 22f)
                lineTo(6f, 22f)
                curveTo(4.9f, 22f, 4f, 21.1f, 4f, 20f)
                lineTo(4f, 6f)
                curveTo(4f, 4.9f, 4.9f, 4f, 6f, 4f)
                lineTo(8f, 4f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(9f, 14f)
                lineTo(11f, 16f)
                lineTo(15f, 12f)
            }
        }.build()
    }

    /** Lucide QrCode 二维码图标 */
    val QrCode: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.QrCode",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 7f)
                lineTo(3f, 5f)
                curveTo(3f, 3.9f, 3.9f, 3f, 5f, 3f)
                lineTo(7f, 3f)
                curveTo(8.1f, 3f, 9f, 3.9f, 9f, 5f)
                lineTo(9f, 7f)
                curveTo(9f, 8.1f, 8.1f, 9f, 7f, 9f)
                lineTo(5f, 9f)
                curveTo(3.9f, 9f, 3f, 8.1f, 3f, 7f)
                close()

                moveTo(15f, 7f)
                lineTo(15f, 5f)
                curveTo(15f, 3.9f, 15.9f, 3f, 17f, 3f)
                lineTo(19f, 3f)
                curveTo(20.1f, 3f, 21f, 3.9f, 21f, 5f)
                lineTo(21f, 7f)
                curveTo(21f, 8.1f, 20.1f, 9f, 19f, 9f)
                lineTo(17f, 9f)
                curveTo(15.9f, 9f, 15f, 8.1f, 15f, 7f)
                close()

                moveTo(3f, 19f)
                lineTo(3f, 17f)
                curveTo(3f, 15.9f, 3.9f, 15f, 5f, 15f)
                lineTo(7f, 15f)
                curveTo(8.1f, 15f, 9f, 15.9f, 9f, 17f)
                lineTo(9f, 19f)
                curveTo(9f, 20.1f, 8.1f, 21f, 7f, 21f)
                lineTo(5f, 21f)
                curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f)
                close()

                moveTo(21f, 15f); lineTo(19f, 15f); lineTo(19f, 17f); lineTo(21f, 17f)
                moveTo(15f, 15f); lineTo(15f, 21f)
                moveTo(19f, 21f); lineTo(21f, 21f)
            }
        }.build()
    }

    /** Lucide Scan 扫描框图标 */
    val Scan: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Scan",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 7f); lineTo(3f, 5f); curveTo(3f, 3.9f, 3.9f, 3f, 5f, 3f); lineTo(7f, 3f)
                moveTo(17f, 3f); lineTo(19f, 3f); curveTo(20.1f, 3f, 21f, 3.9f, 21f, 5f); lineTo(21f, 7f)
                moveTo(21f, 17f); lineTo(21f, 19f); curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f); lineTo(17f, 21f)
                moveTo(7f, 21f); lineTo(5f, 21f); curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f); lineTo(3f, 17f)
            }
        }.build()
    }

    /** Lucide ScanLine 扫码图标(带中心扫描线) */
    val ScanLine: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ScanLine",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(3f, 7f); lineTo(3f, 5f); curveTo(3f, 3.9f, 3.9f, 3f, 5f, 3f); lineTo(7f, 3f)
                moveTo(17f, 3f); lineTo(19f, 3f); curveTo(20.1f, 3f, 21f, 3.9f, 21f, 5f); lineTo(21f, 7f)
                moveTo(21f, 17f); lineTo(21f, 19f); curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f); lineTo(17f, 21f)
                moveTo(7f, 21f); lineTo(5f, 21f); curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f); lineTo(3f, 17f)
                moveTo(7f, 12f); lineTo(17f, 12f)
            }
        }.build()
    }

    /** Lucide Zap 闪电/闪光灯图标 */
    val Zap: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Zap",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(13f, 2f)
                lineTo(3f, 14f)
                lineTo(12f, 14f)
                lineTo(11f, 22f)
                lineTo(21f, 10f)
                lineTo(12f, 10f)
                close()
            }
        }.build()
    }

    /** Lucide Image 图片图标 */
    val Image: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Image",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(5f, 3f)
                curveTo(3.9f, 3f, 3f, 3.9f, 3f, 5f)
                lineTo(3f, 19f)
                curveTo(3f, 20.1f, 3.9f, 21f, 5f, 21f)
                lineTo(19f, 21f)
                curveTo(20.1f, 21f, 21f, 20.1f, 21f, 19f)
                lineTo(21f, 5f)
                curveTo(21f, 3.9f, 20.1f, 3f, 19f, 3f)
                close()

                moveTo(9f, 9f)
                curveTo(9f, 9.55f, 8.55f, 10f, 8f, 10f)
                curveTo(7.45f, 10f, 7f, 9.55f, 7f, 9f)
                curveTo(7f, 8.45f, 7.45f, 8f, 8f, 8f)
                curveTo(8.55f, 8f, 9f, 8.45f, 9f, 9f)
                close()

                moveTo(21f, 15f)
                lineTo(16f, 10f)
                lineTo(5f, 21f)
            }
        }.build()
    }
}
