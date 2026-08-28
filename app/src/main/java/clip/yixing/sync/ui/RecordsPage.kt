package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.activity.BackEventCompat
import androidx.activity.compose.BackHandler
import androidx.activity.compose.PredictiveBackHandler
import androidx.compose.animation.AnimatedVisibility
import kotlinx.coroutines.CancellationException
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.rememberTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.isImeVisible
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.formatTime
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.smartaction.SmartActionEngine
import clip.yixing.sync.smartaction.SmartActionChip
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.AppSourceHelper
import clip.yixing.sync.util.ImageLoader
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import androidx.compose.ui.state.ToggleableState
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Checkbox
import top.yukonga.miuix.kmp.basic.DropdownEntry
import top.yukonga.miuix.kmp.basic.DropdownItem
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.NumberPicker
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.SnackbarResult
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.basic.Close
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.Clear
import top.yukonga.miuix.kmp.icon.extended.Copy
import top.yukonga.miuix.kmp.icon.extended.Delete
import top.yukonga.miuix.kmp.icon.extended.Edit
import top.yukonga.miuix.kmp.icon.extended.Favorites
import top.yukonga.miuix.kmp.icon.extended.FavoritesFill
import top.yukonga.miuix.kmp.icon.extended.Link
import top.yukonga.miuix.kmp.icon.extended.Months
import top.yukonga.miuix.kmp.icon.extended.More
import top.yukonga.miuix.kmp.icon.extended.Phone
import top.yukonga.miuix.kmp.icon.extended.Search
import top.yukonga.miuix.kmp.icon.extended.SelectAll
import top.yukonga.miuix.kmp.icon.extended.Share
import top.yukonga.miuix.kmp.icon.extended.UploadCloud
import top.yukonga.miuix.kmp.blur.rememberLayerBackdrop
import top.yukonga.miuix.kmp.menu.WindowIconCascadingDropdownMenu
import top.yukonga.miuix.kmp.menu.WindowIconDropdownMenu
import top.yukonga.miuix.kmp.window.WindowDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

/** 分类筛选标签枚举 */
private enum class ClipFilterTab(val label: String) {
    All("全部"),
    Favorite("收藏"),
    Link("链接"),
    Image("图片"),
    Text("文本")
}

/** 日期筛选枚举 */
private enum class DateFilterOption(val label: String) {
    All("全部日期"),
    Today("今天"),
    Yesterday("昨天"),
    Last7Days("近7天"),
    Last30Days("近30天"),
    Custom("指定日期")
}

/** 按日期分组后的一个分组:分组标题(今天/昨天/M月d日)+ 组内记录。 */
private data class DayGroup(val label: String, val items: List<CapturedClip>)

private data class EmptyStateInfo(
    val icon: ImageVector,
    val tint: Color,
    val title: String,
    val desc: String
)

