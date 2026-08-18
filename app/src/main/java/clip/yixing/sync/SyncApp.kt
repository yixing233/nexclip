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
        
        val nm = getSystemService(NotificationManager::class.java)
        // 1. 前台常驻监听服务通知渠道（静音低打扰）
        val monitorChannel = NotificationChannel(
            "clipboard_monitor",
            "剪贴板监听服务",
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = "用于保持后台剪贴板监听与 SignalR 连接"
            setShowBadge(false)
        }
        
        // 2. 接收外部同步推送通知渠道（提示通知）
        val pushChannel = NotificationChannel(
            "clipboard_sync_push",
            "剪贴板同步推送",
            NotificationManager.IMPORTANCE_DEFAULT,
        ).apply {
            description = "收到其他设备推送的新剪贴板内容时发送提示"
            setShowBadge(true)
        }
        
        nm.createNotificationChannel(monitorChannel)
        nm.createNotificationChannel(pushChannel)
    }
}
