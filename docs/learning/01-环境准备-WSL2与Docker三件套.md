# 01 WSL2 + Docker 三件套

> **对应阶段**：P0 ｜ **预计耗时**：40 分钟 ｜ **产出**：一个可用的 PostgreSQL + Redis + RabbitMQ 开发环境

---

## 1.1 要解决的问题

后端要跑起来，需要三样东西：

| 服务 | 在本系统里干什么 | 不用它会怎样 |
|---|---|---|
| **PostgreSQL 16** | 所有数据（ABP 框架表 + 业务表） | 什么都存不了 |
| **Redis 7** | 分布式缓存（权限、设置、字典）、分布式锁 | 权限每次查库；多实例部署时缓存不一致 |
| **RabbitMQ 3.13** | 分布式事件（照片上传削峰、通知） | 高峰期照片上传把接口拖垮 |

**为什么不用 Windows 版安装包直接装？**

1. 生产环境是 Linux + PostgreSQL，开发期就用 Linux 版本能提前暴露编码、排序规则、大小写敏感等问题；
2. 学籍系统的姓名里会有生僻字（CJK 扩展 B 区及以上），**排序规则和编码必须在开发期就定下来**，不能等上线才发现乱码；
3. 换版本、清库、加扩展全是命令行一条命令，不用卸载重装。

---

## 1.2 动手步骤

### 步骤 1：启用 WSL2 并装 Ubuntu

以**管理员身份**打开 PowerShell：

```powershell
wsl --install -d Ubuntu-24.04
```

装完重启，设置 Ubuntu 用户名与密码。然后确认版本：

```powershell
wsl -l -v
# 期望输出里有 VERSION 2，例如：
#   NAME            STATE           VERSION
# * Ubuntu-24.04    Running         2
```

> 如果显示 VERSION 1，执行 `wsl --set-version Ubuntu-24.04 2`。

### 步骤 2：装 Docker Desktop 并开启 WSL 集成

装好 Docker Desktop 后进入
**Settings → Resources → WSL Integration**，把 `Ubuntu-24.04` 打开。

验证（在 **PowerShell** 里执行即可，Docker Desktop 会把 WSL 的 docker 暴露出来）：

```powershell
docker version
docker compose version
```

### 步骤 3：创建数据库初始化脚本

在仓库里新建 `deploy/initdb/01-extensions.sql`：

```sql
-- 性能观测：慢 SQL 统计
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- 姓名模糊检索（生僻字按原样匹配，不依赖拼音）
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- UUID 生成
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- 去音标/变音符号，便于姓名与地名的宽松匹配
CREATE EXTENSION IF NOT EXISTS unaccent;

-- 中文 ICU 排序规则
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_collation WHERE collname = 'zh_icu') THEN
        CREATE COLLATION zh_icu (provider = icu, locale = 'zh-CN');
    END IF;
END
$$;

SELECT extname, extversion FROM pg_extension ORDER BY extname;
```

**这个脚本很重要的一个作用是"提前失败"**：如果镜像里的 PostgreSQL
没有编译 ICU 支持，`CREATE COLLATION ... provider = icu` 会直接报错，
你会在项目第一天就发现，而不是上线前一周。

### 步骤 4：写 docker-compose

新建 `deploy/docker-compose.dev.yml`（完整内容见仓库该文件），关键片段：

```yaml
name: xjgl-dev

services:
  postgres:
    # 用 Debian 版而非 alpine：完整支持 ICU 排序规则（中文/生僻字排序要用）
    image: postgres:16
    container_name: xjgl-postgres
    environment:
      POSTGRES_DB: xjgl
      POSTGRES_USER: xjgl
      POSTGRES_PASSWORD: Xjgl@Dev2026
      POSTGRES_INITDB_ARGS: "--encoding=UTF8 --locale=C.UTF-8"
      TZ: Asia/Shanghai
    command:
      - postgres
      - -c
      - shared_preload_libraries=pg_stat_statements
      - -c
      - max_connections=200
      - -c
      - log_min_duration_statement=500      # 慢 SQL 日志（>500ms）
      - -c
      - timezone=Asia/Shanghai
    ports:
      - "5432:5432"
    volumes:
      - xjgl-pgdata:/var/lib/postgresql/data
      - ./initdb:/docker-entrypoint-initdb.d:ro

  redis:
    image: redis:7-alpine
    container_name: xjgl-redis
    command: [redis-server, --requirepass, Xjgl@Dev2026, --appendonly, "yes",
              --appendfsync, everysec, --maxmemory, 512mb, --maxmemory-policy, allkeys-lru]
    ports:
      - "6379:6379"

  rabbitmq:
    image: rabbitmq:3.13-management-alpine
    container_name: xjgl-rabbitmq
    environment:
      RABBITMQ_DEFAULT_USER: xjgl
      RABBITMQ_DEFAULT_PASS: Xjgl@Dev2026
      RABBITMQ_DEFAULT_VHOST: xjgl
    ports:
      - "5672:5672"     # AMQP
      - "15672:15672"   # 管理台

  adminer:              # 数据库可视化 http://localhost:8080
    image: adminer:4
    ports: ["8080:8080"]

volumes:
  xjgl-pgdata:
  xjgl-redisdata:
  xjgl-rabbitmqdata:
```