@Composable
private fun RecordEmptyStateCard(
    icon: ImageVector,
    iconTint: Color = MiuixTheme.colorScheme.primary,
    title: String,
    description: String,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 12.dp),
        insideMargin = PaddingValues(horizontal = 24.dp, vertical = 32.dp)
    ) {
        Column(
            modifier = Modifier.fillMaxWidth(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            Box(
                modifier = Modifier
                    .size(64.dp)
                    .clip(CircleShape)
                    .background(iconTint.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = icon,
                    contentDescription = null,
                    tint = iconTint,
                    modifier = Modifier.size(30.dp)
                )
            }
            Spacer(Modifier.height(16.dp))
            Text(
                text = title,
                fontSize = 16.sp,
                fontWeight = FontWeight.SemiBold,
                color = MiuixTheme.colorScheme.onSurface,
                textAlign = TextAlign.Center
            )
            Spacer(Modifier.height(6.dp))
            Text(
                text = description,
                fontSize = 13.sp,
                lineHeight = 18.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.8f),
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 12.dp)
            )
            if (!actionLabel.isNullOrBlank() && onAction != null) {
                Spacer(Modifier.height(20.dp))
                Button(
                    onClick = onAction,
                    colors = ButtonDefaults.buttonColorsPrimary()
                ) {
                    Text(
                        text = actionLabel,
                        fontWeight = FontWeight.Medium,
                        fontSize = 14.sp
                    )
                }
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun RecordsPage(
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    onOpenSearch: () -> Unit = {},
    onOverlayActiveChanged: (Boolean) -> Unit = {},
    isGlobalOverlayActive: Boolean = false,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val haptic = LocalHapticFeedback.current
    val captured by ClipboardMonitorService.captured.collectAsState()
    val barSurface = MiuixTheme.colorScheme.surface
    val recordsBackdrop = rememberLayerBackdrop(
        onDraw = {
            drawRect(barSurface)
            drawContent()
        }
    )

    var isTimeForward by remember { mutableStateOf(true) }
    var currentFilter by remember { mutableStateOf(ClipFilterTab.All) }
    var currentDateFilter by remember { mutableStateOf(DateFilterOption.All) }
    var currentDeviceFilter by remember { mutableStateOf<String?>(null) }
    var isMultiSelectMode by remember { mutableStateOf(false) }
    var selectedClips by remember { mutableStateOf(setOf<CapturedClip>()) }

    var showClearDialog by remember { mutableStateOf(false) }
    var keepFavoritesOnClear by remember { mutableStateOf(true) }

    // 记录详情二级子页面状态与动画
    val detailPageAnimProgress = remember { Animatable(1f) }
    var activeDetailClip by remember { mutableStateOf<CapturedClip?>(null) }
    var displayedDetailClip by remember { mutableStateOf<CapturedClip?>(null) }
    var previewImageClip by remember { mutableStateOf<CapturedClip?>(null) }
    var isEditingInDetail by remember { mutableStateOf(false) }
    val editFieldState = remember { TextFieldState("") }
    val editFocusRequester = remember { FocusRequester() }
    val keyboardController = LocalSoftwareKeyboardController.current

    val openDetailPage: (CapturedClip) -> Unit = { clip ->
        activeDetailClip = clip
        displayedDetailClip = clip
        editFieldState.edit { replace(0, length, clip.text) }
        isEditingInDetail = false
        scope.launch {
            detailPageAnimProgress.snapTo(1f)
            detailPageAnimProgress.animateTo(0f, animationSpec = tween(280, easing = FastOutSlowInEasing))
        }
    }

    val closeDetailPage: () -> Unit = {
        isEditingInDetail = false
        activeDetailClip = null
        scope.launch {
            detailPageAnimProgress.animateTo(1f, animationSpec = tween(220, easing = LinearOutSlowInEasing))
            displayedDetailClip = null
        }
    }

    LaunchedEffect(isEditingInDetail) {
        if (isEditingInDetail) {
            delay(100)
            editFocusRequester.requestFocus()
            keyboardController?.show()
        }
    }

    // 日期滚轮选择弹窗状态 (MIUI 风格 NumberPicker)
    var showDatePickerDialog by remember { mutableStateOf(false) }
    var customDate by remember { mutableStateOf<Triple<Int, Int, Int>?>(null) } // (Year, Month, Day)

    val currentCal = remember { Calendar.getInstance() }
    val thisYear = remember { currentCal.get(Calendar.YEAR) }
    val thisMonth = remember { currentCal.get(Calendar.MONTH) + 1 }
    val thisDay = remember { currentCal.get(Calendar.DAY_OF_MONTH) }

    var pickerYear by remember { mutableIntStateOf(thisYear) }
    var pickerMonth by remember { mutableIntStateOf(thisMonth) }
    var pickerDay by remember { mutableIntStateOf(thisDay) }

    val daysInPickerMonth = remember(pickerYear, pickerMonth) {
        val cal = Calendar.getInstance().apply {
            set(Calendar.YEAR, pickerYear)
            set(Calendar.MONTH, pickerMonth - 1)
            set(Calendar.DAY_OF_MONTH, 1)
        }
        cal.getActualMaximum(Calendar.DAY_OF_MONTH)
    }

    LaunchedEffect(daysInPickerMonth) {
        if (pickerDay > daysInPickerMonth) {
            pickerDay = daysInPickerMonth
        }
    }

    // 全屏图片预览、多选模式或二级详情页时隐藏主导航底栏并禁止外层 Pager 滑动
    LaunchedEffect(previewImageClip, isMultiSelectMode, displayedDetailClip) {
        onOverlayActiveChanged(previewImageClip != null || isMultiSelectMode || displayedDetailClip != null)
    }

    // 统计日期时间基准
    val todayStart = remember {
        Calendar.getInstance().apply {
            set(Calendar.HOUR_OF_DAY, 0)
            set(Calendar.MINUTE, 0)
            set(Calendar.SECOND, 0)
            set(Calendar.MILLISECOND, 0)
        }.timeInMillis
    }
    val yesterdayStart = remember(todayStart) {
        Calendar.getInstance().apply {
            timeInMillis = todayStart
            add(Calendar.DAY_OF_YEAR, -1)
        }.timeInMillis
    }
    val last7DaysStart = remember(todayStart) {
        Calendar.getInstance().apply {
            timeInMillis = todayStart
            add(Calendar.DAY_OF_YEAR, -6)
        }.timeInMillis
    }
    val last30DaysStart = remember(todayStart) {
        Calendar.getInstance().apply {
            timeInMillis = todayStart
            add(Calendar.DAY_OF_YEAR, -29)
        }.timeInMillis
    }

    val remoteDevices = remember(captured) {
        captured.mapNotNull { it.sourceDevice }
            .filter { it.isNotBlank() && it != "本机" }
            .distinct()
    }

    // 过滤与排序 (isTimeForward: true=时间正序[最新在前], false=时间倒序[最早在前])
    val filteredList = remember(captured, currentFilter, currentDateFilter, currentDeviceFilter, customDate, isTimeForward, todayStart, yesterdayStart, last7DaysStart, last30DaysStart) {
        val typeFiltered = when (currentFilter) {
            ClipFilterTab.All -> captured
            ClipFilterTab.Favorite -> captured.filter { it.isFavorite }
            ClipFilterTab.Link -> captured.filter { it.isLink }
            ClipFilterTab.Image -> captured.filter { it.isImage }
            ClipFilterTab.Text -> captured.filter { !it.isLink && !it.isImage }
        }
        val deviceFiltered = when (currentDeviceFilter) {
            null -> typeFiltered
            "本机" -> typeFiltered.filter { it.sourceDevice == null || it.sourceDevice == "本机" }
            else -> typeFiltered.filter { it.sourceDevice == currentDeviceFilter }
        }
        val dateFiltered = when (currentDateFilter) {
            DateFilterOption.All -> deviceFiltered
            DateFilterOption.Today -> deviceFiltered.filter { it.time >= todayStart }
            DateFilterOption.Yesterday -> deviceFiltered.filter { it.time in yesterdayStart until todayStart }
            DateFilterOption.Last7Days -> deviceFiltered.filter { it.time >= last7DaysStart }
            DateFilterOption.Last30Days -> deviceFiltered.filter { it.time >= last30DaysStart }
            DateFilterOption.Custom -> {
                if (customDate != null) {
                    val (cy, cm, cd) = customDate!!
                    val start = Calendar.getInstance().apply {
                        set(Calendar.YEAR, cy)
                        set(Calendar.MONTH, cm - 1)
                        set(Calendar.DAY_OF_MONTH, cd)
                        set(Calendar.HOUR_OF_DAY, 0)
                        set(Calendar.MINUTE, 0)
                        set(Calendar.SECOND, 0)
                        set(Calendar.MILLISECOND, 0)
                    }.timeInMillis
                    val end = start + 24 * 3600 * 1000L - 1L
                    deviceFiltered.filter { it.time in start..end }
                } else {
                    deviceFiltered
                }
            }
        }
        if (isTimeForward) dateFiltered else dateFiltered.reversed()
    }
    val PAGE_SIZE = 25
    var displayLimit by remember(currentFilter, currentDateFilter, currentDeviceFilter, customDate, isTimeForward) {
        mutableIntStateOf(PAGE_SIZE)
    }

    val paginatedList = remember(filteredList, displayLimit) {
        filteredList.take(displayLimit)
    }
    val groups = remember(paginatedList) { groupByDay(paginatedList) }

    val listState = rememberLazyListState()

    val shouldLoadMore by remember {
        derivedStateOf {
            val layoutInfo = listState.layoutInfo
            val totalItems = layoutInfo.totalItemsCount
            val lastVisibleItemIndex = layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0
            totalItems > 0 && lastVisibleItemIndex >= totalItems - 3 && displayLimit < filteredList.size
        }
    }

    LaunchedEffect(shouldLoadMore) {
        if (shouldLoadMore) {
            displayLimit = (displayLimit + PAGE_SIZE).coerceAtMost(filteredList.size)
        }
    }

    val animatedClipIds = remember { mutableSetOf<String>() }

    var isRecordsMenuExpanded by remember { mutableStateOf(false) }
    var isFilterMenuExpanded by remember { mutableStateOf(false) }
    var isDetailMenuExpanded by remember { mutableStateOf(false) }

    val dispatcherOwner = androidx.navigationevent.compose.LocalNavigationEventDispatcherOwner.current
    val directInput = remember { androidx.navigationevent.DirectNavigationEventInput() }
    androidx.compose.runtime.DisposableEffect(dispatcherOwner, directInput) {
        dispatcherOwner?.navigationEventDispatcher?.addInput(directInput)
        onDispose {
            dispatcherOwner?.navigationEventDispatcher?.removeInput(directInput)
        }
    }

    // 气泡菜单展开时，拦截返回键/返回手势，仅关闭气泡菜单
    BackHandler(enabled = (isRecordsMenuExpanded || isFilterMenuExpanded || isDetailMenuExpanded) && !isGlobalOverlayActive) {
        directInput.backCompleted()
        isRecordsMenuExpanded = false
        isFilterMenuExpanded = false
        isDetailMenuExpanded = false
    }

    // 退出多选模式的预测返回拦截
    PredictiveBackHandler(enabled = isMultiSelectMode && !isRecordsMenuExpanded && !isFilterMenuExpanded && !isDetailMenuExpanded && !isGlobalOverlayActive) { progress ->
        if (!SyncSettings.predictiveBackEnabled(context)) {
            isMultiSelectMode = false
            selectedClips = emptySet()
            return@PredictiveBackHandler
        }
        try {
            progress.collect { }
            isMultiSelectMode = false
            selectedClips = emptySet()
        } catch (e: CancellationException) {
        }
    }

    // 处于编辑模式时，拦截返回键/返回手势，退出编辑模式并还原内容
    BackHandler(enabled = activeDetailClip != null && isEditingInDetail && !isDetailMenuExpanded && !isGlobalOverlayActive) {
        editFieldState.edit { replace(0, length, activeDetailClip?.text ?: "") }
        isEditingInDetail = false
        keyboardController?.hide()
    }

    // 退出记录详情二级页面的预测返回拦截（仅在非编辑模式且无气泡菜单时生效）
    PredictiveBackHandler(
        enabled = activeDetailClip != null &&
            !isEditingInDetail &&
            !showClearDialog &&
            !showDatePickerDialog &&
            previewImageClip == null &&
            !isDetailMenuExpanded &&
            !isGlobalOverlayActive
    ) { progress ->
        if (!SyncSettings.predictiveBackEnabled(context)) {
            closeDetailPage()
            return@PredictiveBackHandler
        }
        try {
            progress.collect { event ->
                val p = FastOutSlowInEasing.transform(event.progress)
                detailPageAnimProgress.snapTo(p)
            }
            detailPageAnimProgress.animateTo(1f, animationSpec = tween(200, easing = LinearOutSlowInEasing))
            activeDetailClip = null
            displayedDetailClip = null
        } catch (e: CancellationException) {
            detailPageAnimProgress.animateTo(0f, animationSpec = spring(stiffness = Spring.StiffnessMediumLow))
        }
    }

    val baseProgress = detailPageAnimProgress.value

    Box(modifier = Modifier.fillMaxSize()) {
        // ---- 1. 底层：一级记录主页（包含 PageShell） ----
        Box(
            modifier = Modifier
                .fillMaxSize()
                .graphicsLayer {
                    if (displayedDetailClip != null) {
                        translationX = -(1f - baseProgress) * size.width * 0.15f
                        val s = 0.94f + 0.06f * baseProgress
                        scaleX = s
                        scaleY = s
                        alpha = 0.82f + 0.18f * baseProgress
                    }
                }
        ) {
            PageShell(
                title = if (isMultiSelectMode) "已选择 ${selectedClips.size} 项" else "剪贴板",
                backdrop = recordsBackdrop,
                bottomInnerPadding = bottomInnerPadding,
                navigationIcon = if (isMultiSelectMode) {
                    {
                        Text(
                            text = "取消",
                            color = MiuixTheme.colorScheme.primary,
                            fontSize = 16.sp,
                            modifier = Modifier
                                .clickable {
                                    isMultiSelectMode = false
                                    selectedClips = emptySet()
                                }
                                .padding(horizontal = 14.dp, vertical = 8.dp)
                        )
                    }
                } else {
                    {
                        IconButton(onClick = onOpenSearch) {
                            Icon(
                                imageVector = MiuixIcons.Normal.Search,
                                contentDescription = "搜索"
                            )
                        }
                    }
                },
                actions = {
                    if (isMultiSelectMode) {
                        val isAllSelected = selectedClips.isNotEmpty() && selectedClips.size == filteredList.size
                        Text(
                            text = if (isAllSelected) "取消全选" else "全选",
                            color = MiuixTheme.colorScheme.primary,
                            fontSize = 16.sp,
                            modifier = Modifier
                                .clickable {
                                    selectedClips = if (isAllSelected) emptySet() else filteredList.toSet()
                                }
                                .padding(horizontal = 14.dp, vertical = 8.dp)
                        )
                    } else {
                        val isFilterActive = currentDateFilter != DateFilterOption.All || currentDeviceFilter != null || !isTimeForward
                        val sortSummary = if (isTimeForward) "时间正序" else "时间倒序"
                        val dateSummary = when (currentDateFilter) {
                            DateFilterOption.All -> "全部日期"
                            DateFilterOption.Today -> "今天"
                            DateFilterOption.Yesterday -> "昨天"
                            DateFilterOption.Last7Days -> "近7天"
                            DateFilterOption.Last30Days -> "近30天"
                            DateFilterOption.Custom -> if (customDate != null) "${customDate!!.second}月${customDate!!.third}日" else "指定日期"
                        }
                        val deviceSummary = currentDeviceFilter ?: "全部设备"

                        // 1. 筛选与排序多级级联气泡菜单 (二级子菜单)
                        WindowIconCascadingDropdownMenu(
                            entry = DropdownEntry(
                                items = buildList {
                                    // 1. 排序方式 (二级菜单)
                                    add(DropdownItem(
                                        text = "排序方式",
                                        summary = sortSummary,
                                        children = listOf(
                                            DropdownItem(
                                                text = "时间正序 (最新在前)",
                                                selected = isTimeForward,
                                                onClick = { isTimeForward = true }
                                            ),
                                            DropdownItem(
                                                text = "时间倒序 (最早在前)",
                                                selected = !isTimeForward,
                                                onClick = { isTimeForward = false }
                                            )
                                        )
                                    ))

                                    // 2. 日期范围 (二级菜单)
                                    add(DropdownItem(
                                        text = "日期范围",
                                        summary = dateSummary,
                                        children = listOf(
                                            DropdownItem(
                                                text = "全部日期 (${captured.size})",
                                                selected = currentDateFilter == DateFilterOption.All,
                                                onClick = { currentDateFilter = DateFilterOption.All; customDate = null }
                                            ),
                                            DropdownItem(
                                                text = "今天 (${captured.count { it.time >= todayStart }})",
                                                selected = currentDateFilter == DateFilterOption.Today,
                                                onClick = { currentDateFilter = DateFilterOption.Today; customDate = null }
                                            ),
                                            DropdownItem(
                                                text = "昨天 (${captured.count { it.time in yesterdayStart until todayStart }})",
                                                selected = currentDateFilter == DateFilterOption.Yesterday,
                                                onClick = { currentDateFilter = DateFilterOption.Yesterday; customDate = null }
                                            ),
                                            DropdownItem(
                                                text = "近7天 (${captured.count { it.time >= last7DaysStart }})",
                                                selected = currentDateFilter == DateFilterOption.Last7Days,
                                                onClick = { currentDateFilter = DateFilterOption.Last7Days; customDate = null }
                                            ),
                                            DropdownItem(
                                                text = "近30天 (${captured.count { it.time >= last30DaysStart }})",
                                                selected = currentDateFilter == DateFilterOption.Last30Days,
                                                onClick = { currentDateFilter = DateFilterOption.Last30Days; customDate = null }
                                            ),
                                            DropdownItem(
                                                text = if (currentDateFilter == DateFilterOption.Custom && customDate != null)
                                                    "指定日期 (${customDate!!.second}月${customDate!!.third}日)"
                                                else "指定日期…",
                                                selected = currentDateFilter == DateFilterOption.Custom,
                                                onClick = {
                                                    if (customDate != null) {
                                                        pickerYear = customDate!!.first
                                                        pickerMonth = customDate!!.second
                                                        pickerDay = customDate!!.third
                                                    } else {
                                                        pickerYear = thisYear
                                                        pickerMonth = thisMonth
                                                        pickerDay = thisDay
                                                    }
                                                    showDatePickerDialog = true
                                                }
                                            )
                                        )
                                    ))

                                    // 3. 来源设备 (二级菜单)
                                    add(DropdownItem(
                                        text = "来源设备",
                                        summary = deviceSummary,
                                        children = buildList {
                                            add(DropdownItem(
                                                text = "全部设备 (${captured.size})",
                                                selected = currentDeviceFilter == null,
                                                onClick = { currentDeviceFilter = null }
                                            ))
                                            val localCount = captured.count { it.sourceDevice == null || it.sourceDevice == "本机" }
                                            add(DropdownItem(
                                                text = "本机 ($localCount)",
                                                selected = currentDeviceFilter == "本机",
                                                onClick = { currentDeviceFilter = "本机" }
                                            ))
                                            remoteDevices.forEach { devName ->
                                                val count = captured.count { it.sourceDevice == devName }
                                                add(DropdownItem(
                                                    text = "$devName ($count)",
                                                    selected = currentDeviceFilter == devName,
                                                    onClick = { currentDeviceFilter = devName }
                                                ))
                                            }
                                        }
                                    ))

                                    // 4. 重置所有筛选与排序
                                    if (isFilterActive || currentFilter != ClipFilterTab.All) {
                                        add(DropdownItem(
                                            text = "重置所有筛选与排序",
                                            onClick = {
                                                isTimeForward = true
                                                currentFilter = ClipFilterTab.All
                                                currentDateFilter = DateFilterOption.All
                                                currentDeviceFilter = null
                                                customDate = null
                                            }
                                        ))
                                    }
                                }
                            ),
                            onExpandedChange = { isFilterMenuExpanded = it }
                        ) {
                            Icon(
                                imageVector = LucideIcons.Filter,
                                contentDescription = "筛选与排序",
                                tint = if (isFilterActive) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                            )
                        }

                        // 2. 更多操作菜单: 多选管理 + 清空全部记录
                        WindowIconDropdownMenu(
                            entry = DropdownEntry(
                                items = listOf(
                                    DropdownItem(
                                        text = "多选管理",
                                        onClick = { isMultiSelectMode = true },
                                    ),
                                    DropdownItem(
                                        text = "清空全部记录",
                                        onClick = { showClearDialog = true },
                                    ),
                                ),
                            ),
                            onExpandedChange = { isRecordsMenuExpanded = it }
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.More,
                                contentDescription = "更多操作",
                            )
                        }
                    }
                }
            ) { scrollBehavior, topPadding ->
                LazyColumn(
                    state = listState,
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = if (isMultiSelectMode) bottomInnerPadding + 64.dp else bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    // 分类筛选横向胶囊栏 (全部 / 收藏 / 链接 / 图片 / 文本 + 激活的日期/设备清除胶囊)
                    item {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 4.dp)
                                .horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            ClipFilterTab.entries.forEach { tab ->
                                val count = when (tab) {
                                    ClipFilterTab.All -> captured.size
                                    ClipFilterTab.Favorite -> captured.count { it.isFavorite }
                                    ClipFilterTab.Link -> captured.count { it.isLink }
                                    ClipFilterTab.Image -> captured.count { it.isImage }
                                    ClipFilterTab.Text -> captured.count { !it.isLink && !it.isImage }
                                }
                                FilterChip(
                                    label = "${tab.label} ($count)",
                                    selected = currentFilter == tab,
                                    onClick = { currentFilter = tab }
                                )
                            }

                            // 激活的日期筛选条件指示与一键清除胶囊
                            if (currentDateFilter != DateFilterOption.All) {
                                val dateText = when (currentDateFilter) {
                                    DateFilterOption.Today -> "今天"
                                    DateFilterOption.Yesterday -> "昨天"
                                    DateFilterOption.Last7Days -> "近7天"
                                    DateFilterOption.Last30Days -> "近30天"
                                    DateFilterOption.Custom -> if (customDate != null) "${customDate!!.second}月${customDate!!.third}日" else "指定日期"
                                    else -> ""
                                }
                                ActiveFilterChip(
                                    icon = MiuixIcons.Normal.Months,
                                    label = dateText,
                                    onClick = {
                                        currentDateFilter = DateFilterOption.All
                                        customDate = null
                                    }
                                )
                            }

                            // 激活的设备筛选条件指示与一键清除胶囊
                            if (currentDeviceFilter != null) {
                                ActiveFilterChip(
                                    icon = resolveDeviceIcon(currentDeviceFilter),
                                    label = currentDeviceFilter ?: "",
                                    onClick = { currentDeviceFilter = null }
                                )
                            }
                        }
                    }

            // 3. 记录列表主体
            if (captured.isEmpty()) {
                item {
                    RecordEmptyStateCard(
                        icon = LucideIcons.ClipboardCheck,
                        title = "暂无剪贴板记录",
                        description = "复制文字、链接或截图，或者接收多端推送后，将自动展示在剪贴板中"
                    )
                }
            } else if (filteredList.isEmpty()) {
                item {
                    val info = when {
                        currentFilter == ClipFilterTab.Favorite -> EmptyStateInfo(
                            icon = MiuixIcons.Normal.Favorites,
                            tint = Color(0xFFFF2D55),
                            title = "暂无收藏记录",
                            desc = "点击记录卡片右上角即可收藏重要内容"
                        )
                        currentFilter == ClipFilterTab.Image -> EmptyStateInfo(
                            icon = LucideIcons.Image,
                            tint = MiuixTheme.colorScheme.primary,
                            title = "暂无图片记录",
                            desc = "复制图片或截图后将自动归类到此处"
                        )
                        currentFilter == ClipFilterTab.Link -> EmptyStateInfo(
                            icon = MiuixIcons.Normal.Link,
                            tint = Color(0xFF34C759),
                            title = "暂无链接记录",
                            desc = "复制网页链接或 URL 后将自动归类到此处"
                        )
                        currentFilter == ClipFilterTab.Text -> EmptyStateInfo(
                            icon = LucideIcons.MessageSquare,
                            tint = MiuixTheme.colorScheme.primary,
                            title = "暂无纯文本记录",
                            desc = "复制纯文本内容后将自动归类到此处"
                        )
                        currentDateFilter != DateFilterOption.All -> EmptyStateInfo(
                            icon = MiuixIcons.Normal.Months,
                            tint = MiuixTheme.colorScheme.primary,
                            title = "该日期下暂无记录",
                            desc = "所选日期范围内未找到匹配的剪贴板记录"
                        )
                        currentDeviceFilter != null -> EmptyStateInfo(
                            icon = LucideIcons.Laptop,
                            tint = MiuixTheme.colorScheme.primary,
                            title = "该设备下暂无记录",
                            desc = "所选设备暂无符合当前筛选条件的剪贴板记录"
                        )
                        else -> EmptyStateInfo(
                            icon = LucideIcons.Layers,
                            tint = MiuixTheme.colorScheme.primary,
                            title = "当前分类下暂无记录",
                            desc = "可切换筛选标签或清除筛选条件查看全部内容"
                        )
                    }

                    RecordEmptyStateCard(
                        icon = info.icon,
                        iconTint = info.tint,
                        title = info.title,
                        description = info.desc,
                        actionLabel = "查看全部记录",
                        onAction = {
                            currentFilter = ClipFilterTab.All
                            currentDateFilter = DateFilterOption.All
                            currentDeviceFilter = null
                        }
                    )
                }
            } else {
                groups.forEach { group ->
                    item(key = "header_" + group.label) {
                        Text(
                            text = group.label,
                            style = MiuixTheme.textStyles.title3,
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            modifier = Modifier.padding(start = 8.dp, top = 10.dp, bottom = 2.dp)
                        )
                    }
                    items(group.items, key = { it.id }) { clip ->
                        val isSelected = clip in selectedClips
                        val shouldAnimate = remember(clip.id) {
                            if (!animatedClipIds.contains(clip.id)) {
                                animatedClipIds.add(clip.id)
                                true
                            } else {
                                false
                            }
                        }
                        AnimatedRecordCardItem(
                            modifier = Modifier.animateItem(),
                            shouldAnimate = shouldAnimate
                        ) {
                            RecordCard(
                                clip = clip,
                                isMultiSelectMode = isMultiSelectMode,
                                isSelected = isSelected,
                                onToggleSelect = {
                                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                    selectedClips = if (isSelected) selectedClips - clip else selectedClips + clip
                                },
                                onCardClick = {
                                    if (isMultiSelectMode) {
                                        haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                        selectedClips = if (isSelected) selectedClips - clip else selectedClips + clip
                                    } else {
                                        openDetailPage(clip)
                                    }
                                },
                                onLongClick = {
                                    haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                                    if (!isMultiSelectMode) {
                                        isMultiSelectMode = true
                                        selectedClips = setOf(clip)
                                    }
                                },
                                onToggleFavorite = {
                                    haptic.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                                    ClipboardMonitorService.toggleFavorite(context, clip)
                                },
                                onPreviewImage = {
                                    previewImageClip = clip
                                },
                                onCopy = {
                                    scope.launch {
                                        if (clip.isImage) {
                                            val ok = ImageLoader.copyImageToClipboard(context, clip.imageRef, clip.text)
                                            snackbarHostState.showAppSnack(if (ok) "已复制图片到剪贴板" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                                        } else {
                                            copyToClipboard(context, clip.text)
                                            snackbarHostState.showAppSnack("已复制文本", SnackType.Success)
                                        }
                                    }
                                },
                                onShare = {
                                    scope.launch {
                                        if (clip.isImage) {
                                            val ok = ImageLoader.shareImage(context, clip.imageRef, clip.text)
                                            if (!ok) snackbarHostState.showAppSnack("分享失败", SnackType.Error)
                                        } else {
                                            shareText(context, clip.text)
                                        }
                                    }
                                },
                                onDelete = {
                                    val index = captured.indexOf(clip)
                                    ClipboardMonitorService.deleteAt(context, index)
                                    scope.launch {
                                        val result = snackbarHostState.showAppSnack(
                                            "已删除该记录", SnackType.Success, actionLabel = "撤销"
                                        )
                                        if (result == SnackbarResult.ActionPerformed) {
                                            ClipboardMonitorService.restoreAt(context, index, clip)
                                        }
                                    }
                                }
                            )
                        }
                    }
                }

                // 列表触底懒加载指示器 / 全部加载完毕提示
                if (displayLimit < filteredList.size) {
                    item(key = "footer_loading") {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 14.dp),
                            horizontalArrangement = Arrangement.Center,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            LoadingSpinner(
                                modifier = Modifier.size(16.dp),
                                color = MiuixTheme.colorScheme.primary,
                                strokeWidth = 2.dp
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                text = "正在加载更多记录 (${paginatedList.size}/${filteredList.size})…",
                                fontSize = 12.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                } else if (filteredList.size > PAGE_SIZE) {
                    item(key = "footer_all_loaded") {
                        Text(
                            text = "已加载全部 ${filteredList.size} 条记录",
                            fontSize = 11.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f),
                            textAlign = TextAlign.Center,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 12.dp)
                        )
                    }
                }
            }
        }
        }

        // 多选模式悬浮毛玻璃操作胶囊 (真实 Miuix 液态玻璃实时模糊 + 选定计数徽标动效与触感)
        AnimatedVisibility(
            visible = isMultiSelectMode,
            enter = fadeIn() + slideInVertically(initialOffsetY = { it / 2 }),
            exit = fadeOut() + slideOutVertically(targetOffsetY = { it / 2 }),
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 24.dp + WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding())
        ) {
            BarBlurSurface(
                backdrop = recordsBackdrop,
                shape = CircleShape,
                refreshKey = isMultiSelectMode,
                modifier = Modifier
                    .shadow(
                        elevation = 16.dp,
                        shape = CircleShape,
                        spotColor = Color.Black.copy(alpha = 0.25f),
                        ambientColor = Color.Black.copy(alpha = 0.12f)
                    )
                    .border(
                        width = 0.8.dp,
                        color = MiuixTheme.colorScheme.onSurface.copy(alpha = 0.10f),
                        shape = CircleShape
                    )
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 18.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    // 1. 批量收藏
                    MultiSelectActionButton(
                        icon = MiuixIcons.Normal.FavoritesFill,
                        tint = if (selectedClips.isNotEmpty()) Color(0xFFFF2D55) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f),
                        badgeCount = selectedClips.size,
                        label = "批量收藏",
                        onClick = {
                            if (selectedClips.isEmpty()) return@MultiSelectActionButton
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            val allFav = selectedClips.all { it.isFavorite }
                            val newFav = !allFav
                            selectedClips.forEach { clip ->
                                if (clip.isFavorite != newFav) {
                                    ClipboardMonitorService.toggleFavorite(context, clip)
                                }
                            }
                            scope.launch {
                                snackbarHostState.showAppSnack(
                                    if (newFav) "已批量收藏 ${selectedClips.size} 项" else "已取消收藏",
                                    SnackType.Success
                                )
                            }
                        }
                    )

                    // 2. 合并复制
                    MultiSelectActionButton(
                        icon = LucideIcons.Copy,
                        tint = if (selectedClips.isNotEmpty()) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f),
                        badgeCount = selectedClips.size,
                        label = "合并复制",
                        onClick = {
                            if (selectedClips.isEmpty()) return@MultiSelectActionButton
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            val merged = selectedClips.sortedByDescending { it.time }.joinToString("\n\n") { it.text }
                            copyToClipboard(context, merged)
                            val count = selectedClips.size
                            isMultiSelectMode = false
                            selectedClips = emptySet()
                            scope.launch {
                                snackbarHostState.showAppSnack("已合并复制 $count 条记录", SnackType.Success)
                            }
                        }
                    )

                    // 3. 批量推送
                    MultiSelectActionButton(
                        icon = LucideIcons.Upload,
                        tint = if (selectedClips.isNotEmpty()) Color(0xFF10B981) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f),
                        badgeCount = selectedClips.size,
                        label = "批量推送",
                        onClick = {
                            if (selectedClips.isEmpty()) return@MultiSelectActionButton
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            val url = SyncSettings.serverUrl(context)
                            if (url.isBlank()) {
                                scope.launch { snackbarHostState.showAppSnack("请先配置服务器地址", SnackType.Info) }
                                return@MultiSelectActionButton
                            }
                            val toPush = selectedClips.toList()
                            val count = toPush.size
                            isMultiSelectMode = false
                            selectedClips = emptySet()
                            scope.launch {
                                try {
                                    val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                    withContext(Dispatchers.IO) {
                                        toPush.forEach { clip ->
                                            if (clip.isImage) {
                                                val bytes = ImageLoader.getImageBytes(context, clip.imageRef, clip.text)
                                                if (bytes != null) {
                                                    api.uploadImage(bytes, SyncSettings.ensureDeviceId(context), SyncSettings.deviceName(context))
                                                }
                                            } else {
                                                api.putText(clip.text, SyncSettings.ensureDeviceId(context), SyncSettings.deviceName(context))
                                            }
                                        }
                                    }
                                    snackbarHostState.showAppSnack("已批量推送 $count 条记录", SnackType.Success)
                                } catch (e: Exception) {
                                    snackbarHostState.showAppSnack(e.message ?: "推送失败", SnackType.Error)
                                }
                            }
                        }
                    )

                    // 4. 批量删除
                    MultiSelectActionButton(
                        icon = LucideIcons.Trash2,
                        tint = if (selectedClips.isNotEmpty()) MiuixTheme.colorScheme.error else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f),
                        badgeCount = selectedClips.size,
                        label = "批量删除",
                        onClick = {
                            if (selectedClips.isEmpty()) return@MultiSelectActionButton
                            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
                            val count = selectedClips.size
                            val snapshot = captured
                            ClipboardMonitorService.deleteClips(context, selectedClips)
                            isMultiSelectMode = false
                            selectedClips = emptySet()
                            scope.launch {
                                val result = snackbarHostState.showAppSnack("已批量删除 $count 条记录", SnackType.Success, actionLabel = "撤销")
                                if (result == SnackbarResult.ActionPerformed) {
                                    ClipboardMonitorService.replaceAll(context, snapshot)
                                }
                            }
                        }
                    )
                }
            }
        }

        // 黑色半透明遮罩层（视差下沉感）
        if (displayedDetailClip != null && (1f - baseProgress) > 0.001f) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .background(Color.Black.copy(alpha = (1f - baseProgress) * 0.22f))
            )
        }
    }

    // ---- 2. 顶层：记录详情二级页面 ----
    displayedDetailClip?.let { detailClip ->
            val p = detailPageAnimProgress.value // 0f (显示) -> 1f (退出到右侧)
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .graphicsLayer {
                        translationX = p * size.width
                        val s = 1f - p * 0.05f
                        scaleX = s
                        scaleY = s
                        transformOrigin = TransformOrigin(0f, 0.5f)
                        this.clip = true
                        shape = RoundedCornerShape((p * 24).dp)
                        shadowElevation = (1f - p) * 24f
                    }
                    .background(MiuixTheme.colorScheme.background)
            ) {
                RecordDetailPage(
                    clip = detailClip,
                    isEditing = isEditingInDetail,
                    onToggleEditing = { isEditingInDetail = it },
                    editFieldState = editFieldState,
                    editFocusRequester = editFocusRequester,
                    bottomInnerPadding = bottomInnerPadding,
                    onBack = { closeDetailPage() },
                    onCopy = {
                        scope.launch {
                            if (detailClip.isImage) {
                                val ok = ImageLoader.copyImageToClipboard(context, detailClip.imageRef, detailClip.text)
                                snackbarHostState.showAppSnack(if (ok) "已复制图片到剪贴板" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                            } else {
                                copyToClipboard(context, detailClip.text)
                                snackbarHostState.showAppSnack("已复制", SnackType.Success)
                            }
                        }
                    },
                    onToggleFavorite = {
                        ClipboardMonitorService.toggleFavorite(context, detailClip)
                        val updated = detailClip.copy(isFavorite = !detailClip.isFavorite)
                        activeDetailClip = updated
                        displayedDetailClip = updated
                    },
                    onUpdateText = { newText ->
                        ClipboardMonitorService.updateClip(context, detailClip, newText)
                        val updated = detailClip.copy(text = newText)
                        activeDetailClip = updated
                        displayedDetailClip = updated
                        isEditingInDetail = false
                        scope.launch {
                            snackbarHostState.showAppSnack("已保存修改", SnackType.Success)
                        }
                    },
                    onDelete = {
                        closeDetailPage()
                        val index = captured.indexOf(detailClip)
                        ClipboardMonitorService.deleteAt(context, index)
                        scope.launch {
                            val result = snackbarHostState.showAppSnack(
                                "已删除该记录", SnackType.Success, actionLabel = "撤销"
                            )
                            if (result == SnackbarResult.ActionPerformed) {
                                ClipboardMonitorService.restoreAt(context, index, detailClip)
                            }
                        }
                    },
                    onPushToAll = {
                        val url = SyncSettings.serverUrl(context)
                        if (url.isBlank()) {
                            scope.launch { snackbarHostState.showAppSnack("请先配置服务器", SnackType.Info) }
                        } else {
                            scope.launch {
                                try {
                                    val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                    withContext(Dispatchers.IO) {
                                        if (detailClip.isImage) {
                                            val bytes = ImageLoader.getImageBytes(context, detailClip.imageRef, detailClip.text)
                                            if (bytes != null) {
                                                api.uploadImage(
                                                    pngBytes = bytes,
                                                    deviceId = SyncSettings.ensureDeviceId(context),
                                                    deviceName = SyncSettings.deviceName(context)
                                                )
                                            } else {
                                                throw Exception("图片数据无法读取")
                                            }
                                        } else {
                                            api.putText(
                                                text = detailClip.text,
                                                deviceId = SyncSettings.ensureDeviceId(context),
                                                deviceName = SyncSettings.deviceName(context)
                                            )
                                        }
                                    }
                                    snackbarHostState.showAppSnack(if (detailClip.isImage) "已推送图片到所有设备" else "已推送到所有设备", SnackType.Success)
                                } catch (e: Exception) {
                                    snackbarHostState.showAppSnack(e.message ?: "推送失败", SnackType.Error)
                                }
                            }
                        }
                    },
                    onPreviewImage = { previewImageClip = detailClip },
                    isMenuExpanded = isDetailMenuExpanded,
                    onMenuExpandedChange = { isDetailMenuExpanded = it }
                )
            }
        }
    }

    // 清空全部记录的二次确认弹窗
    val favoriteCount = captured.count { it.isFavorite }
    WindowDialog(
        show = showClearDialog,
        title = "清空记录",
        summary = "确定要清空本地剪贴板记录吗？此操作可撤销。",
        onDismissRequest = { showClearDialog = false }
    ) {
        Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { keepFavoritesOnClear = !keepFavoritesOnClear }
                    .padding(vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Checkbox(
                    state = if (keepFavoritesOnClear) ToggleableState.On else ToggleableState.Off,
                    onClick = { keepFavoritesOnClear = !keepFavoritesOnClear }
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    text = "保留已收藏记录 (${favoriteCount} 条)",
                    color = MiuixTheme.colorScheme.onSurface
                )
            }
            Spacer(Modifier.height(8.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Button(
                    onClick = { showClearDialog = false },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val snapshot = captured
                        ClipboardMonitorService.clearAll(context, keepFavorites = keepFavoritesOnClear)
                        showClearDialog = false
                        scope.launch {
                            val result = snackbarHostState.showAppSnack(
                                if (keepFavoritesOnClear && snapshot.any { it.isFavorite }) "已清空未收藏记录" else "已清空全部记录",
                                SnackType.Success,
                                actionLabel = "撤销"
                            )
                            if (result == SnackbarResult.ActionPerformed) {
                                ClipboardMonitorService.replaceAll(context, snapshot)
                            }
                        }
                    },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.error,
                        contentColor = Color.White
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("清空")
                }
            }
        }
    }

    // 日期滚轮选择弹窗 (Miuix 风格 NumberPicker)
    WindowDialog(
        show = showDatePickerDialog,
        title = "选择指定日期",
        summary = "${pickerYear}年${pickerMonth}月${pickerDay}日",
        onDismissRequest = { showDatePickerDialog = false }
    ) {
        Column(modifier = Modifier.padding(horizontal = 8.dp, vertical = 8.dp)) {
            // 三滚轮并排选择器 (年, 月, 日)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 12.dp),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically
            ) {
                // 年份滚轮
                NumberPicker(
                    value = pickerYear,
                    onValueChange = { pickerYear = it },
                    range = (thisYear - 6)..(thisYear + 1),
                    label = { "${it}年" },
                    wrapAround = false,
                    modifier = Modifier.weight(1.3f)
                )

                // 月份滚轮
                NumberPicker(
                    value = pickerMonth,
                    onValueChange = { pickerMonth = it },
                    range = 1..12,
                    label = { "${it}月" },
                    wrapAround = true,
                    modifier = Modifier.weight(1f)
                )

                // 日期滚轮
                NumberPicker(
                    value = pickerDay,
                    onValueChange = { pickerDay = it },
                    range = 1..daysInPickerMonth,
                    label = { "${it}日" },
                    wrapAround = true,
                    modifier = Modifier.weight(1f)
                )
            }

            Spacer(Modifier.height(12.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Button(
                    onClick = { showDatePickerDialog = false },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        customDate = Triple(pickerYear, pickerMonth, pickerDay)
                        currentDateFilter = DateFilterOption.Custom
                        showDatePickerDialog = false
                    },
                    modifier = Modifier.weight(1f),
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.primary,
                        contentColor = Color.White
                    )
                ) {
                    Text("确定")
                }
            }
        }
    }

    // 全屏沉浸式大图预览
    FullscreenImagePreviewDialog(
        show = previewImageClip != null,
        imageRef = previewImageClip?.imageRef,
        rawText = previewImageClip?.text,
        onDismissRequest = { previewImageClip = null }
    )
}

