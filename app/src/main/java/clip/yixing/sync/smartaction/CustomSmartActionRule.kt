package clip.yixing.sync.smartaction

import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * 自定义动作执行类型:
 * - URL: 网页或深链打开 (如 https://www.google.com/search?q={match})
 * - SCHEME: 自定义 App Scheme 唤起 (如 zhihu://search?q={match})
 * - COPY: 正则匹配并提取复制
 */
enum class SmartActionType(val label: String) {
    URL("网页或深度链接"),
    SCHEME("应用 Scheme 唤起"),
    COPY("提取纯文本复制")
}

/**
 * 自定义智能动作规则实体
 */
data class CustomSmartActionRule(
    val id: String = UUID.randomUUID().toString(),
    val name: String,
    val pattern: String,
    val type: SmartActionType = SmartActionType.URL,
    val targetTemplate: String = "",
    val enabled: Boolean = true
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("id", id)
        put("name", name)
        put("pattern", pattern)
        put("type", type.name)
        put("targetTemplate", targetTemplate)
        put("enabled", enabled)
    }

    companion object {
        fun fromJson(json: JSONObject): CustomSmartActionRule? {
            return runCatching {
                CustomSmartActionRule(
                    id = json.optString("id", UUID.randomUUID().toString()),
                    name = json.getString("name"),
                    pattern = json.getString("pattern"),
                    type = runCatching { SmartActionType.valueOf(json.optString("type", SmartActionType.URL.name)) }.getOrDefault(SmartActionType.URL),
                    targetTemplate = json.optString("targetTemplate", ""),
                    enabled = json.optBoolean("enabled", true)
                )
            }.getOrNull()
        }

        fun listFromJson(jsonStr: String?): List<CustomSmartActionRule> {
            if (jsonStr.isNullOrBlank()) return emptyList()
            val list = mutableListOf<CustomSmartActionRule>()
            runCatching {
                val array = JSONArray(jsonStr)
                for (i in 0 until array.length()) {
                    val obj = array.getJSONObject(i)
                    fromJson(obj)?.let { list.add(it) }
                }
            }
            return list
        }

        fun listToJson(list: List<CustomSmartActionRule>): String {
            val array = JSONArray()
            for (rule in list) {
                array.put(rule.toJson())
            }
            return array.toString()
        }
    }
}
