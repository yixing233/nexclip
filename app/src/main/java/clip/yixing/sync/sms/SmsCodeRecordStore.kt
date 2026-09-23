package clip.yixing.sync.sms

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/** 本机验证码处理记录。只保存时间、处理状态和提取结果，不保存短信正文或发件人。 */
object SmsCodeRecordStore {
    private const val PREFS = "sms_code_records"
    private const val KEY_RECORDS = "records"
    private const val MAX_RECORDS = 100

    enum class Status { EXTRACTED, NOT_FOUND, DUPLICATE }

    data class Record(val timestamp: Long, val status: Status, val code: String?)

    @Synchronized
    fun add(context: Context, status: Status, code: String? = null) {
        val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val records = read(prefs.getString(KEY_RECORDS, null)).toMutableList()
        records.add(0, Record(System.currentTimeMillis(), status, code))
        val json = JSONArray()
        records.take(MAX_RECORDS).forEach { record ->
            json.put(JSONObject().apply {
                put("time", record.timestamp)
                put("status", record.status.name)
                if (record.code != null) put("code", record.code)
            })
        }
        prefs.edit().putString(KEY_RECORDS, json.toString()).apply()
    }

    fun getAll(context: Context): List<Record> {
        val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        return read(prefs.getString(KEY_RECORDS, null))
    }

    fun clear(context: Context) {
        context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().remove(KEY_RECORDS).apply()
    }

    private fun read(raw: String?): List<Record> {
        if (raw.isNullOrBlank()) return emptyList()
        return runCatching {
            val json = JSONArray(raw)
            (0 until json.length()).mapNotNull { index ->
                val item = json.optJSONObject(index) ?: return@mapNotNull null
                val status = runCatching { Status.valueOf(item.optString("status")) }.getOrNull()
                    ?: return@mapNotNull null
                Record(item.optLong("time"), status, item.optString("code").takeIf { it.isNotBlank() })
            }
        }.getOrDefault(emptyList())
    }
}