/** 记录详情与编辑二级子页面 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun RecordDetailPage(
    clip: CapturedClip,
    isEditing: Boolean,
    onToggleEditing: (Boolean) -> Unit,
    editFieldState: TextFieldState,
    editFocusRequester: FocusRequester,
    bottomInnerPadding: Dp,
    onBack: () -> Unit,
    onCopy: () -> Unit,
    onToggleFavorite: () -> Unit,
    onUpdateText: (String) -> Unit,
    onDelete: () -> Unit,
    onPushToAll: () -> Unit,
    onPreviewImage: () -> Unit,
    isMenuExpanded: Boolean = false,
    onMenuExpandedChange: (Boolean) -> Unit = {}
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val smartActions = remember(clip.text, clip.isImage) {
        if (!clip.isImage) SmartActionEngine.detectActions(context, clip.text) else emptyList()
    }

    val sourceLabel = if (!clip.sourceDevice.isNullOrBlank() && clip.sourceDevice != "本机") {
        clip.sourceDevice
    } else {
        clip.sourceApp ?: "本机"
    }

    val keyboardController = LocalSoftwareKeyboardController.current

    val onCancelEdit = {
        editFieldState.edit { replace(0, length, clip.text) }
        onToggleEditing(false)
        keyboardController?.hide()
    }

    LaunchedEffect(isEditing) {
        if (isEditing) {
            editFocusRequester.requestFocus()
        }
    }

    PageShell(
        title = if (isEditing) "编辑记录" else "记录详情",
        bottomInnerPadding = bottomInnerPadding,
        navigationIcon = {
            if (isEditing) {
                Text(
                    text = "取消",
                    color = MiuixTheme.colorScheme.primary,
                    fontSize = 16.sp,
                    modifier = Modifier
                        .clickable { onCancelEdit() }
                        .padding(horizontal = 14.dp, vertical = 8.dp)
                )
            } else {
                IconButton(onClick = onBack) {
                    Icon(
                        imageVector = MiuixIcons.Normal.Back,
                        contentDescription = "返回"
                    )
                }
            }
        },
        actions = {
            if (isEditing) {
                Text(
                    text = "保存",
                    color = MiuixTheme.colorScheme.primary,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier
                        .clickable {
                            val newText = editFieldState.text.toString().trim()
                            if (newText.isNotBlank()) {
                                onUpdateText(newText)
                                keyboardController?.hide()
                            }
                        }
                        .padding(horizontal = 14.dp, vertical = 8.dp)
                )
            } else {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = onCopy) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Copy,
                            contentDescription = "复制"
                        )
                    }
                    IconButton(onClick = onToggleFavorite) {
                        Icon(
                            imageVector = if (clip.isFavorite) MiuixIcons.Normal.FavoritesFill else MiuixIcons.Normal.Favorites,
                            contentDescription = if (clip.isFavorite) "取消收藏" else "收藏",
                            tint = if (clip.isFavorite) Color(0xFFFF2D55) else MiuixTheme.colorScheme.onSurface
                        )
                    }
                    WindowIconDropdownMenu(
                        entry = DropdownEntry(
                            items = if (clip.isImage) {
                                listOf(
                                    DropdownItem(
                                        text = "使用其他应用打开",
                                        onClick = {
                                            scope.launch {
                                                val ok = ImageLoader.openImageWithOtherApp(context, clip.imageRef, clip.text)
                                                if (!ok) Toast.makeText(context, "无法使用其他应用打开", Toast.LENGTH_SHORT).show()
                                            }
                                        }
                                    ),
                                    DropdownItem(
                                        text = "保存到相册",
                                        onClick = {
                                            scope.launch {
                                                val ok = ImageLoader.saveToGallery(context, clip.imageRef, clip.text)
                                                Toast.makeText(context, if (ok) "已保存到相册 (Pictures/NexClip)" else "保存失败", Toast.LENGTH_SHORT).show()
                                            }
                                        }
                                    ),
                                    DropdownItem(
                                        text = "系统分享",
                                        onClick = {
                                            scope.launch {
                                                val ok = ImageLoader.shareImage(context, clip.imageRef, clip.text)
                                                if (!ok) Toast.makeText(context, "分享失败", Toast.LENGTH_SHORT).show()
                                            }
                                        }
                                    ),
                                    DropdownItem(
                                        text = "推送到所有设备",
                                        onClick = onPushToAll
                                    ),
                                    DropdownItem(
                                        text = "删除此记录",
                                        onClick = onDelete
                                    )
                                )
                            } else {
                                listOf(
                                    DropdownItem(
                                        text = "编辑文本",
                                        onClick = {
                                            editFieldState.edit { replace(0, length, clip.text) }
                                            onToggleEditing(true)
                                        }
                                    ),
                                    DropdownItem(
                                        text = "系统分享",
                                        onClick = { shareText(context, clip.text) }
                                    ),
                                    DropdownItem(
                                        text = "推送到所有设备",
                                        onClick = onPushToAll
                                    ),
                                    DropdownItem(
                                        text = "删除此记录",
                                        onClick = onDelete
                                    )
                                )
                            }
                        ),
                        onExpandedChange = onMenuExpandedChange
                    ) {
                        Icon(
                            imageVector = MiuixIcons.Normal.More,
                            contentDescription = "更多操作"
                        )
                    }
                }
            }
        }
    ) { scrollBehavior, topPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(
                    top = topPadding,
                    bottom = bottomInnerPadding + 16.dp
                )
                .nestedScroll(scrollBehavior.nestedScrollConnection)
        ) {
            // 1. 轻量元信息栏 (时间、来源渠道、类型与字符数) - 简洁置于内容上方
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 20.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.weight(1f, fill = false)
                ) {
                    Text(
                        text = formatFullTime(clip.time),
                        fontSize = 12.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                    if (!sourceLabel.isNullOrBlank()) {
                        AppSourceBadge(
                            label = sourceLabel,
                            packageName = if (clip.sourceDevice == null || clip.sourceDevice == "本机") clip.sourcePackage else null
                        )
                    }
                }
                Text(
                    text = if (clip.isImage) "图片" else if (isEditing) "${editFieldState.text.length} 字符" else "${clip.text.length} 字符",
                    fontSize = 12.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.8f)
                )
            }

            // 2. 智能动作快捷胶囊 (如有识别出的智能动作)
            if (!isEditing && smartActions.isNotEmpty()) {
                FlowRow(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 20.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp)
                ) {
                    smartActions.forEach { action ->
                        SmartActionChip(
                            action = action,
                            onClick = { action.action(context) }
                        )
                    }
                }
            }

            // 3. 整个页面作为完整内容展示区域 (无额外卡片嵌套，直接满幅铺满)
            if (clip.isImage && !isEditing) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .padding(horizontal = 20.dp, vertical = 10.dp),
                    contentAlignment = Alignment.TopCenter
                ) {
                    ClipImageThumbnail(
                        imageRef = clip.imageRef,
                        rawText = clip.text,
                        maxHeight = 480.dp,
                        onClick = onPreviewImage
                    )
                }
            } else {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .verticalScroll(rememberScrollState())
                        .padding(horizontal = 20.dp, vertical = 10.dp)
                ) {
                    if (isEditing) {
                        BasicTextField(
                            state = editFieldState,
                            textStyle = MiuixTheme.textStyles.body1.copy(
                                fontSize = 16.sp,
                                lineHeight = 24.sp,
                                color = MiuixTheme.colorScheme.onSurface
                            ),
                            cursorBrush = SolidColor(MiuixTheme.colorScheme.primary),
                            modifier = Modifier
                                .fillMaxWidth()
                                .focusRequester(editFocusRequester)
                        )
                    } else {
                        SelectionContainer {
                            Text(
                                text = clip.text,
                                style = MiuixTheme.textStyles.body1.copy(
                                    fontSize = 16.sp,
                                    lineHeight = 24.sp,
                                    color = MiuixTheme.colorScheme.onSurface
                                ),
                                modifier = Modifier.fillMaxWidth()
                            )
                        }
                    }
                }
            }
        }
    }
}

/** 带有优雅淡入 + 弹性上浮 + 微缩放的卡片出现动画包装组件（仅未加载过的卡片执行） */
@Composable
private fun AnimatedRecordCardItem(
    modifier: Modifier = Modifier,
    shouldAnimate: Boolean = true,
    content: @Composable () -> Unit
) {
    if (!shouldAnimate) {
        Box(modifier = modifier) {
            content()
        }
        return
    }

    val visibleState = remember {
        MutableTransitionState(false).apply { targetState = true }
    }
    val transition = rememberTransition(visibleState, label = "card_enter_transition")

    val alpha by transition.animateFloat(
        transitionSpec = { tween(durationMillis = 260, easing = FastOutSlowInEasing) },
        label = "alpha"
    ) { if (it) 1f else 0f }

    val translationY by transition.animateFloat(
        transitionSpec = {
            spring(
                dampingRatio = Spring.DampingRatioMediumBouncy,
                stiffness = Spring.StiffnessLow
            )
        },
        label = "translationY"
    ) { if (it) 0f else 28f }

    val scale by transition.animateFloat(
        transitionSpec = {
            spring(
                dampingRatio = Spring.DampingRatioNoBouncy,
                stiffness = Spring.StiffnessMedium
            )
        },
        label = "scale"
    ) { if (it) 1f else 0.95f }

    Box(
        modifier = modifier
            .graphicsLayer {
                this.alpha = alpha
                this.translationY = translationY
                this.scaleX = scale
                this.scaleY = scale
            }
    ) {
        content()
    }
}

