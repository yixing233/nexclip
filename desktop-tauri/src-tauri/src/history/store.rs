//! 历史存储实现。
//!
//! 移植自 C# `Services/HistoryStore.cs`(1020 行)。所有方法线程安全
//! (Mutex 串行);单条数据量小,同步执行。

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use rusqlite::{params, Connection, OptionalExtension};

use super::pinyin;
use crate::clipboard::files_meta as file_meta;
use crate::clipboard::hash;

/// 条目类型常量。
pub const TYPE_TEXT: &str = "Text";
pub const TYPE_IMAGE: &str = "Image";
pub const TYPE_FILE: &str = "File";

/// 来源:0=本地捕获 1=远端推送 2=本端主动同步。
pub const ORIGIN_LOCAL: i32 = 0;
pub const ORIGIN_REMOTE: i32 = 1;
pub const ORIGIN_MANUAL: i32 = 2;

/// .NET DateTime.Ticks 纪元(0001-01-01)与 Unix 纪元的差值,单位 100ns。
const DOTNET_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

/// 拼音检索串中"主读音"与"备选读音"的分段符(控制字符,不会出现在用户输入里)。
const VARIANT_SEPARATOR: char = '\u{0001}';

/// 参与拼音检索串计算的最大源文本长度(字符)。
/// 超出部分不进入拼音索引,但字面检索(LIKE text)仍然覆盖全文。
const MAX_PINYIN_SOURCE_CHARS: usize = 2000;

/// 本地历史条目。
#[derive(Debug, Clone, PartialEq, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoryItem {
    pub id: i64,
    pub server_id: Option<i64>,
    /// Text | Image | File
    pub entry_type: String,
    pub text: Option<String>,
    /// 富文本 HTML 片段(仅 Text 条目;纯文本条目为 null)
    pub html: Option<String>,
    /// 本地缓存文件
    pub image_path: Option<String>,
    /// 远端引用
    pub image_ref: Option<String>,
    /// 文件条目的路径元数据 JSON。仅本地保存,永不上传;
    /// 条目 image_path 必须保持为 NULL,否则超限清理会把用户真实文件当作图片缓存删除。
    pub file_paths_json: Option<String>,
    /// 内容哈希(文本/图片字节,文件条目取路径元数据哈希)
    pub content_hash: Option<String>,
    pub device_id: String,
    pub device_name: Option<String>,
    pub source_app_name: Option<String>,
    pub source_app_path: Option<String>,
    pub source_app_icon: Option<String>,
    /// UTC,内部存储为 .NET DateTime.Ticks(与旧库兼容)
    pub created_at_ticks: i64,
    pub origin: i32,
    pub starred: bool,
    pub remark: Option<String>,
}

impl HistoryItem {
    pub fn is_file(&self) -> bool {
        self.entry_type == TYPE_FILE
    }

    /// IsManual:Origin==2 或来源应用为互传/手动推送。
    pub fn is_manual(&self) -> bool {
        self.origin == ORIGIN_MANUAL
            || self.source_app_name.as_deref() == Some("即时互传")
            || self.source_app_name.as_deref() == Some("手动推送")
    }

    /// 文件条目的文件列表;非文件条目为空列表。
    pub fn files(&self) -> Vec<file_meta::ClipboardFileInfo> {
        if self.is_file() {
            file_meta::parse(self.file_paths_json.as_deref())
        } else {
            Vec::new()
        }
    }

    /// 前端友好的 UTC 毫秒时间戳。
    pub fn created_at_millis(&self) -> i64 {
        ticks_to_millis(self.created_at_ticks)
    }
}

/// .NET Ticks(100ns,0001-01-01 纪元)→ Unix 毫秒。
pub fn ticks_to_millis(ticks: i64) -> i64 {
    (ticks - DOTNET_EPOCH_TICKS) / 10_000
}

/// Unix 毫秒 → .NET Ticks。
pub fn millis_to_ticks(millis: i64) -> i64 {
    millis * 10_000 + DOTNET_EPOCH_TICKS
}

/// 当前时刻的 .NET Ticks(UTC)。Phase 2 捕获管线(Insert/TouchByHash 调用方)使用。
#[allow(dead_code)]
pub fn now_ticks() -> i64 {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("系统时间早于 1970");
    millis_to_ticks(now.as_millis() as i64)
}

struct Inner {
    conn: Connection,
    max_entries: i64,
    db_path: PathBuf,
}

pub struct HistoryStore {
    inner: Mutex<Inner>,
}

impl HistoryStore {
    pub fn open(storage_dir: &Path) -> Result<Self, rusqlite::Error> {
        map_io(storage_dir_creation(storage_dir))?;
        let db_path = storage_dir.join("history.db");
        let conn = open_connection(&db_path)?;
        let store = HistoryStore {
            inner: Mutex::new(Inner {
                conn,
                max_entries: 200,
                db_path,
            }),
        };
        store.with_inner(|inner| ensure_schema(&inner.conn))?;
        Ok(store)
    }

    /// 切换数据储存目录(设置页"修改"储存位置):关闭旧库,在新目录重建连接。
    pub fn reopen(&self, storage_dir: &Path) -> Result<(), rusqlite::Error> {
        map_io(storage_dir_creation(storage_dir))?;
        let db_path = storage_dir.join("history.db");
        let conn = open_connection(&db_path)?;
        self.with_inner(|inner| {
            ensure_schema(&conn)?;
            inner.db_path = db_path;
            inner.conn = conn;
            Ok(())
        })
    }

    /// 数据库文件路径(数据管理页展示)。
    pub fn db_path(&self) -> PathBuf {
        self.with_inner(|inner| Ok(inner.db_path.clone())).unwrap_or_default()
    }

    /// 条目上限(仅限制未收藏条目;收藏项不参与自动淘汰)。
    pub fn max_entries(&self) -> i64 {
        self.with_inner(|inner| Ok(inner.max_entries)).unwrap_or(200)
    }

    pub fn set_max_entries(&self, value: i64) {
        if value <= 0 {
            return;
        }
        let _ = self.with_inner(|inner| {
            inner.max_entries = value;
            trim_to_limit(&inner.conn, value)?;
            Ok(())
        });
    }

