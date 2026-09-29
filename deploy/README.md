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

> **完整的一页说明见 [`fonts.md`](fonts.md)**（ROADMAP 第 15 条的验收物）：
> 上游项目与许可证、compose 与 K8s 两种部署各放哪个目录、
> `RareCharacterFontFamily` 家族名怎么核对、以及**怎么验证真的生效** ——
> 其中包括一步「空目录反向对照」：只看 `ls` 是看不出字体到底有没有生效的
> （目录为空、家族名不匹配都不会报错，只在打印那一刻才失败）。

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

# 5) 启动日志里的「内容根」必须是容器里的 /app/api（appsettings.json 就在那里），
#    并且**不该**出现「内容根下没有 appsettings.json」这条告警
docker compose -f docker-compose.prod.yml logs api | grep -E '内容根|没有 appsettings.json'
```

> 第 4 条很重要：本系统的分布式缓存是「Redis 主 + 内存兜底」的容错装饰器，
> **配置错了不会报错，只会静默降级成单机缓存**（缓存与限流都不再跨实例共享）。
> 开发时就踩到过这个坑（`appsettings.json` 里的密码与实际 Redis 不一致，系统安静地跑着内存缓存）。
>
> 第 5 条是同一类问题的另一面：ASP.NET Core 默认把 `appsettings.json` 当**可选**文件，
> 内容根不对时它**不报错**，应用会安静地用代码默认值启动（照片存储路径、打印字体、
> 学籍号规则一起悄悄换值）。应用已经改成按"程序所在目录"找配置，并把内容根打进启动日志，
> 这样一眼就能确认配置真的被读到了。

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
| ImageSharp | 照片缩略图 | Six Labors Split License：开源与小微组织免费，商用需评估。**版本必须钉在 3.1.x**：4.x 起在 MSBuild 里强制校验许可 key，没有 key 会让 `dotnet build` 直接失败（详见 `Directory.Build.props`） |
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

## 11. 扩容到多实例（Kubernetes）

> **先说结论**：本系统当前是**为单实例设计的**，多副本能跑，但有四处能力会打折。
> 这一章不是"照着做就能上 K8s"的教程，而是把**哪些东西会变、哪些不会变**列清楚，
> 免得在没有分布式锁与 Outbox 的前提下盲目加副本。

> **落成清单在 [`k8s/`](k8s/README.md)**：本章的要点已经变成可照抄的 YAML
> （namespace / ConfigMap / Secret 示例 / PVC / postgres / redis / 迁移 Job / api / web / Ingress），
> 以及一份 **15 项的验收清单**。
> ⚠ 但那些清单**从未在真实集群上应用过**（没有 `kubectl apply` 过、没跑过冒烟），
> 所以本章的定位没有变：它仍然是把"会变什么"讲清楚的地方，
> 而不是"K8s 已经验证过"的证据。

### 11.1 多副本之后会变的东西

| 能力 | 单实例 | 多副本 | 为什么 | 当前对策 |
|---|---|---|---|---|
| 限流精度 | 准确 | **约为 N 倍**（N = 副本数） | 令牌桶的"读-算-写"是三段式，实例之间会互相覆盖 | 已换成 **Redis Lua** 原子脚本（债 25 已完成），桶状态共享后精度恢复 |
| 导出/打印任务抢占 | 单 worker 独占 | 多 worker 抢同一批任务 | `sys_export_task` 需要"原子抢占" | 靠 `status + 乐观并发` 抢占；**未做**分布式锁，理论上存在重复执行（产出物幂等，最坏是白干一次） |
| 领域事件外发 | 无 MQ | 无 MQ | RabbitMQ 模块**当前未接入** | 不影响功能；要接入必须先做 Outbox（方案文档技术债第 4 条） |
| 后台任务（缩略图/补偿/清理） | 每轮一份 | 每副本各跑一份 | 都是"扫表 + 处理" | 幂等，重复执行只是多花 CPU；要收敛得加分布式锁 |

**因此：多副本前请先确认债 25/26/27 与 Outbox 的状态**（方案文档 §16.14）。

### 11.2 必须做成"无状态 + 共享存储"的部分

| 状态 | 位置 | K8s 里的做法 |
|---|---|---|
| 用户会话 / 令牌 | OpenIddict 表 | 已在数据库，无需粘性会话（**不要**开 `sessionAffinity` 来"解决"登录问题，它会掩盖真正的配置错误） |
| 数据保护密钥（DataProtection） | 进程目录 `App_Data/DataProtection-Keys` | **必须挂共享卷或指向数据库**，否则每个副本的密钥不同：表现为"在 A 副本登录、请求落到 B 副本就 401" |
| 照片 | NAS 挂载 | 每个副本都挂同一个 NAS 路径（`Xjgl__Photo__NasRoot`） |
| 照片降级区 | 本地卷 `xjgl-photofallback` | **每副本各自一份**（设计如此：NAS 不可用时先落本机），补偿任务把各自的那份回放回 NAS |
| 导出产物 | 本地卷 `App_Data/exports` | 每副本各自一份 → **下载请求必须落到生成它的那个副本**，否则 404。<br>要么用 RWX 共享卷，要么改用对象存储 |
| 生僻字字体 | `deploy/fonts/rare` | 打进镜像，保持各副本一致 |

> ⚠ **导出产物是本系统上 K8s 最现实的一个坑**：当前实现把产物写在容器本地磁盘
> （`ExportArtifactStore` 的相对路径存在任务行里）。多副本下"任务在 A 生成、下载请求打到 B"
> 就是 404。要么给 `App_Data/exports` 挂 **ReadWriteMany**（NFS/CephFS），
> 要么把 `IExportArtifactStore` 换成对象存储实现 —— 接口是为这件事留好的。

### 11.3 参考清单（Deployment / Service / Ingress 要点）

```yaml
# 只列与"能不能跑对"相关的字段，完整模板请按现场规范补
spec:
  replicas: 2                      # 先 2 副本压测再往上加（见第 12 章）
  template:
    spec:
      containers:
        - name: api
          env:
            - name: ASPNETCORE_URLS
              value: http://+:8080
            - name: Xjgl__Photo__NasRoot
              value: /mnt/nas/xjgl
            # 多副本下限流桶必须共享，否则精度退化为 N 倍
            - name: Redis__Configuration
              value: redis:6379
          volumeMounts:
            - { name: nas,            mountPath: /mnt/nas/xjgl }
            - { name: exports,        mountPath: /app/api/App_Data/exports }
            - { name: dpkeys,         mountPath: /app/api/App_Data/DataProtection-Keys }
          # 探针必须挂在**管道最前面**（方案文档 §16.11）：
          # 挂在 nginx/Ingress 上会在后端已经挂掉时仍然返回 200
          livenessProbe:
            httpGet: { path: /health, port: 8080 }
            initialDelaySeconds: 30
          readinessProbe:
            httpGet: { path: /health, port: 8080 }
            initialDelaySeconds: 15
  # 迁移与种子**不能**随 Pod 启动一起跑（多副本会并发执行）
  # 用 Job（backoffLimit: 0）+ initContainer 依赖，或部署流水线里单独一步
