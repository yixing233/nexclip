package clip.yixing.sync.shizuku

import android.content.ClipData
import android.content.Context
import android.content.pm.PackageManager
import android.os.Binder
import android.os.IBinder
import android.os.IInterface
import android.os.Parcel
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import rikka.shizuku.Shizuku
import rikka.shizuku.ShizukuBinderWrapper
import rikka.shizuku.SystemServiceHelper

/**
 * Shizuku 免 Root 系统级剪贴板服务管理器。
 * 通过 Shell UID (2000) 访问系统 IClipboard 接口，实现后台无感静默剪贴板读写与变更监听。
 */
object ShizukuClipboardManager {

    private const val TAG = "ShizukuClip"
    private const val DESCRIPTOR_CLIP_LISTENER = "android.content.IOnPrimaryClipChangedListener"
    private const val SHELL_PACKAGE = "com.android.shell"

    enum class ShizukuStatus {
        NOT_INSTALLED,
        DEAD_OR_STOPPED,
        UNAUTHORIZED,
        AUTHORIZED_RUNNING
    }

    private val _status = MutableStateFlow(ShizukuStatus.DEAD_OR_STOPPED)
    val status = _status.asStateFlow()

    private var activeListenerBinder: ClipListenerBinder? = null
    private var isInitialized = false

    private val binderReceivedListener = Shizuku.OnBinderReceivedListener {
        updateStatus()
    }

    private val binderDeadListener = Shizuku.OnBinderDeadListener {
        _status.value = ShizukuStatus.DEAD_OR_STOPPED
        activeListenerBinder = null
    }

    private val permissionResultListener = Shizuku.OnRequestPermissionResultListener { _, grantResult ->
        if (grantResult == PackageManager.PERMISSION_GRANTED) {
            _status.value = ShizukuStatus.AUTHORIZED_RUNNING
        } else {
            _status.value = ShizukuStatus.UNAUTHORIZED
        }
    }

    /**
     * 初始化 Shizuku 监听器与状态绑定
     */
    fun init(context: Context) {
        if (isInitialized) return
        isInitialized = true

        try {
            Shizuku.addBinderReceivedListenerSticky(binderReceivedListener)
            Shizuku.addBinderDeadListener(binderDeadListener)
            Shizuku.addRequestPermissionResultListener(permissionResultListener)
        } catch (t: Throwable) {
            Log.w(TAG, "Failed to register Shizuku listeners: ${t.message}")
        }
        updateStatus(context)
    }

    /**
     * 刷新并计算当前 Shizuku 状态
     */
    fun updateStatus(context: Context? = null) {
        try {
            if (!Shizuku.pingBinder()) {
                val isInstalled = context?.let { isShizukuInstalled(it) } ?: false
                _status.value = if (isInstalled) ShizukuStatus.DEAD_OR_STOPPED else ShizukuStatus.NOT_INSTALLED
                return
            }

            if (Shizuku.getVersion() < 11) {
                _status.value = ShizukuStatus.UNAUTHORIZED
                return
            }

            if (Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED) {
                _status.value = ShizukuStatus.AUTHORIZED_RUNNING
            } else {
                _status.value = ShizukuStatus.UNAUTHORIZED
            }
        } catch (t: Throwable) {
            Log.w(TAG, "Error updating Shizuku status: ${t.message}")
            _status.value = ShizukuStatus.DEAD_OR_STOPPED
        }
    }

    /**
     * 请求 Shizuku 权限授权
     */
    fun requestPermission(requestCode: Int = 1001) {
        try {
            if (Shizuku.pingBinder() && Shizuku.getVersion() >= 11) {
                Shizuku.requestPermission(requestCode)
            }
        } catch (t: Throwable) {
            Log.e(TAG, "Request Shizuku permission failed", t)
        }
    }

    /**
     * 检查设备上是否已安装 Shizuku 管理器应用
     */
    fun isShizukuInstalled(context: Context): Boolean {
        return try {
            context.packageManager.getPackageInfo("moe.shizuku.privileged.api", 0)
            true
        } catch (_: Exception) {
            false
        }
    }

    /**
     * 获取系统 IClipboard 远程代理
     */
    private fun getClipboardService(): Any? {
        if (!Shizuku.pingBinder() || Shizuku.checkSelfPermission() != PackageManager.PERMISSION_GRANTED) {
            return null
        }
        return try {
            val rawBinder = SystemServiceHelper.getSystemService("clipboard") ?: return null
            val wrappedBinder = ShizukuBinderWrapper(rawBinder)
            val stubClass = Class.forName("android.content.IClipboard\$Stub")
            val asInterface = stubClass.getMethod("asInterface", IBinder::class.java)
            asInterface.invoke(null, wrappedBinder)
        } catch (t: Throwable) {
            Log.e(TAG, "Failed to get IClipboard via Shizuku", t)
            null
        }
    }

