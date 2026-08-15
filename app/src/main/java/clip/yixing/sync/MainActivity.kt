package clip.yixing.sync

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.core.EaseInOut
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerState
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SearchPage
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.theme.SyncClipboardTheme
import clip.yixing.sync.util.SyncSettings
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.MiuixScrollBehavior
import top.yukonga.miuix.kmp.basic.NavigationBar
import top.yukonga.miuix.kmp.basic.NavigationBarDisplayMode
import top.yukonga.miuix.kmp.basic.NavigationBarItem
import top.yukonga.miuix.kmp.basic.Scaffold
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHost
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TopAppBar
import top.yukonga.miuix.kmp.blur.BlendColorEntry
import top.yukonga.miuix.kmp.blur.BlurDefaults
import top.yukonga.miuix.kmp.blur.LayerBackdrop
import top.yukonga.miuix.kmp.blur.isRuntimeShaderSupported
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.blur.textureBlur
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.basic.ArrowUpDown
import top.yukonga.miuix.kmp.icon.extended.Home
import top.yukonga.miuix.kmp.icon.extended.Search
import top.yukonga.miuix.kmp.icon.extended.Notes
import top.yukonga.miuix.kmp.icon.extended.Settings
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollHorizontal
import top.yukonga.miuix.kmp.utils.overScrollVertical
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // 恢复本地剪贴板记录;按设置自动恢复监听
        ClipboardMonitorService.loadCaptured(applicationContext)
        if (SyncSettings.bootStartEnabled(this)) {
            ClipboardMonitorService.start(this)
        }
        setContent {
            SyncClipboardTheme {
                MainScreen()
            }
        }
    }
}

val cardContentPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp)

/**
 * 底栏页面切换动画,与 KernelSU 一致:
 * 按目标页距离计算时长,用 EaseInOut 缓动滑动整个页面(底栏除外)。
 */
private class PagerNavigator(private val scope: CoroutineScope) {

    private var navJob: Job? = null

    fun animateTo(pagerState: PagerState, targetIndex: Int) {
        if (targetIndex == pagerState.currentPage) return

        navJob?.cancel()

        val distance = abs(targetIndex - pagerState.currentPage).coerceAtLeast(2)
        val duration = 100 * distance + 100
        val layoutInfo = pagerState.layoutInfo
        val pageSize = layoutInfo.pageSize + layoutInfo.pageSpacing
        val currentDistanceInPages = targetIndex - pagerState.currentPage - pagerState.currentPageOffsetFraction
        val scrollPixels = currentDistanceInPages * pageSize

        navJob = scope.launch {
            pagerState.animateScrollToPage(
                page = targetIndex,
                animationSpec = tween(easing = EaseInOut, durationMillis = duration)
            )
        }
    }
}

