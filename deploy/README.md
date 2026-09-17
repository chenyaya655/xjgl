# 开发环境（WSL2 + Docker）

本目录提供学籍管理系统开发所需的三件套与辅助工具，全部跑在 **WSL2 的 Docker** 里。

| 服务 | 镜像 | 端口 | 账号 |
|---|---|---|---|
| PostgreSQL 16 | `postgres:16` | 5432 | 库 `xjgl` / 用户 `xjgl` / 密码 `Xjgl@Dev2026` |
| Redis 7 | `redis:7-alpine` | 6379 | 密码 `Xjgl@Dev2026` |
| RabbitMQ 3.13 | `rabbitmq:3.13-management-alpine` | 5672 / 15672 | vhost `xjgl` / 用户 `xjgl` / 密码 `Xjgl@Dev2026` |
| Adminer（可选） | `adminer:4` | 8080 | 数据库可视化 |
| Seq（可选） | `datalust/seq` | 5341 | 结构化日志查看 |

> ⚠️ 以上密码**仅用于本机开发**，不要用于任何生产环境。

---

## 一、启动

### 1. 先释放端口

如果你机器上已经有 PostgreSQL / Redis / RabbitMQ 在跑（无论是否用 Docker），
必须先让它们腾出 5432 / 6379 / 5672 / 15672，否则新容器起不来。

```bash
# 查看是哪个容器占用了端口
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Ports}}'

# 停掉冲突的容器（把 <name> 换成实际容器名；如果是宿主机原生服务，用 systemctl stop）
docker stop <name>
```

### 2. 启动本项目的三件套

```bash
cd /mnt/d/StudentManagement/deploy
docker compose -f docker-compose.dev.yml up -d

# 看状态，等到 postgres / redis / rabbitmq 都是 healthy
docker compose -f docker-compose.dev.yml ps
```

### 3. 验证

```bash
# PostgreSQL
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "select version();"
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "select extname from pg_extension order by 1;"

# Redis
docker exec -it xjgl-redis redis-cli -a 'Xjgl@Dev2026' ping     # 期望 PONG

# RabbitMQ（管理台 http://localhost:15672 ，用户 xjgl / 密码 Xjgl@Dev2026）
docker exec -it xjgl-rabbitmq rabbitmqctl list_vhosts
```

### 4. 从 Windows 侧验证连通性

WSL2 会把容器端口转发到 Windows 的 `localhost`，所以在 Windows 上直接跑
`dotnet run` 就能连上，**不需要改连接串**：

```powershell
Test-NetConnection 127.0.0.1 -Port 5432   # 5432/6379/5672/15672 都应为 True
```

若不通，用 `wsl hostname -I` 取 WSL 的 IP，写进 `appsettings.Development.json` 的连接串。

---

## 二、建表与初始化数据

```bash
# 在 Windows 侧（进入仓库根目录后执行）
dotnet run --project src\Xjgl.DbMigrator
```

执行顺序：

1. **EF Core 迁移**（`Order = 10`）—— 建 ABP 框架表
   （`AbpUsers`、`AbpRoles`、`AbpPermissionGrants`、`AbpSettings`、`AbpAuditLogs`、`OpenIddict*` 等）
2. **SqlSugar CodeFirst**（`Order = 20`）—— 建业务表（`sys_*`、`stu_*`）
3. **种子数据** —— 默认管理员、语言、权限、OpenIddict 客户端与 Scope

默认管理员账号由 ABP 的 Identity 数据种子创建，见 `XjglDbMigrationService` 的日志输出。

---

## 三、日常命令

```bash
cd /mnt/d/StudentManagement/deploy

docker compose -f docker-compose.dev.yml stop          # 停（保留数据）
docker compose -f docker-compose.dev.yml start         # 启
docker compose -f docker-compose.dev.yml logs -f postgres
docker compose -f docker-compose.dev.yml down          # 删容器，保留数据卷
docker compose -f docker-compose.dev.yml down -v       # ⚠️ 连数据卷一起删，数据全丢
```

---

## 四、数据卷

| 卷名 | 内容 |
|---|---|
| `xjgl-pgdata` | PostgreSQL 数据 |
| `xjgl-redisdata` | Redis AOF |
| `xjgl-rabbitmqdata` | RabbitMQ 数据 |
| `xjgl-seqdata` | Seq 日志 |

---

## 五、为什么 PostgreSQL 用 Debian 版而不是 alpine

中文与生僻字排序需要 **ICU 排序规则**（`CREATE COLLATION ... provider = icu`），
`postgres:16-alpine` 的精简构建不一定带 ICU。因此这里用 `postgres:16`（Debian），
并在 `initdb/01-extensions.sql` 里预建了 `zh_icu` 排序规则。

> 注意：生僻字（CJK 扩展 B 区及以上）无论用哪种排序规则，都无法按拼音排序，
> 因此学生列表默认按「学校 + 年级班级 + 学籍辅号」排序，不按姓名排序。
> 详见方案文档第 7 章「生僻字全链路方案」。
