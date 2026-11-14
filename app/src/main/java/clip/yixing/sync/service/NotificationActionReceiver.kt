package clip.yixing.sync.service

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.widget.Toast
import clip.yixing.sync.paste.PasteEngine
import clip.yixing.sync.data.SyncApi
import clip.yixing.sync.util.SyncSettings
import clip.yixing.sync.ui.saveToDownloads
import clip.yixing.sync.paste.PasteOutcome
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * 接收并处理通知栏/超级岛 Action 按钮的广播接收器
 */
class NotificationActionReceiver : BroadcastReceiver() {

    companion object {
        const val ACTION_COPY_LATEST = "clip.yixing.sync.ACTION_COPY_LATEST"
        const val ACTION_FAVORITE_LATEST = "clip.yixing.sync.ACTION_FAVORITE_LATEST"
        const val ACTION_PASTE_LATEST = "clip.yixing.sync.ACTION_PASTE_LATEST"
        const val ACTION_COPY_TEXT = "clip.yixing.sync.ACTION_COPY_TEXT"
        const val ACTION_EXECUTE_INTENT = "clip.yixing.sync.ACTION_EXECUTE_INTENT"
        const val ACTION_FILE_ACCEPT = "clip.yixing.sync.ACTION_FILE_ACCEPT"
        const val ACTION_FILE_REJECT = "clip.yixing.sync.ACTION_FILE_REJECT"
        const val EXTRA_FILE_ID = "extra_file_id"
        const val EXTRA_CLIP_TEXT = "extra_clip_text"
        const val EXTRA_IS_IMAGE = "extra_is_image"
        const val EXTRA_IMAGE_REF = "extra_image_ref"
        const val EXTRA_TOAST_MSG = "extra_toast_msg"
        const val EXTRA_TARGET_INTENT = "extra_target_intent"
    }

    override fun onReceive(context: Context, intent: Intent?) {
        // 用户点击任何快捷操作按钮后，自动移除当前弹出的超级岛 / 快捷通知
        runCatching {
            val nm = context.getSystemService(android.app.NotificationManager::class.java)
            nm.cancel(SyncNotificationManager.NOTIFICATION_ID_PUSH)
        }

        when (intent?.action) {
            ACTION_FILE_ACCEPT, ACTION_FILE_REJECT -> {
                val fileId = intent.getStringExtra(EXTRA_FILE_ID) ?: return
                val accept = intent.action == ACTION_FILE_ACCEPT
                val pendingResult = goAsync()
                CoroutineScope(Dispatchers.IO).launch {
                    var savedPath: String? = null
                    var errorMsg: String? = null
                    try {
                        if (SyncSettings.isPaired(context) && SyncSettings.deviceToken(context).isNotBlank()) {
                            val api = SyncApi(SyncSettings.serverUrl(context), SyncSettings.ensureDeviceId(context), SyncSettings.deviceToken(context))
                            val ok = if (accept) api.acceptFile(fileId) else api.rejectFile(fileId)
                            if (ok && accept) {
                                val meta = ClipboardMonitorService.pendingFileRequests.value.firstOrNull { it.fileId == fileId }
                                val bytes = api.downloadFile(fileId)
                                if (bytes != null) {
                                    val uri = clip.yixing.sync.ui.saveToDownloads(context, meta?.fileName ?: "nexclip-$fileId", bytes)
                                    savedPath = uri?.toString()
                                } else errorMsg = "文件已不可下载"
                            }
                        } else errorMsg = "配对状态不可用"
                    } catch (e: Exception) {
                        errorMsg = e.message
                    }
                    CoroutineScope(Dispatchers.Main).launch {
                        val nm = context.getSystemService(android.app.NotificationManager::class.java)
                        nm.cancel(SyncNotificationManager.NOTIFICATION_ID_FILE_CONSENT)
                        if (!accept) {
                            Toast.makeText(context, "已拒绝文件传输", Toast.LENGTH_SHORT).show()
                        } else if (savedPath != null) {
                            Toast.makeText(context, "已接受,文件保存到 Downloads", Toast.LENGTH_SHORT).show()
                        } else {
                            Toast.makeText(context, "已接受,但保存失败: ${errorMsg ?: "未知错误"}", Toast.LENGTH_LONG).show()
                        }
                        pendingResult.finish()
                    }
                }
            }
            ACTION_COPY_LATEST -> {
                val isImage = intent.getBooleanExtra(EXTRA_IS_IMAGE, false)
                val imageRef = intent.getStringExtra(EXTRA_IMAGE_REF)
                val latestClip = ClipboardMonitorService.captured.value.firstOrNull()

                if (isImage || !imageRef.isNullOrBlank() || latestClip?.isImage == true) {
                    val ref = imageRef ?: latestClip?.imageRef ?: latestClip?.text
                    if (!ref.isNullOrBlank()) {
                        val pendingResult = goAsync()
                        CoroutineScope(Dispatchers.IO).launch {
                            val success = clip.yixing.sync.util.ImageLoader.copyImageToClipboard(context, ref, latestClip?.text)
                            CoroutineScope(Dispatchers.Main).launch {
                                if (success) {
                                    Toast.makeText(context, "已复制图片到剪贴板", Toast.LENGTH_SHORT).show()
                                } else {
                                    // 降级使用文本
                                    val text = intent.getStringExtra(EXTRA_CLIP_TEXT) ?: latestClip?.text
                                    if (!text.isNullOrBlank()) {
                                        ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("NexClip", text))
                                        Toast.makeText(context, "已复制内容", Toast.LENGTH_SHORT).show()
                                    }
                                }
                                pendingResult.finish()
                            }
                        }
                    }
                } else {
                    val text = intent.getStringExtra(EXTRA_CLIP_TEXT) ?: latestClip?.text
                    if (!text.isNullOrBlank()) {
                        runCatching {
                            ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("NexClip", text))
                            Toast.makeText(context, "已复制最新内容", Toast.LENGTH_SHORT).show()
                        }
                    }
                }
            }

