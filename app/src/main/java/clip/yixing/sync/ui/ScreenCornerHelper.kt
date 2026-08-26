package clip.yixing.sync.ui

import android.content.Context
import android.os.Build
import android.view.RoundedCorner
import android.view.View
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * 屏幕物理圆角检测与自适应工具。
 * 精准获取设备硬件物理屏幕圆角半径（如 HyperOS / 小米 13/14/15 旗舰机典型 32dp~38dp 屏幕圆角），
 * 为预测性返回 (Predictive Back)、卡片缩放及全屏跳板提供完美贴合屏幕边框的圆角视觉裁切。
 */
object ScreenCornerHelper {

    private const val DEFAULT_SCREEN_CORNER_DP = 34f

    fun getScreenCornerRadius(context: Context, view: View?, density: Density): Dp {
        var radiusPx = 0f

        // 1. Android 12+ (API 31+) 官方硬件物理圆角 API
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && view != null) {
            val insets = view.rootWindowInsets
            if (insets != null) {
                val tl = insets.getRoundedCorner(RoundedCorner.POSITION_TOP_LEFT)?.radius ?: 0
                val tr = insets.getRoundedCorner(RoundedCorner.POSITION_TOP_RIGHT)?.radius ?: 0
                val bl = insets.getRoundedCorner(RoundedCorner.POSITION_BOTTOM_LEFT)?.radius ?: 0
                val br = insets.getRoundedCorner(RoundedCorner.POSITION_BOTTOM_RIGHT)?.radius ?: 0
                val maxRadius = maxOf(tl, tr, bl, br).toFloat()
                if (maxRadius > 0f) {
                    radiusPx = maxRadius
                }
            }
        }

        // 2. 尝试从厂商定制系统资源读取物理圆角
        if (radiusPx <= 0f) {
            val resNames = listOf(
                "rounded_corner_radius",
                "status_bar_corner_radius",
                "rounded_corner_radius_top"
            )
            for (resName in resNames) {
                val resId = context.resources.getIdentifier(resName, "dimen", "android")
                if (resId > 0) {
                    runCatching {
                        val d = context.resources.getDimension(resId)
                        if (d > 0f) {
                            radiusPx = d
                            return@runCatching
                        }
                    }
                }
                if (radiusPx > 0f) break
            }
        }

        return if (radiusPx > 0f) {
            with(density) { radiusPx.toDp() }
        } else {
            DEFAULT_SCREEN_CORNER_DP.dp
        }
    }
}

/**
 * 记忆当前设备硬件物理屏幕圆角半径
 */
@Composable
fun rememberScreenCornerRadius(): Dp {
    val context = LocalContext.current
    val density = LocalDensity.current
    val view = LocalView.current
    return remember(view, density) {
        ScreenCornerHelper.getScreenCornerRadius(context, view, density)
    }
}
