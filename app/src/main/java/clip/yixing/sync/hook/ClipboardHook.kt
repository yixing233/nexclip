package clip.yixing.sync.hook

import android.util.Log
import io.github.libxposed.api.XposedModule
import io.github.libxposed.api.XposedModuleInterface.ModuleLoadedParam
import io.github.libxposed.api.XposedModuleInterface.SystemServerStartingParam

/**
 * Xposed 模块入口:在 system_server 中 hook ClipboardService,
 * 将本应用包名加入剪贴板后台读取白名单。
 *
 * Android 16 (API 36) 签名:
 *   private boolean clipboardAccessAllowed(
 *       int op, String callingPackage, String attributionTag,
 *       int uid, int userId, int intendingDeviceId, boolean shouldNoteOp)
 */
class ClipboardHook : XposedModule() {

    companion object {
        const val TAG = "SyncClipboard"
        const val MODULE_PACKAGE = "clip.yixing.sync"
        // AppOpsManager.OP_READ_CLIPBOARD = 29 (AOSP 定义,compileSdk 37 中为隐藏 API)
        private const val OP_READ_CLIPBOARD = 29
    }

    override fun onModuleLoaded(param: ModuleLoadedParam) {
        // 仅在本应用进程注入时,通过 moduleApplicationInfo(dataDir)持久化状态;
        // system_server 等其它进程不传,避免跨 uid 写应用数据目录。
        val appInfo = if (param.processName == MODULE_PACKAGE) moduleApplicationInfo else null
        ModuleStatusStore.update(
            ModuleStatusStore.ModuleStatus(
                activated = true,
                frameworkName = frameworkName,
                frameworkVersion = frameworkVersion,
                frameworkVersionCode = frameworkVersionCode,
                apiVersion = apiVersion,
                processName = param.processName
            ),
            appInfo
        )
        log(
            Log.INFO, TAG,
            "module loaded in ${param.processName}, framework=$frameworkName " +
                "v$frameworkVersion ($frameworkVersionCode), api=$apiVersion"
        )
    }

    override fun onSystemServerStarting(param: SystemServerStartingParam) {
        log(Log.INFO, TAG, "hooking ClipboardService in system_server")
        try {
            val clazz = Class.forName(
                "com.android.server.clipboard.ClipboardService",
                false,
                param.classLoader
            )
            val method = clazz.declaredMethods.firstOrNull {
                it.name == "clipboardAccessAllowed" && it.parameterTypes.size == 8
            }
            if (method == null) {
                log(Log.ERROR, TAG, "clipboardAccessAllowed(int,...) not found, sdk=${android.os.Build.VERSION.SDK_INT}")
                return
            }
            hook(method).intercept { chain ->
                val op = chain.getArg(0) as Int
                val callingPackage = chain.getArg(1) as String
                if (op == OP_READ_CLIPBOARD && callingPackage == MODULE_PACKAGE) {
                    log(Log.INFO, TAG, "whitelist hit: allow $callingPackage to read clipboard in background")
                    true
                } else {
                    chain.proceed()
                }
            }
            log(Log.INFO, TAG, "hook installed: ${clazz.name}#${method.name} (8 params)")
        } catch (t: Throwable) {
            log(Log.ERROR, TAG, "failed to hook ClipboardService", t)
        }
    }
}