```

**迁移器（DbMigrator）必须是单次执行**：多副本同时启动 = 多个进程同时建表/写种子。
用 `Job`（`backoffLimit: 0`）+ `helm.sh/hook: pre-install,pre-upgrade`，
或干脆在流水线里跑完再滚更新。

> 上面这段片段已经在 [`k8s/migrate-job.yaml`](k8s/migrate-job.yaml) 里落成清单
> （`backoffLimit: 0` + `ttlSecondsAfterFinished`，并说明了"Job 的 spec 不可变，升级时要先 delete"）。
> 同样地，本节提到的卷、探针、Service 名都对到了具体文件 —— 见 [`k8s/README.md`](k8s/README.md) 第 1 节。

### 11.4 上 K8s 前的验收清单

1. `kubectl rollout restart` 之后**随便挑一个副本**登录，会话不丢（验证 DataProtection 密钥共享）；
2. 提交一次导出任务，然后**反复刷新下载**（会被负载均衡到不同副本）—— 必须都能下到；
3. 并发上传照片，观察 Redis 里 `ratelimit:*` 的键是**所有副本共用**的；
4. 逐个 kill 副本，`/health` 不应出现 200/503 抖动（`reuseExistingServer` 之外没有粘性会话依赖）。

> 这 4 条已经被扩成 [`k8s/README.md`](k8s/README.md) 第 7 节的 **15 项验收清单**
> （每一项都给了命令与"不符意味着什么"），当前**一项都没打勾** ——
> 需要有人在真实单节点 k3s 上跑一遍并回填实测结果。

---

## 12. 压测与性能基线

> 本仓库**没有**现成的压测脚本 —— 这章给的是"怎么压、看什么、什么结果算不合格"，
> 以及一份可以直接照抄的 k6 脚本。

### 12.1 压什么（按"最容易先垮"排序）

| 优先级 | 场景 | 为什么它先垮 |
|---|---|---|
| 1 | **照片批量上传** | 唯一同时吃 CPU（解码/缩放）、内存（批量缓冲）、磁盘/NAS（写文件）的写路径 |
| 2 | **按学校统计**（`dimension=school`） | `GROUP BY` + `LIMIT 200`，是最重的读；数据上量后扫描面很大 |
| 3 | **花名册 PDF 导出** | PDF 渲染是纯 CPU，且产物要落盘 |
| 4 | 学生列表（分页 + 关键字） | 最高频的读，但都是索引命中，一般不先垮 |
| 5 | 登录 | 只在早高峰短时集中，且 OpenIddict 有自身限流 |

### 12.2 k6 脚本（可直接跑）

```javascript
// tools/k6-smoke.js —— 用法见 12.3
import http from 'k6/http'
import { check, sleep } from 'k6'

