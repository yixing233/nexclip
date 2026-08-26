package clip.yixing.sync.service

import android.app.Activity
import android.app.NotificationManager
import android.content.Intent
import android.os.Build
import android.os.Bundle

/**
 * 专用于处理通知栏 / 超级岛快捷动作跳转的跳板 Activity
 * 符合 Android 12+ (API 31+) Notification Trampoline 规范，
 * 保证在拉起目标应用的同时立即销毁超级岛/弹窗通知。
 */
class NotificationTrampolineActivity : Activity() {

    companion object {
        const val EXTRA_TARGET_INTENT = "extra_target_intent"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 1. 点击后立即消除超级岛 / 弹窗通知
        runCatching {
            val nm = getSystemService(NotificationManager::class.java)
            nm.cancel(SyncNotificationManager.NOTIFICATION_ID_PUSH)
        }

        // 2. 拉起外部目标 App (如抖音、浏览器、搜索等)
        val targetIntent = if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(EXTRA_TARGET_INTENT, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(EXTRA_TARGET_INTENT)
        }

        if (targetIntent != null) {
            targetIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            runCatching {
                startActivity(targetIntent)
            }
        }

        // 3. 0ms 瞬间关闭跳板
        finish()
    }
}
