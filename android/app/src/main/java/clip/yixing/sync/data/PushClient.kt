package clip.yixing.sync.data

import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HubConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * SignalR 推送客户端:/hubs/clipboard(设备 ID + 设备令牌鉴权)。
 * Java 客户端 9.x 无内置自动重连,这里手动实现指数退避重连。
 */
class PushClient(
    private val serverUrl: String,
    private val deviceId: String,
    private val deviceToken: String,
) {
    private var connection: HubConnection? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var reconnectAttempt = 0
    private var reconnectJob: Job? = null
    private var started = false

    var onEntryReceived: ((ClipboardEntry) -> Unit)? = null
    var onStateChanged: ((String) -> Unit)? = null
    var onAuthFailure: ((Throwable) -> Unit)? = null

    val isConnected: Boolean get() = connection?.connectionState == HubConnectionState.CONNECTED

    fun connect() {
        if (started) return
        started = true
        val hubUrl = serverUrl.trimEnd('/') + "/hubs/clipboard?deviceId=" +
            java.net.URLEncoder.encode(deviceId, "UTF-8") + "&deviceToken=" +
            java.net.URLEncoder.encode(deviceToken, "UTF-8")
        val conn = HubConnectionBuilder.create(hubUrl).build()
        connection = conn
        conn.on("ClipboardUpdated", { raw ->
            // Class<T> 重载:回调直接收到反序列化后的条目对象(LinkedHashMap)
            val entry = toEntry(raw)
            if (entry != null) onEntryReceived?.invoke(entry)
        }, Any::class.java)
        conn.onClosed { error ->
            onStateChanged?.invoke("disconnected")
            if (error != null && handleAuthFailure(error)) return@onClosed
            scheduleReconnect()
        }
        startLoop()
    }

    private fun startLoop() {
        scope.launch {
            try {
                connection?.start()?.blockingAwait()
                reconnectAttempt = 0
                onStateChanged?.invoke("connected")
            } catch (e: Exception) {
                if (handleAuthFailure(e)) return@launch
                onStateChanged?.invoke("disconnected")
                scheduleReconnect()
            }
        }
    }

    private fun scheduleReconnect() {
        if (!started) return
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            delay(backoffMs())
            if (started) startLoop()
        }
    }

    private fun backoffMs(): Long {
        val delays = longArrayOf(0, 2000, 5000, 10000, 30000, 60000)
        val d = delays[minOf(reconnectAttempt, delays.size - 1)]
        reconnectAttempt++
        return d
    }

    private fun handleAuthFailure(error: Throwable): Boolean {
        if (!isAuthFailure(error)) return false
        val shouldNotify = synchronized(this) {
            if (!started) false else {
                started = false
                reconnectJob?.cancel()
                reconnectJob = null
                true
            }
        }
        if (shouldNotify) onAuthFailure?.invoke(error)
        return true
    }

    private fun isAuthFailure(error: Throwable): Boolean {
        var current: Throwable? = error
        while (current != null) {
            val text = current.message.orEmpty()
            if (Regex("(^|\\D)(401|403|410|4001)(\\D|$)").containsMatchIn(text) ||
                text.contains("Unauthorized", ignoreCase = true) ||
                text.contains("Forbidden", ignoreCase = true) ||
                text.contains("Device removed", ignoreCase = true) ||
                text.contains("设备已被移除") ||
                text.contains("设备凭证")) {
                return true
            }
            current = current.cause
        }
        return false
    }

    fun disconnect() {
        started = false
        reconnectJob?.cancel()
        reconnectJob = null
        scope.coroutineContext.cancelChildren()
        try {
            connection?.stop()?.blockingAwait()
        } catch (_: Exception) {
        }
        connection = null
    }

    private fun toEntry(raw: Any): ClipboardEntry? = try {
        val map = raw as? Map<*, *> ?: return null
        ClipboardEntry(
            id = (map["id"] as? Number)?.toLong() ?: 0L,
            type = map["type"] as? String ?: "Text",
            text = map["text"] as? String,
            imageRef = map["imageRef"] as? String,
            deviceId = map["deviceId"] as? String ?: "",
            deviceName = map["deviceName"] as? String,
            createdAt = map["createdAt"] as? String ?: "",
            isManual = (map["isManual"] as? Boolean) ?: false,
        )
    } catch (_: Exception) {
        null
    }
}
