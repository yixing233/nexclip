package clip.yixing.sync.ui.nav

import androidx.activity.compose.BackHandler
import androidx.activity.compose.PredictiveBackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.Easing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.graphicsLayer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.roundToInt
import top.yukonga.miuix.kmp.blur.BlendColorEntry
import top.yukonga.miuix.kmp.blur.BlurDefaults
import top.yukonga.miuix.kmp.blur.LayerBackdrop
import top.yukonga.miuix.kmp.blur.textureBlur
import top.yukonga.miuix.kmp.theme.MiuixTheme

/**
 * 层级页面导航模型(移植自 HyperIsland 的 PredictiveNavigationLayer / PredictiveBackMotion)。
 *
 * 改造前 NexClip 有四套互不相同的二级页转场(设置页 AnimatedContent 视差、
 * 记录详情手搓 Animatable、扫码/互传整屏覆盖、搜索页 predictiveBackAnimation),
 * 手感与参数各不一致。这里照搬 HyperIsland 的单一模型,让所有层级页面共用:
 *
 *   进入: 新层整屏从右滑入 420ms,底层同时缩到 96.5% 并向左移 2.5%
 *   手势: 进度直接驱动位移;底层反向插值回原位,同时用 smootherStep 驱动暗化
 *   松手: 用释放速度算一条贝塞尔结算曲线 —— 甩得快就直接退场,慢慢拖到一半就回弹
 *
 * 与参考实现的几处有意差异:
 *  1. maxTranslationPercent 复用 NexClip 已有的设置项
 *     (SyncSettings.predictiveBackMaxProgressPercent, 默认 40%, 10~100),
 *     而不是 HyperIsland 的 50% 固定默认值。
 *  2. 背景 backdrop 走显式传参而非 CompositionLocal —— NexClip 全项目只用显式传参
 *     (唯一的 CompositionLocal 是 FloatingBottomBar 的 tab scale)。传 null 时
 *     暗化退化为纯色遮罩,与 miuix 的 textureBlur 在无 RuntimeShader 时的行为一致。
 *     注意:backdrop 的录制链不能包含读取同一 backdrop 的模糊表面,
 *     否则会触发 hwui GraphicsLayer 自引用崩溃(见 MainActivity 已有注释)。
 *  3. 栈宿主泛化为任意深度。HyperIsland 只有固定 2~3 层,
 *     NexClip 设置页最长四级(设置→通知与隐私→短信验证码→短信记录)。
 */

// ---- 时长与几何常量(与 HyperIsland 保持一致) ----
private const val LAYER_ENTER_DURATION = 420
private const val LAYER_EXIT_DURATION = 380
private const val PREDICTIVE_CANCEL_DURATION = 280
private const val PREDICTIVE_DISMISS_DURATION = 24
private const val BACKGROUND_SCALE_REDUCTION = 0.035f
private const val BACKGROUND_PARALLAX = 0.025f
private const val EFFECT_VISIBILITY_THRESHOLD = 0.001f

// ---- 速度跟踪与结算曲线 ----
private const val PREDICTIVE_SETTLE_DURATION = 480
private const val PREDICTIVE_MIN_SETTLE_DURATION = 24
private const val PREDICTIVE_MIN_EFFECT_INTENSITY = 0.5f
private const val PREDICTIVE_MOTION_EPSILON = 0.0005f
private const val PREDICTIVE_MAX_VELOCITY_SAMPLE_NANOS = 120_000_000L
private const val PREDICTIVE_FULL_MOMENTUM_IDLE_NANOS = 40_000_000L
private const val PREDICTIVE_ZERO_MOMENTUM_IDLE_NANOS = 160_000_000L
private const val NANOS_PER_SECOND = 1_000_000_000f
private const val MILLIS_PER_SECOND = 1_000f
private const val PREDICTIVE_VELOCITY_FILTER_SECONDS = 0.05f
private const val PREDICTIVE_MIN_VELOCITY_BLEND = 0.2f
private const val PREDICTIVE_MAX_VELOCITY_BLEND = 0.65f
private const val PREDICTIVE_MAX_TRACKED_VELOCITY = 6f
private const val PREDICTIVE_EASING_INITIAL_X = 0.30f
private const val PREDICTIVE_MAX_INITIAL_EASING_Y = 0.42f

