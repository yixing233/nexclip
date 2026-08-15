package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.formatTime
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.util.SyncSettings
import clip.yixing.sync.service.CapturedClip
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.SnackbarResult
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Search
import top.yukonga.miuix.kmp.icon.basic.Close
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical

/** 按日期分组后的一个分组:分组标题(今天/昨天/M月d日)+ 组内记录。 */
private data class DayGroup(val label: String, val items: List<CapturedClip>)

@Composable
internal fun RecordsPage(
    scrollBehavior: ScrollBehavior,
    topPadding: Dp,
    bottomInnerPadding: Dp,
    sortDesc: Boolean,
    snackbarHostState: SnackbarHostState
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val captured by ClipboardMonitorService.captured.collectAsState()
    // 按显示顺序排序:倒序 = 最新在前(默认);正序 = 最早在前。
    // 删除/复制仍基于原始列表 indexOf,不受显示顺序影响。
    val displayList = remember(captured, sortDesc) {
        if (sortDesc) captured else captured.reversed()
    }
    val groups = remember(displayList) { groupByDay(displayList) }
    var showClearDialog by remember { mutableStateOf(false) }

    // 注意:不能用 Column(padding(top)) 包裹 LazyColumn —— 那会把列表整体下移,
    // 滚动内容被 LazyColumn 裁剪在自身边界内,永远滚不进顶栏背后的区域,
    // 导致顶栏模糊读到空白(纯色)。直接用 LazyColumn + contentPadding。
    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .overScrollVertical()
            .nestedScroll(scrollBehavior.nestedScrollConnection),
        contentPadding = PaddingValues(
            start = 16.dp,
            end = 16.dp,
            top = topPadding + 8.dp,
            bottom = 8.dp
        ),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text("共 ${captured.size} 条记录", modifier = Modifier.weight(1f))
                        if (captured.isNotEmpty()) {
                            Text(
                                text = "清空",
                                color = MiuixTheme.colorScheme.primary,
                                modifier = Modifier
                                    .clickable { showClearDialog = true }
                                    .padding(horizontal = 4.dp, vertical = 2.dp)
                            )
                        } else {
                            Text(
                                text = "暂无记录",
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                }
            }
            if (captured.isEmpty()) {
                item {
                    Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                        Text("暂无捕获记录")
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "开启「持续监听剪贴板」后,复制的文字会自动记录到这里",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                }
            } else if (displayList.isEmpty()) {
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
                groups.forEach { group ->
                    item(key = "header_" + group.label) {
                        Text(
                            text = group.label,
                            style = MiuixTheme.textStyles.title3,
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            modifier = Modifier.padding(start = 8.dp, top = 10.dp, bottom = 2.dp)
                        )
                    }
                    items(group.items) { clip ->
                        RecordCard(
                            clip = clip,
                            onCopy = {
                                copyToClipboard(context, clip.text)
                            },
                            onDelete = {
                                val index = captured.indexOf(clip)
                                ClipboardMonitorService.deleteAt(context, index)
                                scope.launch {
                                    val result = snackbarHostState.showSnackbar(
                                        message = "已删除该记录",
                                        actionLabel = "撤销"
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
                Spacer(Modifier.height(bottomInnerPadding))
            }
        }

    // 清空全部记录的二次确认
    OverlayDialog(
        show = showClearDialog,
        title = "清空全部记录",
        summary = "将删除本地全部捕获记录,此操作不可恢复。",
        onDismissRequest = { showClearDialog = false }
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
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
                    ClipboardMonitorService.clearAll(context)
                    showClearDialog = false
                    scope.launch {
                        val result = snackbarHostState.showSnackbar(
                            message = "已清空全部记录",
                            actionLabel = "撤销"
                        )
                        if (result == SnackbarResult.ActionPerformed) {
                            ClipboardMonitorService.replaceAll(context, snapshot)
                        }
                    }
                },
                modifier = Modifier.weight(1f)
            ) {
                Text("清空")
            }
        }
    }
}

@Composable
private fun RecordCard(clip: CapturedClip, onCopy: () -> Unit, onDelete: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = formatTime(clip.time),
                style = MiuixTheme.textStyles.footnote1,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                modifier = Modifier.weight(1f)
            )
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
            overflow = TextOverflow.Ellipsis
        )
        Spacer(Modifier.height(8.dp))
        HorizontalDivider(
            color = MiuixTheme.colorScheme.dividerLine,
            thickness = Dp.Hairline
        )
        Spacer(Modifier.height(4.dp))
        Row(modifier = Modifier.fillMaxWidth()) {
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

private fun copyToClipboard(context: Context, text: String) {
    val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    cm.setPrimaryClip(ClipData.newPlainText("SyncClipboard", text))
    Toast.makeText(context, "已复制", Toast.LENGTH_SHORT).show()
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
    sortDesc: Boolean,
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
    val displayList = remember(captured, sortDesc, query) {
        val sorted = if (sortDesc) captured else captured.reversed()
        if (query.isBlank()) {
            sorted
        } else {
            sorted.filter { it.text.contains(query, ignoreCase = true) }
        }
    }

    // 搜索框输入状态(TextFieldState 保持光标,避免倒着输入)
    val searchState = remember { TextFieldState(query) }
    val focusRequester = remember { FocusRequester() }
    LaunchedEffect(searchState.text) {
        onQueryChange(searchState.text.toString())
    }
    LaunchedEffect(Unit) {
        focusRequester.requestFocus()
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
        // 顶部:胶囊搜索框 + 取消(进入时从顶部滑入 + 淡入)
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
                    // 图标与输入框左缘、与文字之间保持合理间距(参考 HyperCeiler 搜索页)
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
            // 取消按钮:搜索框出现后延迟淡入,并从右滑入
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
            // ---- 搜索历史 ----
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
            // ---- 搜索结果 ----
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
                        items(group.items) { clip ->
                            RecordCard(
                                clip = clip,
                                onCopy = {
                                    copyToClipboard(context, clip.text)
                                },
                                onDelete = {
                                    val index = captured.indexOf(clip)
                                    ClipboardMonitorService.deleteAt(context, index)
                                    scope.launch {
                                        val result = snackbarHostState.showSnackbar(
                                            message = "已删除该记录",
                                            actionLabel = "撤销"
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
