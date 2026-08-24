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

    /** Lucide Share2 分享图标 */
    val Share2: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Share2",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f
        ).apply {
            // circle cx="18" cy="5" r="3"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(18f, 2f)
                curveTo(19.657f, 2f, 21f, 3.343f, 21f, 5f)
                curveTo(21f, 6.657f, 19.657f, 8f, 18f, 8f)
                curveTo(16.343f, 8f, 15f, 6.657f, 15f, 5f)
                curveTo(15f, 3.343f, 16.343f, 2f, 18f, 2f)
                close()
            }
            // circle cx="6" cy="12" r="3"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(6f, 9f)
                curveTo(7.657f, 9f, 9f, 10.343f, 9f, 12f)
                curveTo(9f, 13.657f, 7.657f, 15f, 6f, 15f)
                curveTo(4.343f, 15f, 3f, 13.657f, 3f, 12f)
                curveTo(3f, 10.343f, 4.343f, 9f, 6f, 9f)
                close()
            }
            // circle cx="18" cy="19" r="3"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(18f, 16f)
                curveTo(19.657f, 16f, 21f, 17.343f, 21f, 19f)
                curveTo(21f, 20.657f, 19.657f, 22f, 18f, 22f)
                curveTo(16.343f, 22f, 15f, 20.657f, 15f, 19f)
                curveTo(15f, 17.343f, 16.343f, 16f, 18f, 16f)
                close()
            }
            // line x1="8.59" y1="13.51" x2="15.42" y2="17.49"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(8.59f, 13.51f)
                lineTo(15.42f, 17.49f)
            }
            // line x1="15.41" y1="6.51" x2="8.59" y2="10.49"
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(15.41f, 6.51f)
                lineTo(8.59f, 10.49f)
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

    /** Lucide Upload 导出图标 */
    val Upload: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Upload",
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
                moveTo(21f, 15f)
                lineTo(21f, 19f)
                curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f)
                lineTo(5f, 21f)
                curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f)
                lineTo(3f, 15f)

                moveTo(17f, 8f)
                lineTo(12f, 3f)
                lineTo(7f, 8f)

                moveTo(12f, 3f)
                lineTo(12f, 15f)
            }
        }.build()
    }

    /** Lucide Download 导入图标 */
    val Download: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Download",
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
                moveTo(21f, 15f)
                lineTo(21f, 19f)
                curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f)
                lineTo(5f, 21f)
                curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f)
                lineTo(3f, 15f)

                moveTo(7f, 10f)
                lineTo(12f, 15f)
                lineTo(17f, 10f)

                moveTo(12f, 15f)
                lineTo(12f, 3f)
            }
        }.build()
    }

    /** Lucide Send / PaperPlane 发送图标 */
    val Send: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Send",
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
                moveTo(22f, 2f)
                lineTo(11f, 13f)
                moveTo(22f, 2f)
                lineTo(15f, 22f)
                lineTo(11f, 13f)
                lineTo(2f, 9f)
                close()
            }
        }.build()
    }

    /** Lucide ImagePlus 添加图片图标 */
    val ImagePlus: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ImagePlus",
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
                moveTo(21f, 12f)
                lineTo(21f, 19f)
                curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f)
                lineTo(5f, 21f)
                curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f)
                lineTo(3f, 5f)
                curveTo(3.9f, 3f, 3f, 3.9f, 5f, 3f)
                lineTo(12f, 3f)

                moveTo(9f, 9f)
                curveTo(9f, 9.55f, 8.55f, 10f, 8f, 10f)
                curveTo(7.45f, 10f, 7f, 9.55f, 7f, 9f)
                curveTo(7f, 8.45f, 7.45f, 8f, 8f, 8f)
                curveTo(8.55f, 8f, 9f, 8.45f, 9f, 9f)
                close()

                moveTo(21f, 15f)
                lineTo(16f, 10f)
                lineTo(5f, 21f)

                moveTo(19f, 2f)
                lineTo(19f, 8f)
                moveTo(16f, 5f)
                lineTo(22f, 5f)
            }
        }.build()
    }

    /** Lucide MessageSquare 对话/互传图标 */
    val MessageSquare: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.MessageSquare",
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
                moveTo(21f, 15f)
                curveTo(21f, 16.1f, 20.1f, 17f, 19f, 17f)
                lineTo(7f, 17f)
                lineTo(3f, 21f)
                lineTo(3f, 5f)
                curveTo(3f, 3.9f, 3.9f, 3f, 5f, 3f)
                lineTo(19f, 3f)
                curveTo(20.1f, 3f, 21f, 3.9f, 21f, 5f)
                close()
            }
        }.build()
    }

    /** Lucide ArrowDown 向下箭头图标 */
    val ArrowDown: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ArrowDown",
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
                moveTo(12f, 5f)
                lineTo(12f, 19f)
                moveTo(19f, 12f)
                lineTo(12f, 19f)
                lineTo(5f, 12f)
            }
        }.build()
    }

    /** Lucide Plus 加号图标 */
    val Plus: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Plus",
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
                moveTo(12f, 5f)
                lineTo(12f, 19f)
                moveTo(5f, 12f)
                lineTo(19f, 12f)
            }
        }.build()
    }

    /** Lucide X 关闭/清除图标 */
    val X: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.X",
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
                moveTo(18f, 6f)
                lineTo(6f, 18f)
                moveTo(6f, 6f)
                lineTo(18f, 18f)
            }
        }.build()
    }

    /** Lucide Check 勾选图标 */
    val Check: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Check",
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
                moveTo(20f, 6f)
                lineTo(9f, 17f)
                lineTo(4f, 12f)
            }
        }.build()
    }

    /** FontAwesome / Lucide Filter 筛选漏斗图标 */
    val Filter: ImageVector by lazy {
        ImageVector.Builder(
            name = "FontAwesome.Filter",
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
                moveTo(22f, 3f)
                lineTo(2f, 3f)
                lineTo(10f, 12.46f)
                lineTo(10f, 19f)
                lineTo(14f, 21f)
                lineTo(14f, 12.46f)
                close()
            }
        }.build()
    }

    /** Lucide ExternalLink / FontAwesome external-link 外部打开图标 */
    val ExternalLink: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ExternalLink",
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
                moveTo(18f, 13f)
                lineTo(18f, 19f)
                curveTo(18f, 20.1f, 17.1f, 21f, 16f, 21f)
                lineTo(5f, 21f)
                curveTo(3.9f, 21f, 3f, 20.1f, 3f, 19f)
                lineTo(3f, 8f)
                curveTo(3f, 6.9f, 3.9f, 6f, 5f, 6f)
                lineTo(11f, 6f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(15f, 3f)
                lineTo(21f, 3f)
                lineTo(21f, 9f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(10f, 14f)
                lineTo(21f, 3f)
            }
        }.build()
    }
    val ShieldCheck: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ShieldCheck",
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
                moveTo(20f, 13f)
                curveTo(20f, 18f, 16.5f, 20.5f, 12.34f, 21.95f)
                curveTo(12.13f, 22.02f, 11.87f, 22.02f, 11.66f, 21.95f)
                curveTo(7.5f, 20.5f, 4f, 18f, 4f, 13f)
                lineTo(4f, 6f)
                curveTo(4f, 5.45f, 4.45f, 5f, 5f, 5f)
                curveTo(7f, 5f, 9.5f, 3.8f, 11.24f, 2.28f)
                curveTo(11.68f, 1.9f, 12.32f, 1.9f, 12.76f, 2.28f)
                curveTo(14.51f, 3.81f, 17f, 5f, 19f, 5f)
                curveTo(19.55f, 5f, 20f, 5.45f, 20f, 6f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(9f, 12f)
                lineTo(11f, 14f)
                lineTo(15f, 10f)
            }
        }.build()
    }

    /** Lucide Shield-Alert 盾牌警示图标 */
    val ShieldAlert: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.ShieldAlert",
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
                moveTo(20f, 13f)
                curveTo(20f, 18f, 16.5f, 20.5f, 12.34f, 21.95f)
                curveTo(12.13f, 22.02f, 11.87f, 22.02f, 11.66f, 21.95f)
                curveTo(7.5f, 20.5f, 4f, 18f, 4f, 13f)
                lineTo(4f, 6f)
                curveTo(4f, 5.45f, 4.45f, 5f, 5f, 5f)
                curveTo(7f, 5f, 9.5f, 3.8f, 11.24f, 2.28f)
                curveTo(11.68f, 1.9f, 12.32f, 1.9f, 12.76f, 2.28f)
                curveTo(14.51f, 3.81f, 17f, 5f, 19f, 5f)
                curveTo(19.55f, 5f, 20f, 5.45f, 20f, 6f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 8f)
                lineTo(12f, 12f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 16f)
                lineTo(12.01f, 16f)
            }
        }.build()
    }

    /** Lucide Bell 通知铃铛图标 */
    val Bell: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Bell",
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
                moveTo(6f, 8f)
                curveTo(6f, 4.69f, 8.69f, 2f, 12f, 2f)
                curveTo(15.31f, 2f, 18f, 4.69f, 18f, 8f)
                curveTo(18f, 15f, 21f, 17f, 21f, 17f)
                lineTo(3f, 17f)
                curveTo(3f, 17f, 6f, 15f, 6f, 8f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(10.3f, 21f)
                curveTo(10.7f, 21.6f, 11.3f, 22f, 12f, 22f)
                curveTo(12.7f, 22f, 13.3f, 21.6f, 13.7f, 21f)
            }
        }.build()
    }

    /** Lucide Camera 相机图标 */
    val Camera: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Camera",
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
                moveTo(14.5f, 4f)
                lineTo(9.5f, 4f)
                lineTo(7f, 7f)
                lineTo(4f, 7f)
                curveTo(2.9f, 7f, 2f, 7.9f, 2f, 9f)
                lineTo(2f, 18f)
                curveTo(2f, 19.1f, 2.9f, 20f, 4f, 20f)
                lineTo(20f, 20f)
                curveTo(21.1f, 20f, 22f, 19.1f, 22f, 18f)
                lineTo(22f, 9f)
                curveTo(22f, 7.9f, 21.1f, 7f, 20f, 7f)
                lineTo(17f, 7f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 10f)
                curveTo(13.66f, 10f, 15f, 11.34f, 15f, 13f)
                curveTo(15f, 14.66f, 13.66f, 16f, 12f, 16f)
                curveTo(10.34f, 16f, 9f, 14.66f, 9f, 13f)
                curveTo(9f, 11.34f, 10.34f, 10f, 12f, 10f)
                close()
            }
        }.build()
    }

    /** Lucide BatteryCharging 电池优化图标 */
    val BatteryCharging: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.BatteryCharging",
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
                moveTo(15f, 7f)
                lineTo(16f, 7f)
                curveTo(17.1f, 7f, 18f, 7.9f, 18f, 9f)
                lineTo(18f, 15f)
                curveTo(18f, 16.1f, 17.1f, 17f, 16f, 17f)
                lineTo(14f, 17f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(6f, 7f)
                lineTo(4f, 7f)
                curveTo(2.9f, 7f, 2f, 7.9f, 2f, 9f)
                lineTo(2f, 15f)
                curveTo(2f, 16.1f, 2.9f, 17f, 4f, 17f)
                lineTo(5f, 17f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(11f, 7f)
                lineTo(8f, 12f)
                lineTo(12f, 12f)
                lineTo(9f, 17f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(22f, 11f)
                lineTo(22f, 13f)
            }
        }.build()
    }

    /** Lucide Layers 图层/模块图标 */
    val Layers: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Layers",
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
                moveTo(12.83f, 2.18f)
                curveTo(12.31f, 1.94f, 11.69f, 1.94f, 11.17f, 2.18f)
                lineTo(2.6f, 6.08f)
                curveTo(2.23f, 6.25f, 2.23f, 6.75f, 2.6f, 6.92f)
                lineTo(11.17f, 10.82f)
                curveTo(11.69f, 11.06f, 12.31f, 11.06f, 12.83f, 10.82f)
                lineTo(21.4f, 6.92f)
                curveTo(21.77f, 6.75f, 21.77f, 6.25f, 21.4f, 6.08f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(22f, 12.5f)
                lineTo(12.83f, 16.41f)
                curveTo(12.31f, 16.65f, 11.69f, 16.65f, 11.17f, 16.41f)
                lineTo(2f, 12.5f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(22f, 17.5f)
                lineTo(12.83f, 21.41f)
                curveTo(12.31f, 21.65f, 11.69f, 21.65f, 11.17f, 21.41f)
                lineTo(2f, 17.5f)
            }
        }.build()
    }

    /** Lucide Sparkles 闪光自启动优化图标 */
    val Sparkles: ImageVector by lazy {
        ImageVector.Builder(
            name = "Lucide.Sparkles",
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
                moveTo(9.94f, 15.5f)
                curveTo(9.54f, 14.73f, 8.93f, 14.12f, 8.16f, 13.72f)
                lineTo(2.36f, 10.74f)
                curveTo(1.88f, 10.49f, 1.88f, 9.8f, 2.36f, 9.55f)
                lineTo(8.16f, 6.57f)
                curveTo(8.93f, 6.17f, 9.54f, 5.56f, 9.94f, 4.79f)
                lineTo(12.92f, -1.01f) // relative
                curveTo(13.17f, -1.49f, 13.86f, -1.49f, 14.11f, -1.01f)
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(12f, 3f)
                lineTo(14f, 8f)
                lineTo(19f, 10f)
                lineTo(14f, 12f)
                lineTo(12f, 17f)
                lineTo(10f, 12f)
                lineTo(5f, 10f)
                lineTo(10f, 8f)
                close()
            }
            path(
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            ) {
                moveTo(19f, 17f)
                lineTo(20f, 19f)
                lineTo(22f, 20f)
                lineTo(20f, 21f)
                lineTo(19f, 23f)
                lineTo(18f, 21f)
                lineTo(16f, 20f)
                lineTo(18f, 19f)
                close()
            }
        }.build()
    }
}