    pub fn count(&self) -> i64 {
        self.with_inner(|inner| {
            inner.conn.query_row("SELECT COUNT(*) FROM entries", [], |r| r.get(0))
        })
        .unwrap_or(0)
    }

    pub fn count_starred(&self) -> i64 {
        self.with_inner(|inner| {
            inner
                .conn
                .query_row("SELECT COUNT(1) FROM entries WHERE starred = 1", [], |r| r.get(0))
        })
        .unwrap_or(0)
    }

    /// 把 WAL 中的内容强制并回主库文件。
    ///
    /// 用途:WAL 模式下最近写入的页可能还只在 -wal 文件里,主库文件本身并不完整。
    /// 因此任何"只复制 history.db"的操作都必须先调用本方法。
    pub fn checkpoint(&self) {
        let _ = self.with_inner(|inner| {
            inner
                .conn
                .execute_batch("PRAGMA wal_checkpoint(TRUNCATE);")
                .or_else(|e| {
                    log::warn!("WAL checkpoint 失败: {e}");
                    Err(e)
                })
        });
    }

    /// 迁移后更新条目图片路径(旧目录 → 新目录)。
    pub fn update_image_paths(&self, old_dir: &str, new_dir: &str) {
        let _ = self.with_inner(|inner| {
            inner.conn.execute(
                "UPDATE entries
                 SET image_path = REPLACE(image_path, ?1, ?2)
                 WHERE image_path LIKE ?1 || '%'",
                params![old_dir, new_dir],
            )?;
            Ok(())
        });
    }

    /// 查询历史(新→旧)。search 匹配文本、备注、来源应用、来源设备、文件路径,
    /// 以及拼音全拼/首字母;type 过滤;starred_only 只看收藏;url_only 只看链接。
    #[allow(clippy::too_many_arguments)]
    pub fn query(
        &self,
        search: Option<&str>,
        entry_type: Option<&str>,
        starred_only: bool,
        url_only: bool,
        limit: i64,
        offset: i64,
    ) -> Result<Vec<HistoryItem>, rusqlite::Error> {
        self.with_inner(|inner| {
            let conn = &inner.conn;
            let search = search.filter(|s| !s.trim().is_empty());

            let mut sql = String::from(
                "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, \
                 created_at, origin, starred, content_hash, source_app_name, source_app_path, \
                 source_app_icon, remark, html, file_paths FROM entries",
            );
            let mut conds: Vec<String> = Vec::new();
            // 绑定参数统一收集后按索引绑定,占位符索引动态分配,避免错位
            let mut bind_values: Vec<rusqlite::types::Value> = Vec::new();
            let mut next_idx = 1usize;

            if let Some(s) = search {
                // 字面命中:通配符已转义。file_paths 存的是 JSON,反斜杠被转义成双写;
                // 先 REPLACE 归一化,用户按资源管理器里复制的单反斜杠路径才能命中。
                //
                // 拼音命中:直接匹配预计算好的 pinyin_blob / initials_blob,只做连续子串匹配。
                //
                // 优化:拼音串只含 a-z0-9,任何含非 ASCII 字符的查询都不可能命中它,
                // 整列跳过以省掉两列的全表扫描。
                let mut parts = vec![
                    "text LIKE ?1 ESCAPE '\\'".to_string(),
                    "remark LIKE ?1 ESCAPE '\\'".to_string(),
                    "source_app_name LIKE ?1 ESCAPE '\\'".to_string(),
                    "device_name LIKE ?1 ESCAPE '\\'".to_string(),
                    "REPLACE(file_paths, char(92) || char(92), char(92)) LIKE ?1 ESCAPE '\\'".to_string(),
                ];
                bind_values.push(rusqlite::types::Value::Text(format!(
                    "%{}%",
                    escape_like(s)
                )));
                next_idx += 1;
                if is_ascii_only(s) {
                    parts.push(format!("pinyin_blob LIKE ?{next_idx} ESCAPE '\\'"));
                    parts.push(format!("initials_blob LIKE ?{next_idx} ESCAPE '\\'"));
                    bind_values.push(rusqlite::types::Value::Text(format!(
                        "%{}%",
                        escape_like(s.trim().to_lowercase().as_str())
                    )));
                    next_idx += 1;
                }
                conds.push(format!("({})", parts.join(" OR ")));
            }
            if let Some(t) = entry_type.filter(|t| !t.trim().is_empty()) {
                conds.push(format!("type = ?{next_idx}"));
                bind_values.push(rusqlite::types::Value::Text(t.to_string()));
                next_idx += 1;
            }
            if starred_only {
                conds.push("starred = 1".to_string());
            }
            if url_only {
                conds.push("(type = 'Text' AND (text LIKE 'http://%' OR text LIKE 'https://%'))".to_string());
            }
            if !conds.is_empty() {
                sql += " WHERE ";
                sql += &conds.join(" AND ");
            }
            sql += &format!(" ORDER BY created_at DESC LIMIT ?{next_idx} OFFSET ?{}", next_idx + 1);
            bind_values.push(rusqlite::types::Value::Integer(limit));
            bind_values.push(rusqlite::types::Value::Integer(offset));

            let mut stmt = conn.prepare(&sql)?;
            for (i, v) in bind_values.iter().enumerate() {
                stmt.raw_bind_parameter(i + 1, v)?;
            }
            let mut rows = stmt.raw_query();
            read_items(&mut rows)
        })
    }

