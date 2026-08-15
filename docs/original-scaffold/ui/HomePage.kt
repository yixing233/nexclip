package clip.yixing.sync.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.lazy.LazyColumn
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
import clip.yixing.sync.StatusRow
import clip.yixing.sync.cardContentPadding
import clip.yixing.sync.hook.ModuleStatusStore
import clip.yixing.sync.service.ClipboardMonitorService
import androidx.compose.ui.input.nestedscroll.nestedScroll
import top.yukonga.miuix.kmp.basic.ScrollBehavior
import clip.yixing.sync.util.ClipboardTest
import top.yukonga.miuix.kmp.basic.Card
import top.yukonga.miuix.kmp.basic.Switch
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical

@Composable
internal fun HomePage(scrollBehavior: ScrollBehavior, topPadding: Dp, bottomInnerPadding: Dp) {
    val context = LocalContext.current
    val serviceRunning by ClipboardMonitorService.isRunning.collectAsState()
    val captured by ClipboardMonitorService.captured.collectAsState()
    val currentText = remember(captured) {
        captured.firstOrNull()?.text ?: ClipboardTest.readClipboard(context) ?: "(空)"
    }
    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { }

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
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp)
    ) {
            item {
                ModuleStatusCard()
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Text("当前剪贴板")
                    Spacer(Modifier.height(8.dp))
                    Text(
                        text = currentText,
                        maxLines = 5,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
            item {
                Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text("持续监听剪贴板")
                            Text(if (serviceRunning) "运行中:后台监听中" else "未运行")
                        }
                        Switch(
                            checked = serviceRunning,
                            onCheckedChange = { checked ->
                                if (checked) {
                                    requestNotificationPermissionIfNeeded(context, permissionLauncher)
                                    ClipboardMonitorService.start(context)
                                } else {
                                    ClipboardMonitorService.stop(context)
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
}

@Composable
internal fun ModuleStatusCard() {
    val status by ModuleStatusStore.moduleStatus.collectAsState()
    Card(modifier = Modifier.fillMaxWidth(), insideMargin = cardContentPadding) {
        Text("模块状态")
        Spacer(Modifier.height(8.dp))
        StatusRow(
            label = "状态",
            value = if (status.activated) "已激活" else "未激活",
            valueColor = if (status.activated) {
                MiuixTheme.colorScheme.primary
            } else {
                MiuixTheme.colorScheme.onBackgroundVariant
            }
        )
        if (status.activated) {
            Spacer(Modifier.height(6.dp))
            StatusRow(
                label = "框架版本",
                value = buildString {
                    append(status.frameworkName ?: "未知框架")
                    status.frameworkVersion?.let { append(" v$it") }
                    status.frameworkVersionCode?.let { append(" ($it)") }
                }
            )
            Spacer(Modifier.height(6.dp))
            StatusRow(
                label = "Xposed API",
                value = status.apiVersion?.toString() ?: "未知"
            )
        } else {
            Spacer(Modifier.height(8.dp))
            Text(
                text = "请依次确认:\n1. LSPosed 中已启用「剪贴板同步」\n2. 作用域同时勾选「系统框架」和「剪贴板同步」应用本身\n3. 重启手机后,模块才会注入本应用进程,此处才能实时显示激活状态",
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
    }
}

private fun requestNotificationPermissionIfNeeded(
    context: android.content.Context,
    launcher: androidx.activity.compose.ManagedActivityResultLauncher<String, Boolean>
) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
    ) {
        launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
}
