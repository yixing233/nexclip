package clip.yixing.sync.ui

import android.content.ClipData
import android.content.ClipDescription
import android.content.Context
import android.net.Uri
import android.view.View
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.border
import androidx.compose.foundation.draganddrop.dragAndDropSource
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draganddrop.DragAndDropEvent
import androidx.compose.ui.draganddrop.DragAndDropTarget
import androidx.compose.ui.draganddrop.DragAndDropTransferData
import androidx.compose.ui.draganddrop.mimeTypes
import androidx.compose.ui.draganddrop.toAndroidDragEvent
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import clip.yixing.sync.SnackType
import clip.yixing.sync.service.CapturedClip
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.showAppSnack
import clip.yixing.sync.util.ImageLoader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import top.yukonga.miuix.kmp.basic.SnackbarHostState
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.io.File

/**
 * 拖拽源修饰符 (支持将文本或图片条目手动拖出至其他分屏/小窗应用)
 */
fun Modifier.clipDragSource(
    context: Context,
    clip: CapturedClip
): Modifier = this.dragAndDropSource { _ ->
    val clipData = if (clip.isImage) {
        val cacheDir = File(context.cacheDir, "clip_images")
        val key = clip.imageRef?.takeIf { it.isNotBlank() } ?: "drag_img"
        val cleanName = key.replace(Regex("[^a-zA-Z0-9_.-]"), "_") + ".png"
        val file = File(cacheDir, cleanName)
        val uri = if (file.exists()) {
            runCatching {
                FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
            }.getOrNull()
        } else null

        if (uri != null) {
            ClipData.newUri(context.contentResolver, "NexClip Image", uri)
        } else {
            ClipData.newPlainText("NexClip Text", clip.text)
        }
    } else {
        ClipData.newPlainText("NexClip Text", clip.text)
    }

    DragAndDropTransferData(
        clipData = clipData,
        flags = if (clip.isImage) View.DRAG_FLAG_GLOBAL or View.DRAG_FLAG_GLOBAL_URI_READ else View.DRAG_FLAG_GLOBAL
    )
}

/**
 * 拖拽接收目标修饰符 (支持接收从外部应用拖入的文本或图片并自动保存同步)
 */
@Composable
fun Modifier.clipDropTarget(
    snackbarHostState: SnackbarHostState? = null,
    onDropped: ((CapturedClip) -> Unit)? = null
): Modifier {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var isHovered by remember { mutableStateOf(false) }

    val borderColor by animateColorAsState(
        targetValue = if (isHovered) MiuixTheme.colorScheme.primary else Color.Transparent,
        animationSpec = tween(150),
        label = "drop_border"
    )

    return this
        .border(
            width = if (isHovered) 2.dp else 0.dp,
            color = borderColor,
            shape = RoundedCornerShape(16.dp)
        )
        .dragAndDropTarget(
            shouldStartDragAndDrop = { event ->
                val types = event.mimeTypes()
                types.any {
                    it.contains("text", ignoreCase = true) ||
                    it.contains("image", ignoreCase = true) ||
                    it == ClipDescription.MIMETYPE_TEXT_PLAIN ||
                    it == ClipDescription.MIMETYPE_TEXT_HTML ||
                    it == ClipDescription.MIMETYPE_TEXT_URILIST
                }
            },
            target = remember {
                object : DragAndDropTarget {
                    override fun onEntered(event: DragAndDropEvent) {
                        isHovered = true
                    }

                    override fun onExited(event: DragAndDropEvent) {
                        isHovered = false
                    }

                    override fun onEnded(event: DragAndDropEvent) {
                        isHovered = false
                    }

                    override fun onDrop(event: DragAndDropEvent): Boolean {
                        isHovered = false
                        val androidDragEvent = event.toAndroidDragEvent()
                        val clipData = androidDragEvent.clipData ?: return false
                        if (clipData.itemCount <= 0) return false

                        scope.launch(Dispatchers.IO) {
                            var savedCount = 0
                            for (i in 0 until clipData.itemCount) {
                                val item = clipData.getItemAt(i) ?: continue
                                val uri: Uri? = item.uri
                                val mimeType = uri?.let { context.contentResolver.getType(it) }

                                if (uri != null && (mimeType?.startsWith("image/") == true || mimeType == null)) {
                                    val hash = ImageLoader.saveDroppedImage(context, uri)
                                    if (hash != null) {
                                        withContext(Dispatchers.Main) {
                                            ClipboardMonitorService.addCaptured(context, "[图片]", hash)
                                            val newClip = CapturedClip(text = "[图片]", time = System.currentTimeMillis(), imageRef = hash)
                                            onDropped?.invoke(newClip)
                                        }
                                        savedCount++
                                        continue
                                    }
                                }

                                val text = item.text?.toString()
                                    ?: item.coerceToText(context)?.toString()
                                    ?: ""

                                if (text.isNotBlank()) {
                                    withContext(Dispatchers.Main) {
                                        ClipboardMonitorService.addCaptured(context, text)
                                        val newClip = CapturedClip(text = text, time = System.currentTimeMillis())
                                        onDropped?.invoke(newClip)
                                    }
                                    savedCount++
                                }
                            }

                            if (savedCount > 0) {
                                withContext(Dispatchers.Main) {
                                    snackbarHostState?.showAppSnack("已通过拖拽保存 $savedCount 条内容", SnackType.Success)
                                }
                            }
                        }
                        return true
                    }
                }
            }
        )
}
