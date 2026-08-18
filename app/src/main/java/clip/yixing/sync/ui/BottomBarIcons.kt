package clip.yixing.sync.ui

import androidx.compose.ui.graphics.vector.ImageVector
import com.composables.icons.tabler.Tabler
import com.composables.icons.tabler.filled.ClipboardList as FilledClipboardList
import com.composables.icons.tabler.filled.Home as FilledHome
import com.composables.icons.tabler.filled.Settings as FilledSettings
import com.composables.icons.tabler.outline.ClipboardList as OutlineClipboardList
import com.composables.icons.tabler.outline.Home as OutlineHome
import com.composables.icons.tabler.outline.Settings as OutlineSettings

/** 底栏图标统一配置：线框态和填充态均来自 Tabler Icons。 */
object BottomBarIcons {
    private val home = BottomBarIcon(
        outline = Tabler.Outline.OutlineHome,
        filled = Tabler.Filled.FilledHome,
    )
    private val records = BottomBarIcon(
        outline = Tabler.Outline.OutlineClipboardList,
        filled = Tabler.Filled.FilledClipboardList,
    )
    private val settings = BottomBarIcon(
        outline = Tabler.Outline.OutlineSettings,
        filled = Tabler.Filled.FilledSettings,
    )

    fun forTab(index: Int, selected: Boolean): ImageVector {
        val icon = when (index) {
            0 -> home
            1 -> records
            else -> settings
        }
        return if (selected) icon.filled else icon.outline
    }

    private data class BottomBarIcon(
        val outline: ImageVector,
        val filled: ImageVector,
    )
}
