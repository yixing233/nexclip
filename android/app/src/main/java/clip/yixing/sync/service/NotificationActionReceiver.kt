package clip.yixing.sync.service

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.widget.Toast

/**
 * 接收并处理通知栏/超级岛 Action 按钮的广播接收器
 */
class NotificationActionReceiver : BroadcastReceiver() {

    companion object {
        const val ACTION_COPY_LATEST = "clip.yixing.sync.ACTION_COPY_LATEST"
        const val ACTION_FAVORITE_LATEST = "clip.yixing.sync.ACTION_FAVORITE_LATEST"
        const val EXTRA_CLIP_TEXT = "extra_clip_text"
        const val EXTRA_CLIP_TIME = "extra_clip_time"
    }

    override fun onReceive(context: Context, intent: Intent?) {
        when (intent?.action) {
            ACTION_COPY_LATEST -> {
                val text = intent.getStringExtra(EXTRA_CLIP_TEXT)
                    ?: ClipboardMonitorService.captured.value.firstOrNull()?.text
                if (!text.isNullOrBlank()) {
                    runCatching {
                        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cm.setPrimaryClip(ClipData.newPlainText("NexClip", text))
                        Toast.makeText(context, "已复制最新内容", Toast.LENGTH_SHORT).show()
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
        }
    }
}
