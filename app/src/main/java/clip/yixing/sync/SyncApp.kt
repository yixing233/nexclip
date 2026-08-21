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
        
        // 初始化通知渠道 (普通通知 / 实时通知 / HyperOS 超级岛)
        clip.yixing.sync.service.SyncNotificationManager.initChannels(this)
    }
}
