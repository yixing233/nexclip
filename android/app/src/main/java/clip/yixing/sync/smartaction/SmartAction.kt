package clip.yixing.sync.smartaction

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import clip.yixing.sync.util.AppSourceHelper
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.theme.MiuixTheme

/**
 * 智能动作识别模型
 */
data class SmartAction(
    val id: String,
    val title: String,
    val summary: String? = null,
    val icon: ImageVector,
    val color: Color? = null,
    val hexColor: String? = null,
    val targetPackage: String? = null,
    val targetIntent: Intent? = null,
    val isBroadcast: Boolean = false,
    val action: (context: Context) -> Unit
) {
    /**
     * 生成用于通知栏 Action / 小米超级岛 TextButton 的 PendingIntent
     */
    fun createPendingIntent(context: Context, requestCode: Int): PendingIntent? {
        val intent = targetIntent ?: return null
        return if (isBroadcast) {
            PendingIntent.getBroadcast(
                context,
                requestCode,
                intent,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )
        } else {
            val trampolineIntent = Intent(context, clip.yixing.sync.service.NotificationTrampolineActivity::class.java).apply {
                putExtra(clip.yixing.sync.service.NotificationTrampolineActivity.EXTRA_TARGET_INTENT, intent)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            }
            PendingIntent.getActivity(
                context,
                requestCode,
                trampolineIntent,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
            )
        }
    }
}

/**
 * 智能动作小胶囊组件 (优先呈现目标应用原生高清图标)
 */
@Composable
fun SmartActionChip(
    action: SmartAction,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current
    val accentColor = action.color ?: MiuixTheme.colorScheme.primary

    val appIcon = remember(action.targetPackage, action.targetIntent) {
        val pkg = action.targetPackage ?: action.targetIntent?.`package`
        AppSourceHelper.getAppIconBitmap(context, pkg)
    }

    Row(
        modifier = modifier
            .clip(RoundedCornerShape(8.dp))
            .background(accentColor.copy(alpha = 0.10f))
            .clickable { onClick() }
            .padding(horizontal = 9.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center
    ) {
        if (appIcon != null) {
            Image(
                bitmap = appIcon,
                contentDescription = null,
                modifier = Modifier
                    .size(14.dp)
                    .clip(RoundedCornerShape(3.dp))
            )
        } else if (action.id.startsWith("color_") && action.color != null) {
            Box(
                modifier = Modifier
                    .size(11.dp)
                    .clip(CircleShape)
                    .background(action.color)
            )
        } else {
            Icon(
                imageVector = action.icon,
                contentDescription = null,
                tint = accentColor,
                modifier = Modifier.size(13.dp)
            )
        }
        Spacer(Modifier.width(5.dp))
        Text(
            text = action.title,
            color = accentColor,
            fontSize = 12.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}