/** 单条记录卡片组件 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun RecordCard(
    clip: CapturedClip,
    isMultiSelectMode: Boolean,
    isSelected: Boolean,
    onToggleSelect: () -> Unit,
    onCardClick: () -> Unit,
    onLongClick: () -> Unit,
    onToggleFavorite: () -> Unit,
    onPreviewImage: () -> Unit,
    onCopy: () -> Unit,
    onShare: () -> Unit,
    onDelete: () -> Unit
) {
    val context = LocalContext.current
    val smartActions = remember(clip.text, clip.isImage) {
        if (!clip.isImage) SmartActionEngine.detectActions(context, clip.text) else emptyList()
    }

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .combinedClickable(
                onClick = onCardClick,
                onLongClick = onLongClick
            ),
        insideMargin = cardContentPadding
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            if (isMultiSelectMode) {
                Checkbox(
                    state = if (isSelected) ToggleableState.On else ToggleableState.Off,
                    onClick = onToggleSelect
                )
                Spacer(Modifier.width(10.dp))
            }

            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                modifier = Modifier.weight(1f)
            ) {
                Text(
                    text = formatTime(clip.time),
                    style = MiuixTheme.textStyles.footnote1,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                val sourceLabel = if (!clip.sourceDevice.isNullOrBlank() && clip.sourceDevice != "本机") {
                    clip.sourceDevice
                } else {
                    clip.sourceApp ?: "本机"
                }
                if (!sourceLabel.isNullOrBlank()) {
                    AppSourceBadge(
                        label = sourceLabel,
                        packageName = if (clip.sourceDevice == null || clip.sourceDevice == "本机") clip.sourcePackage else null
                    )
                }
            }

            // 收藏状态图标
            IconButton(
                onClick = onToggleFavorite,
                modifier = Modifier.size(28.dp)
            ) {
                Icon(
                    imageVector = if (clip.isFavorite) MiuixIcons.Normal.FavoritesFill else MiuixIcons.Normal.Favorites,
                    contentDescription = if (clip.isFavorite) "取消收藏" else "收藏",
                    tint = if (clip.isFavorite) Color(0xFFFF2D55) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.5f),
                    modifier = Modifier.size(18.dp)
                )
            }
        }

        Spacer(Modifier.height(6.dp))
        if (clip.isImage) {
            ClipImageThumbnail(
                imageRef = clip.imageRef,
                rawText = clip.text,
                onClick = onPreviewImage
            )
        } else {
            Text(
                text = clip.text,
                maxLines = 4,
                overflow = TextOverflow.Ellipsis,
                style = MiuixTheme.textStyles.body1
            )
        }

        // 智能动作识别胶囊
        if (smartActions.isNotEmpty()) {
            Spacer(Modifier.height(6.dp))
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
            ) {
                smartActions.forEach { action ->
                    SmartActionChip(
                        action = action,
                        onClick = { action.action(context) }
                    )
                }
            }
        }

        Spacer(Modifier.height(6.dp))
        HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
        Spacer(Modifier.height(2.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = if (clip.isImage) "图片" else "${clip.text.length} 字符",
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f)
            )
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                IconButton(
                    onClick = onShare,
                    modifier = Modifier.size(28.dp)
                ) {
                    Icon(
                        imageVector = LucideIcons.Share2,
                        contentDescription = "分享",
                        tint = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.size(15.dp)
                    )
                }
                IconButton(
                    onClick = onCopy,
                    modifier = Modifier.size(28.dp)
                ) {
                    Icon(
                        imageVector = LucideIcons.Copy,
                        contentDescription = "复制",
                        tint = MiuixTheme.colorScheme.primary,
                        modifier = Modifier.size(15.dp)
                    )
                }
                IconButton(
                    onClick = onDelete,
                    modifier = Modifier.size(28.dp)
                ) {
                    Icon(
                        imageVector = LucideIcons.Trash2,
                        contentDescription = "删除",
                        tint = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.size(15.dp)
                    )
                }
            }
        }
    }
}

/** 分类筛选胶囊标签 */
@Composable
private fun FilterChip(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(16.dp))
            .background(
                if (selected) MiuixTheme.colorScheme.primary
                else MiuixTheme.colorScheme.surfaceContainer
            )
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 6.dp)
    ) {
        Text(
            text = label,
            color = if (selected) Color.White else MiuixTheme.colorScheme.onSurface,
            fontSize = 13.sp
        )
    }
}

