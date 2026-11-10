package clip.yixing.sync.root

import android.util.Log
import java.io.File

/**
 * 以 root 身份执行命令的最小封装。
 *
 * 为什么需要单独一层:ShizukuProcess 走的是 Shizuku 的 Binder 通道,
 * 而本功能面向的是 KernelSU / Magisk 这类"直接给 su"的场景,两者通道不同。
 *
 * 设计要点:
 * 1. 所有调用都必须在后台线程执行 —— su 请求可能阻塞数秒等用户授权,
 *    放在主线程会直接 ANR。
 * 2. 明确区分三种结果,调用方才能给出准确提示:
 *    Available(true/false) + 退出码,而不是笼统的"成功/失败"。
 * 3. 带超时。某些 root 管理器在未授权时会挂住不返回。
 */
object RootShell {

    private const val TAG = "NexClipRoot"
    private const val DEFAULT_TIMEOUT_MS = 8_000L

    /** 一条 root 命令的执行结果。 */
    data class Result(
        val exited: Boolean,
        val exitCode: Int,
        val stdout: String,
        val stderr: String,
        /** 是否连 su 都拉不起来(未 root / 未授权 / su 不存在)。 */
        val suAvailable: Boolean,
    ) {
        val ok: Boolean get() = exited && exitCode == 0
    }

    /**
     * su 可执行文件是否存在。
     *
     * 注意:文件存在不代表已授权 —— KernelSU / Magisk 的 su 在未授予时会直接返回非零,
     * 真正的可用性要以 [exec] 的 [Result.suAvailable] 为准。
     */
    /** su 的常见位置,按优先级排列。KernelSU 通常在 /system/bin,Magisk 可能在别处。 */
    private val SU_CANDIDATES = arrayOf(
        "/system/bin/su",
        "/system/xbin/su",
        "/sbin/su",
        "/system/sbin/su",
        "/su/bin/su",
        "/debug_ramdisk/su",
    )

    /**
     * 找到可执行的 su 绝对路径;没有则返回 null。
     *
     * 注意:文件存在不代表已授权 —— 未授权时 su 会返回非零退出码,
     * 真正的可用性要以 [exec] 返回的 [Result] 为准。
     */
    fun resolveSuPath(): String? {
        for (path in SU_CANDIDATES) {
            try {
                val f = File(path)
                if (f.exists() && f.canExecute()) return path
            } catch (e: Exception) {
                // 忽略单个路径的检查异常,继续试下一个
            }
        }
        return null
    }

    fun isSuBinaryPresent(): Boolean = resolveSuPath() != null

    /**
     * 执行一条 root 命令。**必须在后台线程调用。**
     *
     * @param command 交给 su 执行的单条 shell 命令(内部用 sh -c 包装)。
     */
    fun exec(command: String, timeoutMs: Long = DEFAULT_TIMEOUT_MS): Result {
        var process: Process? = null
        return try {
            // 必须用绝对路径。ProcessBuilder("su") 依赖 PATH 查找,
            // 而 App 进程的 PATH 不含 /system/bin,会得到 error=13 Permission denied
            // (实测日志:Cannot run program "su": error=13)。
            val suPath = resolveSuPath()
            if (suPath == null) {
                Log.d(TAG, "未找到 su 可执行文件")
                return Result(false, -1, "", "su not found", suAvailable = false)
            }
            process = ProcessBuilder(suPath, "-c", command)
                .redirectErrorStream(false)
                .start()

            if (!waitFor(process, timeoutMs)) {
                process.destroyForcibly()
                Log.w(TAG, "root 命令超时(${timeoutMs}ms): $command")
                return Result(false, -1, "", "timeout", suAvailable = true)
            }

            val exitCode = process.exitValue()
            val stdout = readQuietly(process.inputStream.bufferedReader().use { it.readText() })
            val stderr = readQuietly(process.errorStream.bufferedReader().use { it.readText() })
            if (exitCode != 0) {
                Log.d(TAG, "root 命令失败($exitCode): $command  ${stderr.take(200)}")
            }
            Result(true, exitCode, stdout, stderr, suAvailable = true)
        } catch (e: Exception) {
            // IOException 且没见过 exitCode:通常是 su 不存在或直接被拒绝
            Log.d(TAG, "root 命令无法执行: ${e.javaClass.simpleName} ${e.message}")
            Result(false, -1, "", e.message ?: "", suAvailable = false)
        } finally {
            runCatching { process?.destroy() }
        }
    }

    /**
     * 读取一条 root 命令的 stdout(带 trim)。
     * 失败时返回 null,让调用方能区分"读到空串"与"根本没读到"。
     */
    fun readOutput(command: String, timeoutMs: Long = DEFAULT_TIMEOUT_MS): String? {
        val r = exec(command, timeoutMs)
        return if (r.ok) r.stdout.trim() else null
    }

    private fun waitFor(process: Process, timeoutMs: Long): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
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

    private fun readQuietly(text: String): String =
        try {
            text.take(4096)
        } catch (e: Exception) {
            ""
        }

    /**
     * 单引号包裹并转义,防止参数里的空格与元字符破坏 sh -c 的命令行。
     * 与 ShizukuProcess.shellQuote 行为一致(两边面向不同的提权通道,但转义规则相同)。
     */
    fun shellQuote(arg: String): String {
        if (arg.isEmpty()) return "''"
        return "'" + arg.replace("'", "'\\''") + "'"
    }
}
