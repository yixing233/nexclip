# NexClip .NET C# 服务端

NexClip .NET 服务端基于 ASP.NET Core 9 与 Entity Framework Core 打造的高性能全功能同步服务端。

---

## 核心特性

- **原生高并发**：基于 ASP.NET Core 9 Kestrel 高性能 Web 服务器与 SignalR Hub。
- **强类型与 ORM**：使用 EF Core SQLite 进行持久化存储与迁移。
- **多客户端兼容**：与 Node.js 版服务端保持 100% 协议兼容，支持 Android、Windows 及 Web 端。

---

## 运行与部署

### 环境要求
- .NET 9.0 SDK / ASP.NET Core Runtime 9.0

### 快速启动

```powershell
# 编译并运行
dotnet run --configuration Release

# 发布为独立文件
dotnet publish -c Release -r linux-x64 --self-contained true
```

---

## 独立分支

```bash
git clone -b server-csharp https://github.com/yixing233/nexclip.git
```
