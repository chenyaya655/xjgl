# 生产部署（Linux + Docker）

本目录是**开发环境**（`docker-compose.dev.yml`，WSL2）与**生产编排**（`docker-compose.prod.yml`）的所在地。
开发环境说明见本文件末尾；下面是从零部署一套生产环境的步骤。

---

## 0. 前置条件

| 项 | 要求 | 说明 |
|---|---|---|
| 服务器 | Linux（x86_64），4C8G 起步 | 打印/导出是 CPU 活，核数越多越好 |
| Docker | 20.10+ 与 Compose V2（`docker compose`） | 单机部署足够；多机请自行改造成 K8s 或 Swarm |
| 存储 | PostgreSQL 数据卷 + **NAS 挂载点** + 降级目录卷 | 照片存 NAS，降级目录必须持久化 |
| 网络 | 对外域名 + HTTPS 证书 | 生产 `AuthServer:RequireHttpsMetadata=true`，**必须 HTTPS** |

先在宿主机挂好 NAS（容器以**同路径**绑入，便于排查）：

```bash
sudo mkdir -p /mnt/nas/xjgl
# SMB（Windows 共享）
sudo mount -t cifs //nas.example.local/xjgl /mnt/nas/xjgl -o username=xjgl,password=***,vers=3.0,uid=1000,gid=1000
# 或 NFS
sudo mount -t nfs nas.example.local:/export/xjgl /mnt/nas/xjgl
```

> 建议写进 `/etc/fstab` 并加 `_netdev`，否则重启后 NAS 没挂上，照片会全部走降级。

---

## 1. 取代码并准备环境变量

```bash
git clone <你的仓库地址> /opt/xjgl && cd /opt/xjgl/deploy
cp .env.example .env
vim .env      # 逐项改：域名、数据库/Redis 口令、NAS 路径
chmod 600 .env
```

`.env` 里 **必须改**的：`APP_SELF_URL`、`APP_CLIENT_URL`、`APP_CORS_ORIGINS`（三者通常同域名）、
`POSTGRES_PASSWORD`、`REDIS_PASSWORD`、`SEQ_ADMIN_PASSWORD`。

> compose 里用的是 `${VAR:?提示}`：变量没设会**直接报错退出**，而不是以空值启动后到处报连接失败。

---

## 2. 生僻字字体（可选，但建议）

镜像里已经装了中文字体（`fonts-noto-cjk`），常见汉字都能打。**生僻字**（如「𠮷」）需要额外字体：

```bash
mkdir -p /opt/xjgl/deploy/fonts/rare
# 把 Plangothic 分片（或用得上的其它生僻字字体）放进去
cp /path/to/PlangothicP1-Regular.ttf /opt/xjgl/deploy/fonts/rare/
```

然后在 `.env` 里把字体族名对上：

```
RARE_FONT_DIR=/opt/xjgl/deploy/fonts/rare
```

字体文件**不进仓库**（体积大、授权各异）。目录为空时系统不会报错，只是生僻字可能显示为方块，
学籍卡上会打印「注：姓名含生僻字，请核对原件」作为提示。

---

## 3. 构建镜像

```bash
cd /opt/xjgl
docker build -f deploy/api.Dockerfile -t xjgl/api:latest .
docker build -f deploy/web.Dockerfile -t xjgl/web:latest .
```

---

## 4. 启动依赖并初始化数据库

```bash
cd deploy
docker compose -f docker-compose.prod.yml up -d postgres redis

# 迁移 + 种子数据（服务在 tools profile 下，跑完即退）
docker compose -f docker-compose.prod.yml run --rm migrate
```

迁移是**幂等**的：框架表走 EF Core 迁移，业务表走 SqlSugar CodeFirst（缺表建表、缺列补列），
种子数据重复执行不会产生脏数据。

---

## 5. 启动应用

```bash
docker compose -f docker-compose.prod.yml up -d api web
docker compose -f docker-compose.prod.yml ps
```

访问 `http://<服务器>:${WEB_PORT:-8081}`。前端是 nginx 托管的静态资源，
`/api`、`/connect` 由 nginx 反代到 `api:8080`（同源，因此没有跨域问题）。

**首次登录**：默认管理员 `admin` / `1q2w3E*` —— 请立刻改口令，并参见仓库根目录的 `SECURITY.md`。

---

## 6. 验证清单