### 步骤 5：启动并验证

```powershell
cd deploy
docker compose -f docker-compose.dev.yml up -d
docker compose -f docker-compose.dev.yml ps
```

期望三个服务的 `STATUS` 都是 `healthy`（首次启动要等 20~40 秒）。

---

## 1.3 验证方法（必须做，不要跳）

**① PostgreSQL 能连、扩展建好了**

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "SELECT extname FROM pg_extension ORDER BY extname;"
```

期望看到 `pg_stat_statements`、`pg_trgm`、`unaccent`、`uuid-ossp`。
再验证中文排序规则真的能用：

```powershell
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "SELECT '张' < '李' AS ok;"
docker exec -it xjgl-postgres psql -U xjgl -d xjgl -c "SELECT '𰻞' AS rare_char;"
```

`rare_char` 那一列必须**原样显示出生僻字**，不能是 `?` 或方块。
这一条验证的是整条链路的编码（Windows 终端 → WSL → Docker → PostgreSQL）。

**② Redis 能连且需要密码**

```powershell
docker exec -it xjgl-redis redis-cli -a Xjgl@Dev2026 ping
# 期望：PONG
docker exec -it xjgl-redis redis-cli ping
# 期望：NOAUTH Authentication required.  ← 这条说明密码真的生效了
```

**③ RabbitMQ 管理台能打开**

浏览器访问 <http://localhost:15672>，用 `xjgl / Xjgl@Dev2026` 登录。
注意用户名是 `xjgl` 而**不是**默认的 `guest`（本项目用的是自定义虚拟主机 `xjgl`）。

---

## 1.4 踩坑记录

### 坑 1：端口被占用

`docker compose up` 报 `bind: address already in use`。
八成是 Windows 上已经装过 PostgreSQL/Redis 服务。

```powershell
netstat -ano | findstr ":5432"
# 找到 PID 后
Get-Process -Id <PID>
```

处理办法：要么停掉本地服务，要么把 compose 里的端口映射改成 `"15432:5432"`，
**同时记得改后端连接串**。

### 坑 2：PostgreSQL 用 alpine 镜像，中文排序报错

`CREATE COLLATION ... provider = icu` 在部分 alpine 镜像上会失败
（ICU 未编译进去）。这就是本项目选 `postgres:16`（Debian 版）的原因。
**不要为了省几十 MB 换成 alpine。**

### 坑 3：改了 `initdb` 里的脚本，重新 up 却没生效

`docker-entrypoint-initdb.d` **只在数据卷为空时执行一次**。
改了脚本要：

```powershell
docker compose -f docker-compose.dev.yml down
docker volume rm xjgl-dev_xjgl-pgdata    # 注意：这会清空数据库！
docker compose -f docker-compose.dev.yml up -d
```

**数据卷名带 compose 项目名前缀**（`xjgl-dev_`）。
用 `docker volume ls` 确认实际名字，不要凭感觉敲。

### 坑 4：Windows 上连不上 `localhost:5432`

正常情况下 WSL2 会把容器端口转发到 Windows 的 `localhost`。
如果不通：

```powershell
wsl hostname -I        # 取 WSL 的 IP，例如 172.24.x.x
```

然后把连接串里的 `localhost` 换成这个 IP，并把数据库配置改成
`listen_addresses = '*'`（compose 里默认已是）。

### 坑 5：`down -v` 与 `down` 的区别

- `docker compose down`：停容器，**保留数据**；
- `docker compose down -v`：连数据卷一起删，**数据库全没了**。

学籍系统的开发库里有你手工造的组织树和测试学生，误删一次要重造半小时。
**养成习惯：不加 `-v`。**

---

## 1.5 本章产出

| 项 | 值 |
|---|---|
| PostgreSQL | `127.0.0.1:5432`，库 `xjgl`，用户 `xjgl`，密码 `Xjgl@Dev2026` |
| Redis | `127.0.0.1:6379`，密码 `Xjgl@Dev2026` |
| RabbitMQ | `127.0.0.1:5672`（管理台 15672），虚拟主机 `xjgl` |
| 已就绪扩展 | `pg_stat_statements`、`pg_trgm`、`uuid-ossp`、`unaccent`，中文排序规则 `zh_icu` |
| Adminer | <http://localhost:8080> |

---

**上一章：** [00 写在前面](README.md) ｜ **下一章：** [02 把版本钉死](02-版本钉死-Directory.Build.props.md)