    /// 插入条目(server_id 去重:已存在则跳过)。返回新条目 id;跳过/重复返回 0。
    pub fn insert(&self, item: &HistoryItem) -> Result<i64, rusqlite::Error> {
        self.with_inner(|inner| {
            let inner = &mut *inner;
            let Inner { conn, max_entries, .. } = inner;
            let tx = conn.transaction()?;

            // 文件条目必须保持 image_path 为 NULL:该列的语义是"本应用生成的图片缓存",
            // 所有清理路径都会删除它指向的文件,写入用户真实文件路径会导致文件被误删。
            let image_path = if item.is_file() { None } else { item.image_path.clone() };
            let file_paths = if item.is_file() { item.file_paths_json.clone() } else { None };

            // 拼音检索串在写入时预计算一次,查询阶段只做 LIKE 匹配。
            // 写入空串而非 NULL:NULL 被用作"尚未回填"的标记,供回填逻辑识别存量行。
            let (pinyin_blob, initials_blob) = build_search_blobs(&build_pinyin_source(item));

            let content_hash = resolve_content_hash(item);
            tx.execute(
                "INSERT OR IGNORE INTO entries
                    (server_id, type, text, image_path, image_ref, device_id, device_name, created_at, origin, starred, content_hash, source_app_name, source_app_path, source_app_icon, remark, html, file_paths, pinyin_blob, initials_blob)
                 VALUES
                    (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19)",
                params![
                    item.server_id,
                    item.entry_type,
                    item.text,
                    image_path,
                    item.image_ref,
                    item.device_id,
                    item.device_name,
                    item.created_at_ticks,
                    item.origin,
                    item.starred as i64,
                    content_hash,
                    item.source_app_name,
                    item.source_app_path,
                    item.source_app_icon,
                    item.remark,
                    item.html,
                    file_paths,
                    pinyin_blob,
                    initials_blob,
                ],
            )?;
            let id = if tx.changes() > 0 { tx.last_insert_rowid() } else { 0 };
            if id > 0 {
                trim_to_limit_tx(&tx, *max_entries)?;
            }
            tx.commit()?;
            Ok(id)
        })
    }

    /// 外部复制与已有内容重复时,不新增条目,直接把该内容最新一条置顶
    /// (created_at 更新为当前),并同步 server_id / 设备信息。返回是否命中已有条目。
    #[allow(clippy::too_many_arguments)]
    pub fn touch_by_hash(
        &self,
        content_hash: &str,
        server_id: Option<i64>,
        device_id: &str,
        device_name: Option<&str>,
        created_at_ticks: i64,
        source_app_name: Option<&str>,
        source_app_path: Option<&str>,
        source_app_icon: Option<&str>,
    ) -> Result<bool, rusqlite::Error> {
        if content_hash.is_empty() {
            return Ok(false);
        }
        self.with_inner(|inner| {
            let conn = &mut inner.conn;
            let tx = conn.transaction()?;

            let row: Option<(i64, Option<String>, Option<String>, Option<String>, Option<String>)> = tx
                .query_row(
                    "SELECT id, device_name, source_app_name, source_app_path, source_app_icon
                     FROM entries WHERE content_hash = ?1 ORDER BY created_at DESC LIMIT 1",
                    params![content_hash],
                    |r| {
                        Ok((
                            r.get(0)?,
                            r.get(1)?,
                            r.get(2)?,
                            r.get(3)?,
                            r.get(4)?,
                        ))
                    },
                )
                .optional()?;
            let (id, old_device, old_app, old_path, old_icon) = match row {
                Some(v) => v,
                None => return Ok(false),
            };

            // 与 UPDATE 中的 COALESCE 语义保持一致:传入 None 表示"保留原值",
            // 因此只有传入非空且与旧值不同时,才算真的改动了检索串来源。
            let blobs_need_refresh = changed(device_name, old_device.as_deref())
                || changed(source_app_name, old_app.as_deref())
                || changed(source_app_path, old_path.as_deref())
                || changed(source_app_icon, old_icon.as_deref());

            tx.execute(
                "UPDATE entries SET
                    created_at = ?1,
                    device_id = ?2,
                    device_name = ?3,
                    source_app_name = COALESCE(?4, source_app_name),
                    source_app_path = COALESCE(?5, source_app_path),
                    source_app_icon = COALESCE(?6, source_app_icon)
                 WHERE id = ?7",
                params![
                    created_at_ticks,
                    device_id,
                    device_name,
                    source_app_name,
                    source_app_path,
                    source_app_icon,
                    id,
                ],
            )?;

            // server_id 单独更新:若与其它行 UNIQUE 冲突(极端情况)则保留原值
            if let Some(sid) = server_id {
                if tx
                    .execute("UPDATE entries SET server_id = ?1 WHERE id = ?2", params![sid, id])
                    .is_err()
                {
                    log::debug!("TouchByHash 更新 server_id 冲突,保留原值");
                }
            }
            // 来源应用/设备名参与拼音检索,但只有它们真的变了才需要重算检索串
            if blobs_need_refresh {
                refresh_search_blobs(&tx, id)?;
            }
            tx.commit()?;
            Ok(true)
        })
    }

    /// 按内容哈希读取最新本地条目,用于离线捕获与重复内容置顶后的 UI 更新。
    pub fn find_by_hash(&self, content_hash: &str) -> Result<Option<HistoryItem>, rusqlite::Error> {
        if content_hash.is_empty() {
            return Ok(None);
        }
        self.with_inner(|inner| {
            let mut stmt = inner.conn.prepare(
                "SELECT id, server_id, type, text, image_path, image_ref, device_id, device_name, \
                 created_at, origin, starred, content_hash, source_app_name, source_app_path, \
                 source_app_icon, remark, html, file_paths FROM entries \
                 WHERE content_hash = ?1 ORDER BY created_at DESC LIMIT 1",
            )?;
            stmt.query_row(params![content_hash], read_item).optional()
        })
    }

    /// 按条目数上限清理(设置变更时立即执行)。返回删除条数。
    pub fn prune_to_limit(&self, max: i64) -> Result<usize, rusqlite::Error> {
        if max <= 0 {
            return Ok(0);
        }
        self.with_inner(|inner| {
            let conn = &inner.conn;
            prune_limit_impl(conn, max)
        })
    }