// ---- 底层暗化 / 模糊强度(与 HyperIsland 的 BlurBars 一致) ----
private const val PREDICTIVE_BACK_BLUR_RADIUS = 12f
private const val PREDICTIVE_BACK_DIM_ALPHA = 0.16f

/**
 * 跟手速度跟踪器。
 *
 * 只统计"前进方向"(progress 增大)的速度:返回手势只能往左滑,
 * 反向位移说明用户改主意了,把速度清零即可。
 * 松手瞬间要判断"刚才甩得多快",因此速度在最后一次有效位移后
 * 按 idle 时长衰减(40ms 内全保留 → 160ms 外归零)。
 */
internal class PredictiveBackMotionTracker(
    private val timeNanos: () -> Long = System::nanoTime,
) {
    private var lastProgress = 0f
    private var lastSampleNanos = Long.MIN_VALUE
    private var lastMovementNanos = Long.MIN_VALUE
    private var filteredVelocity = 0f
    private var latchedReleaseVelocity: Float? = null

    fun reset(progress: Float = 0f) {
        lastProgress = progress
        lastSampleNanos = timeNanos()
        lastMovementNanos = lastSampleNanos
        filteredVelocity = 0f
        latchedReleaseVelocity = null
    }

    fun update(progress: Float) {
        val now = timeNanos()
        val delta = progress - lastProgress
        val elapsedNanos = now - lastSampleNanos
        if (elapsedNanos in 1..PREDICTIVE_MAX_VELOCITY_SAMPLE_NANOS &&
            abs(delta) >= PREDICTIVE_MOTION_EPSILON
        ) {
            if (delta > 0f) {
                val elapsedSeconds = elapsedNanos / NANOS_PER_SECOND
                val instantVelocity = (delta / elapsedSeconds)
                    .coerceIn(0f, PREDICTIVE_MAX_TRACKED_VELOCITY)
                val blend = (elapsedSeconds / PREDICTIVE_VELOCITY_FILTER_SECONDS)
                    .coerceIn(PREDICTIVE_MIN_VELOCITY_BLEND, PREDICTIVE_MAX_VELOCITY_BLEND)
                filteredVelocity += (instantVelocity - filteredVelocity) * blend
            } else {
                filteredVelocity = 0f
            }
            lastMovementNanos = now
        }
        lastProgress = progress
        lastSampleNanos = now
        latchedReleaseVelocity = null
    }

    fun releaseVelocity(): Float {
        latchedReleaseVelocity?.let { return it }
        val idleNanos = (timeNanos() - lastMovementNanos).coerceAtLeast(0L)
        val idleRetention = when {
            idleNanos <= PREDICTIVE_FULL_MOMENTUM_IDLE_NANOS -> 1f
            idleNanos >= PREDICTIVE_ZERO_MOMENTUM_IDLE_NANOS -> 0f
            else -> {
                val remaining = (PREDICTIVE_ZERO_MOMENTUM_IDLE_NANOS - idleNanos).toFloat()
                val range = (PREDICTIVE_ZERO_MOMENTUM_IDLE_NANOS -
                    PREDICTIVE_FULL_MOMENTUM_IDLE_NANOS).toFloat()
                smootherStep(remaining / range)
            }
        }
        return (filteredVelocity * idleRetention).also { latchedReleaseVelocity = it }
    }
}

/** 五次平滑阶跃:两端一阶、二阶导数都为 0,把线性手势进度修成无顿挫的强度曲线。 */
internal fun smootherStep(progress: Float): Float {
    val value = progress.coerceIn(0f, 1f)
    return value * value * value * (value * (value * 6f - 15f) + 10f)
}

/** 手势进行中底层效果的残余强度:起手时最强,推到一半后收敛到 0.5。 */
private fun predictiveEffectIntensity(smoothProgress: Float): Float =
    1f - smoothProgress * (1f - PREDICTIVE_MIN_EFFECT_INTENSITY)

