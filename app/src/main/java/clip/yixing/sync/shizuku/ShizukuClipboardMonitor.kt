package clip.yixing.sync.shizuku

import android.content.Context
import android.util.Log

/**
 * Shizuku 后台剪贴板监控器 (基于 HyperCopy 架构)。
 * 结合 ClipboardChangeProbe 与 ShizukuLogcatClipboardDetector，实时捕捉后台剪贴板变更并通过跳板快速取词。
 */
object ShizukuClipboardMonitor {
    private const val TAG = "ShizukuClipboardMonitor"

    private var detector: ShizukuLogcatClipboardDetector? = null
    private var probe: ClipboardChangeProbe? = null
    private var isRunning = false

    fun start(context: Context) {
        val appContext = context.applicationContext
        if (isRunning) return
        isRunning = true

        startProbe(appContext)

        ShizukuPermission.waitForAvailable { available ->
            if (!isRunning) return@waitForAvailable
            if (available) {
                ShizukuPermission.requestIfNeeded { granted ->
                    if (!isRunning) return@requestIfNeeded
                    if (granted) {
                        startDetector(appContext)
                    } else {
                        Log.d(TAG, "Shizuku permission not granted")
                    }
                }
            } else {
                Log.d(TAG, "Shizuku service not available")
            }
        }
    }

    private fun startProbe(context: Context) {
        if (probe != null) return
        probe = ClipboardChangeProbe(context).also { it.start() }
    }

    private fun startDetector(context: Context) {
        if (detector != null) return
        detector = ShizukuLogcatClipboardDetector(
            packageName = context.packageName,
            processStarter = { command -> ShizukuProcess.start(command) },
            onRunningChanged = { running ->
                Log.d(TAG, "Shizuku logcat detector running state: $running")
            },
            onClipboardChanged = {
                ClipboardFocusRequester.request(context)
            }
        ).also { it.start() }
    }

    fun stop() {
        isRunning = false
        detector?.stop()
        detector = null
        probe?.stop()
        probe = null
        Log.d(TAG, "Stopped Shizuku clipboard monitor")
    }
}