    /// 按时间上限清理(超过 days 天的条目删除,0=不清理)。返回删除条数。
    pub fn prune_older_than(&self, days: i64) -> Result<usize, rusqlite::Error> {
        if days <= 0 {
            return Ok(0);
        }
        self.with_inner(|inner| {
            let conn = &inner.conn;
            let cutoff = {
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .expect("系统时间早于 1970");
                let millis = now.as_secs_f64() * 1000.0 - (days as f64) * 86_400_000.0;
                millis_to_ticks(millis as i64)
            };
            let doomed: Vec<(i64, Option<String>)> = {
                let mut stmt =
                    conn.prepare("SELECT id, image_path FROM entries WHERE starred = 0 AND created_at < ?1")?;
                let rows = stmt.query_map(params![cutoff], |r| Ok((r.get(0)?, r.get(1)?)))?;
                rows.collect::<Result<Vec<_>, _>>()?
            };
            if doomed.is_empty() {
                return Ok(0);
            }
            for (_, p) in &doomed {
                if let Some(p) = p {
                    let _ = std::fs::remove_file(p);
                }
            }
            Ok(conn.execute("DELETE FROM entries WHERE starred = 0 AND created_at < ?1", params![cutoff])?)
        })
    }

    /// 为缺图标的存量条目回填来源应用图标(extractor 由调用方提供,失败留空)。
    /// 返回回填条数。启动时执行一次即可。
    pub fn backfill_source_icons(
        &self,
        extract: impl Fn(&str) -> Option<String>,
    ) -> Result<usize, rusqlite::Error> {
        self.with_inner(|inner| {
            let rows: Vec<(i64, String)> = {
                let mut stmt = inner.conn.prepare(
                    "SELECT id, source_app_path FROM entries                      WHERE source_app_icon IS NULL AND source_app_path IS NOT NULL",
                )?;
                let mapped = stmt.query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?;
                mapped.collect::<Result<Vec<_>, _>>()?
            };
            let mut count = 0usize;
            for (id, exe_path) in rows {
                if let Some(icon) = extract(&exe_path) {
                    inner.conn.execute(
                        "UPDATE entries SET source_app_icon = ?1 WHERE id = ?2",
                        params![icon, id],
                    )?;
                    count += 1;
                }
            }
            Ok(count)
        })
    }

    pub fn delete(&self, id: i64) -> Result<(), rusqlite::Error> {
        self.with_inner(|inner| {
            inner.conn.execute("DELETE FROM entries WHERE id = ?1", params![id])?;
            Ok(())
        })
    }

    pub fn toggle_star(&self, id: i64, starred: bool) -> Result<(), rusqlite::Error> {
        self.with_inner(|inner| {
            inner.conn.execute(
                "UPDATE entries SET starred = ?1 WHERE id = ?2",
                params![starred as i64, id],
            )?;
            Ok(())
        })
    }

    /// 编辑条目文本(仅更新本地记录)。
    pub fn update_text(&self, id: i64, text: &str) -> Result<(), rusqlite::Error> {
        self.with_inner(|inner| {
            let conn = &inner.conn;
            conn.execute("UPDATE entries SET text = ?1 WHERE id = ?2", params![text, id])?;
            // 文本是拼音检索的主要来源,改完必须重算检索串,否则新内容用拼音搜不到
            refresh_search_blobs(conn, id)?;
            Ok(())
        })
    }

    /// 更新条目自定义备注。若 remark 为空或纯空白则存为 NULL。
    pub fn update_remark(&self, id: i64, remark: Option<&str>) -> Result<(), rusqlite::Error> {
        self.with_inner(|inner| {
            let conn = &inner.conn;
            let trimmed = remark.map(str::trim).filter(|s| !s.is_empty());
            conn.execute(
                "UPDATE entries SET remark = ?1 WHERE id = ?2",
                params![trimmed, id],
            )?;
            // 备注同样参与拼音检索,改完一并重算
            refresh_search_blobs(conn, id)?;
            Ok(())
        })
    }

    /// 清空历史(含图片缓存,不可恢复;始终保留收藏项,除非 keep_starred=false)。
    pub fn clear(&self, keep_starred: bool) -> Result<(), rusqlite::Error> {
        self.with_inner(|inner| {
            let conn = &inner.conn;
            // 先收集图片文件再删记录
            let sql = if keep_starred {
                "SELECT image_path FROM entries WHERE image_path IS NOT NULL AND starred = 0"
            } else {
                "SELECT image_path FROM entries WHERE image_path IS NOT NULL"
            };
            let paths: Vec<String> = {
                let mut stmt = conn.prepare(sql)?;
                let rows = stmt.query_map([], |r| r.get::<_, String>(0))?;
                rows.collect::<Result<Vec<_>, _>>()?
            };
            for p in paths {
                let _ = std::fs::remove_file(p);
            }
            let del = if keep_starred {
                "DELETE FROM entries WHERE starred = 0"
            } else {
                "DELETE FROM entries"
            };
            conn.execute(del, [])?;
            Ok(())
        })
    }

    fn with_inner<T>(&self, f: impl FnOnce(&mut Inner) -> Result<T, rusqlite::Error>) -> Result<T, rusqlite::Error> {
        let mut guard = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        f(&mut guard)
    }
}

/// 建储存目录,io 错误统一映射为 SQLITE_CANTOPEN(目录在只读盘/权限不足等)。
fn storage_dir_creation(dir: &Path) -> Result<(), std::io::Error> {
    std::fs::create_dir_all(dir)
}

fn map_io<T>(r: Result<T, std::io::Error>) -> Result<T, rusqlite::Error> {
    r.map_err(|e| {
        rusqlite::Error::SqliteFailure(
            rusqlite::ffi::Error::new(rusqlite::ffi::SQLITE_CANTOPEN),
            Some(e.to_string()),
        )
    })
}

/// 打开连接并应用性能相关 PRAGMA。
///
/// WAL 是关键:默认 DELETE 日志模式下每条自动提交写语句都要走一轮 journal 文件
/// fsync;实测(真实库副本)单条 UPDATE:DELETE 自动提交 3448us / WAL 自动提交 577us。
/// synchronous=NORMAL 是 WAL 下的推荐配置:仍保证数据库不会损坏,
/// 只是崩溃时可能丢掉最后一个事务(对剪贴板历史完全可以接受)。
fn open_connection(db_path: &Path) -> Result<Connection, rusqlite::Error> {
    let conn = Connection::open(db_path)?;
    if let Err(e) = conn.pragma_update(None, "journal_mode", "WAL") {
        // 储存目录在只读/网络盘等场景可能不支持 WAL:退回默认模式也要能用
        log::warn!("启用 WAL 失败,继续使用默认日志模式: {e}");
    }
    conn.pragma_update(None, "synchronous", "NORMAL")?;
    Ok(conn)
}

