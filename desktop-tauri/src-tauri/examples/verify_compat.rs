/// 真实数据兼容性验证:把本机 %LOCALAPPDATA%/NexClip 与 %APPDATA%/NexClip
/// 的现有数据拷贝到临时目录,用新代码打开并校验(绝不直接碰真实库)。
///
/// 运行:cargo run --example verify_compat
use nexclip_lib::history::HistoryStore;
use nexclip_lib::settings;

fn main() {
    let local = std::env::var("LOCALAPPDATA").unwrap();
    let roaming = std::env::var("APPDATA").unwrap();

    // 1. 复制真实数据(含 -wal/-shm,WAL 文件一并复制才能读到最新数据)
    let tmp = std::env::temp_dir().join(format!("nexclip-compat-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&tmp);
    std::fs::create_dir_all(&tmp).unwrap();

    let src_db = std::path::Path::new(&local).join("NexClip");
    let has_real_db = src_db.join("history.db").exists();
    if has_real_db {
        for name in ["history.db", "history.db-wal", "history.db-shm"] {
            let from = src_db.join(name);
            if from.exists() {
                std::fs::copy(&from, tmp.join(name)).unwrap();
            }
        }
    }

    // 2. 打开历史库
    let store = HistoryStore::open(&tmp).expect("打开拷贝的 history.db 失败");
    let count = store.count();
    println!("✓ 打开历史库成功,共 {count} 条");

    let rows = store.query(None, None, false, false, 10, 0).expect("查询失败");
    println!("✓ 查询最新 10 条:");
    for r in &rows {
        let preview = match r.entry_type.as_str() {
            "Image" => "[图片]".to_string(),
            "File" => format!("[文件×{}]", r.files().len()),
            _ => r
                .text
                .as_deref()
                .unwrap_or("")
                .chars()
                .take(40)
                .collect(),
        };
        println!(
            "  [{}] {} app={} starred={} t={}",
            r.entry_type,
            preview.replace('\n', "\\n"),
            r.source_app_name.as_deref().unwrap_or("-"),
            r.starred,
            r.created_at_millis()
        );
    }

    // 3. 校验搜索:取最新一条文本条目的前几个字做字面+拼音检索
    let text_rows = store.query(None, Some("Text"), false, false, 5, 0).expect("查询文本失败");
    if let Some(first) = text_rows.first() {
        if let Some(t) = first.text.as_deref() {
            let chars: String = t.chars().take(2).collect();
            if !chars.is_empty() {
                let n = store
                    .query(Some(&chars), None, false, false, 500, 0)
                    .expect("字面检索失败")
                    .len();
                println!("✓ 字面检索 {chars:?} → {n} 条命中");
            }
        }
    }
    let py = store
        .query(Some("wenjian"), None, false, false, 500, 0)
        .expect("拼音检索失败")
        .len();
    println!("✓ 拼音检索 wenjian → {py} 条命中");
    let ini = store
        .query(Some("bjb"), None, false, false, 500, 0)
        .expect("首字母检索失败")
        .len();
    println!("✓ 首字母检索 bjb(笔记本) → {ini} 条命中");

    // 4. 写入回归(在拷贝上):插入/置顶/清理
    let mut probe = nexclip_lib::history::HistoryItem {
        id: 0,
        server_id: None,
        entry_type: "Text".into(),
        text: Some("__compat_probe__测试条目".into()),
        html: None,
        image_path: None,
        image_ref: None,
        file_paths_json: None,
        content_hash: None,
        device_id: "probe".into(),
        device_name: Some("兼容验证".into()),
        source_app_name: None,
        source_app_path: None,
        source_app_icon: None,
        created_at_ticks: nexclip_lib::history::now_ticks(),
        origin: 0,
        starred: false,
        remark: None,
    };
    let id = store.insert(&probe).expect("插入失败");
    assert!(id > 0, "插入未生效");
    // 注意:真实库未收藏条目已达上限(200),插入会立即触发超限裁剪,
    // 所以总数可能不变,但探针条目必须能查到
    let found = store
        .query(Some("__compat_probe__"), None, false, false, 10, 0)
        .expect("查询探针失败");
    assert_eq!(found.len(), 1, "探针条目未入库");
    assert_eq!(found[0].id, id);
    probe.created_at_ticks += 10_000;
    let probe_hash = nexclip_lib::clipboard::hash::hash_text(
        probe.text.as_deref().unwrap(),
        None,
    );
    let ok = store
        .touch_by_hash(
            &probe_hash,
            None,
            "probe",
            None,
            probe.created_at_ticks,
            None,
            None,
            None,
        )
        .expect("TouchByHash 失败");
    assert!(ok, "TouchByHash 未命中");
    println!("✓ 插入 + 重复置顶(TouchByHash)在旧库上工作正常");

    // 5. 设置文件
    let tmp_settings_dir = tmp.join("settings");
    let settings_path = std::path::Path::new(&roaming).join("NexClip").join("settings.json");
    if settings_path.exists() {
        // 借助 settings 模块读取真实 settings.json(只读,不改真实文件)
        let text = std::fs::read_to_string(&settings_path).unwrap();
        let dto: settings::SettingsDto = serde_json::from_str(&text).expect("settings.json 解析失败");
        let s = settings::settings_from_dto(dto);
        println!(
            "✓ settings.json 读取成功: device={} paired={} hotkey={} theme={} maxHistory={} token已解密={}",
            s.device_name,
            s.is_paired,
            s.hotkey,
            s.theme_mode,
            s.max_history,
            !s.auth_token.is_empty()
        );
        // 验证 DPAPI 解出的令牌能重新加密回兼容格式
        if !s.auth_token.is_empty() {
            let enc = settings::dpapi::encrypt(&s.auth_token).expect("DPAPI 加密失败");
            let dec = settings::dpapi::decrypt(&enc).expect("DPAPI 解密失败");
            assert_eq!(dec, s.auth_token, "DPAPI 往返不一致");
            println!("✓ DPAPI 解密旧令牌成功,且往返一致(密文长度 {})", enc.len());
        }
        let _ = tmp_settings_dir;
    } else {
        println!("ℹ 未找到现有 settings.json(本机无配置),跳过");
    }

    let _ = std::fs::remove_dir_all(&tmp);
    println!("\n全部兼容性验证通过 ✅");
}