            ACTION_COPY_TEXT -> {
                val text = intent.getStringExtra(EXTRA_CLIP_TEXT)
                val toastMsg = intent.getStringExtra(EXTRA_TOAST_MSG) ?: "已复制"
                if (!text.isNullOrBlank()) {
                    runCatching {
                        ClipboardMonitorService.copyToClipboardInternal(context, ClipData.newPlainText("NexClip", text), rawText = text)
                        Toast.makeText(context, toastMsg, Toast.LENGTH_SHORT).show()
                    }
                }
            }

            // 通知栏「粘贴」：通知收起后目标输入框才会重新拿到焦点，
            // 重试循环放在 NexClipAccessibilityService.awaitFocusedEditable 里，这里只管调度。
            ACTION_PASTE_LATEST -> {
                val clip = ClipboardMonitorService.captured.value.firstOrNull()
                if (clip != null) {
                    val pendingResult = goAsync()
                    CoroutineScope(Dispatchers.Main).launch {
                        val outcome = try {
                            PasteEngine.paste(context, clip)
                        } catch (_: Throwable) {
                            PasteOutcome.FAILED
                        }
                        Toast.makeText(context, outcome.message, Toast.LENGTH_SHORT).show()
                        pendingResult.finish()
                    }
                }
            }

            ACTION_FAVORITE_LATEST -> {
                val clip = ClipboardMonitorService.captured.value.firstOrNull()
                if (clip != null) {
                    ClipboardMonitorService.toggleFavorite(context, clip)
                    val msg = if (!clip.isFavorite) "已添加到收藏" else "已取消收藏"
                    Toast.makeText(context, msg, Toast.LENGTH_SHORT).show()
                }
            }

            ACTION_EXECUTE_INTENT -> {
                val targetIntent = if (android.os.Build.VERSION.SDK_INT >= 33) {
                    intent.getParcelableExtra(EXTRA_TARGET_INTENT, Intent::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(EXTRA_TARGET_INTENT)
                }
                if (targetIntent != null) {
                    runCatching {
                        targetIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                        context.startActivity(targetIntent)
                    }
                }
            }
        }
    }
}
