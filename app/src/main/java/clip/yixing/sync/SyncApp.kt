package clip.yixing.sync

import android.app.Application
import clip.yixing.sync.hook.ModuleStatusStore
import clip.yixing.sync.shizuku.ShizukuClipboardManager
import io.github.libxposed.service.XposedService
import io.github.libxposed.service.XposedServiceHelper

class SyncApp : Application(), XposedServiceHelper.OnServiceListener {
    override fun onCreate() {
        super.onCreate()
        ModuleStatusStore.attach(this)
        ShizukuClipboardManager.init(this)
        XposedServiceHelper.registerListener(this)

        // 初始化通知渠道 (普通通知 / 实时通知 / HyperOS 超级岛)
        clip.yixing.sync.service.SyncNotificationManager.initChannels(this)

        registerPackageChangeReceiver()
    }

    /**
     * 监听应用安装/卸载/更新, 失效智能动作引擎的「已安装状态」缓存。
     *
     * SmartActionEngine 缓存了各客户端变体(标准版/极速版等)的安装与否, 否则本方法注册的
     * 广播缺失时, 用户新装客户端后动作列表不会出现对应入口, 直到进程重启。此注册置于
     * Application 而非某个界面: 服务与界面都可能先于对方被创建, 只要进程活着就能收到。
     */
    private fun registerPackageChangeReceiver() {
        val filter = android.content.IntentFilter().apply {
            addAction(android.content.Intent.ACTION_PACKAGE_ADDED)
            addAction(android.content.Intent.ACTION_PACKAGE_REMOVED)
            addAction(android.content.Intent.ACTION_PACKAGE_REPLACED)
            // 包相关广播的数据是 package: scheme 的 Uri, 不加此 data scheme 收不到
            addDataScheme("package")
        }
        androidx.core.content.ContextCompat.registerReceiver(
            this,
            packageChangeReceiver,
            filter,
            androidx.core.content.ContextCompat.RECEIVER_NOT_EXPORTED
        )
    }

    private val packageChangeReceiver = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: android.content.Intent?) {
            clip.yixing.sync.smartaction.SmartActionEngine.invalidatePackageCache()
        }
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
