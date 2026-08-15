using System.Data;
using Microsoft.Data.Sqlite;
using SyncClipboard.Desktop.Models;

namespace SyncClipboard.Desktop.Services;

/// <summary>
/// 本地历史(SQLite,设计文档 §5):%LOCALAPPDATA%/SyncClipboard/history.db。
/// 所有方法线程安全(锁串行);同步执行(单条数据量小)。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _conn;

    public HistoryStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncClipboard");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "history.db");
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
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
    }

    /// <summary>查询历史(新→旧)。search 模糊匹配文本;type 过滤;starredOnly 只看收藏。</summary>
    public List<HistoryItem> Query(string? search = null, string? type = null, bool starredOnly = false, int limit = 500)
    {
        lock (_lock)
        {
            var sql = "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred FROM entries";
            var conds = new List<string>();
            if (!string.IsNullOrWhiteSpace(search)) conds.Add("text LIKE @search");
            if (!string.IsNullOrWhiteSpace(type)) conds.Add("type = @type");
            if (starredOnly) conds.Add("starred = 1");
            if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
            sql += " ORDER BY created_at DESC LIMIT @limit";

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@search", $"%{search}%");
            cmd.Parameters.AddWithValue("@type", type ?? "");
            cmd.Parameters.AddWithValue("@limit", limit);
            var list = new List<HistoryItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadItem(reader));
            }
            return list;
        }
    }

    /// <summary>插入条目(server_id 去重:已存在则跳过)。返回是否新增。</summary>
    public bool Insert(HistoryItem item)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO entries
                    (server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred)
                VALUES
                    (@server_id, @type, @text, @image_path, @image_ref, @device_id, @device_name, @created_at, @origin, 0)
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
            var added = cmd.ExecuteNonQuery() > 0;
            if (added) TrimToLimitLocked();
            return added;
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

    public void Clear()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM entries";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>超限清理:删除最旧条目并清理其图片文件。</summary>
    private void TrimToLimitLocked(int? max = null)
    {
        var limit = max ?? 200;
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
    };

    public void Dispose() => _conn.Dispose();
}