/// 建表 + 兼容迁移。
fn ensure_schema(conn: &Connection) -> Result<(), rusqlite::Error> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS entries (
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
        CREATE INDEX IF NOT EXISTS idx_entries_type ON entries(type);",
    )?;
    ensure_column(conn, "content_hash", "TEXT")?;
    conn.execute_batch("CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(content_hash);")?;
    ensure_column(conn, "source_app_name", "TEXT")?;
    ensure_column(conn, "source_app_path", "TEXT")?;
    ensure_column(conn, "source_app_icon", "TEXT")?;
    ensure_column(conn, "remark", "TEXT")?;
    ensure_column(conn, "html", "TEXT")?;
    // 该列与 image_path 语义严格分离:文件条目不得写入 image_path
    ensure_column(conn, "file_paths", "TEXT")?;
    ensure_column(conn, "pinyin_blob", "TEXT")?;
    ensure_column(conn, "initials_blob", "TEXT")?;
    backfill_content_hashes(conn)?;
    backfill_search_blobs(conn)?;
    Ok(())
}

/// 兼容旧库:按需新增列(PRAGMA table_info 逐列检查)。
fn ensure_column(conn: &Connection, name: &str, decl: &str) -> Result<(), rusqlite::Error> {
    let exists: bool = conn
        .prepare("PRAGMA table_info(entries)")?
        .query_map([], |r| r.get::<_, String>(1))?
        .filter_map(Result::ok)
        .any(|col| col == name);
    if !exists {
        conn.execute_batch(&format!("ALTER TABLE entries ADD COLUMN {name} {decl};"))?;
    }
    Ok(())
}

/// 为存量条目回填内容哈希(文本取文本哈希,图片取缓存文件字节哈希,文件取路径元数据哈希)。
fn backfill_content_hashes(conn: &Connection) -> Result<(), rusqlite::Error> {
    let rows: Vec<(i64, String, Option<String>, Option<String>, Option<String>, Option<String>)> = {
        let mut stmt = conn.prepare(
            "SELECT id, type, text, image_path, html, file_paths FROM entries WHERE content_hash IS NULL",
        )?;
        let mapped = stmt.query_map([], |r| {
            Ok((
                r.get(0)?,
                r.get(1)?,
                r.get(2)?,
                r.get(3)?,
                r.get(4)?,
                r.get(5)?,
            ))
        })?;
        mapped.collect::<Result<Vec<_>, _>>()?
    };
    for (id, entry_type, text, image_path, html, file_paths) in rows {
        let hash_value: Option<String> = if entry_type == TYPE_TEXT {
            text.filter(|t| !t.is_empty())
                .map(|t| hash::hash_text(&t, html.as_deref()))
        } else if entry_type == TYPE_IMAGE {
            image_path
                .filter(|p| Path::new(p).is_file())
                .and_then(|p| std::fs::read(p).ok())
                .map(|b| hash::hash_bytes(&b))
        } else if entry_type == TYPE_FILE {
            file_paths
                .filter(|j| !j.is_empty())
                .map(|j| file_meta::compute_hash(&file_meta::parse(Some(&j))))
                .filter(|h| !h.is_empty())
        } else {
            None
        };
        if let Some(h) = hash_value {
            conn.execute(
                "UPDATE entries SET content_hash = ?1 WHERE id = ?2",
                params![h, id],
            )?;
        }
    }
    Ok(())
}

/// 拼出用于拼音检索的源文本。字段与字面检索保持一致,并额外纳入文件名与完整路径。
fn build_pinyin_source(item: &HistoryItem) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for v in [
        item.text.as_deref(),
        item.remark.as_deref(),
        item.source_app_name.as_deref(),
        item.device_name.as_deref(),
    ] {
        if let Some(v) = v {
            if !v.trim().is_empty() {
                parts.push(v);
            }
        }
    }
    let mut source = parts.join(" ");
    if item.entry_type == TYPE_FILE {
        if let Some(json) = item.file_paths_json.as_deref().filter(|j| !j.is_empty()) {
            // 收集为 owned 字符串再拼接,避免借用临时 Vec 的元素
            let mut extra: Vec<String> = Vec::new();
            for f in file_meta::parse(Some(json)) {
                if !f.name.trim().is_empty() {
                    extra.push(f.name);
                }
                if !f.path.trim().is_empty() {
                    extra.push(f.path);
                }
            }
            let joined = extra.join(" ");
            if !source.is_empty() && !joined.is_empty() {
                source.push(' ');
            }
            source.push_str(&joined);
        }
    }
    source
}

/// 由源文本算出「全拼」与「首字母」检索串。
///
/// 多音字的备选读音以分段符追加在同一个字段内:既能让「重庆」同时被
/// zhongqing 与 chongqing 命中,又不破坏两个变体各自内部的音节连续性。
/// 文本不含多音字时只存主读音串,避免存储无谓翻倍。
fn build_search_blobs(source: &str) -> (String, String) {
    if source.is_empty() {
        return (String::new(), String::new());
    }
    // 超长文本只索引前一段:拼音检索是"找哪条记录"的辅助手段,
    // 不截断的话一段 5 万字的复制会生成约 15 万字符的拼音串并长期占库。
    let source: String = if source.chars().count() > MAX_PINYIN_SOURCE_CHARS {
        source.chars().take(MAX_PINYIN_SOURCE_CHARS).collect()
    } else {
        source.to_string()
    };

    let py = pinyin::full_pinyin(&source);
    let py_alt = pinyin::full_pinyin_variant(&source);
    let ini = pinyin::initials(&source);
    let ini_alt = pinyin::initials_variant(&source);

    let pinyin_blob = if !py_alt.is_empty() && py_alt != py {
        format!("{py}{VARIANT_SEPARATOR}{py_alt}")
    } else {
        py
    };
    let initials_blob = if !ini_alt.is_empty() && ini_alt != ini {
        format!("{ini}{VARIANT_SEPARATOR}{ini_alt}")
    } else {
        ini
    };
    (pinyin_blob, initials_blob)
}