/** 激活的高级筛选条件指示与清除胶囊 (使用纯 Lucide 矢量图标，杜绝 Emoji) */
@Composable
private fun ActiveFilterChip(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(16.dp))
            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp)
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MiuixTheme.colorScheme.primary,
            modifier = Modifier.size(13.dp)
        )
        Text(
            text = label,
            color = MiuixTheme.colorScheme.primary,
            fontSize = 13.sp,
            fontWeight = androidx.compose.ui.text.font.FontWeight.Medium
        )
        Icon(
            imageVector = LucideIcons.X,
            contentDescription = "清除筛选",
            tint = MiuixTheme.colorScheme.primary.copy(alpha = 0.7f),
            modifier = Modifier.size(12.dp)
        )
    }
}

private fun copyToClipboard(context: Context, text: String) {
    clip.yixing.sync.service.ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("NexClip", text), rawText = text)
}

private fun shareText(context: Context, text: String) {
    val sendIntent = Intent().apply {
        action = Intent.ACTION_SEND
        putExtra(Intent.EXTRA_TEXT, text)
        type = "text/plain"
    }
    val shareIntent = Intent.createChooser(sendIntent, "分享剪贴板内容")
    context.startActivity(shareIntent)
}

private fun formatFullTime(millis: Long): String {
    val fmt = SimpleDateFormat("yyyy年M月d日 HH:mm:ss", Locale.getDefault())
    return fmt.format(Date(millis))
}

