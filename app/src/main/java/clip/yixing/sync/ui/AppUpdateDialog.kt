package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.UpdateChecker
import clip.yixing.sync.util.UpdateInfo
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.utils.overScrollVertical
import top.yukonga.miuix.kmp.window.WindowDialog
import java.io.File

@Composable
fun AppUpdateDialog(
    info: UpdateInfo,
    snackbarHostState: SnackbarHostState? = null,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var isDownloading by remember { mutableStateOf(false) }
    var downloadProgress by remember { mutableFloatStateOf(0f) }
    var downloadSpeed by remember { mutableStateOf("") }
    var downloadBytesText by remember { mutableStateOf("") }
    var downloadedApkFile by remember { mutableStateOf<File?>(null) }
    var downloadError by remember { mutableStateOf<String?>(null) }

    val openUrl: (String) -> Unit = { targetUrl ->
        runCatching {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(targetUrl)).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        }.onFailure {
            clip.yixing.sync.service.ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("Link", targetUrl), rawText = targetUrl)
            if (snackbarHostState != null) {
                scope.launch { snackbarHostState.showAppSnack("链接已复制", SnackType.Success) }
            }
        }
    }

    val startDownload: () -> Unit = {
        val url = info.downloadUrl
        if (!url.isNullOrBlank()) {
            isDownloading = true
            downloadError = null
            scope.launch {
                val result = UpdateChecker.downloadApk(
                    context = context,
                    downloadUrl = url,
                    latestVersion = info.latestVersion,
                    expectedSha256 = info.sha256
                ) { read, total, pct, speed ->
                    downloadProgress = pct
                    downloadSpeed = speed
                    downloadBytesText = if (total > 0) "${UpdateChecker.formatBytes(read)} / ${UpdateChecker.formatBytes(total)}" else UpdateChecker.formatBytes(read)
                }

                isDownloading = false
                result.onSuccess { apk ->
                    downloadedApkFile = apk
                    downloadProgress = 100f
                    // 自动唤起系统安装器
                    runCatching {
                        UpdateChecker.installApk(context, apk)
                    }.onFailure { ex ->
                        snackbarHostState?.showAppSnack("唤起安装器失败: ${ex.message}", SnackType.Error)
                    }
                }.onFailure { ex ->
                    downloadError = ex.message
                    snackbarHostState?.showAppSnack("下载失败: ${ex.message}", SnackType.Error)
                }
            }
        } else {
            openUrl(info.releaseUrl)
        }
    }

    val sourceText = if (info.isDirectSource) "直连加速通道" else "GitHub 官方源"
    val baseSummary = if (info.releaseTitle.isNotBlank() && info.releaseTitle != "v${info.latestVersion}") info.releaseTitle else "有新的版本可用，建议更新体验"

    WindowDialog(
        show = true,
        title = "发现新版本 v${info.latestVersion}",
        summary = "$baseSummary · $sourceText",
        onDismissRequest = {
            if (!isDownloading) onDismiss()
        }
    ) {
        if (info.releaseNotes.isNotBlank()) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 180.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(MiuixTheme.colorScheme.surfaceContainerHigh)
                    .padding(12.dp)
            ) {
                LazyColumn(modifier = Modifier.fillMaxWidth().overScrollVertical()) {
                    item {
                        Text(
                            text = info.releaseNotes,
                            fontSize = 13.sp,
                            lineHeight = 18.sp,
                            color = MiuixTheme.colorScheme.onSurface
                        )
                    }
                }
            }
            Spacer(Modifier.height(12.dp))
        }

        // 下载进度显示
        AnimatedVisibility(visible = isDownloading || downloadedApkFile != null) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(8.dp))
                    .background(MiuixTheme.colorScheme.surfaceContainerHigh)
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                // 自定义现代进度条
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(6.dp)
                        .clip(RoundedCornerShape(3.dp))
                        .background(MiuixTheme.colorScheme.surfaceContainerHighest)
                ) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth(downloadProgress / 100f)
                            .fillMaxHeight()
                            .clip(RoundedCornerShape(3.dp))
                            .background(MiuixTheme.colorScheme.primary)
                    )
                }

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = if (downloadedApkFile != null) "下载完成，点击立即安装" else (downloadBytesText.ifEmpty { "正在下载安装包..." }),
                        fontSize = 11.sp,
                        color = MiuixTheme.colorScheme.onBackgroundVariant
                    )
                    if (downloadSpeed.isNotBlank()) {
                        Text(
                            text = downloadSpeed,
                            fontSize = 11.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = MiuixTheme.colorScheme.primary
                        )
                    }
                }
            }
        }

        if (isDownloading || downloadedApkFile != null) {
            Spacer(Modifier.height(12.dp))
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 4.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = onDismiss,
                enabled = !isDownloading,
                colors = ButtonDefaults.buttonColors(
                    color = MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = MiuixTheme.colorScheme.onSurface
                ),
                modifier = Modifier.weight(1f)
            ) {
                Text(if (downloadedApkFile != null) "关闭" else "稍后再说")
            }

            if (downloadedApkFile != null) {
                Button(
                    onClick = {
                        UpdateChecker.installApk(context, downloadedApkFile!!)
                    },
                    modifier = Modifier.weight(1f)
                ) {
                    Icon(
                        imageVector = LucideIcons.Check,
                        contentDescription = "安装",
                        modifier = Modifier.size(16.dp)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text("立即安装")
                }
            } else if (!isDownloading) {
                Button(
                    onClick = startDownload,
                    modifier = Modifier.weight(1f)
                ) {
                    Icon(
                        imageVector = LucideIcons.Download,
                        contentDescription = "下载",
                        modifier = Modifier.size(16.dp)
                    )
                    Spacer(Modifier.width(4.dp))
                    Text(if (info.downloadUrl != null) "立即更新" else "前往发布页")
                }
            }
        }
    }
}
