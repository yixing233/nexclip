package clip.yixing.sync.ui

import androidx.compose.animation.ContentTransform
import androidx.compose.animation.core.Easing
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Immutable
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * 阻尼弹簧缓动曲线(移植自 hyperx-compose 的 NavTransitionEasing)。
 *
 * 用物理阻尼振动推导:
 *   x(t) = e^(r·t) · (-cos(w·t) + c2·sin(w·t)) + 1
 * 其中 omega = 2π/response、c = damping·4π/response,
 * w = √(4k - c²)/2、r = -c/2。
 *
 * 相比 FastOutSlowInEasing 的好处:起手更快、收尾带轻微惯性,
 * 页面转场看起来更像系统原生(MIUI 的转场正是这种手感)。
 *
 * @param response 响应时间(秒),越小越快。0.8 是接近系统设置的平衡值。
 * @param damping 阻尼比,1.0 为临界阻尼。0.95 会有极轻微回弹。
 */
@Immutable
class DampedEasing(
    response: Float,
    damping: Float,
) : Easing {
    private val r: Float
    private val w: Float
    private val c2: Float

    init {
        val omega = 2.0 * PI / response
        val k = omega * omega
        val c = damping * 4.0 * PI / response

        w = (sqrt(4.0 * k - c * c) / 2.0).toFloat()
        r = (-c / 2.0).toFloat()
        c2 = r / w
    }

    override fun transform(fraction: Float): Float {
        val t = fraction.toDouble()
        val decay = exp(r * t)
        return (decay * (-cos(w * t) + c2 * sin(w * t)) + 1.0).toFloat()
    }
}

/**
 * 二级页的转场定义(移植自 hyperx-compose 的 HyperXNavTransitions)。
 *
 * 核心是【进入与返回用相反的转场】,两页同时对向移动:
 *
 *   进入: 新页 从右侧整屏滑入 (it → 0)
 *         旧页 向左移出 1/4    (0 → -it/4)
 *
 *   返回: 上一级 从左 1/4 处滑回原位 (-it/4 → 0)
 *         当前页 向右整屏滑出      (0 → it)
 *
 * 这样任何一帧屏幕都被两页之一填满,不会露出底层;
 * 而且返回时上一级是"滑回来"的,不是瞬间出现。
 */
object SettingsNavTransitions {
    private val Easing = DampedEasing(0.8f, 0.95f)
    private const val DURATION = 420

    /** 进入下一级。 */
    fun push(): ContentTransform = ContentTransform(
        targetContentEnter = slideInHorizontally(
            initialOffsetX = { it },
            animationSpec = tween(durationMillis = DURATION, easing = Easing),
        ),
        initialContentExit = slideOutHorizontally(
            targetOffsetX = { -it / 4 },
            animationSpec = tween(durationMillis = DURATION, easing = Easing),
        ),
    )

    /** 返回上一级。 */
    fun pop(): ContentTransform = ContentTransform(
        targetContentEnter = slideInHorizontally(
            initialOffsetX = { -it / 4 },
            animationSpec = tween(durationMillis = DURATION, easing = Easing),
        ),
        initialContentExit = slideOutHorizontally(
            targetOffsetX = { it },
            animationSpec = tween(durationMillis = DURATION, easing = Easing),
        ),
    )
}
