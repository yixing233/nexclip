package clip.yixing.sync.ui

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import clip.yixing.sync.SnackType
import clip.yixing.sync.StatusRow
import clip.yixing.sync.data.DeviceInfo
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.data.PairingCode
import clip.yixing.sync.data.PairingRequestItem
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.util.NotificationStyle
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.isActive
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayBottomSheet
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Back
import top.yukonga.miuix.kmp.icon.extended.ChevronForward
import top.yukonga.miuix.kmp.icon.extended.Copy
import top.yukonga.miuix.kmp.icon.extended.Delete
import top.yukonga.miuix.kmp.icon.extended.Refresh
import top.yukonga.miuix.kmp.icon.extended.Settings
import top.yukonga.miuix.kmp.icon.extended.UploadCloud
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 设置二级页面枚举
 */
enum class SettingsSubPage(val title: String, val subtitle: String) {
    Basic("基础设置", "设备名称、开机自启、悬浮底栏、记录上限"),
    Sync("同步设置", "服务器配置、设备配对、在线设备列表"),
    Filter("过滤规则", "内容过滤黑名单、敏感内容保护"),
    Data("数据管理", "备份导出与导入、应用缓存清理"),
    Permission("权限管理", "通知权限、电池优化白名单、自启动")
}

@Composable
internal fun SettingsPage(
    scrollBehavior: ScrollBehavior,
    topPadding: Dp,
    bottomInnerPadding: Dp,
    snackbarHostState: SnackbarHostState,
    floatingBarEnabled: Boolean,
    onFloatingBarChange: (Boolean) -> Unit,
    onOverlayActiveChanged: (Boolean) -> Unit = {},
    onSubPageTitleChanged: (String?) -> Unit = {},
    backTrigger: Int = 0,
) {
    val context = LocalContext.current
    val prefs = remember { SyncSettings.prefs(context) }
    val scope = rememberCoroutineScope()

    // 当前所处二级子页面（null 为一级设置主页）
    var currentSubPage by remember { mutableStateOf<SettingsSubPage?>(null) }

    // 系统返回手势与按键支持
    BackHandler(enabled = currentSubPage != null) {
        currentSubPage = null
    }

    // 顶栏返回按钮联动
    LaunchedEffect(backTrigger) {
        if (backTrigger > 0) {
            currentSubPage = null
        }
    }

    // 动态同步顶栏标题
    LaunchedEffect(currentSubPage) {
        onSubPageTitleChanged(currentSubPage?.title)
    }

    // ---- 1. 基础设置与设备状态 ----
    var deviceName by remember { mutableStateOf(SyncSettings.deviceName(context)) }
    var selfDeviceId by remember { mutableStateOf(SyncSettings.ensureDeviceId(context)) }
    var showNameDialog by remember { mutableStateOf(false) }
    val nameDialogState = remember { TextFieldState(deviceName) }
    var showResetIdDialog by remember { mutableStateOf(false) }

    var bootStart by remember { mutableStateOf(SyncSettings.bootStartEnabled(context)) }
    var notificationStyle by remember { mutableStateOf(SyncSettings.notificationStyle(context)) }
    var showNotificationStyleDialog by remember { mutableStateOf(false) }
    val historyOptions = SyncSettings.MAX_HISTORY_OPTIONS.toList()
    val historyLabels = historyOptions.map { "$it 条" }
    var historyIndex by remember {
        mutableStateOf(
            historyOptions.indexOf(SyncSettings.maxHistory(context)).coerceAtLeast(0)
        )
    }

    // ---- 2. 同步设置状态 ----
    val urlState = remember { TextFieldState(SyncSettings.serverUrl(context)) }
    var testing by remember { mutableStateOf(false) }
    var pairing by remember { mutableStateOf(false) }

    var showPairDialog by remember { mutableStateOf(false) }
    val dialogCodeState = remember { TextFieldState("") }
    val dialogUidState = remember { TextFieldState("") }

    var generatingCode by remember { mutableStateOf(false) }
    var generatedCode by remember { mutableStateOf<PairingCode?>(null) }
    var showCodeSheet by remember { mutableStateOf(false) }
    var pendingPairRequest by remember { mutableStateOf<PairingRequestItem?>(null) }
    var deleteTargetDevice by remember { mutableStateOf<DeviceInfo?>(null) }
    var isPairWaiting by remember { mutableStateOf(false) }
    var pairWaitingCountdown by remember { mutableIntStateOf(120) }
    var pairWaitingJob by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }

    var devices by remember { mutableStateOf<List<DeviceInfo>>(emptyList()) }
    var devicesLoading by remember { mutableStateOf(true) }
    var devicesError by remember { mutableStateOf<String?>(null) }
    var devicesReload by remember { mutableIntStateOf(0) }
    var devicesManual by remember { mutableStateOf(false) }
    val isServerConnected by ClipboardMonitorService.isServerConnected.collectAsState()
    var autoRefreshUntil by remember { mutableLongStateOf(0L) }

    // 轮询待确认配对请求 (当生成配对码弹层处于开启状态时)
    LaunchedEffect(showCodeSheet, generatedCode) {
        val code = generatedCode
        if (!showCodeSheet || code == null) {
            pendingPairRequest = null
            return@LaunchedEffect
        }
        val serverUrl = SyncSettings.serverUrl(context)
        val genDeviceId = SyncSettings.ensureDeviceId(context)
        if (serverUrl.isBlank()) return@LaunchedEffect

        val api = SyncApi(serverUrl, genDeviceId, SyncSettings.deviceToken(context))
        while (showCodeSheet && generatedCode != null) {
            try {
                val reqs = withContext(Dispatchers.IO) {
                    api.listPairingRequests(code.code, genDeviceId)
                }
                val pending = reqs.firstOrNull { it.status.equals("pending", ignoreCase = true) }
                pendingPairRequest = pending
            } catch (_: Exception) {
            }
            delay(1500)
        }
    }

    // ① 设备列表基础加载
    LaunchedEffect(devicesReload) {
        val serverUrl = SyncSettings.serverUrl(context)
        if (serverUrl.isBlank()) {
            devicesLoading = false
            return@LaunchedEffect
        }
        devicesLoading = devices.isEmpty()
        devicesError = null
        try {
            val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
            devices = withContext(Dispatchers.IO) { api.getDevices() }
        } catch (e: Exception) {
            devicesError = e.message ?: "加载失败"
            if (devicesManual || devices.isEmpty()) {
                snackbarHostState.showAppSnack(devicesError ?: "设备列表加载失败", SnackType.Error)
            }
        } finally {
            devicesManual = false
            devicesLoading = false
        }
    }

    // ② 配对期间高频自动刷新设备列表
    LaunchedEffect(showCodeSheet, autoRefreshUntil) {
        val isCodeSheetActive = showCodeSheet && generatedCode != null
        val isPostPairActive = System.currentTimeMillis() < autoRefreshUntil
        if (isCodeSheetActive || isPostPairActive) {
            while (true) {
                delay(3000)
                val codeActive = showCodeSheet && generatedCode != null
                val postActive = System.currentTimeMillis() < autoRefreshUntil
                if (!codeActive && !postActive) break

                val serverUrl = SyncSettings.serverUrl(context)
                if (serverUrl.isNotBlank()) {
                    try {
                        val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                        val list = withContext(Dispatchers.IO) { api.getDevices() }
                        devices = list
                    } catch (_: Exception) {
                    }
                }
            }
        }
    }

    // ③ 服务器已连接后低频更新设备列表
    LaunchedEffect(isServerConnected) {
        if (isServerConnected) {
            while (true) {
                delay(20_000)
                val codeActive = showCodeSheet && generatedCode != null
                val postActive = System.currentTimeMillis() < autoRefreshUntil
                if (codeActive || postActive) continue

                val serverUrl = SyncSettings.serverUrl(context)
                if (serverUrl.isNotBlank()) {
                    try {
                        val api = SyncApi(serverUrl, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                        val list = withContext(Dispatchers.IO) { api.getDevices() }
                        devices = list
                    } catch (_: Exception) {
                    }
                }
            }
        }
    }

    // ---- 3. 过滤规则状态 ----
    var filterKeywords by remember { mutableStateOf(SyncSettings.filterKeywords(context)) }
    var ignoreSensitive by remember { mutableStateOf(SyncSettings.ignoreSensitive(context)) }
    val addKeywordState = remember { TextFieldState("") }

    // ---- 4. 缓存与数据管理 ----
    var cacheSizeText by remember { mutableStateOf("计算中…") }
    fun refreshCacheSize() {
        scope.launch(Dispatchers.IO) {
            val cacheSize = getFolderSize(context.cacheDir) + getFolderSize(context.codeCacheDir)
            val text = formatSize(cacheSize)
            withContext(Dispatchers.Main) {
                cacheSizeText = text
            }
        }
    }
    LaunchedEffect(Unit) {
        refreshCacheSize()
    }

    val exportLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.CreateDocument("application/json")
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                try {
                    val json = withContext(Dispatchers.IO) {
                        ClipboardMonitorService.exportBackup(context)
                    }
                    withContext(Dispatchers.IO) {
                        context.contentResolver.openOutputStream(uri)?.use { os ->
                            os.write(json.toByteArray(Charsets.UTF_8))
                            os.flush()
                        }
                    }
                    snackbarHostState.showAppSnack("备份已成功导出", SnackType.Success)
                } catch (e: Exception) {
                    snackbarHostState.showAppSnack("导出失败: ${e.message}", SnackType.Error)
                }
            }
        }
    }

    val importLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                try {
                    val content = withContext(Dispatchers.IO) {
                        context.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }
                    }
                    if (content.isNullOrBlank()) {
                        snackbarHostState.showAppSnack("读取备份文件为空", SnackType.Error)
                        return@launch
                    }
                    val count = withContext(Dispatchers.IO) {
                        ClipboardMonitorService.importBackup(context, content)
                    }
                    snackbarHostState.showAppSnack("成功导入 $count 条记录", SnackType.Success)
                } catch (e: Exception) {
                    snackbarHostState.showAppSnack("导入失败: ${e.message}", SnackType.Error)
                }
            }
        }
    }

    // ---- 5. 权限管理状态 ----
    var isNotificationGranted by remember {
        mutableStateOf(
            if (Build.VERSION.SDK_INT >= 33) {
                ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
            } else {
                true
            }
        )
    }
    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { isGranted ->
        isNotificationGranted = isGranted
        if (isGranted) {
            scope.launch { snackbarHostState.showAppSnack("已授予通知权限", SnackType.Success) }
        } else {
            scope.launch { snackbarHostState.showAppSnack("未授予通知权限", SnackType.Info) }
        }
    }

    val powerManager = remember(context) { context.getSystemService(Context.POWER_SERVICE) as PowerManager }
    var isBatteryOptIgnored by remember {
        mutableStateOf(
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                powerManager.isIgnoringBatteryOptimizations(context.packageName)
            } else {
                true
            }
        )
    }

    // 弹层开启时通知底栏收起避让
    LaunchedEffect(currentSubPage, showCodeSheet, showPairDialog, showNameDialog, showResetIdDialog) {
        onOverlayActiveChanged(currentSubPage != null || showCodeSheet || showPairDialog || showNameDialog || showResetIdDialog)
    }

    // 二级页面平滑横向切换动画
    AnimatedContent(
        targetState = currentSubPage,
        transitionSpec = {
            if (targetState != null) {
                (slideInHorizontally { it } + fadeIn()).togetherWith(slideOutHorizontally { -it / 3 } + fadeOut())
            } else {
                (slideInHorizontally { -it / 3 } + fadeIn()).togetherWith(slideOutHorizontally { it } + fadeOut())
            }
        },
        label = "SettingsSubPageAnimation"
    ) { subPage ->
        when (subPage) {
            null -> {
                // ---- 一级设置主页 ----
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
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    // 5 大二级设置入口卡片（无分隔线）
                    item {
                        SectionBlock(title = "设置分类", insideMargin = PaddingValues()) {
                            SettingsNavRow(
                                title = "基础设置",
                                summary = "设备名称、开机自启、悬浮底栏、记录上限",
                                onClick = { currentSubPage = SettingsSubPage.Basic }
                            )
                            SettingsNavRow(
                                title = "同步设置",
                                summary = "服务器配置、设备配对、在线设备列表",
                                onClick = { currentSubPage = SettingsSubPage.Sync }
                            )
                            SettingsNavRow(
                                title = "过滤规则",
                                summary = "内容过滤黑名单、敏感内容保护",
                                onClick = { currentSubPage = SettingsSubPage.Filter }
                            )
                            SettingsNavRow(
                                title = "数据管理",
                                summary = "备份导出与导入、应用缓存清理",
                                onClick = { currentSubPage = SettingsSubPage.Data }
                            )
                            SettingsNavRow(
                                title = "权限管理",
                                summary = "通知权限、电池优化白名单、自启动",
                                onClick = { currentSubPage = SettingsSubPage.Permission }
                            )
                        }
                    }

                    // 关于
                    item {
                        SectionBlock(title = "关于") {
                            Column(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalAlignment = Alignment.CenterHorizontally
                            ) {
                                val icon = remember { appIconBitmap(context) }
                                Image(
                                    bitmap = icon,
                                    contentDescription = "剪贴板同步图标",
                                    modifier = Modifier
                                        .size(56.dp)
                                        .clip(RoundedCornerShape(14.dp))
                                )
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    text = "剪贴板同步",
                                    fontSize = 17.sp,
                                    fontWeight = FontWeight.Bold
                                )
                                Spacer(Modifier.height(2.dp))
                                Text(
                                    text = "版本 " + appVersion(context),
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                            Spacer(Modifier.height(10.dp))
                            StatusRow(label = "包名", value = context.packageName ?: "-")
                            Spacer(Modifier.height(6.dp))
                            StatusRow(label = "构建技术", value = "Miuix · LSPosed")
                        }
                    }

                    item {
                        Spacer(Modifier.height(bottomInnerPadding))
                    }
                }
            }

            SettingsSubPage.Basic -> {
                // ---- 二级页面 1: 基础设置 ----
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "设备信息", insideMargin = PaddingValues()) {
                            // 修改设备名称
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        nameDialogState.edit { replace(0, length, deviceName) }
                                        showNameDialog = true
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "设备名称",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = deviceName,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                                        fontSize = 13.sp
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "修改",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 设备标识 Device ID
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "设备标识 (Device ID)",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = selfDeviceId,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                                        fontSize = 13.sp,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis
                                    )
                                }
                                IconButton(
                                    onClick = {
                                        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                                        cm.setPrimaryClip(ClipData.newPlainText("Device ID", selfDeviceId))
                                        scope.launch { snackbarHostState.showAppSnack("已复制 Device ID", SnackType.Success) }
                                    }
                                ) {
                                    Icon(imageVector = MiuixIcons.Normal.Copy, contentDescription = "复制")
                                }
                                IconButton(
                                    onClick = { showResetIdDialog = true }
                                ) {
                                    Icon(imageVector = MiuixIcons.Normal.Refresh, contentDescription = "重置")
                                }
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "行为设置", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = bootStart,
                                onCheckedChange = { checked ->
                                    bootStart = checked
                                    prefs.edit()
                                        .putBoolean(SyncSettings.KEY_BOOT_START_ENABLED, checked)
                                        .apply()
                                },
                                title = "开机自启",
                                summary = "开机后自动恢复剪贴板监听"
                            )
                            SwitchPreference(
                                checked = floatingBarEnabled,
                                onCheckedChange = onFloatingBarChange,
                                title = "悬浮底栏",
                                summary = "使用液态玻璃悬浮导航栏"
                            )
                            // 通知展示样式 (普通通知 / 实时通知 / HyperOS 超级岛)
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable { showNotificationStyleDialog = true }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "通知展示样式",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = notificationStyle.label + " · " + notificationStyle.summary,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant,
                                        fontSize = 13.sp
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "选择样式",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }
                            OverlayDropdownPreference(
                                items = historyLabels,
                                selectedIndex = historyIndex,
                                onSelectedIndexChange = { index ->
                                    historyIndex = index
                                    prefs.edit()
                                        .putInt(SyncSettings.KEY_MAX_HISTORY, historyOptions[index])
                                        .apply()
                                },
                                title = "记录上限",
                                summary = "本地最多保留 ${historyOptions[historyIndex]} 条捕获记录"
                            )
                        }
                    }
                }
            }

            SettingsSubPage.Sync -> {
                // ---- 二级页面 2: 同步设置 ----
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "服务器设置") {
                            TextField(
                                state = urlState,
                                label = "服务器地址",
                                useLabelAsPlaceholder = true,
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                                modifier = Modifier.fillMaxWidth()
                            )
                            Spacer(Modifier.height(8.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                Button(
                                    onClick = {
                                        val newUrl = urlState.text.toString().trim()
                                        prefs.edit()
                                            .putString(SyncSettings.KEY_SERVER_URL, newUrl)
                                            .apply()
                                        scope.launch { snackbarHostState.showAppSnack("已保存", SnackType.Success) }
                                        devicesReload++
                                        if (ClipboardMonitorService.isRunning.value) {
                                            ClipboardMonitorService.stop(context)
                                            ClipboardMonitorService.start(context)
                                        }
                                    },
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text("保存")
                                }
                                Button(
                                    onClick = {
                                        val url = urlState.text.toString().trim()
                                            .ifEmpty { SyncSettings.serverUrl(context) }
                                        if (url.isEmpty()) {
                                            scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                                            return@Button
                                        }
                                        testing = true
                                        val api = SyncApi(url, SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                        scope.launch {
                                            val (ok, msg) = withContext(Dispatchers.IO) {
                                                api.testConnection()
                                            }
                                            testing = false
                                            snackbarHostState.showAppSnack(msg, if (ok) SnackType.Success else SnackType.Error)
                                        }
                                    },
                                    enabled = !testing,
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Text(if (testing) "测试中…" else "连通性测试")
                                }
                            }
                            if (testing) {
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = "正在连接服务器…",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "配对管理") {
                            Button(
                                onClick = {
                                    val url = urlState.text.toString().trim()
                                        .ifEmpty { SyncSettings.serverUrl(context) }
                                    if (url.isEmpty()) {
                                        scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                                        return@Button
                                    }
                                    generatingCode = true
                                    val genDeviceId = SyncSettings.ensureDeviceId(context)
                                    val genDeviceName = SyncSettings.deviceName(context)
                                    val api = SyncApi(url, genDeviceId, SyncSettings.deviceToken(context))
                                    scope.launch {
                                        val r = withContext(Dispatchers.IO) {
                                            runCatching { api.createPairingCode(genDeviceId, genDeviceName) }
                                        }
                                        generatingCode = false
                                        r.onSuccess {
                                            it.deviceToken?.let { token ->
                                                SyncSettings.setDeviceToken(context, token)
                                                SyncSettings.setPaired(context, true)
                                            }
                                            generatedCode = it
                                            showCodeSheet = true
                                            snackbarHostState.showAppSnack("配对码已生成", SnackType.Success)
                                        }.onFailure { e ->
                                            snackbarHostState.showAppSnack(e.message ?: "生成失败", SnackType.Error)
                                        }
                                    }
                                },
                                enabled = !generatingCode,
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Text(if (generatingCode) "生成中…" else "生成配对码")
                            }
                            Spacer(Modifier.height(8.dp))
                            Button(
                                onClick = { showPairDialog = true },
                                enabled = !pairing,
                                modifier = Modifier.fillMaxWidth()
                            ) {
                                Text(if (pairing) "配对中…" else "输入配对码")
                            }
                            if (pairing) {
                                Spacer(Modifier.height(6.dp))
                                Text(
                                    text = "等待对方确认…",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(
                            title = "设备列表",
                            trailing = {
                                Text(
                                    text = if (devicesLoading && devices.isEmpty()) "加载中…"
                                    else "${devices.count { it.online }} / ${devices.size} 在线",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                                Spacer(Modifier.width(6.dp))
                                IconButton(onClick = { devicesManual = true; devicesReload++ }) {
                                    Icon(
                                        imageVector = LucideIcons.RefreshCw,
                                        contentDescription = "刷新",
                                        tint = MiuixTheme.colorScheme.onBackgroundVariant,
                                        modifier = Modifier.size(16.dp)
                                    )
                                }
                            },
                        ) {
                            if (devices.isEmpty() && !devicesLoading && devicesError == null) {
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    text = "暂无设备。开启首页「持续监听剪贴板」后,本机将自动登记。",
                                    fontSize = 13.sp,
                                    color = MiuixTheme.colorScheme.onBackgroundVariant
                                )
                            }
                            val sortedDevices = remember(devices, selfDeviceId) {
                                devices.sortedWith(
                                    compareByDescending<DeviceInfo> { it.id == selfDeviceId }
                                        .thenByDescending { it.online }
                                        .thenByDescending { it.lastSeenAt }
                                )
                            }
                            sortedDevices.forEach { device ->
                                Spacer(Modifier.height(10.dp))
                                DeviceCard(
                                    device = device,
                                    isSelf = device.id == selfDeviceId,
                                    onDeleteClick = { deleteTargetDevice = it },
                                    onCopyId = { id ->
                                        copyPairingCode(context, id)
                                        scope.launch { snackbarHostState.showAppSnack("设备 ID 已复制", SnackType.Success) }
                                    }
                                )
                            }
                        }
                    }
                }
            }

            SettingsSubPage.Filter -> {
                // ---- 二级页面 3: 过滤规则 ----
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "内容过滤黑名单") {
                            Text(
                                text = "复制包含以下关键词的内容时，应用将自动跳过记录与同步。",
                                fontSize = 13.sp,
                                lineHeight = 18.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                            Spacer(Modifier.height(12.dp))
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                TextField(
                                    state = addKeywordState,
                                    label = "输入要忽略的关键词",
                                    useLabelAsPlaceholder = true,
                                    modifier = Modifier.weight(1f)
                                )
                                Button(
                                    onClick = {
                                        val text = addKeywordState.text.toString().trim()
                                        if (text.isNotBlank()) {
                                            SyncSettings.addFilterKeyword(context, text)
                                            filterKeywords = SyncSettings.filterKeywords(context)
                                            addKeywordState.edit { replace(0, length, "") }
                                            scope.launch { snackbarHostState.showAppSnack("已添加规则", SnackType.Success) }
                                        }
                                    }
                                ) {
                                    Text("添加")
                                }
                            }
                            Spacer(Modifier.height(12.dp))

                            if (filterKeywords.isEmpty()) {
                                Box(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(vertical = 16.dp),
                                    contentAlignment = Alignment.Center
                                ) {
                                    Text(
                                        text = "暂无黑名单规则",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                            } else {
                                Column(
                                    modifier = Modifier.fillMaxWidth(),
                                    verticalArrangement = Arrangement.spacedBy(6.dp)
                                ) {
                                    filterKeywords.forEach { kw ->
                                        Row(
                                            modifier = Modifier
                                                .fillMaxWidth()
                                                .clip(RoundedCornerShape(8.dp))
                                                .background(MiuixTheme.colorScheme.surfaceContainer)
                                                .padding(horizontal = 12.dp, vertical = 6.dp),
                                            verticalAlignment = Alignment.CenterVertically
                                        ) {
                                            Text(
                                                text = kw,
                                                modifier = Modifier.weight(1f),
                                                fontSize = 14.sp
                                            )
                                            IconButton(
                                                onClick = {
                                                    SyncSettings.removeFilterKeyword(context, kw)
                                                    filterKeywords = SyncSettings.filterKeywords(context)
                                                }
                                            ) {
                                                Icon(
                                                    imageVector = MiuixIcons.Normal.Delete,
                                                    contentDescription = "删除",
                                                    tint = MiuixTheme.colorScheme.error
                                                )
                                            }
                                        }
                                    }
                                    Spacer(Modifier.height(6.dp))
                                    Button(
                                        onClick = {
                                            SyncSettings.clearFilterKeywords(context)
                                            filterKeywords = emptyList()
                                            scope.launch { snackbarHostState.showAppSnack("已清空黑名单规则", SnackType.Info) }
                                        },
                                        colors = ButtonDefaults.buttonColors(
                                            color = MiuixTheme.colorScheme.surfaceContainerHigh,
                                            contentColor = MiuixTheme.colorScheme.error
                                        ),
                                        modifier = Modifier.fillMaxWidth()
                                    ) {
                                        Text("清空全部规则")
                                    }
                                }
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "敏感内容保护", insideMargin = PaddingValues()) {
                            SwitchPreference(
                                checked = ignoreSensitive,
                                onCheckedChange = { checked ->
                                    ignoreSensitive = checked
                                    SyncSettings.setIgnoreSensitive(context, checked)
                                },
                                title = "忽略敏感/密码标记",
                                summary = "自动跳过密码管理器或标记为 sensitive 的剪贴板"
                            )
                        }
                    }
                }
            }

            SettingsSubPage.Data -> {
                // ---- 二级页面 4: 数据管理 ----
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "备份与迁移", insideMargin = PaddingValues()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        val timeStr = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.getDefault()).format(Date())
                                        exportLauncher.launch("sync_clipboard_backup_$timeStr.json")
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "导出记录备份",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "将全部捕获历史导出为 JSON 备份文件",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.UploadCloud,
                                    contentDescription = "导出",
                                    tint = MiuixTheme.colorScheme.primary
                                )
                            }

                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        importLauncher.launch(arrayOf("application/json", "text/*", "*/*"))
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "导入记录备份",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "从 JSON 文件导入并合并历史记录",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.Copy,
                                    contentDescription = "导入",
                                    tint = MiuixTheme.colorScheme.primary
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "存储空间", insideMargin = PaddingValues()) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "应用缓存与临时存储",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "已占用 $cacheSizeText",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Button(
                                    onClick = {
                                        scope.launch(Dispatchers.IO) {
                                            deleteFolderContents(context.cacheDir)
                                            deleteFolderContents(context.codeCacheDir)
                                            refreshCacheSize()
                                            snackbarHostState.showAppSnack("缓存已清理", SnackType.Success)
                                        }
                                    }
                                ) {
                                    Text("一键清理")
                                }
                            }
                        }
                    }
                }
            }

            SettingsSubPage.Permission -> {
                // ---- 二级页面 5: 权限管理 ----
                LazyColumn(
                    modifier = Modifier
                        .fillMaxSize()
                        .overScrollVertical()
                        .nestedScroll(scrollBehavior.nestedScrollConnection),
                    contentPadding = PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        top = topPadding + 8.dp,
                        bottom = bottomInnerPadding + 16.dp
                    ),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    item {
                        SectionBlock(title = "系统权限", insideMargin = PaddingValues()) {
                            // 通知权限
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (Build.VERSION.SDK_INT >= 33 && !isNotificationGranted) {
                                            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                                        } else {
                                            val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                                                putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                                            }
                                            runCatching { context.startActivity(intent) }
                                        }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "通知权限",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = if (isNotificationGranted) "已授权（用于前台常驻与推送提示）" else "未授权，点击前往开启",
                                        fontSize = 13.sp,
                                        color = if (isNotificationGranted) Color(0xFF34C759) else MiuixTheme.colorScheme.error
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 电池优化白名单
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                                            val intent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                                                data = Uri.parse("package:${context.packageName}")
                                            }
                                            runCatching { context.startActivity(intent) }.onFailure {
                                                runCatching {
                                                    context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
                                                }
                                            }
                                        }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "忽略电池优化 (防杀后台)",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = if (isBatteryOptIgnored) "已加入白名单（后台保活更稳定）" else "未加入白名单，点击前往开启",
                                        fontSize = 13.sp,
                                        color = if (isBatteryOptIgnored) Color(0xFF34C759) else MiuixTheme.colorScheme.error
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }

                            // 系统应用详情与自启动
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable {
                                        val intent = Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                                            data = Uri.parse("package:${context.packageName}")
                                        }
                                        runCatching { context.startActivity(intent) }
                                    }
                                    .padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(modifier = Modifier.weight(1f)) {
                                    Text(
                                        text = "系统应用详情与自启动",
                                        fontSize = 16.sp,
                                        fontWeight = FontWeight.Medium
                                    )
                                    Spacer(Modifier.height(2.dp))
                                    Text(
                                        text = "配置系统自启动权限、省电策略及后台弹出界面",
                                        fontSize = 13.sp,
                                        color = MiuixTheme.colorScheme.onBackgroundVariant
                                    )
                                }
                                Icon(
                                    imageVector = MiuixIcons.Normal.ChevronForward,
                                    contentDescription = "前往设置",
                                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
                                )
                            }
                        }
                    }

                    item {
                        SectionBlock(title = "LSPosed 模块配置说明") {
                            Text(
                                text = "1. 打开 LSPosed 管理器并勾选「剪贴板同步」\n" +
                                    "2. 作用域必须包含「系统框架」和「剪贴板同步」应用本身\n" +
                                    "3. 完成配置后重启系统即可生效",
                                fontSize = 13.sp,
                                lineHeight = 19.sp,
                                color = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                }
            }
        }
    }

    // ---- 对话框与弹层 ----

    // 配对对话框
    OverlayDialog(
        show = showPairDialog,
        title = "设备配对",
        summary = "输入另一台设备提供的 配对码 + 用户ID,由对方确认后接入",
        onDismissRequest = { showPairDialog = false }
    ) {
        TextField(
            state = dialogCodeState,
            label = "6 位配对验证码",
            useLabelAsPlaceholder = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(10.dp))
        TextField(
            state = dialogUidState,
            label = "用户ID (选填)",
            useLabelAsPlaceholder = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
            modifier = Modifier.fillMaxWidth()
        )
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Button(
                onClick = { showPairDialog = false },
                enabled = !pairing,
                modifier = Modifier.weight(1f)
            ) {
                Text("取消")
            }
            Button(
                onClick = {
                    val url = urlState.text.toString().trim()
                        .ifEmpty { SyncSettings.serverUrl(context) }
                    val code = dialogCodeState.text.toString().trim().uppercase()
                    if (url.isEmpty()) {
                        scope.launch { snackbarHostState.showAppSnack("请先填写服务器地址", SnackType.Info) }
                        return@Button
                    }
                    if (code.isEmpty()) {
                        scope.launch { snackbarHostState.showAppSnack("请输入 6 位配对验证码", SnackType.Info) }
                        return@Button
                    }
                    pairing = true
                    showPairDialog = false
                    val deviceId = SyncSettings.ensureDeviceId(context)
                    val genDevName = SyncSettings.deviceName(context)
                    val api = SyncApi(url, deviceId, SyncSettings.deviceToken(context))
                    val uid = dialogUidState.text.toString().trim()
                    pairWaitingJob?.cancel()
                    pairWaitingJob = scope.launch {
                        // 优先使用 6 位数字单向即入配对 (无须输入用户ID, 无须对方确认)
                        if (code.length == 6 || uid.isEmpty()) {
                            val directRes = withContext(Dispatchers.IO) {
                                runCatching { api.pairDirect(code, deviceId, genDevName) }
                            }
                            if (directRes.isSuccess && !directRes.getOrNull()?.deviceToken.isNullOrBlank()) {
                                val token = directRes.getOrNull()!!.deviceToken!!
                                prefs.edit().putString(SyncSettings.KEY_SERVER_URL, url).apply()
                                SyncSettings.setDeviceToken(context, token)
                                SyncSettings.setPaired(context, true)
                                dialogCodeState.edit { replace(0, length, "") }
                                dialogUidState.edit { replace(0, length, "") }
                                pairing = false
                                snackbarHostState.showAppSnack("配对成功！已加入设备组", SnackType.Success)
                                devicesReload++
                                autoRefreshUntil = System.currentTimeMillis() + 60_000L
                                if (ClipboardMonitorService.isRunning.value) {
                                    ClipboardMonitorService.stop(context)
                                    ClipboardMonitorService.start(context)
                                }
                                return@launch
                            } else if (uid.isEmpty()) {
                                pairing = false
                                val errMsg = directRes.exceptionOrNull()?.message ?: "配对失败"
                                snackbarHostState.showAppSnack(errMsg, SnackType.Error)
                                return@launch
                            }
                        }

                        // 兼容旧版双向握手配对
                        isPairWaiting = true
                        pairWaitingCountdown = 120
                        val result = withContext(Dispatchers.IO) {
                            runCatching { api.pair(code, uid, deviceId, genDevName) }
                        }
                        result.onFailure { e ->
                            pairing = false
                            isPairWaiting = false
                            snackbarHostState.showAppSnack(e.message ?: "配对失败", SnackType.Error)
                            return@launch
                        }
                        val issuedToken = result.getOrNull()?.deviceToken
                        if (issuedToken.isNullOrBlank()) {
                            pairing = false
                            isPairWaiting = false
                            snackbarHostState.showAppSnack("服务器未返回设备凭证，请确认服务端已更新", SnackType.Error)
                            return@launch
                        }

                        // 启动倒计时协程
                        val timerJob = launch {
                            while (pairWaitingCountdown > 0) {
                                delay(1000)
                                pairWaitingCountdown--
                            }
                        }

                        var status = "pending"
                        val deadline = System.currentTimeMillis() + 120_000L
                        while (System.currentTimeMillis() < deadline && isActive) {
                            delay(2000)
                            status = withContext(Dispatchers.IO) {
                                runCatching { api.pairStatus(code, deviceId) }.getOrDefault("pending")
                            }
                            if (status == "approved" || status == "rejected" || status == "expired" || status == "not-found") break
                        }
                        timerJob.cancel()
                        pairing = false
                        isPairWaiting = false
                        when (status) {
                            "approved" -> {
                                prefs.edit()
                                    .putString(SyncSettings.KEY_SERVER_URL, url)
                                    .apply()
                                SyncSettings.setDeviceToken(context, issuedToken)
                                SyncSettings.setPaired(context, true)
                                dialogCodeState.edit { replace(0, length, "") }
                                dialogUidState.edit { replace(0, length, "") }
                                snackbarHostState.showAppSnack("配对成功！已加入设备组", SnackType.Success)
                                devicesReload++
                                autoRefreshUntil = System.currentTimeMillis() + 60_000L
                                if (ClipboardMonitorService.isRunning.value) {
                                    ClipboardMonitorService.stop(context)
                                    ClipboardMonitorService.start(context)
                                }
                            }
                            "rejected" -> snackbarHostState.showAppSnack("配对请求已被对方设备拒绝", SnackType.Error)
                            "expired" -> snackbarHostState.showAppSnack("配对码已过期，请重新获取", SnackType.Error)
                            else -> snackbarHostState.showAppSnack("等待对方确认超时，请重新发起配对", SnackType.Error)
                        }
                    }
                },
                enabled = !pairing,
                modifier = Modifier.weight(1f)
            ) {
                Text(if (pairing) "发起中…" else "确认配对")
            }
        }
    }

    // 等待对方确认接入弹窗 (带 120s 超时倒计时与取消)
    OverlayDialog(
        show = isPairWaiting,
        title = "等待对方设备确认",
        onDismissRequest = {
            isPairWaiting = false
            pairing = false
            pairWaitingJob?.cancel()
            scope.launch { snackbarHostState.showAppSnack("已取消配对等待", SnackType.Info) }
        }
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 10.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "已发起配对请求，请在另一台设备上点击「同意加入」",
                fontSize = 13.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center
            )
            Spacer(Modifier.height(14.dp))
            Text(
                text = "剩余 ${pairWaitingCountdown}s",
                fontSize = 22.sp,
                fontWeight = FontWeight.Bold,
                color = MiuixTheme.colorScheme.primary
            )
            Spacer(Modifier.height(16.dp))
            Button(
                onClick = {
                    isPairWaiting = false
                    pairing = false
                    pairWaitingJob?.cancel()
                    scope.launch { snackbarHostState.showAppSnack("已取消配对等待", SnackType.Info) }
                },
                colors = ButtonDefaults.buttonColors(
                    color = MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = MiuixTheme.colorScheme.onSurface
                ),
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("取消等待")
            }
        }
    }

    // 生成的配对码用底部弹层查看
    OverlayBottomSheet(
        show = showCodeSheet,
        title = "配对码与用户 ID",
        onDismissRequest = {
            showCodeSheet = false
            val revokeCode = generatedCode?.code
            generatedCode = null
            pendingPairRequest = null
            if (revokeCode != null) {
                scope.launch {
                    withContext(Dispatchers.IO) {
                        runCatching { SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context)).revokePairingCode(revokeCode) }
                    }
                    devicesReload++
                }
            }
        }
    ) {
        generatedCode?.let { code ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .windowInsetsPadding(WindowInsets.navigationBars)
                    .padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 32.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                // 1. 配对码卡片
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer)
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Column {
                        Text(
                            text = "配对码",
                            fontSize = 12.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                        Text(
                            text = code.code,
                            fontSize = 22.sp,
                            fontWeight = FontWeight.Bold
                        )
                    }
                    Button(
                        onClick = {
                            copyPairingCode(context, code.code)
                            scope.launch { snackbarHostState.showAppSnack("配对码已复制", SnackType.Success) }
                        }
                    ) {
                        Text("复制配对码")
                    }
                }

                Spacer(Modifier.height(10.dp))

                // 2. 用户 ID 卡片
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer)
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "用户 ID",
                            fontSize = 12.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                        Text(
                            text = code.userId.ifEmpty { "（未分配）" },
                            fontSize = 16.sp,
                            fontWeight = FontWeight.SemiBold
                        )
                    }
                    if (code.userId.isNotEmpty()) {
                        Button(
                            onClick = {
                                copyPairingCode(context, code.userId)
                                scope.launch { snackbarHostState.showAppSnack("用户 ID 已复制", SnackType.Success) }
                            }
                        ) {
                            Text("复制用户 ID")
                        }
                    }
                }

                Spacer(Modifier.height(12.dp))

                // 3. 一键复制全部
                Button(
                    onClick = {
                        copyPairingCode(context, "配对码: " + code.code + "\n用户 ID: " + code.userId)
                        scope.launch { snackbarHostState.showAppSnack("配对信息已全部复制", SnackType.Success) }
                    },
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text("一键复制 (配对码 + 用户 ID)")
                }

                Spacer(Modifier.height(14.dp))

                // 4. 待确认请求卡片 vs 等待状态
                val req = pendingPairRequest
                if (req != null) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(14.dp))
                            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
                            .padding(14.dp)
                    ) {
                        Text(
                            text = "🔔 检测到设备「" + (req.deviceName ?: req.deviceId ?: "新设备") + "」请求接入",
                            fontWeight = FontWeight.Bold,
                            fontSize = 14.sp,
                            color = MiuixTheme.colorScheme.primary
                        )
                        Spacer(Modifier.height(10.dp))
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(10.dp)
                        ) {
                            Button(
                                onClick = {
                                    scope.launch {
                                        val ok = withContext(Dispatchers.IO) {
                                            runCatching {
                                                val api = SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                                api.confirmPairing(code.code, "approve", SyncSettings.ensureDeviceId(context))
                                            }.getOrDefault(false)
                                        }
                                        if (ok) {
                                            showCodeSheet = false
                                            generatedCode = null
                                            pendingPairRequest = null
                                            snackbarHostState.showAppSnack("已同意配对！新设备已加入", SnackType.Success)
                                            devicesReload++
                                        }
                                    }
                                },
                                modifier = Modifier.weight(1f)
                            ) {
                                Text("同意加入")
                            }
                            Button(
                                onClick = {
                                    scope.launch {
                                        withContext(Dispatchers.IO) {
                                            runCatching {
                                                val api = SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                                                api.confirmPairing(code.code, "reject", SyncSettings.ensureDeviceId(context))
                                            }
                                        }
                                        pendingPairRequest = null
                                    }
                                },
                                modifier = Modifier.weight(1f)
                            ) {
                                Text("拒绝")
                            }
                        }
                    }
                } else {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.Center,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = "等待另一台设备输入配对码并接入…",
                            fontSize = 13.sp,
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                }

                Spacer(Modifier.height(10.dp))
                Text(
                    text = "在另一台设备上输入上述配对码与用户 ID 即可发起接入。\n关闭弹层后配对码立即失效。",
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    fontSize = 12.sp,
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center
                )
            }
        }
    }

    // 修改设备名称对话框
    OverlayDialog(
        show = showNameDialog,
        title = "修改设备名称",
        onDismissRequest = { showNameDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
            TextField(
                state = nameDialogState,
                label = "设备名称",
                useLabelAsPlaceholder = true,
                modifier = Modifier.fillMaxWidth()
            )
            Spacer(Modifier.height(16.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Button(
                    onClick = { showNameDialog = false },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                        contentColor = MiuixTheme.colorScheme.onSurface
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val newName = nameDialogState.text.toString().trim()
                        if (newName.isNotBlank()) {
                            SyncSettings.setDeviceName(context, newName)
                            deviceName = newName
                            showNameDialog = false
                            scope.launch { snackbarHostState.showAppSnack("已保存设备名称", SnackType.Success) }
                        }
                    },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("保存")
                }
            }
        }
    }

    // 重置设备 ID 对话框
    OverlayDialog(
        show = showResetIdDialog,
        title = "重置设备标识",
        onDismissRequest = { showResetIdDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
            Text(
                text = "确定要重新生成本机 Device ID 吗？\n重置后该设备在服务端将被视为新设备，需要重新登记或配对。",
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                fontSize = 13.sp,
                lineHeight = 18.sp
            )
            Spacer(Modifier.height(16.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                Button(
                    onClick = { showResetIdDialog = false },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.surfaceContainerHigh,
                        contentColor = MiuixTheme.colorScheme.onSurface
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("取消")
                }
                Button(
                    onClick = {
                        val newId = SyncSettings.resetDeviceId(context)
                        selfDeviceId = newId
                        showResetIdDialog = false
                        devicesReload++
                        scope.launch { snackbarHostState.showAppSnack("已重置 Device ID", SnackType.Success) }
                    },
                    colors = ButtonDefaults.buttonColors(
                        color = MiuixTheme.colorScheme.error,
                        contentColor = Color.White
                    ),
                    modifier = Modifier.weight(1f)
                ) {
                    Text("确定重置")
                }
            }
        }
    }

    // 移除其他设备确认对话框
    val targetDev = deleteTargetDevice
    OverlayDialog(
        show = targetDev != null,
        title = "移除设备",
        onDismissRequest = { deleteTargetDevice = null }
    ) {
        if (targetDev != null) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp)
            ) {
                Text(
                    text = "确定要从设备组中移除「${targetDev.name}」吗？",
                    fontSize = 14.sp,
                    lineHeight = 20.sp,
                    color = MiuixTheme.colorScheme.onBackground
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    text = "移除后该设备将无法接收同步内容，如需恢复需重新配对接入。",
                    fontSize = 12.sp,
                    lineHeight = 16.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Spacer(Modifier.height(16.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = { deleteTargetDevice = null },
                        colors = ButtonDefaults.buttonColors(
                            color = MiuixTheme.colorScheme.surfaceContainerHigh,
                            contentColor = MiuixTheme.colorScheme.onSurface
                        ),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("取消")
                    }
                    Button(
                        onClick = {
                            val targetId = targetDev.id
                            val targetName = targetDev.name
                            deleteTargetDevice = null
                            scope.launch {
                                val result = withContext(Dispatchers.IO) {
                                    runCatching {
                                        SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context)).deleteDevice(targetId)
                                    }
                                }
                                if (result.isSuccess && result.getOrNull() == true) {
                                    devices = devices.filterNot { it.id == targetId }
                                    if (targetId == SyncSettings.ensureDeviceId(context)) {
                                        SyncSettings.clearPairing(context)
                                        ClipboardMonitorService.stop(context)
                                    }
                                    snackbarHostState.showAppSnack("设备「$targetName」已成功移除", SnackType.Success)
                                    devicesReload++
                                } else {
                                    val err = result.exceptionOrNull()?.message ?: "移除设备失败，请稍后重试"
                                    snackbarHostState.showAppSnack(err, SnackType.Error)
                                }
                            }
                        },
                        colors = ButtonDefaults.buttonColors(
                            color = MiuixTheme.colorScheme.error,
                            contentColor = Color.White
                        ),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("确认移除")
                    }
                }
            }
        }
    }

    // 通知展示样式选择弹窗
    OverlayDialog(
        show = showNotificationStyleDialog,
        title = "选择通知展示样式",
        summary = if (SyncSettings.isHyperOs()) "已检测到小米澎湃OS (HyperOS)，推荐使用超级岛" else "根据系统支持选择合适的通知交互模式",
        onDismissRequest = { showNotificationStyleDialog = false }
    ) {
        Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
            NotificationStyle.entries.forEach { style ->
                val isSelected = notificationStyle == style
                val isRecommended = style == NotificationStyle.HYPEROS_ISLAND && SyncSettings.isHyperOs()
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(
                            if (isSelected) MiuixTheme.colorScheme.primary.copy(alpha = 0.12f)
                            else Color.Transparent
                        )
                        .clickable {
                            notificationStyle = style
                            SyncSettings.setNotificationStyle(context, style)
                            ClipboardMonitorService.updateNotification(context)
                            showNotificationStyleDialog = false
                            scope.launch {
                                snackbarHostState.showAppSnack("已切换为 ${style.label}", SnackType.Success)
                            }
                        }
                        .padding(horizontal = 12.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                text = style.label,
                                fontSize = 16.sp,
                                fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Medium,
                                color = if (isSelected) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.onSurface
                            )
                            if (isRecommended) {
                                Spacer(Modifier.width(6.dp))
                                Box(
                                    modifier = Modifier
                                        .clip(RoundedCornerShape(4.dp))
                                        .background(MiuixTheme.colorScheme.primary)
                                        .padding(horizontal = 6.dp, vertical = 2.dp)
                                ) {
                                    Text(
                                        text = "推荐",
                                        color = Color.White,
                                        fontSize = 10.sp,
                                        fontWeight = FontWeight.Bold
                                    )
                                }
                            }
                        }
                        Spacer(Modifier.height(3.dp))
                        Text(
                            text = style.summary,
                            color = MiuixTheme.colorScheme.onBackgroundVariant,
                            fontSize = 12.sp,
                            lineHeight = 16.sp
                        )
                    }
                    if (isSelected) {
                        Spacer(Modifier.width(8.dp))
                        Text(
                            text = "✓",
                            color = MiuixTheme.colorScheme.primary,
                            fontWeight = FontWeight.Bold,
                            fontSize = 16.sp
                        )
                    }
                }
                Spacer(Modifier.height(4.dp))
            }
        }
    }
}

