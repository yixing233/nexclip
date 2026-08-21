package clip.yixing.sync.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.add
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.systemBars
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.unit.Dp
import kotlinx.coroutines.delay
import top.yukonga.miuix.kmp.basic.MiuixScrollBehavior
import top.yukonga.miuix.kmp.basic.Scaffold
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.blur.BlendColorEntry
import top.yukonga.miuix.kmp.blur.BlurBlendMode
import top.yukonga.miuix.kmp.blur.BlurDefaults
import top.yukonga.miuix.kmp.blur.LayerBackdrop
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.blur.textureBlur
import androidx.activity.BackEventCompat
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.unit.dp
import top.yukonga.miuix.kmp.theme.MiuixTheme

/**
 * 顶栏 / 底栏通用的液态玻璃毛玻璃表面容器。
 */
@Composable
fun BarBlurSurface(
    backdrop: LayerBackdrop,
    refreshKey: Any? = Unit,
    content: @Composable () -> Unit
) {
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(refreshKey) {
        repeat(6) {
            delay(80)
            tick++
        }
    }
    val barSurface = MiuixTheme.colorScheme.surface
    Box(
        modifier = Modifier
            .graphicsLayer {
                val t = tick
                alpha = if (t % 2 == 0) 1f else 1f
            }
            .textureBlur(
                backdrop = backdrop,
                shape = RectangleShape,
                blurRadius = BlurDefaults.BlurRadius,
                colors = BlurDefaults.blurColors(
                    blendColors = listOf(
                        BlendColorEntry(
                            color = barSurface.copy(alpha = 0.55f),
                            mode = BlurBlendMode.SrcOver
                        )
                    )
                )
            )
    ) {
        content()
    }
}

/**
 * KernelSU 风格独立页面容器：
 * 每个页面/二级子页面持有独立的 Scaffold、大标题 TopAppBar 与独立的滚动状态。
 */
@Composable
fun PageShell(
    title: String,
    modifier: Modifier = Modifier,
    bottomInnerPadding: Dp = Dp.Unspecified,
    navigationIcon: @Composable () -> Unit = {},
    actions: @Composable RowScope.() -> Unit = {},
    content: @Composable (ScrollBehavior, Dp) -> Unit
) {
    val scrollBehavior = MiuixScrollBehavior()
    val barSurface = MiuixTheme.colorScheme.surface
    val pageBackdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )
    Scaffold(
        modifier = modifier,
        topBar = {
            BarBlurSurface(backdrop = pageBackdrop) {
                TopAppBar(
                    title = title,
                    largeTitle = title,
                    color = Color.Transparent,
                    scrollBehavior = scrollBehavior,
                    navigationIcon = navigationIcon,
                    actions = actions
                )
            }
        },
        contentWindowInsets = WindowInsets.systemBars
            .add(WindowInsets.displayCutout)
            .only(WindowInsetsSides.Horizontal)
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(pageBackdrop)
        ) {
            content(scrollBehavior, padding.calculateTopPadding())
        }
    }
}

/**
 * 预测返回手势动效修饰符。
 * 根据返回进度 (0f ~ 1f) 与边缘方向产生平滑的位移、缩放与圆角变形。
 * 支持左边缘 (EDGE_LEFT) 和右边缘 (EDGE_RIGHT) 侧滑手势。
 */
fun Modifier.predictiveBackAnimation(
    progress: Float,
    edge: Int = BackEventCompat.EDGE_LEFT,
    enabled: Boolean = true
): Modifier = if (enabled && progress > 0f) {
    this.graphicsLayer {
        val p = FastOutSlowInEasing.transform(progress)
        val sign = if (edge == BackEventCompat.EDGE_LEFT) 1f else -1f
        // 横向视差平移 (向滑动方向微移)
        translationX = sign * p * size.width * 0.28f
        // 细腻缩放 (最大缩小至 92%)
        val scale = 1f - p * 0.08f
        scaleX = scale
        scaleY = scale
        // 变换原点贴合触摸滑动侧
        transformOrigin = TransformOrigin(if (edge == BackEventCompat.EDGE_LEFT) 0f else 1f, 0.5f)
        // 圆角卡片化裁切
        clip = true
        shape = RoundedCornerShape((p * 24).dp)
        // 浅层淡出
        alpha = 1f - p * 0.12f
    }
} else {
    this
}
