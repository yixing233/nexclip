package clip.yixing.sync.data

import com.microsoft.signalr.HubConnection
import com.microsoft.signalr.HubConnectionBuilder
import com.microsoft.signalr.HubConnectionState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * SignalR 推送客户端:/hubs/clipboard(免认证,配对码即接入凭据)。
 * Java 客户端 9.x 无内置自动重连,这里手动实现指数退避重连。
 */
class PushClient(private val serverUrl: String, private val deviceId: String) {
    private var connection: HubConnection? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var reconnectAttempt = 0
    private var started = false

    var onEntryReceived: ((ClipboardEntry) -> Unit)? = null
    var onStateChanged: ((String) -> Unit)? = null

    val isConnected: Boolean get() = connection?.connectionState == HubConnectionState.CONNECTED

    fun connect() {
        if (started) return
        started = true
        // 带 deviceId:服务端登记设备 + 心跳,在线状态可见(hub 免认证,无令牌)
        val hubUrl = serverUrl.trimEnd('/') + "/hubs/clipboard?deviceId=" + java.net.URLEncoder.encode(deviceId, "UTF-8")
        val conn = HubConnectionBuilder.create(hubUrl).build()
        connection = conn
        conn.on("ClipboardUpdated", { raw ->
            // Class<T> 重载:回调直接收到反序列化后的条目对象(LinkedHashMap)
            val entry = toEntry(raw)
            if (entry != null) onEntryReceived?.invoke(entry)
        }, Any::class.java)
        conn.onClosed {
            onStateChanged?.invoke("disconnected")
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
            } catch (_: Exception) {
                onStateChanged?.invoke("disconnected")
                scheduleReconnect()
            }
        }
    }

    private fun scheduleReconnect() {
        if (!started) return
        scope.launch {
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

    fun disconnect() {
        started = false
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
        )
    } catch (_: Exception) {
        null
    }
}