const BASE = __ENV.BASE || 'http://127.0.0.1:44300'
// 默认账号与 tools/k6-smoke.js 保持一致：admin / 1q2w3E*
// （演示账号形如 demo-prov-990000，**不是** demo-province-990000 —— 后者登录必然失败，
//   而失败信息是 invalid_grant，看起来像"服务端密码校验坏了"）
const USER = __ENV.USER || 'admin'
const PASS = __ENV.PASS || '1q2w3E*'

export const options = {
  // 阶梯加压：先看拐点在哪，而不是一上来就打满
  stages: [
    { duration: '30s', target: 10 },
    { duration: '1m',  target: 30 },
    { duration: '1m',  target: 60 },
    { duration: '30s', target: 0 }
  ],
  thresholds: {
    // 这两条是"不合格线"，不是"目标线"。超了说明该加索引/加副本，而不是调大超时
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1500', 'p(99)<4000']
  }
}

export function setup() {
  const res = http.post(
    `${BASE}/connect/token`,
    `grant_type=password&client_id=Xjgl_App&username=${USER}&password=${encodeURIComponent(PASS)}&scope=Xjgl offline_access`,
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
  )

  return { token: res.json('access_token') }
}

export default function (data) {
  const auth = { headers: { Authorization: `Bearer ${data.token}` } }

  // 1) 学生列表（最高频的读）
  let r = http.get(`${BASE}/api/app/student?maxResultCount=20&sorting=StudentNo%20ASC`, auth)
  check(r, { '学生列表 200': (x) => x.status === 200 })

  // 2) 按学校统计（最重的读）
  r = http.get(`${BASE}/api/app/statistics/student-statistics?dimension=school`, auth)
  check(r, { '统计 200': (x) => x.status === 200 })

  sleep(1)
}
```

### 12.3 怎么跑、看什么

> ⚠ `tools/k6-smoke.js` 的**语法**已在仓库内校验（ESM 解析通过），
> 但**本机没有装 k6，因此从未实跑过** —— 首次使用请先小并发（`--vus 2 --duration 10s`）
> 确认账号、BASE 与路径都对，再上阶梯。不要把它当成"已验证的基线"。

```bash
# 安装 k6（Linux / WSL）
sudo gpg -k && sudo apt-get install -y k6

# 对着**测试库**压，别对着生产/演示库
k6 run -e BASE=http://127.0.0.1:44300 -e USER=admin -e PASS='1q2w3E*' tools/k6-smoke.js

# 只看统计接口的极限（把上面的 stages 调密）
k6 run --vus 100 --duration 3m -e BASE=http://127.0.0.1:44300 tools/k6-smoke.js
```

**同时要看的（只看 k6 的输出会漏掉真问题）**：

| 观察点 | 在哪看 | 不合格的表现 |
|---|---|---|
| 数据库连接池 | PG `select count(*) from pg_stat_activity where datname='xjgl'` | 接近 `max_connections`，且 `state='idle in transaction'` 堆积 |
| 慢查询 | PG 日志（`log_min_duration_statement=200`） | 统计接口的 `GROUP BY` 出现全表扫 |
| Redis | `redis-cli --latency`、`INFO commandstats` | `EVALSHA` 延迟随并发线性上升（说明 Lua 脚本里有热点） |
| 进程内存 | `docker stats` / `kubectl top` | 照片上传压测时 RSS 持续上涨不回落（zip/批量缓冲没释放） |
| 导出产物磁盘 | `du -sh App_Data/exports` | 压完不回收 → 保留期清理没在跑 |
| 日志 | `grep 429` | 出现 429 是**预期**（限流在工作），但要确认不是把正常批量上传误伤 |

### 12.4 已知的、压测一定会暴露的点（提前知道，别当成 bug）

1. **照片导出 zip 在内存里拼**（债 22 只做了一半）：并发导出会看到内存尖峰，
   这是已知项，修法是改成写临时文件后交给产物存储。
2. **缩略图队列会被"原图读不到"的记录堵住**（债 19 的衍生问题）：
   扫描方向已改为**新的优先**（本轮修复），但积压很多时老照片仍可能长期没有缩略图 ——
   列表页会回落到加载原图，因此压测时"照片列表接口的响应体明显变大"是**预期内**的。
3. **单人限流很紧**（照片上传 5 次/秒/用户、导出提交 1 次/10 秒/用户）：
   压测脚本如果用同一个账号打满，会被 429 挡住 —— 那不是性能问题，
   要测吞吐应该**用多个账号**（每个账号一套令牌）。
4. **统计接口的分组上限是 200**：`isTruncated` 为 true 时结果是被截断的，
   拿它做"总人数"校验会对不上，压测断言里别这么用。

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

它还**顺带核对 `deploy/k8s/`**（同一个脚本的第 8 节）：K8s 清单是否供齐了同一批配置键、
PVC 与探针是否在、Ingress 有没有把未知路径交给 web（深链回落的前半段）、
compose 与 K8s 里那份初始化 SQL 有没有漂移、Secret 示例里有没有真实口令。
⚠ 它只能防"改名/漏配"这类手写错误，**防不了"集群上跑不起来"** ——
后者只能靠人工在真实集群上按 [`k8s/README.md`](k8s/README.md) 第 7 节的清单走一遍。