/** 位移比例(percent 10~100 → 0.1~1.0)。 */
internal fun predictiveTranslationFraction(percent: Long): Float =
    percent.coerceIn(1L, 100L).toFloat() / 100f

/** 要把当前这一层完整推出屏幕,progress 需要到达的目标值。 */
internal fun predictiveExitProgress(maxTranslationPercent: Long): Float =
    1f / predictiveTranslationFraction(maxTranslationPercent)

/** 结算时长随"已推出的距离"递减:已经拖出去大半,收尾就该更快。 */
private fun predictiveSettleDuration(progress: Float, maxTranslationPercent: Long): Int {
    val translationFraction = predictiveTranslationFraction(maxTranslationPercent)
    val currentTranslation = (progress.coerceAtLeast(0f) * translationFraction).coerceIn(0f, 1f)
    return (PREDICTIVE_SETTLE_DURATION * (1f - currentTranslation))
        .roundToInt()
        .coerceIn(PREDICTIVE_MIN_SETTLE_DURATION, PREDICTIVE_SETTLE_DURATION)
}

/**
 * 按松手速度生成结算缓动曲线。
 *
 * 把"剩余进度 + 释放速度"折算成贝塞尔起点处的斜率,再夹到上限 ——
 * 甩得快则曲线起手极陡(几乎瞬移出去),慢慢松手则接近普通缓出。
 */
private fun predictiveSettleEasing(
    releaseVelocity: Float,
    currentProgress: Float,
    targetProgress: Float,
    durationMillis: Int,
): Easing {
    val remainingProgress = (targetProgress - currentProgress).coerceAtLeast(PREDICTIVE_MOTION_EPSILON)
    val normalizedInitialSlope = releaseVelocity * (durationMillis / MILLIS_PER_SECOND) /
        remainingProgress
    val initialY = (PREDICTIVE_EASING_INITIAL_X * normalizedInitialSlope)
        .coerceIn(0f, PREDICTIVE_MAX_INITIAL_EASING_Y)
    return CubicBezierEasing(PREDICTIVE_EASING_INITIAL_X, initialY, 0.35f, 1f)
}

/**
 * 一层页面的导航状态。
 *
 * 三条曲线各管一件事,必须分开插值才能得到"层在退出、底层在回位、
 * 暗化在消退"三者不同速率的效果:
 *  - progress:          当前层自身的位置(0 = 完全就位, 越大 = 被推得越远)
 *  - backdropIntensity: 盖在这一层上的暗化强度
 *  - backgroundDepth:   这一层"作为父层沉下去"的程度(1 = 沉到底, 0 = 完全回位)
 */
@Stable
class PredictiveNavigationLayerState internal constructor() {
    var isBackActive by mutableStateOf(false)
        private set

    var isCommitting by mutableStateOf(false)
        private set

    val progress = Animatable(0f)
    val backdropIntensity = Animatable(0f)
    val backgroundDepth = Animatable(0f)

    private val motion = PredictiveBackMotionTracker()

    internal suspend fun animateVisibility(visible: Boolean) {
        if (isBackActive) return
        val target = if (visible) 1f else 0f
        val duration = if (visible) LAYER_ENTER_DURATION else LAYER_EXIT_DURATION
        coroutineScope {
            launch {
                backdropIntensity.animateTo(
                    target,
                    tween(duration, easing = FastOutSlowInEasing),
                )
            }
            launch {
                backgroundDepth.animateTo(
                    target,
                    tween(duration, easing = FastOutSlowInEasing),
                )
            }
        }
    }

    internal suspend fun trackBack(progress: Float) {
        if (!isBackActive) {
            motion.reset(progress)
        } else {
            motion.update(progress)
        }
        isBackActive = true
        this.progress.snapTo(progress)
        val smoothProgress = smootherStep(progress)
        backdropIntensity.snapTo(predictiveEffectIntensity(smoothProgress))
        backgroundDepth.snapTo(1f - smoothProgress)
    }

