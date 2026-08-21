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