/**
 * 根据设备名称、设备标识或平台信息解析最精确对应的矢量图标：
 * - 电脑/桌面设备（Windows / Mac / Linux / PC / DESKTOP / 主机）：显示台式机 Monitor 或笔记本 Laptop 图标
 * - 手机/平板设备（Android / iOS / Harmony / 手机名）：显示手机 Smartphone 图标
 * - 网页浏览器（Web / Browser）：显示 Globe 图标
 * - 默认作为桌面电脑设备 (Monitor 图标) 展示，彻底杜绝把电脑设备误显示为 Web 地球图标的问题。
 */
fun resolveDeviceIcon(deviceIdentifierOrName: String?, platformHint: String? = null): ImageVector {
    val raw = (deviceIdentifierOrName ?: "").lowercase()
    val hint = (platformHint ?: "").lowercase()
    val combined = "$raw $hint".trim()

    return when {
        // 手机/平板平台与移动设备标识（包含“本机”、手机型号代号、主流手机品牌）
        combined.contains("本机") || combined.contains("手机") || combined.contains("phone") ||
        combined.contains("android") || combined.contains("ios") || combined.contains("iphone") ||
        combined.contains("ipad") || combined.contains("harmony") || combined.contains("mobile") ||
        combined.contains("xiaomi") || combined.contains("redmi") || combined.contains("huawei") ||
        combined.contains("honor") || combined.contains("oppo") || combined.contains("vivo") ||
        combined.contains("oneplus") || combined.contains("meizu") || combined.contains("galaxy") ||
        combined.contains("pixel") || combined.contains("23127") || combined.contains("23116") ||
        combined.contains("24031") || combined.contains("24129") || combined.contains("2210132") ||
        combined.contains("2201123") || Regex("\\b[0-9]{4,5}[a-z0-9]+\\b").containsMatchIn(raw) -> LucideIcons.Smartphone

        // 笔记本/便携电脑
        combined.contains("laptop") || combined.contains("notebook") || combined.contains("thinkpad") ||
        combined.contains("macbook") || combined.contains("surface") || combined.contains("zenbook") ||
        combined.contains("yoga") -> LucideIcons.Laptop

        // 纯网页/浏览器扩展
        combined.contains("web") || combined.contains("browser") || combined.contains("chrome-extension") ||
        combined.contains("firefox-addon") || combined.contains("safari-extension") -> LucideIcons.Globe

        // 台式机/Windows/Mac/Linux/桌面系统或主机名 (如 DESKTOP-*, PC, Win, Mac, Linux 等)
        combined.contains("win") || combined.contains("desktop") || combined.contains("pc") ||
        combined.contains("mac") || combined.contains("linux") || combined.contains("ubuntu") ||
        combined.contains("workstation") || combined.contains("host") -> LucideIcons.Monitor

        // 默认电脑设备 (Monitor 屏幕图标)
        else -> LucideIcons.Monitor
    }
}

