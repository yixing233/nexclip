package clip.yixing.sync.sms

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Telephony
import android.util.Log

/**
 * 短信接收入口。
 *
 * 只做两件事: 把多段短信拼回完整正文, 然后交给 [SmsCodeHandler] 提取验证码。
 * 正文不落盘、不上报, 匹配不到验证码的短信直接丢弃。
 *
 * 系统会在开机、权限变更等场景重投广播, 因此处理必须幂等 —— 幂等性由
 * [SmsCodeHandler] 的去重窗口保证。
 */
class SmsCodeReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action != Telephony.Sms.Intents.SMS_RECEIVED_ACTION) {
            SmsDiagLog.log(context, SmsDiagLog.Stage.IGNORED_ACTION, "action=${intent?.action}")
            return
        }

        val body = runCatching { extractBody(intent) }.getOrElse {
            Log.w(TAG, "parse sms intent failed: ${it.message}")
            SmsDiagLog.log(context, SmsDiagLog.Stage.ERROR, "extractBody=${it.javaClass.simpleName}")
            return
        }
        if (body.isNullOrBlank()) {
            // 区分「广播没到」与「到了但没解析出正文」——排查漏报时这是第一个分叉点。
            // 不打印正文(隐私边界),只记录 PDU 段数与关键 extra 是否存在。
            val segments = intent.getSerializableExtra("pdus")?.let { (it as? Array<*>)?.size } ?: 0
            SmsDiagLog.log(
                context,
                SmsDiagLog.Stage.EMPTY_BODY,
                "seg=$segments hasBodyExtra=${intent.hasExtra("body")}",
            )
            Log.w(TAG, "sms body empty, segments=$segments")
            return
        }

        val segments = intent.getSerializableExtra("pdus")?.let { (it as? Array<*>)?.size } ?: 1
        SmsDiagLog.log(context, SmsDiagLog.Stage.RECEIVED, SmsDiagLog.fingerprint(body, segments))

        // goAsync 让接收器在 onReceive 返回后仍能完成异步工作, 否则进程可能在
        // 写剪贴板/发通知前被回收
        val pendingResult = goAsync()
        try {
            SmsCodeHandler.handle(context.applicationContext, body)
        } catch (t: Throwable) {
            Log.w(TAG, "handle sms failed: ${t.message}")
            SmsDiagLog.log(context, SmsDiagLog.Stage.ERROR, "handle=${t.javaClass.simpleName}")
        } finally {
            pendingResult.finish()
        }
    }

    /**
     * 把一条短信的所有分段拼成完整正文。
     *
     * `getMessagesFromIntent` 返回的是**所有分段**, 长短信会被拆成多条。若只取第一条,
     * 验证码落在后半段时就会漏提取 —— 这是短信类功能最常见的漏报来源。
     */
    private fun extractBody(intent: Intent): String? {
        val messages = Telephony.Sms.Intents.getMessagesFromIntent(intent)
        if (messages.isNullOrEmpty()) return null
        // displayMessageBody 在部分 CDMA 制式上可能为空, 回退到 raw messageBody
        val merged = messages.joinToString(separator = "") {
            it.displayMessageBody ?: it.messageBody.orEmpty()
        }
        return merged.ifBlank { null }
    }

    private companion object {
        const val TAG = "SmsCodeReceiver"
    }
}
