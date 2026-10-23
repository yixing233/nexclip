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
# 安装依赖（含 devDependencies，构建需要 TypeScript）
npm ci

# 构建 TypeScript（src/ -> dist/）
npm run build

# 启动服务
npm start
```

### 部署（生产）

生产目录 `/var/www/nexclip-server` 是 **git 检出**，不要手工拷贝 dist。

```bash
cd /var/www/nexclip-server
git pull --ff-only origin server
npm ci                 # 必须带 devDependencies，否则没有 tsc
npm run build          # src/ -> dist/
pm2 restart nexclip-server
curl -fsS http://127.0.0.1:5033/api/health
```

以上四步已固化在仓库内 `deploy.sh`，一条命令完成：

```bash
cd /var/www/nexclip-server && ./deploy.sh
```

脚本做的事：记录部署前 commit → `git pull --ff-only` → 备份旧 `dist/` → `npm ci` → `npm run build` →
`pm2 restart` → 健康检查。**任何一步失败都会中止并且不重启服务**，构建失败时保留旧 dist 继续跑。

部署后确认线上版本：

```bash
git -C /var/www/nexclip-server rev-parse --short HEAD   # 线上跑的是哪个 commit
pm2 describe nexclip-server | grep script                # 确认指向 dist/server.js
```

### 配置文件

`config.json` 是**按机器不同**的本地配置，已被 `.gitignore` 忽略，不再进入版本库。
新机器从模板复制后改：

```bash
cp config.example.json config.json
cp .env.example .env        # 填 ADMIN_PASSWORD，不要提交
```

生产用 `.env` 里的 `SC_WEB_DIST` 指向实际静态资源目录，不必改 config.json。

### 常用环境变量

| 环境变量 | 默认值 | 描述 |
| :--- | :--- | :--- |
| `SC_PORT` | `5033` | 服务端口 |
| `SC_AUTH_TOKEN` | (空) | 认证令牌 |
| `SC_DB_PATH` | `data/syncclipboard.db` | SQLite 数据库路径 |
| `SC_IMAGE_PATH` | `data/images` | 图片保存路径 |
| `SC_MAX_HISTORY` | `200` | 历史记录上限 |
| `SC_ONLINE_THRESHOLD_SECONDS` | `120` | 设备在线判定心跳阈值(秒) |
| `SC_WEB_DIST` | `./web/dist` | Web 管理端静态资源目录(生产可指向任意路径) |

---

## 独立分支

```bash
git clone -b server https://github.com/yixing233/nexclip.git
```