/// 转义 LIKE 通配符(配合 SQL 里的 ESCAPE '\')。
/// 用户输入的下划线/百分号必须按字面处理:旧实现把输入直接拼进 '%...%',
/// 导致"搜一个下划线"会命中整库。
fn escape_like(value: &str) -> String {
    value.replace('\\', "\\\\").replace('%', "\\%").replace('_', "\\_")
}

/// 查询串是否为纯 ASCII。拼音检索串只由 a-z0-9 组成,
/// 含中文/全角/emoji 的查询不可能命中它,可以整列跳过以省掉一次全表扫描。
fn is_ascii_only(value: &str) -> bool {
    !value.is_empty() && value.chars().all(|c| (c as u32) < 128)
}

/// 为存量条目回填拼音/首字母检索串。
/// 以 IS NULL 作为"尚未计算"的标记:新写入的条目即使无可检索内容也会写入空串。
fn backfill_search_blobs(conn: &Connection) -> Result<(), rusqlite::Error> {
    let rows: Vec<(i64, Option<String>, Option<String>, Option<String>, Option<String>, Option<String>, Option<String>)> = {
        let mut stmt = conn.prepare(
            "SELECT id, text, remark, source_app_name, device_name, type, file_paths \
             FROM entries WHERE pinyin_blob IS NULL",
        )?;
        let mapped = stmt.query_map([], |r| {
            Ok((
                r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?, r.get(6)?,
            ))
        })?;
        mapped.collect::<Result<Vec<_>, _>>()?
    };
    if rows.is_empty() {
        return Ok(());
    }
    let row_count = rows.len();
    for (id, text, remark, app, device, entry_type, file_paths) in rows {
        let source = build_pinyin_source(&HistoryItem {
            id,
            server_id: None,
            entry_type: entry_type.unwrap_or_default(),
            text,
            html: None,
            image_path: None,
            image_ref: None,
            file_paths_json: file_paths,
            content_hash: None,
            device_id: String::new(),
            device_name: device,
            source_app_name: app,
            source_app_path: None,
            source_app_icon: None,
            created_at_ticks: 0,
            origin: 0,
            starred: false,
            remark,
        });
        let (py, ini) = build_search_blobs(&source);
        conn.execute(
            "UPDATE entries SET pinyin_blob = ?1, initials_blob = ?2 WHERE id = ?3",
            params![py, ini, id],
        )?;
    }
    log::info!("已为 {} 条存量历史回填拼音检索串", row_count);
    Ok(())
}

/// 文本或备注变更后重算该条目的拼音检索串。传入事务时复用调用方事务。
fn refresh_search_blobs(conn: &Connection, id: i64) -> Result<(), rusqlite::Error> {
    let row: Option<(Option<String>, Option<String>, Option<String>, Option<String>, Option<String>, Option<String>)> = conn
        .query_row(
            "SELECT text, remark, source_app_name, device_name, type, file_paths FROM entries WHERE id = ?1",
            params![id],
            |r| {
                Ok((
                    r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?, r.get(5)?,
                ))
            },
        )
        .optional()?;
    let (text, remark, app, device, entry_type, file_paths) = match row {
        Some(v) => v,
        None => return Ok(()),
    };
    let source = build_pinyin_source(&HistoryItem {
        id,
        server_id: None,
        entry_type: entry_type.unwrap_or_default(),
        text,
        html: None,
        image_path: None,
        image_ref: None,
        file_paths_json: file_paths,
        content_hash: None,
        device_id: String::new(),
        device_name: device,
        source_app_name: app,
        source_app_path: None,
        source_app_icon: None,
        created_at_ticks: 0,
        origin: 0,
        starred: false,
        remark,
    });
    let (py, ini) = build_search_blobs(&source);
    conn.execute(
        "UPDATE entries SET pinyin_blob = ?1, initials_blob = ?2 WHERE id = ?3",
        params![py, ini, id],
    )?;
    Ok(())
}

/// 调用方未显式提供时计算内容哈希(文本/图片字节/文件路径元数据)。图片文件缺失则留空。
fn resolve_content_hash(item: &HistoryItem) -> Option<String> {
    if let Some(h) = item.content_hash.as_deref().filter(|h| !h.is_empty()) {
        return Some(h.to_string());
    }
    match item.entry_type.as_str() {
        TYPE_TEXT => item
            .text
            .as_deref()
            .filter(|t| !t.is_empty())
            .map(|t| hash::hash_text(t, item.html.as_deref())),
        TYPE_IMAGE => item
            .image_path
            .as_deref()
            .filter(|p| Path::new(p).is_file())
            .and_then(|p| std::fs::read(p).ok())
            .map(|b| hash::hash_bytes(&b)),
        TYPE_FILE => item
            .file_paths_json
            .as_deref()
            .filter(|j| !j.is_empty())
            .map(|j| file_meta::compute_hash(&file_meta::parse(Some(j))))
            .filter(|h| !h.is_empty()),
        _ => None,
    }
}

/// 与 COALESCE 语义一致:传入非空且与旧值不同才算真的改动。
fn changed(new: Option<&str>, old: Option<&str>) -> bool {
    match new {
        Some(n) if !n.is_empty() => old != Some(n),
        _ => false,
    }
}

/// 超限清理:只淘汰最旧的未收藏条目并清理其图片文件。独立连接版本。
fn trim_to_limit(conn: &Connection, limit: i64) -> Result<(), rusqlite::Error> {
    let doomed: Vec<i64> = {
        let mut stmt = conn.prepare(
            "SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1",
        )?;
        let rows = stmt.query_map(params![limit], |r| r.get(0))?;
        rows.collect::<Result<Vec<_>, _>>()?
    };
    if doomed.is_empty() {
        return Ok(());
    }
    for id in &doomed {
        delete_image_for(conn, *id)?;
    }
    conn.execute(
        "DELETE FROM entries WHERE starred = 0 AND id IN \
         (SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1)",
        params![limit],
    )?;
    Ok(())
}

