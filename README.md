# NexClip Windows 桌面端（Tauri 版）

NexClip Windows 桌面端的下一代实现：**Tauri 2 + React 18 + antd 5**（前端）与 **Rust**（后端），
替代此前的 WinUI 3 版本（WinUI 源码在 `Windows-WinUI` 分支留存）。

---

## 核心特性

- **轻量运行时**：Tauri 2 + 系统 WebView2，告别 .NET / WinAppSDK 运行环境依赖。
- **三窗口架构**：主剪贴板弹窗（无边框、光标处呼出）、设置窗口、智能直达 toast。
- **多种剪贴板类型**：纯文本、HTML、高清图片、文件元数据，去重入库。
- **系统托盘与全局热键**：呼出主面板 / 打开直达链接 / 置顶切换等，均可配置。
- **多设备同步**：REST + 原生 WebSocket 推送（协议见 `desktop-tauri/docs/ws-protocol.md`），
  支持配对码配对、文件互传聊天。
- **智能直达 (SmartAction)**：链接 / 网盘 / 色值 / 本地路径 / 提取码识别与一键直达。
- **拼音与全文检索**：拼音首字母与全拼模糊搜索历史记录。
- **应用内更新**：双通道更新检查（GitHub Releases / 直连服务器），下载进度事件、SHA-256 校验、一键安装。

---

## 构建与开发

### 环境要求
- Windows 10 (1809+) 或 Windows 11
- Node.js 18+ 与 npm
- Rust 1.77+（MSVC 工具链）
- WebView2 Runtime（Win11 自带）

### 常用命令

```bash
cd desktop-tauri

npm install          # 安装前端依赖（首次）

npm run tauri dev    # 开发模式（Vite 1420 端口 + Rust 增量编译）
npm run tauri build  # 打包 NSIS 安装包
npm run build        # 仅构建前端（tsc --noEmit + vite build）

# 真实数据兼容性验证（拷贝 %LOCALAPPDATA%\NexClip 到临时目录校验，不碰原库）
cargo run --example verify_compat
```

---

## 目录结构

```
desktop-tauri/
├── src/                  # React 前端（按 Tauri 窗口 label 路由）
│   ├── pages/            # ClipboardPopup / Settings / TransferChat / LinkToast
│   └── ipc/              # invoke 封装（history / chat / settings）
└── src-tauri/            # Rust 后端
    └── src/
        ├── lib.rs        # 窗口 / 托盘 / 热键总装
        ├── capture.rs    # 剪贴板采集管线（防环 / 去抖 / 过滤）
        ├── commands/     # 39 个 tauri command
        ├── history/      # SQLite 存储（WAL + 兼容迁移）+ 拼音检索
        ├── sync/         # REST API + 原生 WS 推送 + 同步引擎
        ├── smart.rs      # 智能直达识别
        ├── update.rs     # 应用内更新
        └── platform/     # 热键 / 监听 / 写入线程 / 粘贴回注
```

---

## 数据兼容

新版本直接打开旧 WinUI 版的 `%LOCALAPPDATA%\NexClip\history.db`：
建表用 `CREATE TABLE IF NOT EXISTS`，新列按 `PRAGMA table_info` 逐列 `ALTER TABLE` 迁移，
存量数据自动回填内容哈希与拼音索引。

---

## 独立分支

```bash
git clone -b Windows-Tauri https://github.com/yixing233/nexclip.git
```