    internal suspend fun cancelBack() {
        if (isCommitting) return
        coroutineScope {
            launch {
                progress.animateTo(
                    0f,
                    tween(PREDICTIVE_CANCEL_DURATION, easing = FastOutSlowInEasing),
                )
            }
            launch {
                backdropIntensity.animateTo(
                    1f,
                    tween(PREDICTIVE_CANCEL_DURATION, easing = FastOutSlowInEasing),
                )
            }
            launch {
                backgroundDepth.animateTo(
                    1f,
                    tween(PREDICTIVE_CANCEL_DURATION, easing = FastOutSlowInEasing),
                )
            }
        }
        motion.reset()
        isBackActive = false
    }

    internal suspend fun commitBack(
        maxTranslationPercent: Long,
        additionalAnimation: suspend (durationMillis: Int, easing: Easing) -> Unit,
        onDismiss: () -> Unit,
    ) {
        if (isCommitting) return
        isCommitting = true
        try {
            val targetProgress = predictiveExitProgress(maxTranslationPercent)
            val duration = predictiveSettleDuration(
                progress = progress.value,
                maxTranslationPercent = maxTranslationPercent,
            )
            val settleEasing = predictiveSettleEasing(
                releaseVelocity = motion.releaseVelocity(),
                currentProgress = progress.value,
                targetProgress = targetProgress,
                durationMillis = duration,
            )
            coroutineScope {
                launch {
                    progress.animateTo(
                        targetProgress,
                        tween(duration, easing = settleEasing),
                    )
                }
                launch {
                    backdropIntensity.animateTo(0f, tween(duration, easing = settleEasing))
                }
                launch {
                    backgroundDepth.animateTo(0f, tween(duration, easing = settleEasing))
                }
                launch { additionalAnimation(duration, settleEasing) }
            }
            onDismiss()
            delay(PREDICTIVE_DISMISS_DURATION.toLong())
        } finally {
            withContext(NonCancellable) {
                progress.snapTo(0f)
                motion.reset()
                isBackActive = false
                isCommitting = false
            }
        }
    }
}

@Composable
fun rememberPredictiveNavigationLayerState(): PredictiveNavigationLayerState =
    remember { PredictiveNavigationLayerState() }

/**
 * 把系统返回手势接到层状态上。
 *
 * 提交路径交给 [PredictiveNavigationLayerState.commitBack] 做速度结算;
 * 中途取消则回弹。提交动画期间吞掉后续返回事件,避免重复出栈。
 *
 * 可见性动画不在这里做 —— 由 [PredictiveNavigationLayer] 自己按 visible 驱动,
 * 这样"关掉跟手手势"时(只挂普通 BackHandler)进出场动画依然成立。
 */
@Composable
fun PredictiveNavigationBackHandler(
    enabled: Boolean,
    state: PredictiveNavigationLayerState,
    maxTranslationPercent: Long,
    onDismiss: () -> Unit,
    additionalCommitAnimation: suspend (durationMillis: Int, easing: Easing) -> Unit = { _, _ -> },
) {
    PredictiveBackHandler(enabled = enabled) { events ->
        var cancelled = false
        try {
            events.collect { event -> state.trackBack(event.progress) }
        } catch (_: CancellationException) {
            state.cancelBack()
            cancelled = true
        }
        if (!cancelled) {
            state.commitBack(maxTranslationPercent, additionalCommitAnimation, onDismiss)
        }
    }

    BackHandler(enabled = state.isBackActive && state.isCommitting) {
        // 提交中的转场结束前,继续吞掉返回事件。
    }
}

/**
 * 一层页面的容器:负责整屏进出场与(手势驱动时的)位移变换。
 *
 * 返回手势进行中时把 exit 设为 None —— 此时位移完全由 [state] 的
 * progress 逐帧驱动,若再叠一个 slideOut 会双倍计算距离。
 */
@Composable
fun PredictiveNavigationLayer(
    visible: Boolean,
    state: PredictiveNavigationLayerState,
    maxTranslationPercent: Long,
    modifier: Modifier = Modifier,
    backgroundState: PredictiveNavigationLayerState? = null,
    content: @Composable () -> Unit,
) {
    LaunchedEffect(visible, state.isBackActive) {
        state.animateVisibility(visible)
    }

    AnimatedVisibility(
        visible = visible,
        modifier = modifier
            .fillMaxSize()
            .predictiveNavigationTransform(
                foregroundState = state,
                backgroundState = backgroundState,
                maxTranslationPercent = maxTranslationPercent,
            ),
        enter = slideInHorizontally(
            tween(LAYER_ENTER_DURATION, easing = FastOutSlowInEasing),
        ) { it },
        exit = if (state.isBackActive) {
            ExitTransition.None
        } else {
            slideOutHorizontally(
                tween(LAYER_EXIT_DURATION, easing = FastOutSlowInEasing),
            ) { it }
        },
    ) {
        content()
    }
}