/**
 * 一级设置分类导航行
 */
@Composable
private fun SettingsNavRow(
    title: String,
    summary: String,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = 16.sp,
                fontWeight = FontWeight.Medium
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = summary,
                color = MiuixTheme.colorScheme.onBackgroundVariant,
                fontSize = 13.sp,
                lineHeight = 17.sp
            )
        }
        Spacer(Modifier.width(8.dp))
        Icon(
            imageVector = MiuixIcons.Normal.ChevronForward,
            contentDescription = title,
            tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.45f)
        )
    }
}

@Composable
private fun DeviceCard(
    device: DeviceInfo,
    isSelf: Boolean,
    onDeleteClick: (DeviceInfo) -> Unit,
    onCopyId: (String) -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(MiuixTheme.colorScheme.surfaceContainer)
            .padding(horizontal = 14.dp, vertical = 12.dp)
    ) {
        // 头部整行：左侧（在线点+设备名+本机标签）与 右侧（状态徽章+Lucide删除按钮）
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            // 左侧：状态圆点 + 设备名称 + 本机 Pill
            Row(
                modifier = Modifier
                    .weight(1f, fill = true)
                    .padding(end = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .background(
                            color = if (device.online) Color(0xFF34C759)
                            else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.35f),
                            shape = CircleShape
                        )
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    text = device.name,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false)
                )
                if (isSelf) {
                    Spacer(Modifier.width(6.dp))
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(4.dp))
                            .background(MiuixTheme.colorScheme.primary.copy(alpha = 0.12f))
                            .padding(horizontal = 6.dp, vertical = 2.dp)
                    ) {
                        Text(
                            text = "本机",
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Bold,
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                }
            }

            // 右侧：在线/离线状态指示 + (非本机提供 Lucide 删除按钮)
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(4.dp))
                        .background(
                            (if (device.online) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant)
                                .copy(alpha = 0.12f)
                        )
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                ) {
                    Text(
                        text = if (device.online) "在线" else "离线",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = if (device.online) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant
                    )
                }

                if (!isSelf) {
                    Box(
                        modifier = Modifier
                            .size(28.dp)
                            .clip(RoundedCornerShape(6.dp))
                            .background(Color(0xFFE53935).copy(alpha = 0.12f))
                            .clickable { onDeleteClick(device) },
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            imageVector = LucideIcons.Trash2,
                            contentDescription = "移除设备",
                            tint = Color(0xFFE53935),
                            modifier = Modifier.size(15.dp)
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(8.dp))

        // 中部: 真实 IP 地址展示 (高亮样式) + 平台/版本
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = "IP: ",
                    fontSize = 12.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
                Text(
                    text = device.ip ?: "未获取",
                    fontSize = 12.sp,
                    fontWeight = if (device.ip != null) FontWeight.Medium else FontWeight.Normal,
                    color = if (device.ip != null) MiuixTheme.colorScheme.onBackground else MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
            Text(
                text = buildString {
                    append(device.platform)
                    if (!device.version.isNullOrBlank()) append(" v${device.version}")
                },
                fontSize = 12.sp,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }

        Spacer(Modifier.height(6.dp))

        // 底部: 设备 ID (可点击复制) + 最近活跃时间
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .clip(RoundedCornerShape(4.dp))
                    .clickable { onCopyId(device.id) }
                    .padding(vertical = 2.dp)
            ) {
                Text(
                    text = "ID: " + if (device.id.length > 14) device.id.take(14) + "…" else device.id,
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.85f)
                )
                Spacer(Modifier.width(4.dp))
                Icon(
                    imageVector = LucideIcons.Copy,
                    contentDescription = "复制ID",
                    tint = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.6f),
                    modifier = Modifier.size(12.dp)
                )
            }
            val lastSeen = relativeTime(device.lastSeenAt)
            if (lastSeen.isNotEmpty()) {
                Text(
                    text = "活跃: $lastSeen",
                    fontSize = 11.sp,
                    color = MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.7f)
                )
            }
        }
    }
}