/// 事务内版本:使"插入 + 裁剪"对外原子。
fn trim_to_limit_tx(tx: &rusqlite::Transaction, limit: i64) -> Result<(), rusqlite::Error> {
    let doomed: Vec<i64> = {
        let mut stmt = tx.prepare(
            "SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1",
        )?;
        let rows = stmt.query_map(params![limit], |r| r.get(0))?;
        rows.collect::<Result<Vec<_>, _>>()?
    };
    if doomed.is_empty() {
        return Ok(());
    }
    for id in &doomed {
        let path: Option<String> = tx
            .query_row(
                "SELECT image_path FROM entries WHERE id = ?1",
                params![id],
                |r| r.get(0),
            )
            .optional()?
            .flatten();
        if let Some(p) = path {
            let _ = std::fs::remove_file(p);
        }
    }
    tx.execute(
        "DELETE FROM entries WHERE starred = 0 AND id IN \
         (SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1)",
        params![limit],
    )?;
    Ok(())
}

fn prune_limit_impl(conn: &Connection, max: i64) -> Result<usize, rusqlite::Error> {
    let doomed: Vec<i64> = {
        let mut stmt = conn.prepare(
            "SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1",
        )?;
        let rows = stmt.query_map(params![max], |r| r.get(0))?;
        rows.collect::<Result<Vec<_>, _>>()?
    };
    if doomed.is_empty() {
        return Ok(0);
    }
    for id in &doomed {
        delete_image_for(conn, *id)?;
    }
    Ok(conn.execute(
        "DELETE FROM entries WHERE starred = 0 AND id IN \
         (SELECT id FROM entries WHERE starred = 0 ORDER BY created_at DESC LIMIT -1 OFFSET ?1)",
        params![max],
    )?)
}

fn delete_image_for(conn: &Connection, id: i64) -> Result<(), rusqlite::Error> {
    let path: Option<String> = conn
        .query_row(
            "SELECT image_path FROM entries WHERE id = ?1",
            params![id],
            |r| r.get(0),
        )
        .optional()?
        .flatten();
    if let Some(p) = path {
        let _ = std::fs::remove_file(p);
    }
    Ok(())
}

fn read_items(rows: &mut rusqlite::Rows<'_>) -> Result<Vec<HistoryItem>, rusqlite::Error> {
    let mut out = Vec::new();
    while let Some(item) = rows.next()? {
        out.push(read_item(&item)?);
    }
    Ok(out)
}

