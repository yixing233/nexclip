package clip.yixing.sync.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.data.DeviceInfo
import clip.yixing.sync.data.PairingCode
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.util.SyncSettings
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.HorizontalDivider
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.IconButton
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.overlay.OverlayBottomSheet
import top.yukonga.miuix.kmp.overlay.OverlayDialog
import top.yukonga.miuix.kmp.preference.OverlayDropdownPreference
import top.yukonga.miuix.kmp.preference.SwitchPreference
import top.yukonga.miuix.kmp.icon.MiuixIcons
import top.yukonga.miuix.kmp.icon.extended.Refresh
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
internal fun SettingsPage(scrollBehavior: ScrollBehavior, topPadding: Dp, bottomInnerPadding: Dp) {
    val context = LocalContext.current
    val prefs = remember { SyncSettings.prefs(context) }

    // 服务器配置输入
    val urlState = remember { TextFieldState(SyncSettings.serverUrl(context)) }
    var saved by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    var testing by remember { mutableStateOf(false) }
    var testResult by remember { mutableStateOf<Pair<Boolean, String>?>(null) }
    var pairing by remember { mutableStateOf(false) }
    var pairResult by remember { mutableStateOf<Pair<Boolean, String>?>(null) }

    // 配对对话框:输入配对码用对话框完成
    var showPairDialog by remember { mutableStateOf(false) }
    val dialogCodeState = remember { TextFieldState("") }

    // 配对码管理(已配对设备可直接生成,无需管理令牌)
    var generatingCode by remember { mutableStateOf(false) }
    var genResult by remember { mutableStateOf<Pair<Boolean, String>?>(null) }
    var generatedCode by remember { mutableStateOf<PairingCode?>(null) }
    // 生成的配对码用底部弹层查看;关闭底部弹层即作废(不按时间失效)
    var showCodeSheet by remember { mutableStateOf(false) }

    // 设备列表(服务端登记的设备,含在线状态)
    var devices by remember { mutableStateOf<List<DeviceInfo>>(emptyList()) }
    var devicesLoading by remember { mutableStateOf(true) }
    var devicesError by remember { mutableStateOf<String?>(null) }
    var devicesReload by remember { mutableStateOf(0) }
    val selfDeviceId = remember { SyncSettings.ensureDeviceId(context) }

    LaunchedEffect(devicesReload) {
        while (true) {
            devicesLoading = devices.isEmpty()
            devicesError = null
            try {
                val api = SyncApi(SyncSettings.serverUrl(context))
                devices = withContext(Dispatchers.IO) { api.getDevices() }
            } catch (e: Exception) {
                devicesError = e.message ?: "加载失败"
            }
            devicesLoading = false
            delay(30_000)
        }
    }

    // 监听与历史
    var bootStart by remember { mutableStateOf(SyncSettings.bootStartEnabled(context)) }
    val historyOptions = SyncSettings.MAX_HISTORY_OPTIONS.toList()
    val historyLabels = historyOptions.map { "$it 条" }
    var historyIndex by remember {
        mutableStateOf(
            historyOptions.indexOf(SyncSettings.maxHistory(context)).coerceAtLeast(0)
        )
    }

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
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("服务器设置")
                    Spacer(Modifier.height(10.dp))
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
                                prefs.edit()
                                    .putString(SyncSettings.KEY_SERVER_URL, urlState.text.toString().trim())
                                    .apply()
                                saved = true
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
                                    testResult = false to "请先填写服务器地址"
                                    return@Button
                                }
                                testing = true
                                testResult = null
                                val api = SyncApi(url)
                                scope.launch {
                                    val (ok, msg) = withContext(Dispatchers.IO) {
                                        api.testConnection()
                                    }
                                    testing = false
                                    testResult = ok to msg
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
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                    testResult?.let { (ok, msg) ->
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = msg,
                            color = if (ok) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.error
                        )
                    }
                    if (saved) {
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = "已保存",
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("配对管理")
                    Spacer(Modifier.height(10.dp))
                    // 生成配对码 → 底部弹层查看
                    Button(
                        onClick = {
                            val url = urlState.text.toString().trim()
                                .ifEmpty { SyncSettings.serverUrl(context) }
                            if (url.isEmpty()) {
                                genResult = false to "请先填写服务器地址"
                                return@Button
                            }
                            generatingCode = true
                            genResult = null
                            // 生成配对码免认证:任何设备(含未配对的新设备)都可直接生成;
                            // 携带本机设备信息,服务端登记生成方设备
                            val api = SyncApi(url)
                            val genDeviceId = SyncSettings.ensureDeviceId(context)
                            val genDeviceName = SyncSettings.deviceName(context)
                            scope.launch {
                                val r = withContext(Dispatchers.IO) {
                                    runCatching { api.createPairingCode(genDeviceId, genDeviceName) }
                                }
                                generatingCode = false
                                r.onSuccess {
                                    generatedCode = it
                                    genResult = true to "配对码已生成"
                                    // 生成成功:弹出底部弹层查看配对码
                                    showCodeSheet = true
                                }.onFailure { e ->
                                    genResult = false to (e.message ?: "生成失败")
                                }
                            }
                        },
                        enabled = !generatingCode,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(if (generatingCode) "生成中…" else "生成配对码")
                    }
                    Spacer(Modifier.height(8.dp))
                    // 输入配对码 → 对话框配对
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
                            text = "正在与服务器配对…",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                    genResult?.let { (ok, msg) ->
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = msg,
                            color = if (ok) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.error
                        )
                    }
                    pairResult?.let { (ok, msg) ->
                        Spacer(Modifier.height(6.dp))
                        Text(
                            text = msg,
                            color = if (ok) MiuixTheme.colorScheme.primary else MiuixTheme.colorScheme.error
                        )
                    }
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("设备列表", modifier = Modifier.weight(1f))
                        Text(
                            text = if (devicesLoading && devices.isEmpty()) "加载中…"
                            else "${devices.count { it.online }} / ${devices.size} 在线",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                        Spacer(Modifier.width(6.dp))
                        IconButton(onClick = { devicesReload++ }) {
                            Icon(
                                imageVector = MiuixIcons.Refresh,
                                contentDescription = "刷新",
                                tint = MiuixTheme.colorScheme.onBackgroundVariant
                            )
                        }
                    }
                    devicesError?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(text = it, color = MiuixTheme.colorScheme.error)
                    }
                    if (devices.isEmpty() && !devicesLoading && devicesError == null) {
                        Spacer(Modifier.height(8.dp))
                        Text(
                            text = "暂无设备。开启首页「持续监听剪贴板」后,本机将自动登记。",
                            color = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                    devices.forEach { device ->
                        Spacer(Modifier.height(8.dp))
                        DeviceRow(device = device, isSelf = device.id == selfDeviceId)
                    }
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth()) {
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
                    HorizontalDivider(
                        color = MiuixTheme.colorScheme.dividerLine,
                        thickness = Dp.Hairline,
                        modifier = Modifier.padding(horizontal = 16.dp)
                    )
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
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("使用说明")
                    Spacer(Modifier.height(8.dp))
                    Text(
                        text = "1. 在 LSPosed 中启用「剪贴板同步」\n" +
                            "2. 作用域勾选「系统框架」和「剪贴板同步」应用本身\n" +
                            "3. 重启手机后生效\n" +
                            "4. 返回首页开启「持续监听剪贴板」"
                    )
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("关于")
                    Spacer(Modifier.height(8.dp))
                    Text(
                        text = "剪贴板同步 " + appVersion(context) + "\n基于 Miuix + LSPosed 构建"
                    )
                }
            }
            item {
                Spacer(Modifier.height(bottomInnerPadding))
            }
        }

    // 配对对话框:输入配对码完成配对
    OverlayDialog(
        show = showPairDialog,
        title = "设备配对",
        summary = "输入另一台设备生成的配对码(10 分钟有效,一码一设备)",
        onDismissRequest = { showPairDialog = false }
    ) {
        TextField(
            state = dialogCodeState,
            label = "配对码",
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
                        pairResult = false to "请先填写服务器地址"
                        return@Button
                    }
                    if (code.isEmpty()) {
                        pairResult = false to "请输入配对码"
                        return@Button
                    }
                    pairing = true
                    pairResult = null
                    showPairDialog = false
                    val api = SyncApi(url) // 配对接口无需认证
                    val deviceId = SyncSettings.ensureDeviceId(context)
                    val deviceName = SyncSettings.deviceName(context)
                    scope.launch {
                        val result = withContext(Dispatchers.IO) {
                            runCatching { api.pair(code, deviceId, deviceName) }
                        }
                        pairing = false
                        result.onSuccess { r ->
                            prefs.edit()
                                .putString(SyncSettings.KEY_SERVER_URL, url)
                                .apply()
                            SyncSettings.setPaired(context, true)
                            pairResult = true to "配对成功"
                            dialogCodeState.edit { replace(0, length, "") }
                            // 本机已由服务端登记:立即刷新设备列表
                            devicesReload++
                            // 监听服务若在运行,重启以用新令牌重连 SignalR,本机保持在线
                            if (ClipboardMonitorService.isRunning.value) {
                                ClipboardMonitorService.stop(context)
                                ClipboardMonitorService.start(context)
                            }
                        }.onFailure { e ->
                            pairResult = false to (e.message ?: "配对失败")
                        }
                    }
                },
                enabled = !pairing,
                modifier = Modifier.weight(1f)
            ) {
                Text(if (pairing) "配对中…" else "确认配对")
            }
        }
    }

    // 生成的配对码用底部弹层查看(大号展示 + 倒计时 + 复制)
    OverlayBottomSheet(
        show = showCodeSheet,
        title = "配对码",
        onDismissRequest = {
            showCodeSheet = false
            // 关闭底部弹层 → 配对码立即作废
            val revokeCode = generatedCode?.code
            generatedCode = null
            if (revokeCode != null) {
                scope.launch {
                    withContext(Dispatchers.IO) {
                        runCatching { SyncApi(SyncSettings.serverUrl(context)).revokePairingCode(revokeCode) }
                    }
                }
            }
        }
    ) {
        generatedCode?.let { code ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 32.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(14.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainer)
                        .padding(vertical = 20.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = code.code,
                        style = MiuixTheme.textStyles.title1
                    )
                }
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.Center,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "关闭后此配对码立即失效",
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                    Spacer(Modifier.width(16.dp))
                    Text(
                        text = "复制",
                        color = MiuixTheme.colorScheme.primary,
                        modifier = Modifier
                            .clickable { copyPairingCode(context, code.code) }
                            .padding(horizontal = 4.dp, vertical = 2.dp)
                    )
                }
                Spacer(Modifier.height(4.dp))
                Text(
                    text = "把此码输入到新设备的配对框即可接入,一次性使用",
                    color = MiuixTheme.colorScheme.onBackgroundVariant,
                    fontSize = 12.sp
                )
            }
        }
    }
}

