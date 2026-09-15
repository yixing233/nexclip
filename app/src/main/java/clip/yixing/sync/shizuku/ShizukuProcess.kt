package clip.yixing.sync.shizuku

import android.util.Log

object ShizukuProcess {
    private const val TAG = "ShizukuProcess"
    private const val DEFAULT_TIMEOUT_MILLIS = 3_000L

    fun start(command: Array<String>): Process? {
        return runCatching {
            val method = Class.forName("rikka.shizuku.Shizuku").getDeclaredMethod(
                "newProcess",
                Array<String>::class.java,
                Array<String>::class.java,
                String::class.java,
            )
            method.isAccessible = true
            method.invoke(null, command, null, null) as Process
        }.getOrElse { throwable ->
            Log.d(TAG, "Shizuku newProcess reflection failed: ${throwable.message}")
            null
        }
    }

    /**
     * 以 shell 身份执行一条命令并等待退出。
     *
     * 返回退出码;拉不起进程或超时(进程会被强杀)时返回 null,让调用方能区分
     * "命令执行了但失败" 与 "根本没跑起来" 两种情况。
     */
    fun exec(command: String, timeoutMillis: Long = DEFAULT_TIMEOUT_MILLIS): Int? {
        return runCatching {
            val process = start(arrayOf("sh", "-c", command)) ?: return null
            if (!waitForExit(process, timeoutMillis)) {
                process.destroyForcibly()
                Log.d(TAG, "command timeout: $command")
                return null
            }
            val exitCode = process.exitValue()
            if (exitCode != 0) {
                val output = runCatching {
                    process.inputStream.bufferedReader().use { it.readText() }
                }.getOrDefault("")
                Log.d(TAG, "command failed ($exitCode): $command ${output.take(300)}")
            }
            exitCode
        }.getOrElse { throwable ->
            Log.d(TAG, "command exception: $command ${throwable.message}")
            null
        }
    }

    /** 轮询等待进程退出;超时返回 false(调用方负责强杀) */
    fun waitForExit(process: Process, timeoutMillis: Long = DEFAULT_TIMEOUT_MILLIS): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMillis
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

    /** 单引号包裹并转义,防止参数里的空格与元字符破坏 sh -c 的命令行 */
    fun shellQuote(arg: String): String {
        if (arg.isEmpty()) return "''"
        return "'" + arg.replace("'", "'\\''") + "'"
    }
}
