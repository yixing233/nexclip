package clip.yixing.sync.receiver

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.content.ContextCompat
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.util.SyncSettings

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        if (!SyncSettings.bootStartEnabled(context)) return
        val svc = Intent(context, ClipboardMonitorService::class.java)
        if (Build.VERSION.SDK_INT >= 26) {
            ContextCompat.startForegroundService(context, svc)
        } else {
            context.startService(svc)
        }
        clip.yixing.sync.paste.FloatingBubbleService.sync(context)

        // 开机阶段部分 ROM 会把无障碍服务重置为关闭。用 goAsync 短暂延长广播生命周期,
        // 让 root 检查能在后台完成(su 调用可能耗时,不能阻塞 onReceive)。
        if (SyncSettings.accessibilityGuardEnabled(context)) {
            val pending = goAsync()
            Thread {
                try {
                    clip.yixing.sync.root.AccessibilityGuard.ensureEnabled(context, force = true)
                } catch (e: Exception) {
                    android.util.Log.w("NexClip", "开机无障碍守护失败: ${e.message}")
                } finally {
                    pending.finish()
                }
            }.start()
        }
    }
}