@Composable
private fun DeviceRow(device: DeviceInfo, isSelf: Boolean) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .background(
                            color = if (device.online) Color(0xFF34C759)
                            else MiuixTheme.colorScheme.onBackgroundVariant.copy(alpha = 0.35f),
                            shape = CircleShape
                        )
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = device.name,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (isSelf) {
                    Spacer(Modifier.width(6.dp))
                    Text(
                        text = "本机",
                        color = MiuixTheme.colorScheme.primary
                    )
                }
            }
            Spacer(Modifier.height(2.dp))
            Text(
                text = buildString {
                    append(device.platform)
                    device.version?.let { append(" v$it") }
                    device.ip?.let { append(" · $it") }   // 数据层已规范化(去 ::ffff: 前缀)
                    val t = relativeTime(device.lastSeenAt)
                    if (t.isNotEmpty()) append(" · $t")
                },
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
        Spacer(Modifier.width(10.dp))
        Text(
            text = if (device.online) "在线" else "离线",
            color = if (device.online) Color(0xFF34C759) else MiuixTheme.colorScheme.onBackgroundVariant
        )
    }
}

/** 服务端 lastSeenAt(可能带 Z 也可能无时区后缀)→ 相对时间文本 */
private fun relativeTime(iso: String): String = try {
    val t = try {
        java.time.OffsetDateTime.parse(iso).toInstant()
    } catch (_: Exception) {
        // 无时区后缀:按 UTC 处理(服务端以 UtcNow 写入)
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
