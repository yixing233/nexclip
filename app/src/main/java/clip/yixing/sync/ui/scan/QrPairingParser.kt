package clip.yixing.sync.ui.scan

import android.net.Uri
import org.json.JSONObject

/**
 * 扫码配对结果数据结构
 */
data class QrPairingResult(
    val serverUrl: String?,
    val pairCode: String,
    val rawContent: String
)

object QrPairingParser {

    /**
     * 智能解析二维码内容
     * 支持三种格式:
     * 1. 直连 URL (如 http://192.168.0.100:9999/index?pairCode=123456)
     * 2. JSON 格式 (如 {"serverUrl":"http://...","code":"123456"})
     * 3. 纯配对码 (如 123456)
     */
    fun parse(rawContent: String, fallbackServerUrl: String = ""): QrPairingResult? {
        val trimmed = rawContent.trim()
        if (trimmed.isBlank()) return null

        // 1. 尝试解析为 URL
        if (trimmed.startsWith("http://", ignoreCase = true) || trimmed.startsWith("https://", ignoreCase = true)) {
            val uri = runCatching { Uri.parse(trimmed) }.getOrNull()
            if (uri != null) {
                val pairCode = uri.getQueryParameter("pairCode")
                    ?: uri.getQueryParameter("code")
                    ?: uri.getQueryParameter("pairingCode")

                if (!pairCode.isNullOrBlank()) {
                    val portStr = if (uri.port != -1 && uri.port != 80 && uri.port != 443) ":${uri.port}" else ""
                    val serverUrl = "${uri.scheme}://${uri.host}$portStr"
                    return QrPairingResult(
                        serverUrl = serverUrl,
                        pairCode = pairCode.trim(),
                        rawContent = trimmed
                    )
                }
            }
        }

        // 2. 尝试解析为 JSON
        if (trimmed.startsWith("{") && trimmed.endsWith("}")) {
            val json = runCatching { JSONObject(trimmed) }.getOrNull()
            if (json != null) {
                val code = json.optString("pairCode").ifBlank {
                    json.optString("code").ifBlank {
                        json.optString("pairingCode")
                    }
                }
                val url = json.optString("serverUrl").ifBlank {
                    json.optString("url")
                }
                if (code.isNotBlank()) {
                    return QrPairingResult(
                        serverUrl = url.ifBlank { fallbackServerUrl.ifBlank { null } },
                        pairCode = code.trim(),
                        rawContent = trimmed
                    )
                }
            }
        }

        // 3. 尝试匹配纯 6 位数字或字母配对码
        val codeRegex = Regex("^[A-Za-z0-9]{4,8}$")
        if (codeRegex.matches(trimmed)) {
            return QrPairingResult(
                serverUrl = fallbackServerUrl.ifBlank { null },
                pairCode = trimmed.uppercase(),
                rawContent = trimmed
            )
        }

        return null
    }
}
