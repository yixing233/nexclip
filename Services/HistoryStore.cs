using System.Data;
using Microsoft.Data.Sqlite;
using NexClip.Desktop.Models;

namespace NexClip.Desktop.Services;

/// <summary>
/// 本地历史(SQLite,设计文档 §5):%LOCALAPPDATA%/SyncClipboard/history.db。
/// 所有方法线程安全(锁串行);同步执行(单条数据量小)。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly object _lock = new();
    private SqliteConnection _conn;
    private int _maxEntries = 200;

    /// <summary>数据库文件路径(数据管理页展示)。</summary>
    public string DbPath { get; private set; }

    /// <summary>条目上限(超限删除最旧条目并清理图片)。</summary>
    public int MaxEntries
    {
        get => _maxEntries;
        set
        {
            _maxEntries = value;
            if (value > 0) PruneToLimit(value);
        }
    }

    /// <summary>当前条目总数。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM entries";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    public HistoryStore(string storageDir)
    {
        Directory.CreateDirectory(storageDir);
        DbPath = Path.Combine(storageDir, "history.db");
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();
        EnsureSchema();
    }

    /// <summary>建表 + 兼容迁移(构造与 Reopen 共用)。</summary>
    private void EnsureSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id   INTEGER UNIQUE,
                type        TEXT NOT NULL,
                text        TEXT,
                image_path  TEXT,
                image_ref   TEXT,
                device_id   TEXT NOT NULL,
                device_name TEXT,
                created_at  INTEGER NOT NULL,
                origin      INTEGER NOT NULL DEFAULT 0,
                starred     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_entries_created ON entries(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_entries_type ON entries(type);
            """;
        cmd.ExecuteNonQuery();
        EnsureContentHashColumn();
        EnsureSourceAppColumns();
        BackfillContentHashes();
    }

    /// <summary>切换数据储存目录(设置页"修改"储存位置):关闭旧库,在新目录重建连接。</summary>
    public void Reopen(string storageDir)
    {
        lock (_lock)
        {
            _conn.Dispose();
            Directory.CreateDirectory(storageDir);
            DbPath = Path.Combine(storageDir, "history.db");
            _conn = new SqliteConnection($"Data Source={DbPath}");
            _conn.Open();
            EnsureSchema();
        }
    }

    /// <summary>迁移后更新条目图片路径(旧目录 → 新目录)。</summary>
    public void UpdateImagePaths(string oldDir, string newDir)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE entries
                SET image_path = REPLACE(image_path, @old, @new)
                WHERE image_path LIKE @old || '%'
                """;
            cmd.Parameters.AddWithValue("@old", oldDir);
            cmd.Parameters.AddWithValue("@new", newDir);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>兼容旧库:新增 content_hash 列(内容去重/置顶用)。</summary>
    private void EnsureContentHashColumn()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var has = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (r.GetString(1) == "content_hash") { has = true; break; }
            }
        }
        if (!has)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN content_hash TEXT";
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(content_hash)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>兼容旧库:新增 source_app_name, source_app_path, source_app_icon 列。</summary>
    private void EnsureSourceAppColumns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var hasName = false;
        var hasPath = false;
        var hasIcon = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "source_app_name") hasName = true;
                else if (col == "source_app_path") hasPath = true;
                else if (col == "source_app_icon") hasIcon = true;
            }
        }

        if (!hasName)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN source_app_name TEXT";
            cmd.ExecuteNonQuery();
        }
        if (!hasPath)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN source_app_path TEXT";
            cmd.ExecuteNonQuery();
        }
        if (!hasIcon)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN source_app_icon TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>为历史存量条目回填内容哈希(文本取文本哈希,图片取缓存文件字节哈希)。</summary>
    private void BackfillContentHashes()
    {
        var rows = new List<(long Id, string Type, string? Text, string? ImagePath)>();
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT id, type, text, image_path FROM entries WHERE content_hash IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }
        foreach (var (id, type, text, imagePath) in rows)
        {
            string? hash = null;
            try
            {
                if (type == "Text" && !string.IsNullOrEmpty(text))
                {
                    hash = ClipboardMonitor.HashText(text);
                }
                else if (type == "Image" && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    hash = ClipboardMonitor.HashBytes(File.ReadAllBytes(imagePath));
                }
            }
            catch
            {
                // 文件缺失/读取失败:哈希留空,不影响其它逻辑
            }
            if (hash is null) continue;
            using var up = _conn.CreateCommand();
            up.CommandText = "UPDATE entries SET content_hash = @hash WHERE id = @id";
            up.Parameters.AddWithValue("@hash", hash);
            up.Parameters.AddWithValue("@id", id);
            up.ExecuteNonQuery();
        }
    }

    /// <summary>查询历史(新→旧)。search 模糊匹配文本;type 过滤;starredOnly 只看收藏;urlOnly 只看链接。</summary>
    public List<HistoryItem> Query(string? search = null, string? type = null, bool starredOnly = false, int limit = 500, bool urlOnly = false, int offset = 0)
    {
        lock (_lock)
        {
            var sql = "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon FROM entries";
            var conds = new List<string>();
            if (!string.IsNullOrWhiteSpace(search)) conds.Add("text LIKE @search");
            if (!string.IsNullOrWhiteSpace(type)) conds.Add("type = @type");
            if (starredOnly) conds.Add("starred = 1");
            if (urlOnly) conds.Add("(type = 'Text' AND (text LIKE 'http://%' OR text LIKE 'https://%'))");
            if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
            sql += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@search", $"%{search}%");
            cmd.Parameters.AddWithValue("@type", type ?? "");
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
            var list = new List<HistoryItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadItem(reader));
            }
            return list;
        }
    }

    /// <summary>插入条目(server_id 去重:已存在则跳过)。返回新条目 id;跳过/重复返回 0。</summary>
    public long Insert(HistoryItem item)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO entries
                    (server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon)
                VALUES
                    (@server_id, @type, @text, @image_path, @image_ref, @device_id, @device_name, @created_at, @origin, 0, @content_hash, @source_app_name, @source_app_path, @source_app_icon);
                SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;
                """;
            cmd.Parameters.AddWithValue("@server_id", (object?)item.ServerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", item.Type);
            cmd.Parameters.AddWithValue("@text", (object?)item.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_path", (object?)item.ImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_ref", (object?)item.ImageRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@device_id", item.DeviceId);
            cmd.Parameters.AddWithValue("@device_name", (object?)item.DeviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", item.CreatedAt.ToUniversalTime().Ticks);
            cmd.Parameters.AddWithValue("@origin", item.Origin);
            cmd.Parameters.AddWithValue("@content_hash", (object?)ResolveContentHash(item) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_name", (object?)item.SourceAppName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_path", (object?)item.SourceAppPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_icon", (object?)item.SourceAppIcon ?? DBNull.Value);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            if (id > 0) TrimToLimitLocked();
            return id;
        }
    }

    /// <summary>调用方未显式提供时计算内容哈希(文本/图片字节)。图片文件缺失则留空。</summary>
    private static string? ResolveContentHash(HistoryItem item)
    {
        if (!string.IsNullOrEmpty(item.ContentHash)) return item.ContentHash;
        try
        {
            if (item.Type == "Text" && !string.IsNullOrEmpty(item.Text))
            {
                return ClipboardMonitor.HashText(item.Text);
            }
            if (item.Type == "Image" && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                return ClipboardMonitor.HashBytes(File.ReadAllBytes(item.ImagePath));
            }
        }
        catch
        {
            // 读取失败不影响入库
        }
        return null;
    }

    /// <summary>
    /// 外部复制与已有内容重复时,不新增条目,直接把该内容最新一条置顶(created_at 更新为当前),
    /// 并同步 server_id / 设备信息。返回是否命中已有条目。
    /// </summary>
    public bool TouchByHash(
        string contentHash,
        long? serverId,
        string deviceId,
        string? deviceName,
        DateTime createdAt,
        string? sourceAppName = null,
        string? sourceAppPath = null,
        string? sourceAppIcon = null)
    {
        if (string.IsNullOrEmpty(contentHash)) return false;
        lock (_lock)
        {
            using var find = _conn.CreateCommand();
            find.CommandText = "SELECT id FROM entries WHERE content_hash = @hash ORDER BY created_at DESC LIMIT 1";
            find.Parameters.AddWithValue("@hash", contentHash);
            var found = find.ExecuteScalar();
            if (found is null) return false;
            var id = Convert.ToInt64(found);

            using var upd = _conn.CreateCommand();
            upd.CommandText = """
                UPDATE entries SET
                    created_at = @created_at,
                    device_id = @device_id,
                    device_name = @device_name,
                    source_app_name = COALESCE(@source_app_name, source_app_name),
                    source_app_path = COALESCE(@source_app_path, source_app_path),
                    source_app_icon = COALESCE(@source_app_icon, source_app_icon)
                WHERE id = @id
                """;
            upd.Parameters.AddWithValue("@created_at", createdAt.ToUniversalTime().Ticks);
            upd.Parameters.AddWithValue("@device_id", deviceId);
            upd.Parameters.AddWithValue("@device_name", (object?)deviceName ?? DBNull.Value);
            upd.Parameters.AddWithValue("@source_app_name", (object?)sourceAppName ?? DBNull.Value);
            upd.Parameters.AddWithValue("@source_app_path", (object?)sourceAppPath ?? DBNull.Value);
            upd.Parameters.AddWithValue("@source_app_icon", (object?)sourceAppIcon ?? DBNull.Value);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();

            // server_id 单独更新:若与其它行 UNIQUE 冲突(极端情况)则保留原值
            if (serverId is { } sid)
            {
                try
                {
                    using var su = _conn.CreateCommand();
                    su.CommandText = "UPDATE entries SET server_id = @sid WHERE id = @id";
                    su.Parameters.AddWithValue("@sid", sid);
                    su.Parameters.AddWithValue("@id", id);
                    su.ExecuteNonQuery();
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // UNIQUE 冲突:忽略,保持原 server_id
                }
            }
            return true;
        }
    }

    /// <summary>按内容哈希读取最新本地条目,用于离线捕获与重复内容置顶后的 UI 更新。</summary>
    public HistoryItem? FindByHash(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash)) return null;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon FROM entries WHERE content_hash = @hash ORDER BY created_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@hash", contentHash);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    /// <summary>按条目数上限清理(设置变更时立即执行)。返回删除条数。</summary>
    public int PruneToLimit(int max)
    {
        if (max <= 0) return 0;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM entries ORDER BY created_at DESC LIMIT -1 OFFSET @limit";
            cmd.Parameters.AddWithValue("@limit", max);
            var doomed = new List<long>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) doomed.Add(r.GetInt64(0));
            }
            if (doomed.Count == 0) return 0;
            foreach (var id in doomed) DeleteImageForLocked(id);
            cmd.CommandText = "DELETE FROM entries WHERE id IN (SELECT id FROM entries ORDER BY created_at DESC LIMIT -1 OFFSET @limit)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@limit", max);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>按时间上限清理(超过 days 天的条目删除,0=不清理)。返回删除条数。</summary>
    public int PruneOlderThan(int days)
    {
        if (days <= 0) return 0;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days).Ticks;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, image_path FROM entries WHERE created_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            var doomed = new List<(long Id, string? Path)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) doomed.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            if (doomed.Count == 0) return 0;
            foreach (var (_, p) in doomed)
            {
                if (!string.IsNullOrEmpty(p))
                {
                    try { File.Delete(p); } catch { /* 忽略 */ }
                }
            }
            cmd.CommandText = "DELETE FROM entries WHERE created_at < @cutoff";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ToggleStar(long id, bool starred)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE entries SET starred = @starred WHERE id = @id";
            cmd.Parameters.AddWithValue("@starred", starred ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>编辑条目文本(仅更新本地记录)。</summary>
    public void UpdateText(long id, string text)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE entries SET text = @text WHERE id = @id";
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int CountStarred()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM entries WHERE starred = 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Clear(bool keepStarred = false)
    {
        lock (_lock)
        {
            // 先收集图片文件再删记录(清空历史含图片缓存,不可恢复)
            var files = new List<string>();
            using (var q = _conn.CreateCommand())
            {
                q.CommandText = keepStarred
                    ? "SELECT image_path FROM entries WHERE image_path IS NOT NULL AND starred = 0"
                    : "SELECT image_path FROM entries WHERE image_path IS NOT NULL";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0) && !string.IsNullOrEmpty(r.GetString(0))) files.Add(r.GetString(0));
                }
            }
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* 忽略 */ }
            }
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = keepStarred ? "DELETE FROM entries WHERE starred = 0" : "DELETE FROM entries";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>超限清理:删除最旧条目并清理其图片文件。</summary>
    private void TrimToLimitLocked()
    {
        var limit = _maxEntries;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM entries WHERE id IN (
                SELECT id FROM entries ORDER BY created_at DESC LIMIT -1 OFFSET @limit
            )
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        // 简单做法: 先查被删行,再删;这里改为两步
        reader.Close();
        cmd.CommandText = "SELECT id FROM entries ORDER BY created_at DESC LIMIT -1 OFFSET @limit";
        var doomed = new List<long>();
        using (var r2 = cmd.ExecuteReader())
        {
            while (r2.Read()) doomed.Add(r2.GetInt64(0));
        }
        foreach (var id in doomed)
        {
            DeleteImageForLocked(id);
        }
        cmd.CommandText = "DELETE FROM entries WHERE id IN (SELECT id FROM entries ORDER BY created_at DESC LIMIT -1 OFFSET @limit)";
        cmd.ExecuteNonQuery();
    }

    private void DeleteImageForLocked(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT image_path FROM entries WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var path = cmd.ExecuteScalar() as string;
        if (!string.IsNullOrEmpty(path))
        {
            try { File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    private static HistoryItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ServerId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        Type = reader.GetString(2),
        Text = reader.IsDBNull(3) ? null : reader.GetString(3),
        ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
        ImageRef = reader.IsDBNull(5) ? null : reader.GetString(5),
        DeviceId = reader.GetString(6),
        DeviceName = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAt = new DateTime(reader.GetInt64(8), DateTimeKind.Utc),
        Origin = reader.GetInt32(9),
        Starred = reader.GetInt32(10) != 0,
        ContentHash = reader.IsDBNull(11) ? null : reader.GetString(11),
        SourceAppName = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
        SourceAppPath = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null,
        SourceAppIcon = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
    };

    public void Dispose() => _conn.Dispose();
}
