package clip.yixing.sync.paste

import android.content.Context
import android.view.View
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.ComposeView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.lifecycle.setViewTreeViewModelStoreOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import clip.yixing.sync.ui.theme.NexClipTheme

/**
 * 让 Compose 跑在 [android.view.WindowManager] 覆盖窗口里的样板宿主。
 *
 * `ComposeView` 会向上找 ViewTree 里的 Lifecycle / ViewModelStore / SavedStateRegistry,
 * Activity 会自动提供这三样,Service 里的悬浮窗必须自己造 —— 少任何一个 setContent 都会直接抛异常。
 *
 * 用法:[view] 挂进 WindowManager 之后调 [onShown],摘下来之前调 [onHidden],Service 销毁时调 [destroy]。
 */
class OverlayComposeHost(
    context: Context,
    content: @Composable () -> Unit
) : LifecycleOwner, ViewModelStoreOwner, SavedStateRegistryOwner {

    private val lifecycleRegistry = LifecycleRegistry(this)
    private val savedStateRegistryController = SavedStateRegistryController.create(this)

    override val lifecycle: Lifecycle get() = lifecycleRegistry
    override val viewModelStore: ViewModelStore = ViewModelStore()
    override val savedStateRegistry: SavedStateRegistry
        get() = savedStateRegistryController.savedStateRegistry

    /** 挂进 WindowManager 的根视图 */
    val view: View = ComposeView(context).apply {
        setViewTreeLifecycleOwner(this@OverlayComposeHost)
        setViewTreeViewModelStoreOwner(this@OverlayComposeHost)
        setViewTreeSavedStateRegistryOwner(this@OverlayComposeHost)
        setContent { NexClipTheme { content() } }
    }

    init {
        savedStateRegistryController.performRestore(null)
        lifecycleRegistry.currentState = Lifecycle.State.CREATED
    }

    /** 窗口已添加:推到 RESUMED,组合开始生效 */
    fun onShown() {
        if (lifecycleRegistry.currentState != Lifecycle.State.DESTROYED) {
            lifecycleRegistry.currentState = Lifecycle.State.RESUMED
        }
    }

    /** 窗口即将移除:退回 CREATED,停掉动画与 collect */
    fun onHidden() {
        if (lifecycleRegistry.currentState != Lifecycle.State.DESTROYED) {
            lifecycleRegistry.currentState = Lifecycle.State.CREATED
        }
    }

    /** 彻底销毁,之后这个宿主不可再用 */
    fun destroy() {
        if (lifecycleRegistry.currentState == Lifecycle.State.DESTROYED) return
        lifecycleRegistry.currentState = Lifecycle.State.DESTROYED
        viewModelStore.clear()
    }
}
