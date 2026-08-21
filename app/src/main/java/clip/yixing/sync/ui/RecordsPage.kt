package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.isImeVisible
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
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
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.formatTime
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Card
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
import top.yukonga.miuix.kmp.menu.OverlayIconDropdownMenu
import top.yukonga.miuix.kmp.overlay.OverlayBottomSheet
import top.yukonga.miuix.kmp.overlay.OverlayDialog
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

@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun RecordsPage(
    scrollBehavior: ScrollBehavior,
    topPadding: Dp,
    bottomInnerPadding: Dp,
    sortForward: Boolean,
    snackbarHostState: SnackbarHostState,
    enterMultiSelectTrigger: Int = 0,
    clearDialogTrigger: Int = 0,
    onMultiSelectStateChanged: (inMultiSelect: Boolean, selectedCount: Int, totalCount: Int, toggleSelectAll: () -> Unit, exitMultiSelect: () -> Unit) -> Unit = { _, _, _, _, _ -> },
    onOverlayActiveChanged: (Boolean) -> Unit = {}
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val captured by ClipboardMonitorService.captured.collectAsState()

    var currentFilter by remember { mutableStateOf(ClipFilterTab.All) }
    var currentDateFilter by remember { mutableStateOf(DateFilterOption.All) }
    var isMultiSelectMode by remember { mutableStateOf(false) }
    var selectedClips by remember { mutableStateOf(setOf<CapturedClip>()) }

    var showClearDialog by remember { mutableStateOf(false) }
    var keepFavoritesOnClear by remember { mutableStateOf(true) }

    // 详情/编辑弹层状态
    var activeDetailClip by remember { mutableStateOf<CapturedClip?>(null) }
    var isEditingInSheet by remember { mutableStateOf(false) }
    val editFieldState = remember { TextFieldState("") }

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

    LaunchedEffect(enterMultiSelectTrigger) {
        if (enterMultiSelectTrigger > 0) {
            isMultiSelectMode = true
        }
    }

    LaunchedEffect(clearDialogTrigger) {
        if (clearDialogTrigger > 0) {
            showClearDialog = true
        }
    }

    LaunchedEffect(activeDetailClip, isMultiSelectMode, showClearDialog, showDatePickerDialog) {
        onOverlayActiveChanged(activeDetailClip != null || isMultiSelectMode || showClearDialog || showDatePickerDialog)
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

    // 过滤与排序 (sortForward: true=时间正序[最新在前], false=时间倒序[最早在前])
    val filteredList = remember(captured, currentFilter, currentDateFilter, customDate, sortForward, todayStart, yesterdayStart, last7DaysStart, last30DaysStart) {
        val typeFiltered = when (currentFilter) {
            ClipFilterTab.All -> captured
            ClipFilterTab.Favorite -> captured.filter { it.isFavorite }
            ClipFilterTab.Link -> captured.filter { it.isLink }
            ClipFilterTab.Image -> captured.filter { it.isImage }
            ClipFilterTab.Text -> captured.filter { !it.isLink && !it.isImage }
        }
        val dateFiltered = when (currentDateFilter) {
            DateFilterOption.All -> typeFiltered
            DateFilterOption.Today -> typeFiltered.filter { it.time >= todayStart }
            DateFilterOption.Yesterday -> typeFiltered.filter { it.time in yesterdayStart until todayStart }
            DateFilterOption.Last7Days -> typeFiltered.filter { it.time >= last7DaysStart }
            DateFilterOption.Last30Days -> typeFiltered.filter { it.time >= last30DaysStart }
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
                    typeFiltered.filter { it.time in start..end }
                } else {
                    typeFiltered
                }
            }
        }
        if (sortForward) dateFiltered else dateFiltered.reversed()
    }
    val groups = remember(filteredList) { groupByDay(filteredList) }

    // 将多选状态与全选/退出回调同步给标题栏 (TopBar)
    LaunchedEffect(isMultiSelectMode, selectedClips.size, filteredList.size) {
        onMultiSelectStateChanged(
            isMultiSelectMode,
            selectedClips.size,
            filteredList.size,
            {
                selectedClips = if (selectedClips.size == filteredList.size) {
                    emptySet()
                } else {
                    filteredList.toSet()
                }
            },
            {
                isMultiSelectMode = false
                selectedClips = emptySet()
            }
        )
    }

    // 退出多选模式的返回拦截
    BackHandler(enabled = isMultiSelectMode) {
        isMultiSelectMode = false
        selectedClips = emptySet()
    }

    Box(modifier = Modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .overScrollVertical()
                .nestedScroll(scrollBehavior.nestedScrollConnection),
            contentPadding = PaddingValues(
                start = 16.dp,
                end = 16.dp,
                top = topPadding + 8.dp,
                bottom = if (isMultiSelectMode) bottomInnerPadding + 64.dp else bottomInnerPadding
            ),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            // 筛选栏 (类型 + 日期横向滚动胶囊)
            item {
                SectionBlock(
                    title = "筛选记录",
                    insideMargin = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
                ) {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        // 1. 类型筛选
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .horizontalScroll(rememberScrollState()),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
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
                        }

                        // 2. 日期筛选 (左侧横向滚动快捷时间段 + 右侧固定日历图标按钮)
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            // 左侧横向滚动快捷时间段
                            Row(
                                modifier = Modifier
                                    .weight(1f)
                                    .horizontalScroll(rememberScrollState()),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                listOf(
                                    DateFilterOption.All,
                                    DateFilterOption.Today,
                                    DateFilterOption.Yesterday,
                                    DateFilterOption.Last7Days,
                                    DateFilterOption.Last30Days
                                ).forEach { dateOpt ->
                                    val count = when (dateOpt) {
                                        DateFilterOption.All -> captured.size
                                        DateFilterOption.Today -> captured.count { it.time >= todayStart }
                                        DateFilterOption.Yesterday -> captured.count { it.time in yesterdayStart until todayStart }
                                        DateFilterOption.Last7Days -> captured.count { it.time >= last7DaysStart }
                                        DateFilterOption.Last30Days -> captured.count { it.time >= last30DaysStart }
                                        else -> 0
                                    }
                                    FilterChip(
                                        label = "${dateOpt.label} ($count)",
                                        selected = currentDateFilter == dateOpt,
                                        onClick = { currentDateFilter = dateOpt }
                                    )
                                }

                                // 如果当前处于自定义指定日期模式，也在胶囊列表里呈现该选中胶囊
                                if (currentDateFilter == DateFilterOption.Custom && customDate != null) {
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
                                    val count = captured.count { it.time in start..end }
                                    FilterChip(
                                        label = "${cm}月${cd}日 ($count)",
                                        selected = true,
                                        onClick = {
                                            pickerYear = cy
                                            pickerMonth = cm
                                            pickerDay = cd
                                            showDatePickerDialog = true
                                        }
                                    )
                                }
                            }

                            Spacer(Modifier.width(8.dp))

                            // 右侧固定：指定日期日历图标按钮
                            Box(
                                modifier = Modifier
                                    .size(32.dp)
                                    .clip(CircleShape)
                                    .background(
                                        if (currentDateFilter == DateFilterOption.Custom) MiuixTheme.colorScheme.primary
                                        else MiuixTheme.colorScheme.surfaceContainer
                                    )
                                    .clickable {
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
                                    },
                                contentAlignment = Alignment.Center
                            ) {
                                Icon(
                                    imageVector = MiuixIcons.Normal.Months,
                                    contentDescription = "指定日期",
                                    tint = if (currentDateFilter == DateFilterOption.Custom) Color.White else MiuixTheme.colorScheme.onSurface,
                                    modifier = Modifier.size(16.dp)
                                )
                            }
                        }
                    }
                }
            }

            // 3. 记录列表主体
            if (captured.isEmpty()) {
                item {
                    SectionBlock(title = "记录列表") {
                        Text("暂无捕获记录")
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "开启「持续监听剪贴板」后，复制的文字会自动记录到这里",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                }
            } else if (filteredList.isEmpty()) {
                item {
                    SectionBlock(title = "记录列表") {
                        Text("当前分类下暂无记录")
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "可切换至「全部」标签查看所有记录",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
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
                        RecordCard(
                            clip = clip,
                            isMultiSelectMode = isMultiSelectMode,
                            isSelected = isSelected,
                            onToggleSelect = {
                                selectedClips = if (isSelected) selectedClips - clip else selectedClips + clip
                            },
                            onCardClick = {
                                if (isMultiSelectMode) {
                                    selectedClips = if (isSelected) selectedClips - clip else selectedClips + clip
                                } else {
                                    activeDetailClip = clip
                                    editFieldState.edit { replace(0, length, clip.text) }
                                    isEditingInSheet = false
                                }
                            },
                            onLongClick = {
                                if (!isMultiSelectMode) {
                                    isMultiSelectMode = true
                                    selectedClips = setOf(clip)
                                }
                            },
                            onToggleFavorite = {
                                ClipboardMonitorService.toggleFavorite(context, clip)
                            },
                            onCopy = {
                                copyToClipboard(context, clip.text)
                                scope.launch {
                                    snackbarHostState.showAppSnack("已复制", SnackType.Success)
                                }
                            },
                            onShare = {
                                shareText(context, clip.text)
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

        // 多选模式底部操作栏 (紧凑胶囊悬浮阴影)
        AnimatedVisibility(
            visible = isMultiSelectMode,
            enter = fadeIn() + slideInVertically(initialOffsetY = { it }),
            exit = fadeOut() + slideOutVertically(targetOffsetY = { it }),
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 24.dp + WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding())
        ) {
            Card(
                modifier = Modifier
                    .wrapContentWidth()
                    .shadow(
                        elevation = 16.dp,
                        shape = CircleShape,
                        spotColor = Color.Black.copy(alpha = 0.25f),
                        ambientColor = Color.Black.copy(alpha = 0.12f)
                    )
                    .clip(CircleShape),
                insideMargin = PaddingValues(horizontal = 24.dp, vertical = 6.dp)
            ) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(28.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(
                        onClick = {
                            if (selectedClips.isEmpty()) return@IconButton
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
                        },
                        enabled = selectedClips.isNotEmpty()
                    ) {
                        Icon(
                            imageVector = MiuixIcons.Normal.FavoritesFill,
                            contentDescription = "批量收藏",
                            tint = if (selectedClips.isNotEmpty()) Color(0xFFFFCC00) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f)
                        )
                    }

                    IconButton(
                        onClick = {
                            if (selectedClips.isEmpty()) return@IconButton
                            val merged = selectedClips.sortedByDescending { it.time }.joinToString("\n\n") { it.text }
                            copyToClipboard(context, merged)
                            val count = selectedClips.size
                            isMultiSelectMode = false
                            selectedClips = emptySet()
                            scope.launch {
                                snackbarHostState.showAppSnack("已合并复制 $count 条记录", SnackType.Success)
                            }
                        },
                        enabled = selectedClips.isNotEmpty()
                    ) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Copy,
                            contentDescription = "合并复制",
                            tint = if (selectedClips.isNotEmpty()) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f)
                        )
                    }

                    IconButton(
                        onClick = {
                            if (selectedClips.isEmpty()) return@IconButton
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
                        },
                        enabled = selectedClips.isNotEmpty()
                    ) {
                        Icon(
                            imageVector = MiuixIcons.Normal.Delete,
                            contentDescription = "批量删除",
                            tint = if (selectedClips.isNotEmpty()) MiuixTheme.colorScheme.error else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.4f)
                        )
                    }
                }
            }
        }
    }

    // 清空全部记录的二次确认弹窗
    OverlayDialog(
        show = showClearDialog,
        title = "清空记录",
        summary = if (captured.any { it.isFavorite }) "是否清空记录？默认将保留已收藏的项目。" else "将删除本地全部捕获记录，此操作可撤销。",
        onDismissRequest = { showClearDialog = false }
    ) {
        Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
            if (captured.any { it.isFavorite }) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { keepFavoritesOnClear = !keepFavoritesOnClear }
                        .padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        modifier = Modifier
                            .size(20.dp)
                            .clip(CircleShape)
                            .background(
                                if (keepFavoritesOnClear) MiuixTheme.colorScheme.primary
                                else MiuixTheme.colorScheme.surfaceContainerHigh
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        if (keepFavoritesOnClear) {
                            Text("✓", color = Color.White, fontSize = 12.sp)
                        }
                    }
                    Spacer(Modifier.width(8.dp))
                    Text("保留已收藏记录 (${captured.count { it.isFavorite }} 条)")
                }
                Spacer(Modifier.height(8.dp))
            }
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
    OverlayDialog(
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

    // 记录详情与快速编辑弹层
    activeDetailClip?.let { clip ->
        val smartActions = remember(clip.text) { detectSmartActions(clip.text) }
        OverlayBottomSheet(
            show = true,
            title = if (isEditingInSheet) "编辑记录" else "记录详情",
            onDismissRequest = {
                activeDetailClip = null
                isEditingInSheet = false
            }
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .windowInsetsPadding(WindowInsets.navigationBars)
                    .padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 24.dp)
            ) {
                // 顶部信息与操作菜单行
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = formatFullTime(clip.time),
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 12.sp
                        )
                        Spacer(Modifier.height(2.dp))
                        Text(
                            text = "${clip.text.length} 字符",
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 12.sp
                        )
                    }

                    if (!isEditingInSheet) {
                        // 快捷收藏切换
                        IconButton(
                            onClick = {
                                ClipboardMonitorService.toggleFavorite(context, clip)
                                activeDetailClip = clip.copy(isFavorite = !clip.isFavorite)
                            }
                        ) {
                            Icon(
                                imageVector = if (clip.isFavorite) MiuixIcons.Normal.FavoritesFill else MiuixIcons.Normal.Favorites,
                                contentDescription = if (clip.isFavorite) "取消收藏" else "收藏",
                                tint = if (clip.isFavorite) Color(0xFFFFCC00) else MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }

                        // 集成更多功能气泡菜单
                        OverlayIconDropdownMenu(
                            entry = DropdownEntry(
                                items = listOf(
                                    DropdownItem(
                                        text = "复制内容",
                                        onClick = {
                                            copyToClipboard(context, clip.text)
                                            scope.launch { snackbarHostState.showAppSnack("已复制", SnackType.Success) }
                                        }
                                    ),
                                    DropdownItem(
                                        text = "编辑文本",
                                        onClick = {
                                            editFieldState.edit { replace(0, length, clip.text) }
                                            isEditingInSheet = true
                                        }
                                    ),
                                    DropdownItem(
                                        text = "系统分享",
                                        onClick = {
                                            shareText(context, clip.text)
                                        }
                                    ),
                                    DropdownItem(
                                        text = "推送到所有设备",
                                        onClick = {
                                            val url = SyncSettings.serverUrl(context)
                                            if (url.isBlank()) {
                                                scope.launch { snackbarHostState.showAppSnack("请先配置服务器", SnackType.Info) }
                                            } else {
                                                scope.launch {
                                                    try {
                                                        val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                                        withContext(Dispatchers.IO) {
                                                            api.putText(
                                                                text = clip.text,
                                                                deviceId = SyncSettings.ensureDeviceId(context),
                                                                deviceName = SyncSettings.deviceName(context)
                                                            )
                                                        }
                                                        snackbarHostState.showAppSnack("已推送到所有设备", SnackType.Success)
                                                    } catch (e: Exception) {
                                                        snackbarHostState.showAppSnack(e.message ?: "推送失败", SnackType.Error)
                                                    }
                                                }
                                            }
                                        }
                                    ),
                                    DropdownItem(
                                        text = "删除此记录",
                                        onClick = {
                                            activeDetailClip = null
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
                                )
                            ),
                            onExpandedChange = { expanded -> onOverlayActiveChanged(expanded) }
                        ) {
                            Icon(
                                imageVector = MiuixIcons.Normal.More,
                                contentDescription = "更多操作"
                            )
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))

                if (isEditingInSheet) {
                    // 编辑模式
                    TextField(
                        state = editFieldState,
                        label = "记录内容",
                        useLabelAsPlaceholder = true,
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = 120.dp, max = 360.dp)
                    )
                    Spacer(Modifier.height(12.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        Button(
                            onClick = { isEditingInSheet = false },
                            modifier = Modifier.weight(1f)
                        ) {
                            Text("取消")
                        }
                        Button(
                            onClick = {
                                val newText = editFieldState.text.toString().trim()
                                if (newText.isNotBlank()) {
                                    ClipboardMonitorService.updateClip(context, clip, newText)
                                    activeDetailClip = clip.copy(text = newText)
                                    isEditingInSheet = false
                                    scope.launch {
                                        snackbarHostState.showAppSnack("已保存修改", SnackType.Success)
                                    }
                                }
                            },
                            modifier = Modifier.weight(1f)
                        ) {
                            Text("保存")
                        }
                    }
                } else {
                    // 查看模式: 完整文本区域 (支持超出范围垂直滑动)
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = 60.dp, max = 380.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(MiuixTheme.colorScheme.surfaceContainer)
                            .verticalScroll(rememberScrollState())
                            .padding(12.dp)
                    ) {
                        Text(
                            text = clip.text,
                            style = MiuixTheme.textStyles.body1
                        )
                    }

                    // 智能动作按钮组 (识别到链接/电话/邮箱)
                    if (smartActions.urls.isNotEmpty() || smartActions.phones.isNotEmpty() || smartActions.emails.isNotEmpty()) {
                        Spacer(Modifier.height(10.dp))
                        FlowRow(
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            smartActions.urls.forEach { url ->
                                SmartActionChip(
                                    icon = MiuixIcons.Normal.Link,
                                    label = "打开链接",
                                    onClick = { openUrl(context, url) }
                                )
                            }
                            smartActions.phones.forEach { phone ->
                                SmartActionChip(
                                    icon = MiuixIcons.Normal.Phone,
                                    label = "拨打电话: $phone",
                                    onClick = { dialNumber(context, phone) }
                                )
                            }
                            smartActions.emails.forEach { email ->
                                SmartActionChip(
                                    icon = MiuixIcons.Normal.Share,
                                    label = "发送邮件: $email",
                                    onClick = { sendEmail(context, email) }
                                )
                            }
                        }
                    }
                }
                Spacer(Modifier.height(12.dp))
            }
        }
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
    onCopy: () -> Unit,
    onShare: () -> Unit,
    onDelete: () -> Unit
) {
    val context = LocalContext.current
    val smartActions = remember(clip.text) { detectSmartActions(clip.text) }

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
                Box(
                    modifier = Modifier
                        .size(22.dp)
                        .clip(CircleShape)
                        .background(
                            if (isSelected) MiuixTheme.colorScheme.primary
                            else MiuixTheme.colorScheme.surfaceContainerHigh
                        )
                        .clickable(onClick = onToggleSelect),
                    contentAlignment = Alignment.Center
                ) {
                    if (isSelected) {
                        Text("✓", color = Color.White, fontSize = 13.sp)
                    }
                }
                Spacer(Modifier.width(10.dp))
            }

            Text(
                text = formatTime(clip.time),
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                modifier = Modifier.weight(1f)
            )

            // 收藏状态图标
            IconButton(
                onClick = onToggleFavorite,
                modifier = Modifier.size(28.dp)
            ) {
                Icon(
                    imageVector = if (clip.isFavorite) MiuixIcons.Normal.FavoritesFill else MiuixIcons.Normal.Favorites,
                    contentDescription = if (clip.isFavorite) "取消收藏" else "收藏",
                    tint = if (clip.isFavorite) Color(0xFFFFCC00) else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.5f),
                    modifier = Modifier.size(18.dp)
                )
            }

            Spacer(Modifier.width(4.dp))
            Text(
                text = "复制",
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.primary,
                modifier = Modifier
                    .clickable(onClick = onCopy)
                    .padding(horizontal = 4.dp, vertical = 2.dp)
            )
        }

        Spacer(Modifier.height(6.dp))
        Text(
            text = clip.text,
            maxLines = 4,
            overflow = TextOverflow.Ellipsis,
            style = MiuixTheme.textStyles.body1
        )

        // 智能动作识别胶囊
        if (smartActions.urls.isNotEmpty() || smartActions.phones.isNotEmpty()) {
            Spacer(Modifier.height(6.dp))
            Row(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                smartActions.urls.firstOrNull()?.let { url ->
                    SmartActionMiniChip(
                        icon = MiuixIcons.Normal.Link,
                        label = "打开链接",
                        onClick = { openUrl(context, url) }
                    )
                }
                smartActions.phones.firstOrNull()?.let { phone ->
                    SmartActionMiniChip(
                        icon = MiuixIcons.Normal.Phone,
                        label = "拨号",
                        onClick = { dialNumber(context, phone) }
                    )
                }
            }
        }

        Spacer(Modifier.height(8.dp))
        HorizontalDivider(color = MiuixTheme.colorScheme.dividerLine, thickness = Dp.Hairline)
        Spacer(Modifier.height(4.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "分享",
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                modifier = Modifier
                    .clickable(onClick = onShare)
                    .padding(horizontal = 4.dp, vertical = 2.dp)
            )
            Text(
                text = "删除",
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                modifier = Modifier
                    .clickable(onClick = onDelete)
                    .padding(horizontal = 4.dp, vertical = 2.dp)
            )
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

/** 智能动作大胶囊按钮 */
@Composable
private fun SmartActionChip(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(8.dp))
            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MiuixTheme.colorScheme.primary,
            modifier = Modifier.size(14.dp)
        )
        Spacer(Modifier.width(6.dp))
        Text(
            text = label,
            color = MiuixTheme.colorScheme.primary,
            fontSize = 12.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

/** 卡片内微型智能动作按钮 */
@Composable
private fun SmartActionMiniChip(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.1f))
            .clickable(onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(
            imageVector = icon,
            contentDescription = null,
            tint = MiuixTheme.colorScheme.primary,
            modifier = Modifier.size(12.dp)
        )
        Spacer(Modifier.width(4.dp))
        Text(
            text = label,
            color = MiuixTheme.colorScheme.primary,
            fontSize = 11.sp
        )
    }
}