/** 按 今天 / 昨天 / M月d日(跨年加年份) 分组,保持新→旧顺序。 */
private fun groupByDay(clips: List<CapturedClip>): List<DayGroup> {
    if (clips.isEmpty()) return emptyList()

    val todayCal = Calendar.getInstance().apply {
        set(Calendar.HOUR_OF_DAY, 0)
        set(Calendar.MINUTE, 0)
        set(Calendar.SECOND, 0)
        set(Calendar.MILLISECOND, 0)
    }
    val todayStart = todayCal.timeInMillis
    val yesterdayStart = Calendar.getInstance().apply {
        timeInMillis = todayStart
        add(Calendar.DAY_OF_YEAR, -1)
    }.timeInMillis
    val thisYear = todayCal.get(Calendar.YEAR)
    val dateFmt = SimpleDateFormat("M月d日", Locale.getDefault())
    val dateFmtWithYear = SimpleDateFormat("yyyy年M月d日", Locale.getDefault())

    val groups = ArrayList<DayGroup>()
    var currentLabel: String? = null
    var currentItems = ArrayList<CapturedClip>()
    for (clip in clips) {
        val label = when {
            clip.time >= todayStart -> "今天"
            clip.time >= yesterdayStart -> "昨天"
            else -> {
                val cal = Calendar.getInstance().apply { timeInMillis = clip.time }
                if (cal.get(Calendar.YEAR) == thisYear) {
                    dateFmt.format(Date(clip.time))
                } else {
                    dateFmtWithYear.format(Date(clip.time))
                }
            }
        }
        if (label != currentLabel) {
            if (currentLabel != null) {
                val labelToAdd = currentLabel
                groups.add(DayGroup(labelToAdd, currentItems))
            }
            currentLabel = label
            currentItems = ArrayList()
        }
        currentItems.add(clip)
    }
    if (currentLabel != null) {
        groups.add(DayGroup(currentLabel, currentItems))
    }
    return groups
}

