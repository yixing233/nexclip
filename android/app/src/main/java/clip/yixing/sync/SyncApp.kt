package clip.yixing.sync

import android.app.Application
import clip.yixing.sync.hook.ModuleStatusStore
import io.github.libxposed.service.XposedService
import io.github.libxposed.service.XposedServiceHelper

class SyncApp : Application(), XposedServiceHelper.OnServiceListener {
    override fun onCreate() {
        super.onCreate()
        ModuleStatusStore.attach(this)
        XposedServiceHelper.registerListener(this)
        
        // 初始化通知渠道 (普通通知 / 实时通知 / HyperOS 超级岛)
        clip.yixing.sync.service.SyncNotificationManager.initChannels(this)
    }

    override fun onServiceBind(service: XposedService) {
        xposedService = service
        ModuleStatusStore.updateFromService(service)
    }

    override fun onServiceDied(service: XposedService) {
        if (xposedService == service) {
            xposedService = null
            ModuleStatusStore.onServiceDied()
        }
    }

    companion object {
        @Volatile
        var xposedService: XposedService? = null
            private set
    }
}
