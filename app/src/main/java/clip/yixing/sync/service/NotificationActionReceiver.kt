package clip.yixing.sync.service

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.widget.Toast
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
        const val ACTION_COPY_TEXT = "clip.yixing.sync.ACTION_COPY_TEXT"
        const val ACTION_EXECUTE_INTENT = "clip.yixing.sync.ACTION_EXECUTE_INTENT"
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