private fun copyToClipboard(context: Context, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText("SyncClipboard", text))
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

private fun openUrl(context: Context, url: String) {
    runCatching {
        val target = if (url.startsWith("http://", ignoreCase = true) || url.startsWith("https://", ignoreCase = true)) url else "https://$url"
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse(target))
        context.startActivity(intent)
    }
}

private fun dialNumber(context: Context, number: String) {
    runCatching {
        val intent = Intent(Intent.ACTION_DIAL, Uri.parse("tel:${number.trim()}"))
        context.startActivity(intent)
    }
}

private fun sendEmail(context: Context, email: String) {
    runCatching {
        val intent = Intent(Intent.ACTION_SENDTO, Uri.parse("mailto:${email.trim()}"))
        context.startActivity(intent)
    }
}

/** 智能动作识别 */
private data class SmartActions(
    val urls: List<String> = emptyList(),
    val phones: List<String> = emptyList(),
    val emails: List<String> = emptyList(),
)

private fun detectSmartActions(text: String): SmartActions {
    if (text.length > 5000) return SmartActions()
    val urlRegex = Regex("https?://[\\w\\-._~:/?#\\[\\]@!$&'()*+,;=%]+", RegexOption.IGNORE_CASE)
    val phoneRegex = Regex("(?:\\+?86)?1[3-9]\\d{9}|\\b\\d{3,4}-\\d{7,8}\\b")
    val emailRegex = Regex("[a-zA-Z0-9_.+-]+@[a-zA-Z0-9-]+\\.[a-zA-Z0-9-.]+")

    val urls = urlRegex.findAll(text).map { it.value }.distinct().take(2).toList()
    val phones = phoneRegex.findAll(text).map { it.value }.distinct().take(2).toList()
    val emails = emailRegex.findAll(text).map { it.value }.distinct().take(2).toList()

    return SmartActions(urls, phones, emails)
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
            sorted.filter { it.text.contains(query, ignoreCase = true) }
        }
    }

    val searchState = remember { TextFieldState(query) }
    val focusRequester = remember { FocusRequester() }
    LaunchedEffect(searchState.text) {
        onQueryChange(searchState.text.toString())
    }
    LaunchedEffect(Unit) {
        focusRequester.requestFocus()
    }

    val isKeyboardVisible = WindowInsets.isImeVisible
    val keyboardController = LocalSoftwareKeyboardController.current
    BackHandler {
        if (isKeyboardVisible) {
            keyboardController?.hide()
        } else {
            onClose()
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(MiuixTheme.colorScheme.surface)
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .windowInsetsPadding(WindowInsets.statusBars)
        ) {
            var barVisible by remember { mutableStateOf(false) }
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
                            Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                                Text("未找到匹配记录")
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = "换个关键词试试",
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
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
                                RecordCard(
                                    clip = clip,
                                    isMultiSelectMode = false,
                                    isSelected = false,
                                    onToggleSelect = {},
                                    onCardClick = {
                                        copyToClipboard(context, clip.text)
                                        scope.launch {
                                            snackbarHostState.showAppSnack("已复制", SnackType.Success)
                                        }
                                    },
                                    onLongClick = {},
                                    onToggleFavorite = {
                                        ClipboardMonitorService.toggleFavorite(context, clip)
                                    },
                                    onCopy = {
                                        copyToClipboard(context, clip.text)
                                        scope.launch {
                                            snackbarHostState.showAppSnack("已复制", SnackType.Success)
                                        }
                                    },
                                    onShare = {
                                        shareText(context, clip.text)
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
                    item {
                        Spacer(Modifier.height(8.dp))
                    }
                }
            }
        }
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
