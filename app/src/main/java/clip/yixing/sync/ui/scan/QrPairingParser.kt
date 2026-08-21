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
     * 支持多种格式:
     * 1. 网页直连 URL (如 http://192.168.0.100:5033/index?pairCode=123456)
     * 2. 普通服务端 URL (如 http://192.168.0.100:5033)
     * 3. JSON 格式 (如 {"serverUrl":"http://...","code":"123456"})
     * 4. 纯配对码 (如 123456 或 SYNC-1234)
     * 5. 任意字符串保底
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

                val portStr = if (uri.port != -1 && uri.port != 80 && uri.port != 443) ":${uri.port}" else ""
                val serverUrl = "${uri.scheme}://${uri.host}$portStr"

                return if (!pairCode.isNullOrBlank()) {
                    QrPairingResult(
                        serverUrl = serverUrl,
                        pairCode = pairCode.trim(),
                        rawContent = trimmed
                    )
                } else {
                    // 如果 URL 中没有明确带 pairCode 参数，则该 URL 为服务端地址，配对码留空由用户在弹窗填写
                    QrPairingResult(
                        serverUrl = serverUrl,
                        pairCode = "",
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
                return QrPairingResult(
                    serverUrl = url.ifBlank { fallbackServerUrl.ifBlank { null } },
                    pairCode = code.trim(),
                    rawContent = trimmed
                )
            }
        }

        // 3. 尝试匹配配对码
        val cleanCode = trimmed.replace("-", "").replace(" ", "")
        val codeRegex = Regex("^[A-Za-z0-9]{4,12}$")
        if (codeRegex.matches(cleanCode)) {
            return QrPairingResult(
                serverUrl = fallbackServerUrl.ifBlank { null },
                pairCode = cleanCode.uppercase(),
                rawContent = trimmed
            )
        }

        // 4. 任意文本保底带入弹窗
        return QrPairingResult(
            serverUrl = fallbackServerUrl.ifBlank { null },
            pairCode = trimmed,
            rawContent = trimmed
        )
    }
}