/**
 * 全屏搜索子页(参考 HyperCeiler 搜索页设计):
 * 覆盖整个界面(含顶栏/底栏),状态栏下方即胶囊搜索框 + 「取消」;
 * 无输入时展示搜索历史标签;输入时展示过滤结果。
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun SearchPage(
    sortForward: Boolean,
    query: String,
    onQueryChange: (String) -> Unit,
    onClose: () -> Unit,
    snackbarHostState: SnackbarHostState
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val captured by ClipboardMonitorService.captured.collectAsState()
    var history by remember { mutableStateOf(SyncSettings.searchHistory(context)) }

    // 结果列表:排序 + 按关键词过滤(忽略大小写)
    val displayList = remember(captured, sortForward, query) {
        val sorted = if (sortForward) captured else captured.reversed()
        if (query.isBlank()) {
            sorted
        } else {
            sorted.filter {
                it.text.contains(query, ignoreCase = true) ||
                (it.sourceApp?.contains(query, ignoreCase = true) == true) ||
                (it.sourceDevice?.contains(query, ignoreCase = true) == true) ||
                (it.sourcePackage?.contains(query, ignoreCase = true) == true)
            }
        }
    }

    val searchState = remember { TextFieldState(query) }
    val focusRequester = remember { FocusRequester() }
    var searchPreviewImageClip by remember { mutableStateOf<CapturedClip?>(null) }
    LaunchedEffect(searchState.text) {
        onQueryChange(searchState.text.toString())
    }
    LaunchedEffect(Unit) {
        focusRequester.requestFocus()
    }

    val isKeyboardVisible = WindowInsets.isImeVisible
    val keyboardController = LocalSoftwareKeyboardController.current
    var searchBackProgress by remember { mutableFloatStateOf(0f) }
    var searchBackEdge by remember { mutableIntStateOf(BackEventCompat.EDGE_LEFT) }
    var isSearchBackActive by remember { mutableStateOf(false) }

    PredictiveBackHandler(enabled = true) { progress ->
        if (isKeyboardVisible) {
            keyboardController?.hide()
            return@PredictiveBackHandler
        }
        if (!SyncSettings.predictiveBackEnabled(context)) {
            onClose()
            return@PredictiveBackHandler
        }
        try {
            isSearchBackActive = true
            progress.collect { event ->
                searchBackProgress = event.progress
                searchBackEdge = event.swipeEdge
            }
            onClose()
        } catch (e: CancellationException) {
        } finally {
            isSearchBackActive = false
            searchBackProgress = 0f
        }
    }

    val screenCornerRadius = rememberScreenCornerRadius()

    Box(
        modifier = Modifier
            .fillMaxSize()
            .predictiveBackAnimation(
                progress = searchBackProgress,
                edge = searchBackEdge,
                enabled = isSearchBackActive,
                screenCornerRadius = screenCornerRadius
            )
            .background(MiuixTheme.colorScheme.surface)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .windowInsetsPadding(WindowInsets.statusBars)
        ) {
            var barVisible by remember { mutableStateOf(false) }
            val searchAnimatedClipIds = remember { mutableSetOf<String>() }
            LaunchedEffect(Unit) { barVisible = true }
            AnimatedVisibility(
                visible = barVisible,
                enter = fadeIn(tween(260)) + slideInVertically(
                    initialOffsetY = { -it / 2 },
                    animationSpec = tween(260)
                )
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 16.dp, end = 16.dp, top = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    TextField(
                        state = searchState,
                        label = "搜索",
                        useLabelAsPlaceholder = true,
                        cornerRadius = 24.dp,
                        leadingIcon = {
                            Icon(
                                imageVector = MiuixIcons.Normal.Search,
                                contentDescription = null,
                                modifier = Modifier.padding(start = 14.dp, end = 8.dp)
                            )
                        },
                        modifier = Modifier
                            .weight(1f)
                            .focusRequester(focusRequester),
                        trailingIcon = {
                            if (searchState.text.isNotEmpty()) {
                                IconButton(
                                    onClick = {
                                        searchState.edit { replace(0, length, "") }
                                    }
                                ) {
                                    Icon(
                                        imageVector = MiuixIcons.Basic.Close,
                                        contentDescription = "清除"
                                    )
                                }
                            }
                        }
                    )
                    var cancelVisible by remember { mutableStateOf(false) }
                    LaunchedEffect(Unit) { delay(140); cancelVisible = true }
                    AnimatedVisibility(
                        visible = cancelVisible,
                        enter = fadeIn(tween(220)) + slideInHorizontally(
                            initialOffsetX = { it / 2 },
                            animationSpec = tween(220)
                        )
                    ) {
                        Text(
                            text = "取消",
                            color = MiuixTheme.colorScheme.primary,
                            modifier = Modifier
                                .clickable(onClick = onClose)
                                .padding(start = 12.dp, top = 4.dp, bottom = 4.dp)
                        )
                    }
                }
            }

            if (query.isBlank()) {
                if (history.isNotEmpty()) {
                    Text(
                        text = "搜索记录",
                        style = MiuixTheme.textStyles.title3,
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier.padding(start = 16.dp, top = 18.dp, bottom = 10.dp)
                    )
                    FlowRow(
                        modifier = Modifier.padding(horizontal = 16.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        history.forEach { term ->
                            SearchHistoryChip(term = term) {
                                onQueryChange(term)
                            }
                        }
                    }
                    Text(
                        text = "清除搜索记录",
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier
                            .align(Alignment.CenterHorizontally)
                            .padding(top = 24.dp)
                            .clickable {
                                SyncSettings.clearSearchHistory(context)
                                history = emptyList()
                            }
                    )
                } else {
                    Text(
                        text = "暂无搜索记录",
                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                        modifier = Modifier
                            .align(Alignment.CenterHorizontally)
                            .padding(top = 32.dp)
                    )
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical(),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = 8.dp,
                        bottom = 8.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    if (displayList.isEmpty()) {
                        item {
                            RecordEmptyStateCard(
                                icon = MiuixIcons.Normal.Search,
                                iconTint = MiuixTheme.colorScheme.primary,
                                title = "未找到匹配记录",
                                description = if (searchState.text.isNotEmpty()) "未找到与「${searchState.text}」相关的记录\n可尝试更换关键词搜索" else "请输入关键词开始搜索"
                            )
                        }
                    } else {
                        groupByDay(displayList).forEach { group ->
                            item(key = "header_" + group.label) {
                                Text(
                                    text = group.label,
                                    style = MiuixTheme.textStyles.title3,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                                    modifier = Modifier.padding(start = 8.dp, top = 10.dp, bottom = 2.dp)
                                )
                            }
                            items(group.items, key = { it.id }) { clip ->
                                val shouldAnimate = remember(clip.id) {
                                    if (!searchAnimatedClipIds.contains(clip.id)) {
                                        searchAnimatedClipIds.add(clip.id)
                                        true
                                    } else {
                                        false
                                    }
                                }
                                AnimatedRecordCardItem(
                                    modifier = Modifier.animateItem(),
                                    shouldAnimate = shouldAnimate
                                ) {
                                    RecordCard(
                                        clip = clip,
                                        isMultiSelectMode = false,
                                        isSelected = false,
                                        onToggleSelect = {},
                                        onCardClick = {
                                            if (clip.isImage) {
                                                searchPreviewImageClip = clip
                                            } else {
                                                copyToClipboard(context, clip.text)
                                                scope.launch {
                                                    snackbarHostState.showAppSnack("已复制", SnackType.Success)
                                                }
                                            }
                                        },
                                        onLongClick = {},
                                        onToggleFavorite = {
                                            ClipboardMonitorService.toggleFavorite(context, clip)
                                        },
                                        onPreviewImage = {
                                            searchPreviewImageClip = clip
                                        },
                                        onCopy = {
                                            scope.launch {
                                                if (clip.isImage) {
                                                    val ok = ImageLoader.copyImageToClipboard(context, clip.imageRef, clip.text)
                                                    snackbarHostState.showAppSnack(if (ok) "已复制图片" else "复制失败", if (ok) SnackType.Success else SnackType.Error)
                                                } else {
                                                    copyToClipboard(context, clip.text)
                                                    snackbarHostState.showAppSnack("已复制", SnackType.Success)
                                                }
                                            }
                                        },
                                        onShare = {
                                            scope.launch {
                                                if (clip.isImage) {
                                                    val ok = ImageLoader.shareImage(context, clip.imageRef, clip.text)
                                                    if (!ok) snackbarHostState.showAppSnack("分享失败", SnackType.Error)
                                                } else {
                                                    shareText(context, clip.text)
                                                }
                                            }
                                        },
                                        onDelete = {
                                            val index = captured.indexOf(clip)
                                            ClipboardMonitorService.deleteAt(context, index)
                                            scope.launch {
                                                val result = snackbarHostState.showAppSnack(
                                                    "已删除该记录", SnackType.Success, actionLabel = "撤销"
                                                )
                                                if (result == SnackbarResult.ActionPerformed) {
                                                    ClipboardMonitorService.restoreAt(context, index, clip)
                                                }
                                            }
                                        }
                                    )
                                }
                            }
                        }
                    }
                    item {
                        Spacer(Modifier.height(8.dp))
                    }
                }
            }
        }

        // 搜索模式下的大图全屏预览
        FullscreenImagePreviewDialog(
            show = searchPreviewImageClip != null,
            imageRef = searchPreviewImageClip?.imageRef,
            rawText = searchPreviewImageClip?.text,
            onDismissRequest = { searchPreviewImageClip = null }
        )
    }
}

/** 搜索历史标签(白色圆角胶囊,点击直接搜索)。 */
@Composable
private fun SearchHistoryChip(term: String, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(10.dp))
            .background(MiuixTheme.colorScheme.surfaceContainer)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp)
    ) {
        Text(term, style = MiuixTheme.textStyles.body2)
    }
}

