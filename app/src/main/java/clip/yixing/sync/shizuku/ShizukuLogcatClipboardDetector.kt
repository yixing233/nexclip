package clip.yixing.sync.shizuku

import android.util.Log
import java.io.BufferedReader
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

class ShizukuLogcatClipboardDetector(
    private val packageName: String,
    private val processStarter: (Array<String>) -> Process?,
    private val onRunningChanged: (Boolean) -> Unit,
    private val onClipboardChanged: () -> Unit,
) {
    private val running = AtomicBoolean(false)
    private var process: Process? = null
    private var worker: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        worker = Thread(::readLoop, "NexClip-ShizukuLogcat").also { it.start() }
    }

    fun stop() {
        running.set(false)
        process?.destroy()
        process = null
        worker = null
    }

    private fun readLoop() {
        runCatching {
            val since = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US).format(Date())
            Log.d(TAG, "start Shizuku logcat clipboard detector since $since")
            process = processStarter(arrayOf("logcat", "-T", since, "ClipboardService:E", "*:S"))
            if (process == null) {
                running.set(false)
                onRunningChanged(false)
                return
            }
            onRunningChanged(true)
            process?.inputStream?.bufferedReader()?.use(::readLines)
        }.onFailure { throwable ->
            if (running.get()) Log.d(TAG, "Shizuku logcat detector failed: ${throwable.message}")
        }
        running.set(false)
        onRunningChanged(false)
    }

    private fun readLines(reader: BufferedReader) {
        while (running.get()) {
            val line = reader.readLine() ?: break
            if (line.contains(packageName) && line.contains("Clipboard", ignoreCase = true)) {
                Log.d(TAG, "Shizuku detected clipboard log: $line")
                onClipboardChanged()
            }
        }
    }

    private companion object {
        const val TAG = "ShizukuLogcat"
    }
}