/** 服务端 lastSeenAt(可能带 Z 也可能无时区后缀)→ 相对时间文本 */
private fun relativeTime(iso: String): String = try {
    val t = try {
        java.time.OffsetDateTime.parse(iso).toInstant()
    } catch (_: Exception) {
        java.time.LocalDateTime.parse(iso).toInstant(java.time.ZoneOffset.UTC)
    }
    val sec = java.time.Duration.between(t, java.time.Instant.now()).seconds
    when {
        sec < 60 -> "刚刚"
        sec < 3600 -> "${sec / 60} 分钟前"
        sec < 86400 -> "${sec / 3600} 小时前"
        else -> "${sec / 86400} 天前"
    }
} catch (_: Exception) {
    ""
}

private fun copyPairingCode(context: android.content.Context, code: String) {
    val cm = context.getSystemService(android.content.Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
    cm.setPrimaryClip(android.content.ClipData.newPlainText("PairingCode", code))
    android.widget.Toast.makeText(context, "已复制", android.widget.Toast.LENGTH_SHORT).show()
}

/** 取应用图标(关于页居中圆角头像用) */
private fun appIconBitmap(context: android.content.Context): androidx.compose.ui.graphics.ImageBitmap {
    val drawable = context.packageManager.getApplicationIcon(context.applicationInfo)
    val bmp = if (drawable is android.graphics.drawable.BitmapDrawable && drawable.bitmap != null) {
        drawable.bitmap!!
    } else {
        android.graphics.Bitmap.createBitmap(
            drawable.intrinsicWidth.coerceAtLeast(1),
            drawable.intrinsicHeight.coerceAtLeast(1),
            android.graphics.Bitmap.Config.ARGB_8888
        ).also {
            drawable.setBounds(0, 0, it.width, it.height)
            drawable.draw(android.graphics.Canvas(it))
        }
    }
    return bmp.asImageBitmap()
}

private fun appVersion(context: android.content.Context): String {
    return runCatching {
        val info = context.packageManager.getPackageInfo(
            context.packageName,
            android.content.pm.PackageManager.PackageInfoFlags.of(0L)
        )
        buildString {
            append(info.versionName ?: "?")
            append(" (")
            append(info.longVersionCode)
            append(")")
        }
    }.getOrDefault("?")
}

private fun getFolderSize(file: File?): Long {
    if (file == null || !file.exists()) return 0L
    var size = 0L
    if (file.isDirectory) {
        file.listFiles()?.forEach { size += getFolderSize(it) }
    } else {
        size += file.length()
    }
    return size
}

private fun deleteFolderContents(file: File?) {
    if (file == null || !file.exists()) return
    if (file.isDirectory) {
        file.listFiles()?.forEach {
            deleteFolderContents(it)
            it.delete()
        }
    } else {
        file.delete()
    }
}

private fun formatSize(bytes: Long): String {
    if (bytes <= 0) return "0 B"
    if (bytes < 1024) return "$bytes B"
    val kb = bytes / 1024.0
    if (kb < 1024) return "%.1f KB".format(kb)
    val mb = kb / 1024.0
    return "%.1f MB".format(mb)
}
