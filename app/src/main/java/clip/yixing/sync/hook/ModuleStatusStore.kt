package clip.yixing.sync.hook

import android.content.Context
import android.content.SharedPreferences
import android.content.pm.ApplicationInfo
import java.io.File
import org.json.JSONObject
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 模块激活状态存储,供 App 进程 UI 读取。
 *
 * 注意:libxposed 的 onModuleLoaded 会在「每个被注入的进程」中回调,
 * 内存状态是进程隔离的 —— 只有模块被注入到本应用进程时,
 * 这里的 StateFlow 才会被实时更新。因此同时把状态持久化到本应用
 * SharedPreferences(由 SyncApp 在 Application.onCreate 时恢复),
 * 这样即使模块只注入到了 system_server,重启 App 后也能显示上次状态。
 *
 * 要求:LSPosed 作用域必须同时勾选「系统框架」和「剪贴板同步」应用本身。
 */
object ModuleStatusStore {

    data class ModuleStatus(
        val activated: Boolean = false,
        val frameworkName: String? = null,
        val frameworkVersion: String? = null,
        val frameworkVersionCode: Long? = null,
        val apiVersion: Int? = null,
        /** 模块本次加载到的进程名(如 system_server 或本应用进程)。 */
        val processName: String? = null
    )

    private const val PREFS_NAME = "module_status"
    private const val KEY_ACTIVATED = "activated"
    private const val KEY_FRAMEWORK_NAME = "framework_name"
    private const val KEY_FRAMEWORK_VERSION = "framework_version"
    private const val KEY_FRAMEWORK_VERSION_CODE = "framework_version_code"
    private const val KEY_API_VERSION = "api_version"
    private const val KEY_PROCESS_NAME = "process_name"

    /** 模块侧写入的状态文件名(位于应用 files 目录)。 */
    private const val STATUS_FILE = "module_status.json"

    private val _moduleStatus = MutableStateFlow(ModuleStatus())
    val moduleStatus: StateFlow<ModuleStatus> = _moduleStatus.asStateFlow()

    private var prefs: SharedPreferences? = null

    /**
     * 由 SyncApp(Application.onCreate)调用:恢复上次状态并开启持久化。
     *
     * 读取顺序:模块侧写入的状态文件(跨 classloader 通道)> SharedPreferences。
     * 模块的 onModuleLoaded 早于 Application 创建,无法在此处用 ActivityThread
     * 拿 Context,因此模块侧改为直接写 files 目录下的 JSON 文件。
     */
    fun attach(context: Context) {
        val p = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        prefs = p
        val fromFile = readStatusFile(context)
        val persisted = read(p)
        val restored = when {
            fromFile != null -> fromFile
            persisted.activated -> persisted
            else -> _moduleStatus.value
        }
        _moduleStatus.value = restored
        persist(restored)
    }

    /**
     * 由 ClipboardHook 在模块加载时调用(任意被注入的进程)。
     *
     * 注意:libxposed 用独立的 classloader 加载模块代码,模块侧与本 UI 侧
     * 的 ModuleStatusStore 是两个不同的类,内存互不相通。模块侧通过
     * 传入本应用的 [ApplicationInfo](getModuleApplicationInfo 提供 dataDir),
     * 把状态写入应用 files 目录的 JSON 文件,UI 侧 attach() 时读取。
     *
     * @param appInfo 仅在本应用进程注入时传入(模块自身);system_server 等
     *        其它进程传 null,不落盘(避免跨 uid 写权限问题)。
     */
    fun update(status: ModuleStatus, appInfo: ApplicationInfo? = null) {
        _moduleStatus.value = status
        if (appInfo != null) {
            runCatching {
                val dir = File(appInfo.dataDir, "files")
                dir.mkdirs()
                File(dir, STATUS_FILE).writeText(statusToJson(status), Charsets.UTF_8)
            }
        }
        persist(status)
    }

    private fun statusToJson(status: ModuleStatus): String = JSONObject()
        .put("activated", status.activated)
        .put("framework_name", status.frameworkName)
        .put("framework_version", status.frameworkVersion)
        .put("framework_version_code", status.frameworkVersionCode)
        .put("api_version", status.apiVersion)
        .put("process_name", status.processName)
        .toString()

    private fun readStatusFile(context: Context): ModuleStatus? {
        val file = File(context.filesDir, STATUS_FILE)
        if (!file.exists()) return null
        return runCatching {
            val obj = JSONObject(file.readText(Charsets.UTF_8))
            ModuleStatus(
                activated = obj.optBoolean("activated", false),
                frameworkName = obj.optString("framework_name").takeIf { it.isNotEmpty() && it != "null" },
                frameworkVersion = obj.optString("framework_version").takeIf { it.isNotEmpty() && it != "null" },
                frameworkVersionCode = if (obj.has("framework_version_code") && !obj.isNull("framework_version_code")) {
                    obj.optLong("framework_version_code", -1L).takeIf { it >= 0 }
                } else {
                    null
                },
                apiVersion = if (obj.has("api_version") && !obj.isNull("api_version")) {
                    obj.optInt("api_version", -1).takeIf { it >= 0 }
                } else {
                    null
                },
                processName = obj.optString("process_name").takeIf { it.isNotEmpty() && it != "null" }
            )
        }.getOrNull()
    }

    private fun read(p: SharedPreferences): ModuleStatus = ModuleStatus(
        activated = p.getBoolean(KEY_ACTIVATED, false),
        frameworkName = p.getString(KEY_FRAMEWORK_NAME, null),
        frameworkVersion = p.getString(KEY_FRAMEWORK_VERSION, null),
        frameworkVersionCode = if (p.contains(KEY_FRAMEWORK_VERSION_CODE)) {
            p.getLong(KEY_FRAMEWORK_VERSION_CODE, 0L)
        } else {
            null
        },
        apiVersion = if (p.contains(KEY_API_VERSION)) p.getInt(KEY_API_VERSION, 0) else null,
        processName = p.getString(KEY_PROCESS_NAME, null)
    )

    // 用 commit() 同步落盘:模块侧写入后,主 classloader 的 attach() 立即可读,
    // 避免 apply() 异步写盘导致的跨 classloader 读取旧值问题。
    private fun persist(status: ModuleStatus) {
        val p = prefs ?: return
        p.edit()
            .putBoolean(KEY_ACTIVATED, status.activated)
            .putString(KEY_FRAMEWORK_NAME, status.frameworkName)
            .putString(KEY_FRAMEWORK_VERSION, status.frameworkVersion)
            .putString(KEY_PROCESS_NAME, status.processName)
            .commit()
        val edit = p.edit()
        if (status.frameworkVersionCode != null) {
            edit.putLong(KEY_FRAMEWORK_VERSION_CODE, status.frameworkVersionCode)
        } else {
            edit.remove(KEY_FRAMEWORK_VERSION_CODE)
        }
        if (status.apiVersion != null) {
            edit.putInt(KEY_API_VERSION, status.apiVersion)
        } else {
            edit.remove(KEY_API_VERSION)
        }
        edit.commit()
    }
}
