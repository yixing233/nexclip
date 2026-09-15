package clip.yixing.sync.paste

import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.os.IBinder
import android.provider.Settings
import android.util.Log
import android.view.ContextThemeWrapper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import clip.yixing.sync.R
import clip.yixing.sync.service.ClipboardMonitorService
import clip.yixing.sync.ui.LucideIcons
import clip.yixing.sync.util.SyncSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.theme.MiuixTheme
import kotlin.math.hypot

/**
 * 悬浮球服务:在别的应用里提供「挑一条历史记录直接粘贴」的入口。
 *
 * 两个覆盖窗口都必须带 [WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE] ——
 * 一旦窗口可获得焦点,目标输入框就会失焦,注入与 ACTION_PASTE 全部失效。
 *
 * 显隐由 [NexClipAccessibilityService.editableFocused] 驱动;无障碍没开时退化为常显。
 */
class FloatingBubbleService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private lateinit var windowManager: WindowManager
    private val themedContext: Context by lazy { ContextThemeWrapper(this, R.style.Theme_Miuix) }

    private var bubbleHost: OverlayComposeHost? = null
    private var bubbleParams: WindowManager.LayoutParams? = null
    private var panelHost: OverlayComposeHost? = null

    private var touchSlop = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        windowManager = getSystemService(Context.WINDOW_SERVICE) as WindowManager
        touchSlop = ViewConfiguration.get(this).scaledTouchSlop

        scope.launch {
            combine(
                NexClipAccessibilityService.isConnected,
                NexClipAccessibilityService.editableFocused
            ) { connected, focused ->
                // 无障碍能报焦点时按焦点显隐,报不了就常显(否则用户根本看不到入口)
                !connected || focused
            }.distinctUntilChanged().collect { visible ->
                if (visible) showBubble() else hideAll()
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!Settings.canDrawOverlays(this)) {
            Log.w(TAG, "overlay permission missing, stopping")
            stopSelf()
            return START_NOT_STICKY
        }
        return START_STICKY
    }

    override fun onDestroy() {
        hideAll()
        bubbleHost?.destroy()
        bubbleHost = null
        scope.cancel()
        super.onDestroy()
    }

    // ---- 气泡 ----

    private fun showBubble() {
        if (bubbleHost != null) return
        if (!Settings.canDrawOverlays(this)) return

        val host = OverlayComposeHost(themedContext) { BubbleContent() }
        val bounds = windowManager.currentWindowMetrics.bounds
        val density = resources.displayMetrics.density
        val bubbleSizePx = ((BUBBLE_SIZE_DP + BUBBLE_SHADOW_PADDING_DP * 2) * density).toInt()
        val marginPx = (BUBBLE_MARGIN_DP * density).toInt()

        val (savedX, savedY) = SyncSettings.bubblePosition(this)
        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = if (savedX >= 0) savedX else bounds.width() - bubbleSizePx - marginPx
            y = if (savedY >= 0) savedY else (bounds.height() * DEFAULT_Y_RATIO).toInt()
        }

        host.view.setOnTouchListener(BubbleTouchListener(params, bubbleSizePx, marginPx))

        val added = runCatching {
            windowManager.addView(host.view, params)
        }.onFailure { Log.w(TAG, "add bubble failed: ${it.message}") }.isSuccess
        if (!added) {
            host.destroy()
            return
        }
        host.onShown()
        bubbleHost = host
        bubbleParams = params
    }

    private fun hideBubble() {
        val host = bubbleHost ?: return
        bubbleHost = null
        bubbleParams = null
        host.onHidden()
        runCatching { windowManager.removeViewImmediate(host.view) }
        host.destroy()
    }

    private fun hideAll() {
        hidePanel()
        hideBubble()
    }

    /**
     * 气泡只负责画一个圆 —— 触摸全部交给 [BubbleTouchListener],
     * 这里不要加 clickable,否则拖拽与点击会互相抢手势。
     */
    @Composable
    private fun BubbleContent() {
        Box(modifier = Modifier.padding(BUBBLE_SHADOW_PADDING_DP.dp)) {
            Box(
                modifier = Modifier
                    .size(BUBBLE_SIZE_DP.dp)
                    .shadow(6.dp, CircleShape)
                    .clip(CircleShape)
                    .background(MiuixTheme.colorScheme.primary),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    imageVector = LucideIcons.ClipboardPaste,
                    contentDescription = "一键粘贴",
                    tint = Color.White,
                    modifier = Modifier.size(22.dp)
                )
            }
        }
    }

    // ---- 面板 ----

    private fun togglePanel() {
        if (panelHost != null) hidePanel() else showPanel()
    }

    private fun showPanel() {
        if (panelHost != null) return
        if (!Settings.canDrawOverlays(this)) return

        val host = OverlayComposeHost(themedContext) { PanelContent() }
        val bounds = windowManager.currentWindowMetrics.bounds
        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP
            // 顶部落位:输入法弹起时不会盖住面板
            y = (bounds.height() * PANEL_TOP_RATIO).toInt()
        }

        // ACTION_OUTSIDE 才消费,其余一律放行给 Compose,否则列表点不动
        host.view.setOnTouchListener { _, event ->
            if (event.actionMasked == MotionEvent.ACTION_OUTSIDE) {
                hidePanel()
                true
            } else {
                false
            }
        }

        val added = runCatching {
            windowManager.addView(host.view, params)
        }.onFailure { Log.w(TAG, "add panel failed: ${it.message}") }.isSuccess
        if (!added) {
            host.destroy()
            return
        }
        host.onShown()
        panelHost = host
    }

    private fun hidePanel() {
        val host = panelHost ?: return
        panelHost = null
        host.onHidden()
        runCatching { windowManager.removeViewImmediate(host.view) }
        host.destroy()
    }

    @Composable
    private fun PanelContent() {
        val clips by ClipboardMonitorService.captured.collectAsState()
        Box(modifier = Modifier.padding(horizontal = PANEL_HORIZONTAL_MARGIN_DP.dp)) {
            FloatingPanelContent(
                clips = clips,
                onPaste = { clip ->
                    // 先收面板:注入前让目标输入框重新拿到焦点
                    hidePanel()
                    scope.launch {
                        val outcome = PasteEngine.paste(this@FloatingBubbleService, clip)
                        toast(outcome.message)
                    }
                },
                onCopy = { clip ->
                    scope.launch {
                        val ok = PasteEngine.copyOnly(this@FloatingBubbleService, clip)
                        toast(if (ok) "已复制" else "复制失败")
                    }
                },
                onToggleFavorite = { clip ->
                    ClipboardMonitorService.toggleFavorite(this@FloatingBubbleService, clip)
                },
                onClose = { hidePanel() }
            )
        }
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }

    /** 拖拽 + 贴边吸附;没超过 touchSlop 的按下抬起当成点击 */
    private inner class BubbleTouchListener(
        private val params: WindowManager.LayoutParams,
        private val bubbleSizePx: Int,
        private val marginPx: Int
    ) : View.OnTouchListener {
        private var downRawX = 0f
        private var downRawY = 0f
        private var startX = 0
        private var startY = 0
        private var dragging = false

        override fun onTouch(view: View, event: MotionEvent): Boolean {
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downRawX = event.rawX
                    downRawY = event.rawY
                    startX = params.x
                    startY = params.y
                    dragging = false
                    return true
                }

                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - downRawX
                    val dy = event.rawY - downRawY
                    if (!dragging && hypot(dx, dy) > touchSlop) dragging = true
                    if (dragging) {
                        val bounds = windowManager.currentWindowMetrics.bounds
                        params.x = (startX + dx).toInt()
                            .coerceIn(0, (bounds.width() - bubbleSizePx).coerceAtLeast(0))
                        params.y = (startY + dy).toInt()
                            .coerceIn(0, (bounds.height() - bubbleSizePx).coerceAtLeast(0))
                        runCatching { windowManager.updateViewLayout(view, params) }
                    }
                    return true
                }

                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    if (dragging) {
                        snapToEdge(view)
                    } else if (event.actionMasked == MotionEvent.ACTION_UP) {
                        togglePanel()
                    }
                    return true
                }
            }
            return false
        }

        private fun snapToEdge(view: View) {
            val bounds = windowManager.currentWindowMetrics.bounds
            params.x = if (params.x + bubbleSizePx / 2 < bounds.width() / 2) {
                marginPx
            } else {
                (bounds.width() - bubbleSizePx - marginPx).coerceAtLeast(0)
            }
            runCatching { windowManager.updateViewLayout(view, params) }
            SyncSettings.setBubblePosition(this@FloatingBubbleService, params.x, params.y)
        }
    }

    companion object {
        private const val TAG = "NexClipBubble"

        private const val BUBBLE_SIZE_DP = 44
        private const val BUBBLE_SHADOW_PADDING_DP = 6
        private const val BUBBLE_MARGIN_DP = 8
        private const val DEFAULT_Y_RATIO = 0.42f
        private const val PANEL_TOP_RATIO = 0.12f
        private const val PANEL_HORIZONTAL_MARGIN_DP = 12

        /** 按当前开关与权限决定启停,设置页开关、开机自启都只调这一个 */
        fun sync(context: Context) {
            val shouldRun = SyncSettings.floatingBubbleEnabled(context) &&
                Settings.canDrawOverlays(context)
            if (shouldRun) start(context) else stop(context)
        }

        fun start(context: Context) {
            runCatching {
                context.startService(Intent(context, FloatingBubbleService::class.java))
            }.onFailure { Log.w(TAG, "start bubble service failed: ${it.message}") }
        }

        fun stop(context: Context) {
            runCatching {
                context.stopService(Intent(context, FloatingBubbleService::class.java))
            }
        }
    }
}
