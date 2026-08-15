package clip.yixing.sync

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import clip.yixing.sync.hook.ModuleStatusStore

class SyncApp : Application() {
    override fun onCreate() {
        super.onCreate()
        // 恢复 Xposed 模块激活状态(供首页模块状态卡片展示)
        ModuleStatusStore.attach(this)
        val channel = NotificationChannel(
            "clipboard_monitor", getString(R.string.notification_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }
}