@Composable
private fun MainScreen() {
    val pagerState = rememberPagerState(pageCount = { 3 })
    val scope = rememberCoroutineScope()
    val pagerNavigator = remember { PagerNavigator(scope) }
    var sortDesc by remember { mutableStateOf(false) }
    var searchOpen by remember { mutableStateOf(false) }
    var searchQuery by remember { mutableStateOf("") }
    val snackbarHostState = remember { SnackbarHostState() }
    // 顶/底栏毛玻璃:页面内容层被 layerBackdrop 捕获,模糊条叠加其上
    // 注意:miuix 0.9.3 的 RuntimeShader 模糊在小米 15 Pro + MIUI OS3.0(HWUI)上
    // 触发原生 SIGSEGV(isRuntimeShaderSupported 返回 true 仍崩溃),故本版本禁用,
    // 用半透明 surface 视觉替代。待 miuix 修复或换 RenderEffect 方案后恢复。
    val shaderSupported = false
    val backdrop: LayerBackdrop? = null
    val titles = listOf("剪贴板同步", "捕获记录", "设置")
    // 每页独立滚动状态(页面间互不共享)
    val sb0 = MiuixScrollBehavior()
    val sb1 = MiuixScrollBehavior()
    val sb2 = MiuixScrollBehavior()
    val scrollBehaviors = listOf(sb0, sb1, sb2)

    Box(Modifier.fillMaxSize()) {
        // 内容层(被捕获为模糊背景,滚动到顶/底栏底下;不支持 shader 时纯色)
        Box(
            modifier = Modifier
                .matchParentSize()
                .then(
                    if (backdrop != null) Modifier.layerBackdrop(backdrop)
                    else Modifier.background(MiuixTheme.colorScheme.surface)
                )
        ) {
            Scaffold(
                bottomBar = {
                    BlurredBar(backdrop, shaderSupported) {
                        NavigationBar(mode = NavigationBarDisplayMode.IconAndText) {
                            NavigationBarItem(
                                selected = pagerState.currentPage == 0,
                                onClick = { pagerNavigator.animateTo(pagerState, 0) },
                                icon = MiuixIcons.Normal.Home,
                                label = "首页"
                            )
                            NavigationBarItem(
                                selected = pagerState.currentPage == 1,
                                onClick = { pagerNavigator.animateTo(pagerState, 1) },
                                icon = MiuixIcons.Normal.Notes,
                                label = "记录"
                            )
                            NavigationBarItem(
                                selected = pagerState.currentPage == 2,
                                onClick = { pagerNavigator.animateTo(pagerState, 2) },
                                icon = MiuixIcons.Normal.Settings,
                                label = "设置"
                            )
                        }
                    }
                }
            ) { padding ->
                val topPadding = padding.calculateTopPadding()
                val bottomInnerPadding = padding.calculateBottomPadding()
                HorizontalPager(
                    state = pagerState,
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollHorizontal(),
                    beyondViewportPageCount = 3
                ) { page ->
                    when (page) {
                        0 -> HomePage(scrollBehaviors[0], topPadding, bottomInnerPadding)
                        1 -> RecordsPage(
                            scrollBehaviors[1], topPadding, bottomInnerPadding,
                            sortDesc = sortDesc,
                            snackbarHostState = snackbarHostState,
                        )
                        else -> SettingsPage(scrollBehaviors[2], topPadding, bottomInnerPadding)
                    }
                }
            }
        }

        // 顶栏(毛玻璃覆盖,滚动时内容从底下穿过)
        BlurredBar(backdrop, shaderSupported) {
            TopAppBar(
                title = titles[pagerState.currentPage],
                largeTitle = titles[pagerState.currentPage],
                scrollBehavior = scrollBehaviors[pagerState.currentPage],
                color = Color.Transparent,
                actions = {
                    if (pagerState.currentPage == 1) {
                        // 排序:倒序(最新在前,默认)/ 正序(最早在前)
                        IconButton(
                            onClick = { sortDesc = !sortDesc },
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Basic.ArrowUpDown,
                                contentDescription = if (sortDesc) "切换为顺序排列" else "切换为倒序排列"
                            )
                        }
                        // 搜索:打开全屏搜索页
                        IconButton(
                            onClick = {
                                searchQuery = ""
                                searchOpen = true
                            },
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Search,
                                contentDescription = "搜索"
                            )
                        }
                    }
                }
            )
        }

        // Snackbar(删除/清空撤销提示)
        SnackbarHost(
            state = snackbarHostState,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 88.dp, start = 16.dp, end = 16.dp)
        )
    }

    // 全屏搜索页(覆盖顶栏/底栏)
    if (searchOpen) {
        SearchPage(
            sortDesc = sortDesc,
            query = searchQuery,
            onQueryChange = { searchQuery = it },
            onClose = { searchOpen = false },
            snackbarHostState = snackbarHostState,
        )
    }
}

/** 毛玻璃条:内容由 backdrop 捕获,当前条以 surface 混合色模糊叠加;不支持 shader 时降级纯色 */
@Composable
private fun BlurredBar(
    backdrop: LayerBackdrop?,
    blurEnabled: Boolean,
    content: @Composable () -> Unit,
) {
    Box(
        modifier = if (blurEnabled && backdrop != null) {
            Modifier.textureBlur(
                backdrop = backdrop,
                shape = RectangleShape,
                blurRadius = 25f,
                colors = BlurDefaults.blurColors(
                    blendColors = listOf(
                        BlendColorEntry(color = MiuixTheme.colorScheme.surface.copy(0.8f)),
                    ),
                ),
            )
        } else {
            Modifier.background(MiuixTheme.colorScheme.surface.copy(alpha = 0.85f))
        }
    ) {
        content()
    }
}

/**
 * KernelSU 风格整页结构:每页独立大标题 TopAppBar 与滚动状态,页面间互不共享。
 */
@Composable
private fun PageShell(
    title: String,
    bottomInnerPadding: Dp,
    content: @Composable (ScrollBehavior, Dp) -> Unit
) {
    val scrollBehavior = MiuixScrollBehavior()
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = title,
            largeTitle = title,
            scrollBehavior = scrollBehavior
        )
        Box(Modifier.weight(1f).fillMaxWidth()) {
            content(scrollBehavior, 0.dp)
        }
    }
}

/** 状态行:左标签右值(模块状态卡片用) */
@Composable
fun StatusRow(
    label: String,
    value: String,
    valueColor: Color = MiuixTheme.colorScheme.onBackgroundVariant,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(label, color = MiuixTheme.colorScheme.onBackgroundVariant)
        Text(value, color = valueColor)
    }
}

/** 记录时间格式化("HH:mm") */
fun formatTime(millis: Long): String {
    val fmt = SimpleDateFormat("HH:mm", Locale.getDefault())
    return fmt.format(Date(millis))
}
