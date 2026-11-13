package clip.yixing.sync.sms

import clip.yixing.sync.hook.SmsHandlerHook
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Telephony
import android.util.Log

/**
 * 短信接收入口,双通道:
 *
 * 1. **Hook 通道**(主路径,见 [clip.yixing.sync.hook.SmsHandlerHook]):
 *    com.android.phone 进程内拦截 SMS_DELIVER 后以
 *    [ACTION_SMS_CODE_INTERCEPT] 发来的正文广播。实测 HyperOS 不把
 *    SMS_RECEIVED 投给第三方接收器,这条是唯一可靠路径。
 * 2. **系统广播通道**(SMS_RECEIVED):标准 AOSP 路径,在未激活 Xposed
 *    或 hook 失效的设备上兜底。收到但解析不出正文时记入诊断日志。
 *
 * 两条通道最终都汇入 [SmsCodeHandler.handle()],由其去重窗口保证幂等 ——
 * 同一条短信可能同时走两条通道到达(Hook 先、广播后),去重窗口会挡住第二次。
 *
 * 正文不落盘、不上报, 匹配不到验证码的短信直接丢弃。
 */
class SmsCodeReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent?) {
        when (intent?.action) {
            SmsHandlerHook.ACTION_SMS_CODE_INTERCEPT -> handleHookChannel(context, intent)
            Telephony.Sms.Intents.SMS_RECEIVED_ACTION -> handleSystemChannel(context, intent)
            else -> {
                SmsDiagLog.log(context, SmsDiagLog.Stage.IGNORED_ACTION, "action=${intent?.action}")
            }
        }
    }

    /** Hook 通道:正文已由 hook 进程拼好,直接处理 */
    private fun handleHookChannel(context: Context, intent: Intent) {
        val body = intent.getStringExtra(SmsHandlerHook.EXTRA_BODY)
        if (body.isNullOrBlank()) {
            SmsDiagLog.log(context, SmsDiagLog.Stage.EMPTY_BODY, "hook channel empty body")
            return
        }
        val subscription = intent.getIntExtra(SmsHandlerHook.EXTRA_SUBSCRIPTION, -1)
        SmsDiagLog.log(context, SmsDiagLog.Stage.RECEIVED, SmsDiagLog.fingerprint(body, 1, channel = "hook"))
        dispatch(context, body, "hook sub=$subscription")
    }

    /** 系统广播通道:自己从 pdus 组装正文 */
    private fun handleSystemChannel(context: Context, intent: Intent) {
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
        SmsDiagLog.log(context, SmsDiagLog.Stage.RECEIVED, SmsDiagLog.fingerprint(body, segments, channel = "system"))
        dispatch(context, body, "system")
    }

    private fun dispatch(context: Context, body: String, source: String) {
        // goAsync 让接收器在 onReceive 返回后仍能完成异步工作, 否则进程可能在
        // 写剪贴板/发通知前被回收
        val pendingResult = goAsync()
        try {
            SmsCodeHandler.handle(context.applicationContext, body)
        } catch (t: Throwable) {
            Log.w(TAG, "handle sms failed: ${t.message}")
            SmsDiagLog.log(context, SmsDiagLog.Stage.ERROR, "handle=$source ${t.javaClass.simpleName}")
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