    /**
     * 通过 Shizuku 静默读取系统剪贴板
     */
    fun getPrimaryClip(): ClipData? {
        val service = getClipboardService() ?: return null
        return try {
            val clazz = service.javaClass
            val method = clazz.methods.find { it.name == "getPrimaryClip" } ?: return null
            val paramTypes = method.parameterTypes
            val args = Array<Any?>(paramTypes.size) { null }
            for (i in paramTypes.indices) {
                when (paramTypes[i]) {
                    String::class.java -> args[i] = SHELL_PACKAGE
                    Int::class.javaPrimitiveType -> args[i] = 0
                    else -> args[i] = null
                }
            }
            method.invoke(service, *args) as? ClipData
        } catch (t: Throwable) {
            Log.w(TAG, "getPrimaryClip via Shizuku error: ${t.message}")
            null
        }
    }

    /**
     * 通过 Shizuku 静默写入系统剪贴板（突破后台写入限制）
     */
    fun setPrimaryClip(clip: ClipData): Boolean {
        val service = getClipboardService() ?: return false
        return try {
            val clazz = service.javaClass
            val method = clazz.methods.find { it.name == "setPrimaryClip" } ?: return false
            val paramTypes = method.parameterTypes
            val args = Array<Any?>(paramTypes.size) { null }
            for (i in paramTypes.indices) {
                when {
                    paramTypes[i] == ClipData::class.java -> args[i] = clip
                    paramTypes[i] == String::class.java -> args[i] = SHELL_PACKAGE
                    paramTypes[i] == Int::class.javaPrimitiveType -> args[i] = 0
                    else -> args[i] = null
                }
            }
            method.invoke(service, *args)
            true
        } catch (t: Throwable) {
            Log.e(TAG, "setPrimaryClip via Shizuku error: ${t.message}", t)
            false
        }
    }

    /**
     * 注册系统级剪贴板变更监听器
     */
    @Synchronized
    fun registerClipboardListener(onClipChanged: () -> Unit): Boolean {
        val service = getClipboardService() ?: return false
        try {
            unregisterClipboardListener()

            val binder = ClipListenerBinder(onClipChanged)
            activeListenerBinder = binder

            val clazz = service.javaClass
            val method = clazz.methods.find { it.name == "addPrimaryClipChangedListener" } ?: return false
            val paramTypes = method.parameterTypes
            val args = Array<Any?>(paramTypes.size) { null }
            for (i in paramTypes.indices) {
                when {
                    IBinder::class.java.isAssignableFrom(paramTypes[i]) ||
                        paramTypes[i].name.contains("IOnPrimaryClipChangedListener") -> args[i] = binder
                    paramTypes[i] == String::class.java -> args[i] = SHELL_PACKAGE
                    paramTypes[i] == Int::class.javaPrimitiveType -> args[i] = 0
                    else -> args[i] = null
                }
            }
            method.invoke(service, *args)
            Log.i(TAG, "Successfully registered IOnPrimaryClipChangedListener via Shizuku")
            return true
        } catch (t: Throwable) {
            Log.e(TAG, "Failed to register clip changed listener via Shizuku", t)
            return false
        }
    }

    /**
     * 注销系统级剪贴板变更监听器
     */
    @Synchronized
    fun unregisterClipboardListener() {
        val binder = activeListenerBinder ?: return
        activeListenerBinder = null
        val service = getClipboardService() ?: return
        try {
            val clazz = service.javaClass
            val method = clazz.methods.find { it.name == "removePrimaryClipChangedListener" } ?: return
            val paramTypes = method.parameterTypes
            val args = Array<Any?>(paramTypes.size) { null }
            for (i in paramTypes.indices) {
                when {
                    IBinder::class.java.isAssignableFrom(paramTypes[i]) ||
                        paramTypes[i].name.contains("IOnPrimaryClipChangedListener") -> args[i] = binder
                    paramTypes[i] == String::class.java -> args[i] = SHELL_PACKAGE
                    paramTypes[i] == Int::class.javaPrimitiveType -> args[i] = 0
                    else -> args[i] = null
                }
            }
            method.invoke(service, *args)
            Log.i(TAG, "Unregistered IOnPrimaryClipChangedListener via Shizuku")
        } catch (t: Throwable) {
            Log.w(TAG, "Failed to unregister clip listener: ${t.message}")
        }
    }

    /**
     * 响应系统 IPC 广播的动态 Binder 桩
     */
    private class ClipListenerBinder(
        private val callback: () -> Unit
    ) : Binder(), IInterface {

        init {
            attachInterface(this, DESCRIPTOR_CLIP_LISTENER)
        }

        override fun asBinder(): IBinder = this

        override fun onTransact(code: Int, data: Parcel, reply: Parcel?, flags: Int): Boolean {
            if (code == FIRST_CALL_TRANSACTION) { // 1: dispatchPrimaryClipChanged
                data.enforceInterface(DESCRIPTOR_CLIP_LISTENER)
                try {
                    callback()
                } catch (t: Throwable) {
                    Log.e(TAG, "Error inside Shizuku onClipChanged callback", t)
                }
                return true
            } else if (code == INTERFACE_TRANSACTION) {
                reply?.writeString(DESCRIPTOR_CLIP_LISTENER)
                return true
            }
            return super.onTransact(code, data, reply, flags)
        }
    }
}
