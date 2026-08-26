package clip.yixing.sync.service

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.WindowManager
import clip.yixing.sync.shizuku.ClipboardFocusRequester

/**
 * 极简透明无感焦点跳板 Activity (参考 HyperCopy 架构设计)。
 *
 * 在 Android 10+ 限制后台获取剪贴板的场景下：
 * 当通过 Shizuku 检测到系统剪贴板发生变更时，通过 Shizuku privileged shell 极速拉起此透明跳板。
 * 在获取到窗口焦点的 20ms 内直接读取完整 ClipData (文本、图片等)，随后零动画迅速销毁，
 * 既突破了后台剪贴板隐私限制，又完全不干扰用户当前正在使用的应用。
 */
class ClipboardFloatingActivity : Activity() {

    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val token = intent.getStringExtra(ClipboardFocusRequester.EXTRA_START_TOKEN)
        if (!ClipboardFocusRequester.consumeToken(token)) {
            finish()
            return
        }

        window.setBackgroundDrawableResource(android.R.color.transparent)
        val params = window.attributes
        params.dimAmount = 0f
        params.flags = params.flags or
            WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
            WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
            WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
        window.attributes = params
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        readClipboardAndFinish()
    }

    private fun readClipboardAndFinish() {
        val manager = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val clipData = runCatching { manager.primaryClip }.getOrNull()
        val sourcePkg = intent.getStringExtra(ClipboardFocusRequester.EXTRA_SOURCE_PACKAGE).orEmpty()

        finishWithoutAnimation()

        if (clipData != null) {
            Handler(Looper.getMainLooper()).postDelayed({
                ClipboardMonitorService.onClipCaptured(clipData, sourcePkg)
            }, 60L)
        }
    }

    private fun finishWithoutAnimation() {
        overridePendingTransition(0, 0)
        moveTaskToBack(true)
        finishAndRemoveTask()
        overridePendingTransition(0, 0)
    }

    override fun onDestroy() {
        super.onDestroy()
        if (!handled) {
            handled = true
            readClipboardAndFinish()
        }
    }

    private companion object {
        const val TAG = "ClipboardFloating"
    }
}
