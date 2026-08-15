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
    }
}