```bash
# 1) 健康检查：200 且 database=up
curl -s http://localhost:8081/health

# 2) 登录拿令牌
curl -s -X POST http://localhost:8081/connect/token \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -d 'grant_type=password&username=admin&password=1q2w3E*&client_id=Xjgl_App&scope=Xjgl%20offline_access'

# 3) 学籍卡 PDF：应返回 %PDF 开头（这一步同时验证中文字体装好了）
TOKEN=<上一步的 access_token>
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:8081/api/print/student-roster?MaxRows=10" -o roster.pdf
head -c 5 roster.pdf      # 期望输出 %PDF-

# 4) 日志里**不能**出现「分布式缓存主实现不可用」
docker compose -f docker-compose.prod.yml logs api | grep -i '缓存主实现不可用' && echo '⚠ Redis 没连上，正在用内存缓存'
```

> 第 4 条很重要：本系统的分布式缓存是「Redis 主 + 内存兜底」的容错装饰器，
> **配置错了不会报错，只会静默降级成单机缓存**（缓存与限流都不再跨实例共享）。
> 开发时就踩到过这个坑（`appsettings.json` 里的密码与实际 Redis 不一致，系统安静地跑着内存缓存）。

---

## 7. HTTPS 与域名

生产要求 HTTPS（`AuthServer:RequireHttpsMetadata=true`）。两种做法：

- **外加一层反向代理**（推荐）：宿主机的 nginx/Caddy 终止 TLS，转发到 `127.0.0.1:${WEB_PORT}`，
  并把 `.env` 里的 `APP_SELF_URL` / `APP_CLIENT_URL` / `APP_CORS_ORIGINS` 设成 `https://` 域名。
- 直接给 `web` 容器挂证书：改 `nginx.conf` 增加 443 server 段（证书文件从宿主机只读挂入）。

改完域名后**必须重新签发令牌**（`APP_SELF_URL` 是令牌的签发者，改了之后旧令牌全部失效）。

---

## 8. 备份与恢复

| 对象 | 位置 | 建议 |
|---|---|---|
| 数据库 | 卷 `xjgl-pgdata` | `docker compose exec postgres pg_dump -U $POSTGRES_USER $POSTGRES_DB > xjgl-$(date +%F).sql`，每日定时 |
| 照片 | NAS 的 `/mnt/nas/xjgl` | 走 NAS 自身快照/备份策略 |
| 降级照片 | 卷 `xjgl-photofallback` | **不要清理**：里面是 NAS 熔断期间暂存、等待补偿的原始照片 |
| 生僻字字体 | `deploy/fonts/rare` | 与镜像一起纳入部署资产 |

---

## 9. 第三方组件的许可证（商用前请确认）

| 组件 | 用途 | 许可证要点 |
|---|---|---|
| QuestPDF | 学籍卡 / 花名册 PDF | 社区版对年收入 < 100 万美元的组织免费；否则需购买。许可证声明集中在 `PrintFontRegistry` |
| ImageSharp | 照片缩略图 | Six Labors Split License：开源与小微组织免费，商用需评估 |
| ABP Framework | 应用框架 | LGPL-3.0（或商业版） |
| 字体（Noto CJK / Plangothic） | 中文与生僻字渲染 | Noto 系列为 SIL OFL；Plangothic 遵循其自身授权，请自行确认 |

替换渲染/图像库只需改 `src/Xjgl.Reporting` 与 `src/Xjgl.Imaging` ——
业务层依赖的是 `IStudentCardPdfRenderer` / `IStudentRosterPdfRenderer` / `IImageProcessor` 接口。

---

## 10. 可选组件

```bash
# 结构化日志（Seq，http://<服务器>:5341）
docker compose -f docker-compose.prod.yml --profile observability up -d

# 消息队列（当前版本**未接入**：需先实现 Outbox，见方案文档技术债第 4 条）
docker compose -f docker-compose.prod.yml --profile mq up -d
```

---

## 开发环境（WSL2）

```bash
cd /mnt/d/StudentManagement/deploy
docker compose -f docker-compose.dev.yml up -d
docker compose -f docker-compose.dev.yml ps
```

开发编排包含 postgres / redis / rabbitmq / adminer（8080）/ seq（5341），
端口直接映射到宿主机的 localhost，因此 Windows 上 `dotnet run` 可直接连。

---

## 部署文件的自动核对

手写编排最容易出的问题是"变量名写错一个字母"——容器照样起来，功能上线才炸。
因此仓库里有一道核对：

```bash
node tools/verify-deploy.mjs                       # 本机即可运行
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.example config -q   # CI 里跑（校验语法与插值）
```

前者以 `src/Xjgl.HttpApi.Host/appsettings.json` 为事实来源，反推 compose 必须提供的环境变量
（`A:B` → `A__B`）逐个核对，并检查字体、健康检查、数据卷、nginx 的深链回落与上传体积等；
后者用 docker 自己的解析器校验 YAML 语法与变量插值。两者都在 CI 里执行。
