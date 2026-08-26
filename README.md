# NexClip Node.js 服务端

NexClip Node.js 服务端是基于 TypeScript 与 Node.js 构建的轻量级同步服务端。与 ASP.NET Core 版服务端的 API 契约与 SignalR 线协议完全兼容。

---

## 核心特性

- **超轻量部署**：仅依赖 `ws`，内置使用 `node:sqlite` 原生轻量数据库，运行时内存占用约 30~50MB。
- **实时推送**：完整实现 SignalR JSON 线协议（WebSocket 传输），支持广播与按设备定向推送。
- **零配置数据存储**：直接使用 SQLite 数据库存储历史与设备信息，自动管理图片与临时文件。
- **静态资源托管**：支持直接托管 Web 管理端静态资源（SPA 自动回退）。

---

## 运行与部署

### 环境要求
- Node.js 22.5+ (推荐 Node.js 24+)

### 快速启动

```bash
# 安装依赖
npm install

# 构建 TypeScript
npm run build

# 启动服务
npm start
```

### 常用环境变量

| 环境变量 | 默认值 | 描述 |
| :--- | :--- | :--- |
| `SC_PORT` | `5033` | 服务端口 |
| `SC_AUTH_TOKEN` | (空) | 认证令牌 |
| `SC_DB_PATH` | `data/syncclipboard.db` | SQLite 数据库路径 |
| `SC_IMAGE_PATH` | `data/images` | 图片保存路径 |
| `SC_MAX_HISTORY` | `200` | 历史记录上限 |
| `SC_ONLINE_THRESHOLD_SECONDS` | `120` | 设备在线判定心跳阈值(秒) |

---

## 独立分支

```bash
git clone -b server https://github.com/yixing233/nexclip.git
```
