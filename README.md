# NexClip Windows 桌面端（Tauri 版）

NexClip Windows 桌面端的下一代实现：**Tauri 2 + React 18 + antd 5**（前端）与 **Rust**（后端），
替代此前的 WinUI 3 版本（WinUI 源码在 `Windows-WinUI` 分支留存）。

当前正式版：**2.0.0**，Windows x64。
[下载安装包](https://github.com/yixing233/nexclip/releases/download/v2.0.0/NexClip_Tauri_2.0.0_x64-setup.exe) ·
[发布说明](docs/releases/v2.0.0.md)

安装前请退出旧 WinUI 客户端。Tauri 使用独立安装目录与卸载项，共用旧版的配置、历史与自启动设置；
请一次只运行一个版本。卸载 Tauri 后返回 WinUI 时，请在旧版设置中重新开启需要的开机自启动。

---

## 核心特性

- **轻量运行时**：Tauri 2 + 系统 WebView2，告别 .NET / WinAppSDK 运行环境依赖。
- **桌面窗口**：主剪贴板弹窗（无边框、光标处呼出）、设置窗口、智能直达 toast、截图与贴图窗口。
- **多种剪贴板类型**：纯文本、HTML、高清图片、文件元数据，去重入库。
- **系统托盘与全局热键**：呼出主面板 / 打开直达链接 / 置顶切换等，均可配置。
- **多设备同步**：REST + 原生 WebSocket 推送（协议见 `docs/ws-protocol.md`），
  支持配对码配对、文本与二进制文件互传、持久化消息记录。超过 10 MB 的图片保留本地提示，不自动同步。
- **截图与贴图**：区域截图、标注编辑、滚动截图与桌面贴图。
- **智能直达 (SmartAction)**：链接 / 网盘 / 色值 / 本地路径 / 提取码识别与一键直达。
- **拼音与全文检索**：拼音首字母与全拼模糊搜索历史记录。
- **应用内更新**：双通道更新检查（GitHub Releases / 直连服务器），下载进度事件、SHA-256 校验、一键安装。

---

## 构建与开发

### 环境要求
- Windows 10 (1809+) 或 Windows 11
- Node.js 20.19+ 或 22.12+ 与 npm（本次构建使用 24.16.0）
- Rust stable（MSVC 工具链；本次构建使用 1.96.0）
- WebView2 Runtime（Win11 自带）

### 常用命令

```bash
npm ci               # 按锁文件安装前端依赖（首次）

npm run tauri dev    # 开发模式（Vite 1420 端口 + Rust 增量编译）
npm run tauri build  # 打包 NSIS 安装包
npm run build        # 仅构建前端（tsc --noEmit + vite build）
cargo test --manifest-path src-tauri/Cargo.toml

# 真实数据兼容性验证（拷贝 %LOCALAPPDATA%\NexClip 到临时目录校验，不碰原库）
cargo run --example verify_compat --manifest-path src-tauri/Cargo.toml
```

---

## 目录结构

```
src/                  # React 前端（按 Tauri 窗口 label 路由）
├── pages/            # ClipboardPopup / Settings / TransferChat / LinkToast
└── ipc/              # invoke 封装（history / chat / settings）
src-tauri/            # Rust 后端
└── src/
        ├── lib.rs        # 窗口 / 托盘 / 热键总装
        ├── capture.rs    # 剪贴板采集管线（防环 / 去抖 / 过滤）
        ├── commands/     # 前后端 IPC 命令
        ├── history/      # SQLite 存储（WAL + 兼容迁移）+ 拼音检索
        ├── sync/         # REST API + 原生 WS 推送 + 同步引擎
        ├── smart.rs      # 智能直达识别
        ├── screenshot/   # 截图 / 滚动截图 / 贴图
        ├── update.rs     # 应用内更新
        └── platform/     # 热键 / 监听 / 写入线程 / 粘贴回注
```

---

## 数据兼容

新版本直接打开旧 WinUI 版的 `%LOCALAPPDATA%\NexClip\history.db`：
建表用 `CREATE TABLE IF NOT EXISTS`，新列按 `PRAGMA table_info` 逐列 `ALTER TABLE` 迁移，
存量数据自动回填内容哈希与拼音索引。

安装目录默认为 `%LOCALAPPDATA%\NexClip Tauri`，配置目录为 `%APPDATA%\NexClip`，
默认数据目录为 `%LOCALAPPDATA%\NexClip`（自定义数据目录按已有设置使用）。卸载保留共用的配置与历史。

Tauri 更新只识别 `NexClip_Tauri_*_x64-setup.exe`。直连服务器使用 `version.json` 的
`windowsTauri` 段；尚未提供该通道时自动检查 GitHub Releases，不使用 WinUI 的 `windows` 段。

---

## 独立分支

```bash
git clone -b Windows-Tauri https://github.com/yixing233/nexclip.git
```
