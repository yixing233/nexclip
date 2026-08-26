package clip.yixing.sync.hook

import android.content.Context
import io.github.libxposed.service.XposedService
import java.io.File
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 模块激活状态存储,供 App 进程 UI 实时读取。
 *
 * 遵循现代 LibXposed 标准架构:
 * 1. 启动时初始状态为未激活 (activated = false)；
 * 2. 通过 XposedServiceHelper 与 LSPosed 服务建立 Binder 连接，实时响应 onServiceBind / onServiceDied；
 * 3. 不在本地缓存残留历史激活状态，确保关闭模块或重启后状态精准即时同步。
 */
object ModuleStatusStore {

    data class ModuleStatus(
        val activated: Boolean = false,
        val frameworkName: String? = null,
        val frameworkVersion: String? = null,
        val frameworkVersionCode: Long? = null,
        val apiVersion: Int? = null,
        val processName: String? = null
    )

    private const val PREFS_NAME = "module_status"
    private const val STATUS_FILE = "module_status.json"

    private val _moduleStatus = MutableStateFlow(ModuleStatus())
    val moduleStatus: StateFlow<ModuleStatus> = _moduleStatus.asStateFlow()

    fun attach(context: Context) {
        // 清理旧版历史残留缓存文件，避免状态陈旧不一致
        runCatching {
            File(context.filesDir, STATUS_FILE).delete()
            context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit().clear().apply()
        }
    }

    /**
     * 当 XposedService 成功绑定时(LSPosed 激活)，直接由框架回调更新状态。
     */
    fun updateFromService(service: XposedService) {
        val status = ModuleStatus(
            activated = true,
            frameworkName = runCatching { service.frameworkName }.getOrNull(),
            frameworkVersion = runCatching { service.frameworkVersion }.getOrNull(),
            frameworkVersionCode = runCatching { service.frameworkVersionCode }.getOrNull(),
            apiVersion = runCatching { service.apiVersion }.getOrNull(),
            processName = "app"
        )
        _moduleStatus.value = status
    }

    fun onServiceDied() {
        _moduleStatus.value = ModuleStatus(activated = false)
    }

    fun update(status: ModuleStatus) {
        _moduleStatus.value = status
    }
}