/**
 * 盖在被覆盖页面上的暗化(可选叠加模糊)。
 *
 * [backdrop] 为空(或设备不支持运行时着色器)时退化为纯色暗化 ——
 * 与 miuix 的 textureBlur 在无 shader 时的降级行为一致。
 */
@Composable
fun PredictiveBackBackdrop(
    state: PredictiveNavigationLayerState,
    modifier: Modifier = Modifier,
    backdrop: LayerBackdrop? = null,
) {
    val intensity = state.backdropIntensity.value.coerceIn(0f, 1f)
    if (intensity <= EFFECT_VISIBILITY_THRESHOLD) return
    val dimColor = MiuixTheme.colorScheme.windowDimming.copy(
        alpha = PREDICTIVE_BACK_DIM_ALPHA * intensity,
    )
    val effectModifier = if (backdrop != null) {
        modifier.textureBlur(
            backdrop = backdrop,
            shape = RectangleShape,
            blurRadius = PREDICTIVE_BACK_BLUR_RADIUS * intensity,
            colors = BlurDefaults.blurColors(
                blendColors = listOf(BlendColorEntry(color = dimColor)),
            ),
        )
    } else {
        modifier.background(dimColor)
    }
    Box(modifier = effectModifier)
}

/** 这一层是否还需要继续录制画面(供 backdrop 的启用条件判断)。 */
fun PredictiveNavigationLayerState.requiresBackdropCapture(visible: Boolean): Boolean =
    visible || isBackActive || backdropIntensity.value > EFFECT_VISIBILITY_THRESHOLD

/**
 * 被覆盖页面的"下沉"变换:有子层推进时整体缩到 96.5% 并向左微移,
 * 让层级关系在视觉上成立。
 */
fun Modifier.predictiveNavigationBackground(
    state: PredictiveNavigationLayerState,
): Modifier = predictiveNavigationBackground { state.backgroundDepth.value }

/**
 * 同上,但下沉程度由调用方给出。
 *
 * 用于多个平级覆盖层(如主页上的扫码页与即时互传页)共享同一个底层:
 * 底层要跟随"当前活着的那一层"下沉,而不是某一份固定 state。
 */
fun Modifier.predictiveNavigationBackground(
    depth: () -> Float,
): Modifier = graphicsLayer {
    val d = depth().coerceIn(0f, 1f)
    scaleX = 1f - d * BACKGROUND_SCALE_REDUCTION
    scaleY = scaleX
    translationX = -size.width * d * BACKGROUND_PARALLAX
}

/**
 * 前景层的变换:自身位移(手势进度),由调用方给出位移比例。
 *
 * 用于不走 [PredictiveNavigationLayer] 的场合(如全屏搜索页),
 * 让它的位移曲线与其他层级页面保持一致。
 */
fun Modifier.predictiveNavigationTransform(
    state: PredictiveNavigationLayerState,
    maxTranslationPercent: Long,
): Modifier = graphicsLayer {
    val progress = state.progress.value.coerceAtLeast(0f)
    translationX = size.width * progress * predictiveTranslationFraction(maxTranslationPercent)
}

private fun Modifier.predictiveNavigationTransform(
    foregroundState: PredictiveNavigationLayerState,
    backgroundState: PredictiveNavigationLayerState?,
    maxTranslationPercent: Long,
): Modifier = graphicsLayer {
    val progress = foregroundState.progress.value.coerceAtLeast(0f)
    val depth = backgroundState?.backgroundDepth?.value?.coerceIn(0f, 1f) ?: 0f
    translationX = -size.width * depth * BACKGROUND_PARALLAX +
        size.width * progress * predictiveTranslationFraction(maxTranslationPercent)
    scaleX = 1f - depth * BACKGROUND_SCALE_REDUCTION
    scaleY = scaleX
}

