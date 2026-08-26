package clip.yixing.sync.shizuku

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log
import clip.yixing.sync.service.ClipboardFloatingActivity
import java.util.UUID

object ClipboardFocusRequester {
    private const val TAG = "ClipboardFocusRequester"
    private const val REQUEST_DEBOUNCE_MILLIS = 800L
    private const val SHIZUKU_COMMAND_TIMEOUT_MILLIS = 3_000L

    private var lastRequestAt = 0L
    private var pendingToken: String? = null

    fun request(context: Context) {
        val now = System.currentTimeMillis()
        if (now - lastRequestAt < REQUEST_DEBOUNCE_MILLIS) return
        lastRequestAt = now
        val token = UUID.randomUUID().toString()
        pendingToken = token
        val sourcePackage = foregroundPackageName(context)
        if (ShizukuPermission.isGranted() && startByShizuku(context, token, sourcePackage)) return
        runCatching { context.startActivity(floatingActivityIntent(context, token, sourcePackage)) }
            .onFailure { Log.d(TAG, "start clipboard floating activity failed: ${it.message}") }
    }

    fun consumeToken(token: String?): Boolean {
        val expected = pendingToken ?: return false
        if (token != expected) return false
        pendingToken = null
        return true
    }

    private fun startByShizuku(context: Context, token: String, sourcePackage: String): Boolean {
        val component = ComponentName(context.packageName, ClipboardFloatingActivity::class.java.name).flattenToString()
        val commandParts = mutableListOf(
            "am",
            "start",
            "--user",
            "0",
            "-n",
            component,
            "--es",
            EXTRA_START_TOKEN,
            token,
            "--es",
            EXTRA_ACTION,
            ACTION_READ_CLIPBOARD,
        )
        if (sourcePackage.isNotBlank()) {
            commandParts += listOf("--es", EXTRA_SOURCE_PACKAGE, sourcePackage)
        }
        commandParts += listOf(
            "-f",
            Intent.FLAG_ACTIVITY_NEW_TASK.toString(),
        )
        val command = commandParts.joinToString(" ") { shellQuote(it) }
        return runCatching {
            Log.d(TAG, "Shizuku start clipboard floating activity: $command")
            val process = ShizukuProcess.start(arrayOf("sh", "-c", command)) ?: return false
            if (!waitForExit(process)) {
                process.destroyForcibly()
                Log.d(TAG, "Shizuku start clipboard floating activity timeout")
                return false
            }
            val exitCode = process.exitValue()
            if (exitCode != 0) {
                val output = process.inputStream.bufferedReader().use { it.readText() }
                Log.d(TAG, "Shizuku start clipboard floating activity failed: ${output.take(300)}")
            }
            exitCode == 0
        }.getOrElse { throwable ->
            Log.d(TAG, "Shizuku start clipboard floating activity exception: ${throwable.message}")
            false
        }
    }

    private fun floatingActivityIntent(context: Context, token: String, sourcePackage: String): Intent {
        return Intent(context, ClipboardFloatingActivity::class.java)
            .putExtra(EXTRA_START_TOKEN, token)
            .putExtra(EXTRA_SOURCE_PACKAGE, sourcePackage)
            .putExtra(EXTRA_ACTION, ACTION_READ_CLIPBOARD)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }

    private fun foregroundPackageName(context: Context): String {
        if (!ShizukuPermission.isGranted()) return ""
        return runCatching {
            val process = ShizukuProcess.start(arrayOf("sh", "-c", "dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'"))
                ?: return ""
            val output = process.inputStream.bufferedReader().use { it.readText() }
            if (!waitForExit(process)) process.destroyForcibly()
            Regex("[a-zA-Z0-9_]+(?:\\.[a-zA-Z0-9_]+)+").findAll(output)
                .map { it.value }
                .firstOrNull { it != context.packageName && !it.startsWith("com.android.systemui") } ?: ""
        }.getOrElse { throwable ->
            Log.d(TAG, "read foreground package failed: ${throwable.message}")
            ""
        }
    }

    private fun waitForExit(process: Process): Boolean {
        val deadline = System.currentTimeMillis() + SHIZUKU_COMMAND_TIMEOUT_MILLIS
        while (System.currentTimeMillis() < deadline) {
            val exited = runCatching {
                process.exitValue()
                true
            }.getOrDefault(false)
            if (exited) return true
            runCatching { Thread.sleep(50L) }
        }
        return false
    }

    private fun shellQuote(arg: String): String {
        if (arg.isEmpty()) return "''"
        return "'" + arg.replace("'", "'\\''") + "'"
    }

    const val EXTRA_START_TOKEN = "clip.yixing.sync.extra.FLOATING_START_TOKEN"
    const val EXTRA_SOURCE_PACKAGE = "clip.yixing.sync.extra.CLIPBOARD_SOURCE_PACKAGE"
    const val EXTRA_ACTION = "clip.yixing.sync.extra.FLOATING_ACTION"
    const val ACTION_READ_CLIPBOARD = "read_clipboard"
}
