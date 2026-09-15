using System.Data;
using System.Text;
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

    /// <summary>条目上限(仅限制未收藏条目；收藏项不参与自动淘汰)。</summary>
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
        EnsureRemarkColumn();
        EnsureHtmlColumn();
        EnsureFilePathColumn();
        EnsureSearchColumns();
        BackfillContentHashes();
        BackfillSearchBlobs();
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

    /// <summary>兼容旧库:新增 remark 列(用户自定义备注)。</summary>
    private void EnsureRemarkColumn()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var hasRemark = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (r.GetString(1) == "remark") { hasRemark = true; break; }
            }
        }
        if (!hasRemark)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN remark TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>兼容旧库:新增 html 列(富文本条目的 HTML 片段;纯文本条目为 NULL)。</summary>
    private void EnsureHtmlColumn()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var hasHtml = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (r.GetString(1) == "html") { hasHtml = true; break; }
            }
        }
        if (!hasHtml)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN html TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 兼容旧库:新增 file_paths 列(文件条目的路径元数据 JSON)。
    /// 该列与 image_path 语义严格分离:文件条目不得写入 image_path,
    /// 因为所有清理路径(TrimToLimitLocked / PruneOlderThan / Clear)都会直接删除 image_path 指向的文件。
    /// </summary>
    private void EnsureFilePathColumn()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var hasFilePaths = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (r.GetString(1) == "file_paths") { hasFilePaths = true; break; }
            }
        }
        if (!hasFilePaths)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN file_paths TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>拼音检索串中"主读音"与"备选读音"的分段符（控制字符，不会出现在用户输入里）。</summary>
    private const char VariantSeparator = '\u0001';

    /// <summary>
    /// 兼容旧库:新增 pinyin_blob / initials_blob 两列（拼音与首字母检索串）。
    ///
    /// 检索串在写入时预计算，查询阶段只做 LIKE 子串匹配。
    /// 旧实现把拼音匹配放在 C# 注册的 SQLite 函数里：每行、每字段各回调一次，
    /// 且每次都重新计算整段文本的拼音——既随库增长线性变慢，又让所有索引失效。
    /// </summary>
    private void EnsureSearchColumns()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(entries)";
        var hasPinyin = false;
        var hasInitials = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "pinyin_blob") hasPinyin = true;
                else if (col == "initials_blob") hasInitials = true;
            }
        }
        if (!hasPinyin)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN pinyin_blob TEXT";
            cmd.ExecuteNonQuery();
        }
        if (!hasInitials)
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN initials_blob TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 拼出用于拼音检索的源文本。字段与字面检索保持一致，并额外纳入文件名与完整路径
    /// （字面检索靠 file_paths 的 JSON 原文，拼音检索需要的是去转义后的可读文本）。
    /// </summary>
    private static string BuildPinyinSource(
        string? text, string? remark, string? appName, string? deviceName, string? type, string? filePathsJson)
    {
        var sb = new StringBuilder();
        Append(text);
        Append(remark);
        Append(appName);
        Append(deviceName);
        if (string.Equals(type, "File", StringComparison.Ordinal) && !string.IsNullOrEmpty(filePathsJson))
        {
            foreach (var f in ClipboardFileMeta.Parse(filePathsJson))
            {
                Append(f.Name);
                Append(f.Path);
            }
        }
        return sb.ToString();

        void Append(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(value);
        }
    }

    /// <summary>
    /// 由源文本算出「全拼」与「首字母」检索串。
    ///
    /// 多音字的备选读音以 <see cref="VariantSeparator"/> 分段后追加在同一个字段内：
    /// 这样既能让「重庆」同时被 zhongqing 与 chongqing 命中，又不会破坏两个变体各自内部的
    /// 音节连续性（跨分段符的查询不会误命中）。文本不含多音字时只存主读音串，避免存储无谓翻倍。
    /// </summary>
    private static (string Pinyin, string Initials) BuildSearchBlobs(string source)
    {
        if (source.Length == 0) return ("", "");

        var pinyin = PinyinHelper.GetFullPinyin(source);
        var pinyinAlt = PinyinHelper.GetFullPinyinVariant(source);
        var initials = PinyinHelper.GetInitials(source);
        var initialsAlt = PinyinHelper.GetInitialsVariant(source);

        return (
            pinyinAlt.Length > 0 && !string.Equals(pinyinAlt, pinyin, StringComparison.Ordinal)
                ? pinyin + VariantSeparator + pinyinAlt
                : pinyin,
            initialsAlt.Length > 0 && !string.Equals(initialsAlt, initials, StringComparison.Ordinal)
                ? initials + VariantSeparator + initialsAlt
                : initials);
    }

    /// <summary>
    /// 转义 LIKE 通配符（配合 SQL 里的 ESCAPE '\'）。
    /// 用户输入的下划线/百分号必须按字面处理：旧实现把输入直接拼进 '%...%'，
    /// 而 LIKE 的 _ 匹配任意单字符、% 匹配任意串，导致"搜一个下划线"会命中整库。
    /// </summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// 为存量条目回填拼音/首字母检索串。
    /// 以 IS NULL 作为"尚未计算"的标记：新写入的条目即使无可检索内容也会写入空串，
    /// 因此不会在每次启动时被重复回填。
    /// </summary>
    private void BackfillSearchBlobs()
    {
        var rows = new List<(long Id, string? Text, string? Remark, string? App, string? Device, string? Type, string? FilePaths)>();
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT id, text, remark, source_app_name, device_name, type, file_paths FROM entries WHERE pinyin_blob IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            var source = BuildPinyinSource(row.Text, row.Remark, row.App, row.Device, row.Type, row.FilePaths);
            var (pinyin, initials) = BuildSearchBlobs(source);
            using var up = _conn.CreateCommand();
            up.CommandText = "UPDATE entries SET pinyin_blob = @py, initials_blob = @ini WHERE id = @id";
            up.Parameters.AddWithValue("@py", pinyin);
            up.Parameters.AddWithValue("@ini", initials);
            up.Parameters.AddWithValue("@id", row.Id);
            up.ExecuteNonQuery();
        }
        Log.Info($"已为 {rows.Count} 条存量历史回填拼音检索串");
    }

    /// <summary>文本或备注变更后重算该条目的拼音检索串。</summary>
    private void RefreshSearchBlobsLocked(long id)
    {
        string? text, remark, app, device, type, filePaths;
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT text, remark, source_app_name, device_name, type, file_paths FROM entries WHERE id = @id";
            q.Parameters.AddWithValue("@id", id);
            using var r = q.ExecuteReader();
            if (!r.Read()) return;
            text = r.IsDBNull(0) ? null : r.GetString(0);
            remark = r.IsDBNull(1) ? null : r.GetString(1);
            app = r.IsDBNull(2) ? null : r.GetString(2);
            device = r.IsDBNull(3) ? null : r.GetString(3);
            type = r.IsDBNull(4) ? null : r.GetString(4);
            filePaths = r.IsDBNull(5) ? null : r.GetString(5);
        }

        var (pinyin, initials) = BuildSearchBlobs(BuildPinyinSource(text, remark, app, device, type, filePaths));
        using var up = _conn.CreateCommand();
        up.CommandText = "UPDATE entries SET pinyin_blob = @py, initials_blob = @ini WHERE id = @id";
        up.Parameters.AddWithValue("@py", pinyin);
        up.Parameters.AddWithValue("@ini", initials);
        up.Parameters.AddWithValue("@id", id);
        up.ExecuteNonQuery();
    }

    /// <summary>为历史存量条目回填内容哈希(文本取文本哈希,图片取缓存文件字节哈希,文件取路径元数据哈希)。</summary>
    private void BackfillContentHashes()
    {
        var rows = new List<(long Id, string Type, string? Text, string? ImagePath, string? Html, string? FilePaths)>();
        using (var q = _conn.CreateCommand())
        {
            q.CommandText = "SELECT id, type, text, image_path, html, file_paths FROM entries WHERE content_hash IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt64(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5)));
            }
        }
        foreach (var (id, type, text, imagePath, html, filePaths) in rows)
        {
            string? hash = null;
            try
            {
                if (type == "Text" && !string.IsNullOrEmpty(text))
                {
                    // 存量条目 html 恒为 NULL,哈希与加富文本之前完全一致
                    hash = ClipboardMonitor.HashText(text, html);
                }
                else if (type == "Image" && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    hash = ClipboardMonitor.HashBytes(File.ReadAllBytes(imagePath));
                }
                else if (type == "File" && !string.IsNullOrEmpty(filePaths))
                {
                    // 只对元数据求哈希,不读取任何文件内容
                    hash = ClipboardFileMeta.ComputeHash(ClipboardFileMeta.Parse(filePaths));
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

    /// <summary>
    /// 查询历史(新→旧)。search 匹配文本、备注、来源应用、来源设备、文件路径,以及拼音全拼/首字母;
    /// type 过滤;starredOnly 只看收藏;urlOnly 只看链接。
    /// </summary>
    public List<HistoryItem> Query(string? search = null, string? type = null, bool starredOnly = false, int limit = 500, bool urlOnly = false, int offset = 0)
    {
        lock (_lock)
        {
            var sql = "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon, remark, html, file_paths FROM entries";
            var conds = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                // 字面命中:通配符已转义,用户输入的下划线/百分号按字面处理。
                // device_name 一并纳入,使"按来源设备找"成为可能。
                // file_paths 存的是 JSON,反斜杠被转义成双写;先 REPLACE 归一化,
                // 用户按资源管理器里复制的单反斜杠路径才能命中。
                //
                // 拼音命中:直接匹配预计算好的 pinyin_blob / initials_blob,不再走托管回调。
                // 只做连续子串匹配——旧实现的"顺序子序列"兜底会让 sf/ab 这类两字母查询
                // 命中全库三分之一,是搜索结果杂乱的主因。
                conds.Add(@"(
                    text LIKE @search ESCAPE '\'
                    OR remark LIKE @search ESCAPE '\'
                    OR source_app_name LIKE @search ESCAPE '\'
                    OR device_name LIKE @search ESCAPE '\'
                    OR REPLACE(file_paths, char(92) || char(92), char(92)) LIKE @search ESCAPE '\'
                    OR pinyin_blob LIKE @phonetic ESCAPE '\'
                    OR initials_blob LIKE @phonetic ESCAPE '\')");
            }
            if (!string.IsNullOrWhiteSpace(type)) conds.Add("type = @type");
            if (starredOnly) conds.Add("starred = 1");
            if (urlOnly) conds.Add("(type = 'Text' AND (text LIKE 'http://%' OR text LIKE 'https://%'))");
            if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
            sql += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@search", $"%{EscapeLike(search ?? "")}%");
            cmd.Parameters.AddWithValue("@phonetic", $"%{EscapeLike(search?.Trim().ToLowerInvariant() ?? "")}%");
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
                    (server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon, remark, html, file_paths, pinyin_blob, initials_blob)
                VALUES
                    (@server_id, @type, @text, @image_path, @image_ref, @device_id, @device_name, @created_at, @origin, @starred, @content_hash, @source_app_name, @source_app_path, @source_app_icon, @remark, @html, @file_paths, @pinyin_blob, @initials_blob);
                SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() ELSE 0 END;
                """;
            cmd.Parameters.AddWithValue("@server_id", (object?)item.ServerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", item.Type);
            cmd.Parameters.AddWithValue("@text", (object?)item.Text ?? DBNull.Value);
            // 文件条目必须保持 image_path 为 NULL:该列的语义是"本应用生成的图片缓存",
            // 所有清理路径都会删除它指向的文件,写入用户真实文件路径会导致文件被误删。
            cmd.Parameters.AddWithValue("@image_path", item.IsFile ? DBNull.Value : (object?)item.ImagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_ref", (object?)item.ImageRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@device_id", item.DeviceId);
            cmd.Parameters.AddWithValue("@device_name", (object?)item.DeviceName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", item.CreatedAt.ToUniversalTime().Ticks);
            cmd.Parameters.AddWithValue("@origin", item.Origin);
            cmd.Parameters.AddWithValue("@starred", item.Starred ? 1 : 0);
            cmd.Parameters.AddWithValue("@content_hash", (object?)ResolveContentHash(item) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_name", (object?)item.SourceAppName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_path", (object?)item.SourceAppPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source_app_icon", (object?)item.SourceAppIcon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remark", (object?)item.Remark ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@html", (object?)item.Html ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@file_paths", item.IsFile ? (object?)item.FilePathsJson ?? DBNull.Value : DBNull.Value);

            // 拼音检索串在写入时预计算一次,查询阶段只做 LIKE 匹配。
            // 注意写入空串而非 NULL:NULL 被用作"尚未回填"的标记,供 BackfillSearchBlobs 识别存量行。
            var (pinyinBlob, initialsBlob) = BuildSearchBlobs(
                BuildPinyinSource(item.Text, item.Remark, item.SourceAppName, item.DeviceName, item.Type, item.FilePathsJson));
            cmd.Parameters.AddWithValue("@pinyin_blob", pinyinBlob);
            cmd.Parameters.AddWithValue("@initials_blob", initialsBlob);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            if (id > 0) TrimToLimitLocked();
            return id;
        }
    }

    /// <summary>调用方未显式提供时计算内容哈希(文本/图片字节/文件路径元数据)。图片文件缺失则留空。</summary>
    private static string? ResolveContentHash(HistoryItem item)
    {
        if (!string.IsNullOrEmpty(item.ContentHash)) return item.ContentHash;
        try
        {
            if (item.Type == "Text" && !string.IsNullOrEmpty(item.Text))
            {
                return ClipboardMonitor.HashText(item.Text, item.Html);
            }
            if (item.Type == "Image" && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                return ClipboardMonitor.HashBytes(File.ReadAllBytes(item.ImagePath));
            }
            if (item.Type == "File" && !string.IsNullOrEmpty(item.FilePathsJson))
            {
                // 仅对路径元数据求哈希,不读取文件内容(文件可达数百 MB)
                return ClipboardFileMeta.ComputeHash(ClipboardFileMeta.Parse(item.FilePathsJson));
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
            // 来源应用/设备名参与拼音检索,置顶时可能被刷新,需同步重算检索串
            RefreshSearchBlobsLocked(id);
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
            cmd.CommandText = "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon, remark, html, file_paths FROM entries WHERE content_hash = @hash ORDER BY created_at DESC LIMIT 1";
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
            cmd.CommandText = "SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET @limit";
            cmd.Parameters.AddWithValue("@limit", max);
            var doomed = new List<long>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) doomed.Add(r.GetInt64(0));
            }
            if (doomed.Count == 0) return 0;
            foreach (var id in doomed) DeleteImageForLocked(id);
            cmd.CommandText = "DELETE FROM entries WHERE starred = 0 AND id IN (SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET @limit)";
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
            cmd.CommandText = "SELECT id, image_path FROM entries WHERE starred = 0 AND created_at < @cutoff";
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
            cmd.CommandText = "DELETE FROM entries WHERE starred = 0 AND created_at < @cutoff";
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
            // 文本是拼音检索的主要来源,改完必须重算检索串,否则新内容用拼音搜不到
            RefreshSearchBlobsLocked(id);
        }
    }

    /// <summary>更新条目自定义备注。若 remark 为空或纯空白则存为 NULL。</summary>
    public void UpdateRemark(long id, string? remark)
    {
        lock (_lock)
        {
            var trimmed = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE entries SET remark = @remark WHERE id = @id";
            cmd.Parameters.AddWithValue("@remark", (object?)trimmed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
            // 备注同样参与拼音检索,改完一并重算
            RefreshSearchBlobsLocked(id);
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

    public void Clear(bool keepStarred = true)
    {
        lock (_lock)
        {
            // 先收集图片文件再删记录(清空历史含图片缓存,不可恢复,且始终保留收藏项)
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

    /// <summary>超限清理:只淘汰最旧的未收藏条目并清理其图片文件。</summary>
    private void TrimToLimitLocked()
    {
        var limit = _maxEntries;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        var doomed = new List<long>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) doomed.Add(reader.GetInt64(0));
        }
        if (doomed.Count == 0) return;
        foreach (var id in doomed)
        {
            DeleteImageForLocked(id);
        }
        cmd.CommandText = "DELETE FROM entries WHERE starred = 0 AND id IN (SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET @limit)";
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
        Remark = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null,
        Html = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : null,
        FilePathsJson = reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetString(17) : null,
    };

    public void Dispose() => _conn.Dispose();
}
