package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.SnackType
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.UpdateInfo
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.ButtonDefaults
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.utils.overScrollVertical
import top.yukonga.miuix.kmp.window.WindowDialog
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun AppUpdateDialog(
    info: UpdateInfo,
    snackbarHostState: SnackbarHostState? = null,
    onDismiss: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    val openUrl: (String) -> Unit = { targetUrl ->
        runCatching {
            val intent = Intent(Intent.ACTION_VIEW, Uri.parse(targetUrl)).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            context.startActivity(intent)
        }.onFailure {
            val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("Link", targetUrl))
            if (snackbarHostState != null) {
                scope.launch { snackbarHostState.showAppSnack("链接已复制", SnackType.Success) }
            }
        }
    }

    val sourceText = if (info.isDirectSource) "直连加速通道" else "GitHub 官方源"
    val baseSummary = if (info.releaseTitle.isNotBlank() && info.releaseTitle != "v${info.latestVersion}") info.releaseTitle else "有新的版本可用，建议更新体验"

    WindowDialog(
        show = true,
        title = "发现新版本 v${info.latestVersion}",
        summary = "$baseSummary · $sourceText",
        onDismissRequest = onDismiss
    ) {
        if (info.releaseNotes.isNotBlank()) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 200.dp)
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
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = onDismiss,
                colors = ButtonDefaults.buttonColors(
                    color = MiuixTheme.colorScheme.surfaceContainerHigh,
                    contentColor = MiuixTheme.colorScheme.onSurface
                ),
                modifier = Modifier.weight(1f)
            ) {
                Text("稍后再说")
            }
            Button(
                onClick = {
                    val target = info.downloadUrl ?: info.releaseUrl
                    onDismiss()
                    openUrl(target)
                },
                modifier = Modifier.weight(1f)
            ) {
                Icon(
                    imageVector = LucideIcons.Download,
                    contentDescription = "下载",
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(4.dp))
                Text(if (info.downloadUrl != null) "立即下载" else "前往发布页")
            }
        }
    }
}
