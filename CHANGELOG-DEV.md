# NexClip 改动记录

> 本文件记录每一次非平凡改动的**动机、方案、验证方式与遗留问题**。

---

## 2026-11-13 · 网页端文件互传

### 背景

网页控制台此前只能传剪贴板文本与图片(图片走 /api/clipboard/image,
会进 Entries 表并被 trimHistory 清理)。需要一个独立的「文件互传」通道:
网页上传文件 → 同账号的设备与其它网页会话收到实时通知 → 可下载/删除。

### 方案(server 分支)

**独立 Files 表 + FileTransferService**(src/fileTransfer.ts),不复用 Entries:
剪贴板条目会被 trimHistory 清理、被各端当作"可写入剪贴板的内容"消费;
文件互传是对话式传输流,生命周期不同,复用会让两边清理策略互相干扰。

- 存储:fileStoragePath(沿用 SC_IMAGE_PATH,按 yyyyMMdd 分目录),
  元数据(FileId/原名/大小/SHA-256)存 SQLite Files 表
- 端点:POST /api/files(multipart,限速 30 次/分)、GET /api/files(列表)、
  GET /api/files/{id}/download(支持 HEAD)、DELETE /api/files/{id}
- 推送:SignalR 新增 FileTransferred / FileDeleted 事件,
  与 broadcastUpdated 相同的用户组隔离(userId 为 null 全量广播)
- 下载鉴权:fileId 正则只认 hex(天然防穿越),路径再用 resolve+relative
  校验必须在存储根内;用户组隔离(登录用户只能下载/删除自己组的文件)
- Content-Disposition 带 RFC 5987 filename*(中文原名),ASCII 兜底

### 自测抓到并修掉的 3 个 bug

1. **下载全部 410「文件已丢失」**:`resolve(row.StoragePath)` 是相对进程
   CWD 解析,与存储根拼接后校验永远失败。改为 static.ts 同款
   `resolve(root, rel)` + `relative` 校验。
2. **HEAD /api/files/{id}/download 返回 404**:路由只匹配 GET。
   下载工具/浏览器预检常用 HEAD 探测大小,补上 HEAD 分支。
3. (客户端)curl Windows 下中文文件名乱码 —— 服务端已用 filename* 兜底,
   浏览器端 FormData 原生 UTF-8 无此问题。

### 方案(web 分支)

- api.ts:listFiles / uploadFile / deleteFile / downloadFile
  (下载走 fetch→blob→objectURL,因为 Authorization 头不能放在 <a href> 上)
- hub.ts:FileTransferred / FileDeleted 事件;App.tsx 收到推送弹
  「收到新文件: xxx(来自 设备名)」并 bump fileTick
- 新页面 FilesPage(antd Table):上传(antd Upload multiple)、列表、
  下载、Popconfirm 删除;侧栏菜单「文件互传」;过期响应丢弃防乱序
- App.tsx:fileTick 经 props 传 AdminConsole(fileTick 与 refreshTick
  分开,文件推送不会连带刷新全部页面)

### 验证(全部实测通过)

- 服务端 tsc/build 零错误;临时实例(独立端口/库/存储)curl 全链路:
  上传 → HEAD(Content-Length 正确)→ GET 下载(字节一致)→ DELETE → 404
- 未授权访问 401;目录穿越 fileId(非 hex)404
- web tsc + vite build 通过;服务端托管 dist 后:
  - 登录 → /pro/files 菜单项与页面正常渲染
  - 列表正确显示 curl 上传的旧文件(相对时间、大小、来源)
  - **浏览器上下文内真实 fetch 上传中文名文件成功**,列表即时出现

### 验证边界

- Android/Windows 端收到 FileTransferred 推送目前会忽略(未实现接收),
  不影响其既有功能;后续如需「服务器→手机」方向的文件分发,再在各端
  加 FileTransferred 处理(下载用现有 HTTP 凭证即可)。
- 上限沿用 maxImageSizeBytes(默认 10MB),大文件分块未做 —— 网页场景
  10MB 覆盖绝大多数文档/截图,需要时调 SC_MAX_IMAGE_BYTES 即可。
- 广播用「同名 target 新事件」实现,老客户端(不认识 FileTransferred 的
  版本)收到未知 SignalR 事件会静默忽略,无兼容性风险。