fn read_item(r: &rusqlite::Row<'_>) -> Result<HistoryItem, rusqlite::Error> {
    Ok(HistoryItem {
        id: r.get(0)?,
        server_id: r.get(1)?,
        entry_type: r.get(2)?,
        text: r.get(3)?,
        image_path: r.get(4)?,
        image_ref: r.get(5)?,
        device_id: r.get(6)?,
        device_name: r.get(7)?,
        created_at_ticks: r.get(8)?,
        origin: r.get(9)?,
        starred: r.get::<_, i64>(10)? != 0,
        content_hash: r.get(11)?,
        source_app_name: r.get(12)?,
        source_app_path: r.get(13)?,
        source_app_icon: r.get(14)?,
        remark: r.get(15)?,
        html: r.get(16)?,
        file_paths_json: r.get(17)?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "nexclip-test-{tag}-{}-{}",
            std::process::id(),
            uuid::Uuid::new_v4().simple()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn item(type_: &str, text: Option<&str>, device: &str) -> HistoryItem {
        HistoryItem {
            id: 0,
            server_id: None,
            entry_type: type_.to_string(),
            text: text.map(String::from),
            html: None,
            image_path: None,
            image_ref: None,
            file_paths_json: None,
            content_hash: None,
            device_id: "dev-1".into(),
            device_name: Some(device.into()),
            source_app_name: None,
            source_app_path: None,
            source_app_icon: None,
            created_at_ticks: now_ticks(),
            origin: ORIGIN_LOCAL,
            starred: false,
            remark: None,
        }
    }

    #[test]
    fn insert_and_query_roundtrip() {
        let dir = temp_dir("roundtrip");
        let store = HistoryStore::open(&dir).unwrap();
        assert_eq!(store.count(), 0);

        let mut a = item(TYPE_TEXT, Some("hello world"), "PC-A");
        a.source_app_name = Some("Visual Studio Code".into());
        let id = store.insert(&a).unwrap();
        assert!(id > 0);

        // server_id 去重:相同 server_id 跳过
        let mut b = item(TYPE_TEXT, Some("other"), "PC-B");
        b.server_id = Some(42);
        assert!(store.insert(&b).unwrap() > 0);
        let mut c = item(TYPE_TEXT, Some("dup"), "PC-B");
        c.server_id = Some(42);
        assert_eq!(store.insert(&c).unwrap(), 0);

        let rows = store.query(None, None, false, false, 500, 0).unwrap();
        assert_eq!(rows.len(), 2);
        // 按内容断言(同毫秒插入时 created_at 相同,排序不保证先后)
        let a_row = rows.iter().find(|r| r.id == id).unwrap();
        assert_eq!(a_row.entry_type, TYPE_TEXT);
        assert_eq!(a_row.source_app_name.as_deref(), Some("Visual Studio Code"));

        // created_at 毫秒换算合理(非负且接近当前)
        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;
        assert!((rows[0].created_at_millis() - now_ms).abs() < 5_000);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn touch_by_hash_dedupes() {
        let dir = temp_dir("touch");
        let store = HistoryStore::open(&dir).unwrap();
        let mut a = item(TYPE_TEXT, Some("same text"), "PC-A");
        a.content_hash = Some(hash::hash_text("same text", None));
        let id = store.insert(&a).unwrap();

        let ticks_before = a.created_at_ticks;
        std::thread::sleep(std::time::Duration::from_millis(20));

        let touched = store
            .touch_by_hash(&a.content_hash.unwrap(), Some(7), "PC-B", Some("手机"), now_ticks(), None, None, None)
            .unwrap();
        assert!(touched);

        let rows = store.query(None, None, false, false, 500, 0).unwrap();
        assert_eq!(rows.len(), 1, "重复内容不新增条目");
        assert_eq!(rows[0].device_name.as_deref(), Some("手机"));
        assert!(rows[0].created_at_ticks > ticks_before, "置顶更新时间");

        // server_id 已同步
        let found = store.find_by_hash(&hash::hash_text("same text", None)).unwrap().unwrap();
        assert_eq!(found.id, id);
        assert_eq!(found.server_id, Some(7));
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn search_literal_and_pinyin() {
        let dir = temp_dir("search");
        let store = HistoryStore::open(&dir).unwrap();
        let mut a = item(TYPE_TEXT, Some("我的文件 v2"), "PC-A");
        a.remark = Some("项目笔记".into());
        store.insert(&a).unwrap();
        let mut b = item(TYPE_TEXT, Some("git push origin"), "PC-B");
        b.device_name = Some("重庆的笔记本".into());
        store.insert(&b).unwrap();

        // 字面
        assert_eq!(store.query(Some("v2"), None, false, false, 500, 0).unwrap().len(), 1);
        assert_eq!(store.query(Some("项目"), None, false, false, 500, 0).unwrap().len(), 1);
        // 拼音全拼
        assert_eq!(store.query(Some("wenjian"), None, false, false, 500, 0).unwrap().len(), 1);
        // 拼音首字母
        assert_eq!(store.query(Some("wdwj"), None, false, false, 500, 0).unwrap().len(), 1);
        // 备选读音:重庆的 chongqing 也能命中
        assert_eq!(store.query(Some("chongqing"), None, false, false, 500, 0).unwrap().len(), 1);
        // LIKE 通配符按字面处理
        assert_eq!(store.query(Some("_"), None, false, false, 500, 0).unwrap().len(), 0);
        assert_eq!(store.query(Some("%"), None, false, false, 500, 0).unwrap().len(), 0);
        // 非命中
        assert_eq!(store.query(Some("不存在的检索"), None, false, false, 500, 0).unwrap().len(), 0);

        // 类型过滤与收藏过滤
        store.insert(&item(TYPE_IMAGE, None, "PC-A")).unwrap();
        assert_eq!(store.query(Some(""), Some(TYPE_IMAGE), false, false, 500, 0).unwrap().len(), 1);
        store.toggle_star(1, true).unwrap();
        assert_eq!(store.query(None, None, true, false, 500, 0).unwrap().len(), 1);
        assert_eq!(store.count_starred(), 1);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn update_text_refreshes_pinyin_blob() {
        let dir = temp_dir("update-text");
        let store = HistoryStore::open(&dir).unwrap();
        store.insert(&item(TYPE_TEXT, Some("hello"), "PC-A")).unwrap();
        assert_eq!(store.query(Some("wendang"), None, false, false, 500, 0).unwrap().len(), 0);
        store.update_text(1, "我的文档").unwrap();
        // "我的文档" 更新后按拼音可搜到
        assert_eq!(store.query(Some("wendang"), None, false, false, 500, 0).unwrap().len(), 1);
        store.update_remark(1, Some("重要资料")).unwrap();
        assert_eq!(store.query(Some("zhongyao"), None, false, false, 500, 0).unwrap().len(), 1);
        // 空白备注落为 NULL
        store.update_remark(1, Some("   ")).unwrap();
        assert_eq!(store.find_by_hash("").unwrap(), None);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn prune_keeps_starred() {
        let dir = temp_dir("prune");
        let store = HistoryStore::open(&dir).unwrap();
        store.set_max_entries(3);
        // 显式递增时间戳:真实场景插入间隔大于 1ms,但同毫秒批量插入会让
        // created_at 并列,淘汰顺序在并列时不确定
        for i in 0..5 {
            let mut it = item(TYPE_TEXT, Some(&format!("entry-{i}")), "PC");
            it.created_at_ticks = now_ticks() + i * 10_000; // 每条错开 1ms
            store.insert(&it).unwrap();
        }
        assert_eq!(store.count(), 3, "超限淘汰最旧");

        // 收藏项不参与淘汰
        store.toggle_star(4, true).unwrap();
        store.set_max_entries(1);
        assert_eq!(store.count(), 2, "收藏项保留");

        store.clear(true).unwrap();
        assert_eq!(store.count(), 1);
        store.clear(false).unwrap();
        assert_eq!(store.count(), 0);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn reopens_existing_database() {
        let dir = temp_dir("reopen");
        {
            let store = HistoryStore::open(&dir).unwrap();
            store.insert(&item(TYPE_TEXT, Some("persist me"), "PC")).unwrap();
            store.checkpoint();
        }
        let store = HistoryStore::open(&dir).unwrap();
        assert_eq!(store.count(), 1);
        assert_eq!(store.query(Some("persist"), None, false, false, 500, 0).unwrap().len(), 1);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn file_entries_never_touch_image_path() {
        let dir = temp_dir("file-entry");
        let store = HistoryStore::open(&dir).unwrap();
        let files = vec![file_meta::ClipboardFileInfo {
            path: r"C:\Users\test\report.pdf".into(),
            name: "report.pdf".into(),
            is_directory: false,
            size_bytes: 1024,
        }];
        let mut f = item(TYPE_FILE, None, "PC");
        f.file_paths_json = Some(file_meta::serialize(&files));
        store.insert(&f).unwrap();
        let rows = store.query(None, Some(TYPE_FILE), false, false, 500, 0).unwrap();
        assert_eq!(rows.len(), 1);
        assert!(rows[0].image_path.is_none(), "文件条目 image_path 必须为 NULL");
        assert_eq!(rows[0].files().len(), 1);
        // 按文件名可搜到
        assert_eq!(store.query(Some("report"), None, false, false, 500, 0).unwrap().len(), 1);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn url_only_filter() {
        let dir = temp_dir("url");
        let store = HistoryStore::open(&dir).unwrap();
        store.insert(&item(TYPE_TEXT, Some("https://github.com"), "PC")).unwrap();
        store.insert(&item(TYPE_TEXT, Some("http://example.com/x"), "PC")).unwrap();
        store.insert(&item(TYPE_TEXT, Some("plain text"), "PC")).unwrap();
        let rows = store.query(None, None, false, true, 500, 0).unwrap();
        assert_eq!(rows.len(), 2);
        std::fs::remove_dir_all(&dir).ok();
    }
}