/**
 * 任意深度页面栈的渲染宿主。
 *
 * 层级顺序严格照 HyperIsland(AppShell 的三层嵌套):
 *
 *   base                    ← 用 state(0) 做下沉变换
 *   backdrop(state(0))      ← 盖在 base 上
 *   layer(0)                ← 用 state(0) 定位,用 state(1) 做下沉
 *   backdrop(state(1))
 *   layer(1) ...
 *
 * 按"深度槽位"渲染:槽位长期保留,压栈时内容不变(进出场交给 AnimatedVisibility),
 * 出栈时其 visible 立即变 false、退场页仍保持组合直到滑出屏幕 —— 这是 HyperIsland
 * 用 renderedExtensionSubDetail 保留退场页的等价做法,但能覆盖任意深度。
 *
 * 手势返回路径下不需要这个快照:commitBack 会先把层推出屏幕,再调 onDismiss 出栈,
 * 出栈发生在画面之外。快照只服务于"点顶栏返回按钮"这类先出栈后动画的路径。
 */
@Composable
fun <T> PredictiveNavigationStack(
    stack: List<T>,
    maxTranslationPercent: Long,
    predictiveBackEnabled: Boolean,
    onDismissTop: () -> Unit,
    modifier: Modifier = Modifier,
    /**
     * 是否由栈接管返回事件。弹窗/下拉菜单打开时必须传 false ——
     * 本栈在组合树里位于那些弹层之后,会抢走本应关闭弹窗的返回事件。
     */
    backEnabled: Boolean = true,
    backdropFor: (depth: Int) -> LayerBackdrop? = { null },
    base: @Composable () -> Unit,
    content: @Composable (page: T, depth: Int, isTop: Boolean) -> Unit,
) {
    // 各深度的最后一帧内容:出栈后仍要能渲染正在滑出的那一页。
    // 用普通容器而非 state —— 这里只做"记住",不需要触发重组(重组由 stack 变化驱动)。
    val retained = remember { mutableMapOf<Int, T>() }
    val peakRef = remember { intArrayOf(0) }
    if (stack.size > peakRef[0]) peakRef[0] = stack.size
    val peak = peakRef[0]
    for (i in stack.indices) retained[i] = stack[i]

    // 每个深度一份独立层状态(各自的进度/暗化/下沉曲线)。
    val states = remember { mutableMapOf<Int, PredictiveNavigationLayerState>() }
    fun stateAt(depth: Int): PredictiveNavigationLayerState =
        states.getOrPut(depth) { PredictiveNavigationLayerState() }

    val topDepth = stack.size - 1
    if (topDepth >= 0 && backEnabled) {
        val topState = stateAt(topDepth)
        if (predictiveBackEnabled) {
            PredictiveNavigationBackHandler(
                enabled = true,
                state = topState,
                maxTranslationPercent = maxTranslationPercent,
                onDismiss = onDismissTop,
            )
        } else {
            // 关掉跟手手势后仍要能返回:普通返回键,进出场动画由各层自己负责。
            BackHandler(enabled = true) { onDismissTop() }
        }
    }

    Box(modifier = modifier.fillMaxSize()) {
        // base 是栈底下那一层(如设置主页/记录列表),始终渲染;
        // 有子层压上来时它按 state(0) 下沉。
        Box(
            modifier = Modifier
                .fillMaxSize()
                .predictiveNavigationBackground(stateAt(0)),
        ) {
            base()
        }
        for (depth in 0 until peak) {
            val state = stateAt(depth)
            val page = retained[depth]
            PredictiveBackBackdrop(
                state = state,
                modifier = Modifier.fillMaxSize(),
                backdrop = backdropFor(depth),
            )
            PredictiveNavigationLayer(
                visible = depth < stack.size,
                state = state,
                maxTranslationPercent = maxTranslationPercent,
                backgroundState = if (depth + 1 < peak) stateAt(depth + 1) else null,
            ) {
                if (page != null) content(page, depth, depth == topDepth)
            }
        }
    }
}
