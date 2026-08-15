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
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.add
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerState
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.navigationevent.compose.LocalNavigationEventDispatcherOwner
import androidx.navigationevent.compose.rememberNavigationEventDispatcherOwner
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.HomePage
import clip.yixing.sync.ui.RecordsPage
import clip.yixing.sync.ui.SearchPage
import clip.yixing.sync.ui.SettingsPage
import clip.yixing.sync.ui.theme.SyncClipboardTheme
import clip.yixing.sync.util.SyncSettings
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.DropdownEntry
import top.yukonga.miuix.kmp.basic.DropdownItem
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
import top.yukonga.miuix.kmp.blur.BlurBlendMode
import top.yukonga.miuix.kmp.blur.BlurDefaults
import top.yukonga.miuix.kmp.blur.LayerBackdrop
import top.yukonga.miuix.kmp.blur.layerBackdrop
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.blur.textureBlur
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.menu.OverlayIconDropdownMenu
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
            // 气泡菜单/弹窗等组件依赖 NavigationEventDispatcher 处理返回手势,
            // 需在根节点提供 DispatcherOwner,否则点击下拉菜单会闪退。
            val dispatcherOwner = rememberNavigationEventDispatcherOwner(
                enabled = true,
                parent = null,
            )
            CompositionLocalProvider(
                LocalNavigationEventDispatcherOwner provides dispatcherOwner
            ) {
                SyncClipboardTheme {
                    MainScreen()
                }
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

    // 单个 backdrop:录制外层 content(Pager 整体,含各页大标题顶栏),底栏 blur
    // 表面读取它 —— 录制范围不含任何 blur 表面(顶栏在页面内无模糊,与 KernelSU
    // 一致),避免 GraphicsLayer 自引用导致的 hwui native 崩溃;且录制节点与底栏
    // 同属外层 Scaffold 坐标系,不存在 Pager 页面 graphicsLayer 平移导致的错位。
    val barSurface = MiuixTheme.colorScheme.surface
    val backdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )
    val currentPage = pagerState.currentPage

    Box(Modifier.fillMaxSize()) {
        Scaffold(
        bottomBar = {
            BarBlurSurface(backdrop = backdrop, refreshKey = currentPage) {
                NavigationBar(
                    color = Color.Transparent,
                    mode = NavigationBarDisplayMode.IconAndText
                ) {
                    NavigationBarItem(
                        selected = currentPage == 0,
                        onClick = { pagerNavigator.animateTo(pagerState, 0) },
                        icon = MiuixIcons.Normal.Home,
                        label = "首页"
                    )
                    NavigationBarItem(
                        selected = currentPage == 1,
                        onClick = { pagerNavigator.animateTo(pagerState, 1) },
                        icon = MiuixIcons.Normal.Notes,
                        label = "记录"
                    )
                    NavigationBarItem(
                        selected = currentPage == 2,
                        onClick = { pagerNavigator.animateTo(pagerState, 2) },
                        icon = MiuixIcons.Normal.Settings,
                        label = "设置"
                    )
                }
            }
        }
    ) { padding ->
        val bottomInnerPadding = padding.calculateBottomPadding()
        // 整页(含大标题顶栏)在 Pager 内滑动切换,与 KernelSU 一致
        Box(
            modifier = Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
        ) {
            HorizontalPager(
                state = pagerState,
                modifier = Modifier
                    .fillMaxSize()
                    .overScrollHorizontal(),
                beyondViewportPageCount = 3
            ) { page ->
                when (page) {
                    0 -> PageShell(title = "剪贴板同步", bottomInnerPadding = bottomInnerPadding) { scrollBehavior, topPadding ->
                        HomePage(scrollBehavior, topPadding, bottomInnerPadding)
                    }
                    1 -> PageShell(
                        title = "捕获记录",
                        bottomInnerPadding = bottomInnerPadding,
                        actions = {
                            // 排序:气泡卡片菜单选择 时间 倒序(最新在前)/ 时间 正序(最早在前)
                            OverlayIconDropdownMenu(
                                entry = DropdownEntry(
                                    items = listOf(
                                        DropdownItem(
                                            text = "时间 倒序",
                                            summary = "最新在前",
                                            selected = sortDesc,
                                            onClick = { sortDesc = true },
                                        ),
                                        DropdownItem(
                                            text = "时间 正序",
                                            summary = "最早在前",
                                            selected = !sortDesc,
                                            onClick = { sortDesc = false },
                                        ),
                                    ),
                                ),
                            ) {
                                Icon(
                                    imageVector = MiuixIcons.Basic.ArrowUpDown,
                                    contentDescription = "排序",
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
                    ) { scrollBehavior, topPadding ->
                        RecordsPage(
                            scrollBehavior, topPadding, bottomInnerPadding,
                            sortDesc = sortDesc,
                            snackbarHostState = snackbarHostState,
                        )
                    }
                    else -> PageShell(title = "设置", bottomInnerPadding = bottomInnerPadding) { scrollBehavior, topPadding ->
                        SettingsPage(scrollBehavior, topPadding, bottomInnerPadding)
                    }
                }
            }
        }
    }

    // Snackbar(删除/清空撤销提示)
    SnackbarHost(
        state = snackbarHostState,
        modifier = Modifier
            .align(Alignment.BottomCenter)
            .padding(bottom = 88.dp, start = 16.dp, end = 16.dp)
    )

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
}

/**
 * 毛玻璃表面(顶栏/底栏通用):内容由 [backdrop] 捕获后由 textureBlur 模糊。
 *
 * backdrop 的层录制/坐标定位与 blur 表面的绘制存在时序差:层就绪后
 * blur 表面不会自动重绘,静止帧会残留透明。因此组合后(以及 [refreshKey]
 * 变化后)短时 tick 强制重绘,保证显示的是最新捕获内容。
 */
@Composable
private fun BarBlurSurface(
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
            // 读取 tick:每次变化都会让 graphicsLayer 节点更新并触发重绘
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
 * KernelSU 风格整页结构:每个页面持有自己的 Scaffold、大标题 TopAppBar 与
 * 独立的滚动状态,整页(含顶栏)随 HorizontalPager 一起滑动切换,底栏固定在外层。
 */
@Composable
internal fun PageShell(
    title: String,
    bottomInnerPadding: Dp,
    actions: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit = {},
    content: @Composable (ScrollBehavior, Dp) -> Unit
) {
    val scrollBehavior = MiuixScrollBehavior()
    Scaffold(
        topBar = {
            TopAppBar(
                title = title,
                largeTitle = title,
                scrollBehavior = scrollBehavior,
                actions = actions
            )
        },
        contentWindowInsets = WindowInsets.systemBars
            .add(WindowInsets.displayCutout)
            .only(WindowInsetsSides.Horizontal)
    ) { padding ->
        content(scrollBehavior, padding.calculateTopPadding())
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
