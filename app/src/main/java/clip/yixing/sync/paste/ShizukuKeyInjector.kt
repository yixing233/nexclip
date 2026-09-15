package clip.yixing.sync.paste

import android.util.Log
import clip.yixing.sync.shizuku.ShizukuProcess

/**
 * 无障碍不可用时的降级粘贴执行者:借 Shizuku 的 shell 身份注入粘贴按键。
 *
 * 前提是内容已经写进系统剪贴板 —— 按键只是替用户点了一下「粘贴」,
 * 所以这条路径必然会覆盖用户自己的剪贴板,与无障碍注入的体验差异要在 UI 上讲清楚。
 */
object ShizukuKeyInjector {
    private const val TAG = "ShizukuKeyInjector"
    private const val COMMAND_TIMEOUT_MILLIS = 3_000L

    /** KEYCODE_PASTE,部分 ROM / 输入法不响应 */
    private const val KEYCODE_PASTE = 279

    /** Ctrl + V 组合键回退:KEYCODE_CTRL_LEFT=113,KEYCODE_V=50(input keycombination 需要 API 30+) */
    private const val KEYCODE_CTRL_LEFT = 113
    private const val KEYCODE_V = 50

    /**
     * 模拟一次粘贴按键。
     *
     * 先试 [KEYCODE_PASTE],不被响应时回退 Ctrl+V —— 两者都可能在个别 ROM 上静默失效,
     * 这是按键注入方案的固有局限,调用方需要准备「已复制,请手动粘贴」的兜底提示。
     */
    fun pasteKey(): Boolean {
        if (exec("input keyevent $KEYCODE_PASTE")) return true
        Log.d(TAG, "keyevent $KEYCODE_PASTE failed, falling back to ctrl+v")
        return exec("input keycombination $KEYCODE_CTRL_LEFT $KEYCODE_V")
    }

    private fun exec(command: String): Boolean =
        ShizukuProcess.exec(command, COMMAND_TIMEOUT_MILLIS) == 0
}