/** 剪贴板来源应用/设备标识胶囊 */
@Composable
fun AppSourceBadge(
    label: String,
    packageName: String?,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val iconBitmap = remember(packageName) {
        AppSourceHelper.getAppIconBitmap(context, packageName)
    }

    Row(
        modifier = modifier
            .clip(RoundedCornerShape(6.dp))
            .background(MiuixTheme.colorScheme.surfaceContainerHigh.copy(alpha = 0.8f))
            .padding(horizontal = 6.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (iconBitmap != null) {
            androidx.compose.foundation.Image(
                bitmap = iconBitmap,
                contentDescription = null,
                modifier = Modifier
                    .size(12.dp)
                    .clip(RoundedCornerShape(2.dp))
            )
            Spacer(Modifier.width(4.dp))
        } else {
            val devIcon = resolveDeviceIcon(label)
            Icon(
                imageVector = devIcon,
                contentDescription = null,
                tint = MiuixTheme.colorScheme.primary,
                modifier = Modifier.size(11.dp)
            )
            Spacer(Modifier.width(3.dp))
        }
        Text(
            text = label,
            fontSize = 11.sp,
            color = MiuixTheme.colorScheme.onBackgroundVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

/** 多选底部操作栏带计数徽标按钮 */
/** 多选底部操作栏带计数徽标按钮 */
@Composable
private fun MultiSelectActionButton(
    icon: ImageVector,
    tint: Color,
    badgeCount: Int,
    label: String,
    onClick: () -> Unit
) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier.padding(horizontal = 2.dp)
    ) {
        IconButton(
            onClick = onClick,
            enabled = badgeCount > 0
        ) {
            Icon(
                imageVector = icon,
                contentDescription = label,
                tint = tint,
                modifier = Modifier.size(20.dp)
            )
        }

        androidx.compose.animation.AnimatedVisibility(
            visible = badgeCount > 0,
            enter = androidx.compose.animation.scaleIn() + androidx.compose.animation.fadeIn(),
            exit = androidx.compose.animation.scaleOut() + androidx.compose.animation.fadeOut(),
            modifier = Modifier
                .align(Alignment.TopEnd)
                .offset(x = (-2).dp, y = 2.dp)
        ) {
            Box(
                modifier = Modifier
                    .defaultMinSize(minWidth = 16.dp, minHeight = 16.dp)
                    .clip(CircleShape)
                    .background(MiuixTheme.colorScheme.primary)
                    .padding(horizontal = 4.dp, vertical = 1.dp),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    text = if (badgeCount > 99) "99+" else "$badgeCount",
                    color = Color.White,
                    fontSize = 9.sp,
                    fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center
                )
            }
        }
    }
}


