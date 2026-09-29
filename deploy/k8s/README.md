# Kubernetes 编排清单（`deploy/k8s/`）

> ⚠ **先说最重要的一件事：这些清单从来没有在真实集群上应用过。**
>
> - 目前做过的核对只有两类：YAML 能被解析（`js-yaml` 逐文件 `loadAll`），
>   以及 `node tools/verify-deploy.mjs` 的静态检查（配置键是否齐全、
>   卷与探针是否存在、口令是不是字面量…）；
> - **没有**跑过 `kubectl apply --dry-run=server`（本机没有集群，也没有 kubectl），
>   **没有**在任何单节点 k3s / kind / minikube 上启动过一次。
> - 因此 [ROADMAP 第 14 条](../../ROADMAP.md)的验收标准
>   「**在任意单节点 K8s 上完整部署并跑通冒烟**」**仍未完成**。
>   本目录连同这份文档，是把 `deploy/README.md` 第 11 章的要落成"可照抄的清单 + 可执行的验收清单"，
>   而不是"上 K8s 这件事已经做完了"。
>
> 这个仓库的约定是：**没验证过的事就说没验证过**。所以下面第 7 节的验收清单
> **每一项都还没打勾**；第一次真正 apply 时，请把实际结果（包括失败与报错原文）
> 回填进去，而不是把复选框直接勾上。

---

## 0. 目录里有什么

| 文件 | 内容 | 需要替换的值 |
|---|---|---|
| `namespace.yaml` | 命名空间 `xjgl` | 无（改名则处处要改） |
| `configmap.yaml` | 非敏感配置（对外地址、Redis 地址、照片/字体/导出目录…） | **`App__SelfUrl` / `App__ClientUrl` / `App__CorsOrigins` / `AuthServer__Authority` / 两个 `RootUrl` 共 6 处域名** |
| `secret.example.yaml` | 口令的**示例**（`POSTGRES_PASSWORD` / `REDIS_PASSWORD`） | 全部（见第 4 节；**不要直接 apply**） |
| `pvc.yaml` | 4 个 PVC：导出产物、降级照片、DataProtection 密钥环、NAS 冒烟替身 | `storageClassName`（默认 `local-path`，k3s 自带） |
| `postgres.yaml` | 冒烟用 PostgreSQL（StatefulSet + Service + 初始化 SQL 的 ConfigMap） | `storageClassName` |
| `redis.yaml` | 冒烟用 Redis（Deployment + Service + PVC） | `storageClassName` |
| `migrate-job.yaml` | **单次执行**的迁移 + 种子（Job，`backoffLimit: 0`） | 无 |
| `api-deployment.yaml` / `api-service.yaml` | API（2 副本 + 卷）与 Service `api` | 镜像名、`/mnt/nas/xjgl`、`/opt/xjgl/fonts/rare` |
| `web-deployment.yaml` / `web-service.yaml` | PC 端静态资源（nginx）与 Service `web` | 镜像名 |
| `ingress.yaml` | 外部入口（深链回落靠它 + web 里的 nginx.conf 两段配合） | **`ingressClassName`、`host`** |

服务名 `api` 与 `web` **不能随便改**：`deploy/nginx.conf` 里写的是 `proxy_pass http://api:8080`，
靠的就是这两个名字在同命名空间下的 DNS 解析（详见 `api-service.yaml` 的注释）。

---

## 1. 与 `docker-compose.prod.yml` 的关系

两者是**平行的两套部署方式**，配置项刻意一一对应（同一个配置键、同一个容器内路径）：

| 配置项 | compose | K8s |
|---|---|---|
| 环境变量来源 | `deploy/.env` + `environment:` | `configmap.yaml` + `secretKeyRef` |
| 口令 | `${POSTGRES_PASSWORD}` 插值 | Secret + `$(POSTGRES_PASSWORD)` 展开 |
| 生僻字字体 | `deploy/fonts/rare` → `/app/fonts/rare`（只读） | 节点 `/opt/xjgl/fonts/rare` → `/app/fonts/rare`（只读） |
| 照片主存储 | NAS 同路径绑入 | hostPath 同路径绑入（或冒烟替身 PVC） |
| 降级照片 | 命名卷 `xjgl-photofallback` | PVC `xjgl-photo-fallback` |
| 导出产物 | 容器本地目录 | PVC `xjgl-exports`（**多副本必须共享**，见第 6 节） |
| 迁移 | `docker compose run --rm migrate` | Job `xjgl-migrate` |
| 健康检查 | `healthcheck: curl /health` | `readinessProbe` / `livenessProbe` → `/health` |

**差异（不是"等价迁移"，选型时要看清）**：

1. **多副本**：compose 是单实例，K8s 清单默认 `replicas: 2`。
   多副本会让四处能力打折，也会把"必须共享的状态"暴露出来 ——
   这就是第 6 节存在的理由，也是 `deploy/README.md` 第 11 章的一整章内容。
2. **降级照片区的语义变了**：compose 的命名卷只有一个消费者，而共享 PVC 有 N 个（见 `pvc.yaml` 的注释）。
3. **入口能力**：上传体积上限、长请求超时、深链回落这三件事，compose 里全在 `nginx.conf`；
   在 K8s 里"上传体积/超时"要看入口控制器（ingress-nginx 注解或 Traefik Middleware），
   而"深链回落"仍然只能由 web 容器里的那份 nginx.conf 做（Ingress 没有 `try_files`）。

---

## 2. 必须替换的值

| # | 位置 | 现在的值 | 换成 | 不换会怎样 |
|---|---|---|---|---|
| 1 | `configmap.yaml`（6 处） | `https://xjgl.example.edu.cn` | **浏览器实际访问的地址** | 登录成功后所有接口 401/403（令牌签发者与 Authority 不是一个字符串） |
| 2 | `ingress.yaml` | `host: xjgl.example.edu.cn` | 同上，必须一致 | 同上；且 Ingress 匹配不到 Host 会 404 |
| 3 | `ingress.yaml` | `ingressClassName: nginx` | 集群里实际存在的类（k3s 默认是 `traefik`） | Ingress 不会被任何控制器接管，外部访问 404 |
| 4 | `secret`（用命令创建） | `请改成强口令` | 强口令 | 等于没设口令 |
| 5 | `pvc.yaml`、`postgres.yaml`、`redis.yaml` | `storageClassName: local-path` | 现场实际存在的存储类 | PVC 一直 Pending（`kubectl get pvc` 会明显显示） |
| 6 | `*.yaml` 里的 `image: xjgl/*:latest` | 本地构建的标签 | 你们 registry 里的地址（若不走本地导入） | 保持 `imagePullPolicy: IfNotPresent` 时不会去拉；换成 `Always` 则 ImagePullBackOff |
| 7 | `api-deployment.yaml` | `/mnt/nas/xjgl`（hostPath） | 节点上真实的 NAS 挂载点 | hostPath `type: Directory` 会让 Pod 起不来（**刻意如此**，见第 9 节） |
| 8 | `api-deployment.yaml` | `/opt/xjgl/fonts/rare`（hostPath） | 你们放字体的目录 | 同上；目录为空也不报错，只是生僻字打印时会明确失败（见 [`../fonts.md`](../fonts.md)） |
| 9 | `api/web` 的 `resources` | 拍的 | 压测后按实测调 | 给低了会被 CPU 限流或 OOMKilled（`deploy/README.md` 第 12 章） |

---

## 3. 先决条件（单节点 k3s 冒烟）

**3.1 节点上的目录必须先存在**（hostPath 用了 `type: Directory`）：

```bash
sudo mkdir -p /mnt/nas/xjgl          # NAS 挂载点（或改用 pvc.yaml 的 xjgl-photo-nas，见下）
sudo mkdir -p /opt/xjgl/fonts/rare   # 生僻字字体目录（内容从哪来见 ../fonts.md；空目录也能起）
```

> 冒烟时手上没有 NAS：把 `api-deployment.yaml` 里 `nas` 这个卷从
> `hostPath: {...}` 换成 `persistentVolumeClaim: { claimName: xjgl-photo-nas }`
> （该 PVC 在 `pvc.yaml` 里）。照片会落在集群盘上 —— **语义与生产不同**，只用于冒烟。

**3.2 镜像怎么进到集群里**（k3s 默认用 containerd，不共享 docker 的镜像库）：

```bash
# 在仓库根目录构建（见 deploy/README.md 第 3 章）
docker build -f deploy/api.Dockerfile -t xjgl/api:latest .
docker build -f deploy/web.Dockerfile -t xjgl/web:latest .

# 导入 k3s 的 containerd
docker save xjgl/api:latest -o /tmp/xjgl-api.tar
docker save xjgl/web:latest -o /tmp/xjgl-web.tar
sudo k3s ctr images import /tmp/xjgl-api.tar
sudo k3s ctr images import /tmp/xjgl-web.tar
sudo k3s ctr images ls | grep xjgl        # 期望看到两行
```

> 这也是为什么清单里 `imagePullPolicy: IfNotPresent`：`xjgl/*:latest` 不在任何 registry 上，
> 用默认的 `Always` 会直接 ImagePullBackOff。

**3.3 HTTPS 与域名（这一步最容易把冒烟卡住）**

生产 `AuthServer__RequireHttpsMetadata: "true"`，**必须 HTTPS**。冒烟只有两种正经做法：

- 有证书：在 `configmap.yaml` 里把 6 处地址设成 `https://<你的域名>`，
  并按 `ingress.yaml` 注释里的提示加 `tls:` 段；
- 没证书、只想看链路通不通：把 6 处地址设成 `http://<你的域名>`，**同时**把
  `AuthServer__RequireHttpsMetadata` 临时改成 `"false"`。
  ⚠ 这是安全性上的降级（令牌在明文链路上走），冒烟完必须改回来。

**3.4 不要用 `port-forward` 去做"绕过域名"的冒烟**

`App__SelfUrl` 是令牌的**签发者**，而浏览器访问的地址（`Host` 头）必须与它一致。
用 `kubectl port-forward` 把服务映射到 `127.0.0.1:8081` 之后，浏览器的地址是
`http://127.0.0.1:8081` —— 除非把 `App__SelfUrl` 也改成这个值，
否则登录会以"令牌校验失败"收场（症状和"密码错了"完全不同，很容易查错方向）。

**3.5 入口控制器**

```bash
kubectl get ingressclass
```

- 是 `nginx`（ingress-nginx）→ 兜里那份 `ingress.yaml` 可直接用，注解也生效；
- 是 `traefik`（k3s 默认）→ 把 `ingressClassName` 改成 `traefik`，
  并把 `nginx.ingress.kubernetes.io/*` 注解换成 `ingress.yaml` 末尾注释里的 Middleware。
  否则"上传体积 210m / 长请求 300s"这两条**不会生效**：
  表现就是大文件上传被 413、导出 zip 必超时。

---

## 4. 创建 Secret（口令不要落在任何文件里）

```bash
kubectl -n xjgl create secret generic xjgl-secret \
  --from-literal=POSTGRES_PASSWORD='<换成强口令>' \
  --from-literal=REDIS_PASSWORD='<换成强口令>'
```

改口令时用 `kubectl -n xjgl create secret generic xjgl-secret ... --dry-run=client -o yaml | kubectl apply -f -`，
改完记得**滚动重启** api（环境变量只在容器启动时读一次）：`kubectl -n xjgl rollout restart deployment/api`。

**为什么 Secret 里是 `POSTGRES_PASSWORD` / `REDIS_PASSWORD`，而应用要的是
`ConnectionStrings__Default` / `Redis__Password`**：这与 compose 里 `${POSTGRES_PASSWORD}`
插值是同一个目的 —— **口令只写一处**。连接串由 `api-deployment.yaml` 里的
`$(POSTGRES_PASSWORD)` 展开拼出来，Redis 口令用 `secretKeyRef` 映射成 `Redis__Password`。
如果反过来把整个连接串放进 Secret，同一份口令就会出现两处，改口令漏改一处时
症状是"连接失败/认证失败"，看起来完全不像"漏改了一个字符串"。

> ⚠ `secret.example.yaml` **只是给人看的模板**，值全是占位符。
> `kubectl apply -f deploy/k8s/` 会把整个目录（包括这个示例）都 apply 进去，
> 用占位符口令建出一个"看起来存在、其实用不了"的 Secret ——
> **请按第 5 节的顺序逐个 apply，不要整目录 apply**（迁移 Job 也要求"数据库先就绪"这个顺序）。
> 万一真的 apply 了，`kubectl -n xjgl delete secret xjgl-secret` 后按上面的命令重建。
>
> 自己复制出 `deploy/k8s/secret.yaml`（真实口令版本）时不要提交 ——
> 它已经在 `.gitignore` 里。Secret 默认只是 base64，不是加密：生产要开 etcd 加密与 RBAC。

---

## 5. 冒烟步骤（单节点 k3s）

顺序不能颠倒：**命名空间 → Secret → 配置与卷 → 数据库/Redis → 迁移 Job → 应用 → 入口**。
（整目录 apply 会在数据库没就绪时就把迁移 Job 跑起来，而它 `backoffLimit: 0`，失败即停。）

```bash
kubectl apply -f deploy/k8s/namespace.yaml
# Secret 见第 4 节
kubectl apply -f deploy/k8s/configmap.yaml
kubectl apply -f deploy/k8s/pvc.yaml

kubectl apply -f deploy/k8s/postgres.yaml
kubectl apply -f deploy/k8s/redis.yaml
kubectl -n xjgl rollout status statefulset/postgres --timeout=10m
kubectl -n xjgl rollout status deployment/redis --timeout=5m

# 迁移 + 种子：**必须单次执行**，等它 complete 再继续（多副本并发迁移会互相踩）
kubectl apply -f deploy/k8s/migrate-job.yaml
kubectl -n xjgl wait --for=condition=complete job/xjgl-migrate --timeout=15m
kubectl -n xjgl logs -l job-name=xjgl-migrate --tail=100

kubectl apply -f deploy/k8s/api-deployment.yaml
kubectl apply -f deploy/k8s/api-service.yaml
kubectl apply -f deploy/k8s/web-deployment.yaml
kubectl apply -f deploy/k8s/web-service.yaml
kubectl apply -f deploy/k8s/ingress.yaml

kubectl -n xjgl rollout status deployment/api --timeout=10m
kubectl -n xjgl rollout status deployment/web --timeout=5m
kubectl -n xjgl get pods -o wide
```

冒烟（把域名指到节点上，参考第 3.4 节 —— 不要用 port-forward 绕过域名）：

```bash
# 本机 /etc/hosts 加一行，例如： 192.168.1.10  xjgl.example.edu.cn
curl -s http://xjgl.example.edu.cn/health
# 期望：{"status":"ok","database":"up","time":"..."}   503 则说明数据库不可达

curl -s -o /dev/null -w '%{http_code}\n' http://xjgl.example.edu.cn/student/list
# 期望：200（这是**深链回落**：/student/list 不是文件，交给 index.html，由前端路由接手；
#       404 说明 web 镜像里的 nginx.conf 没生效，或 Ingress 的 catch-all 没指到 web）
```

**首次登录**：默认管理员 `admin` / `1q2w3E*` —— 立刻改口令，并见仓库根目录 `SECURITY.md`。

---

## 6. 两条必须共享、不能"每副本一份"的状态（多副本的硬约束）

> 这一节是 `deploy/README.md` 第 11.2 节的落地版。它**不是 boilerplate**：
> 这两条各自对应一个"看起来像别的问题"的故障。

### 6.1 DataProtection 密钥环

- 代码把密钥环从默认的 `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`
  改到了**内容根下的 `App_Data/DataProtection-Keys`**
  （`XjglHttpApiHostModule.ConfigureDataProtection`），容器里就是 `/app/api/App_Data/DataProtection-Keys`。
- 每个副本各写一份时，密钥不同 → **在 A 副本登录、请求落到 B 副本就 401**。
- 本清单把 PVC `xjgl-dataprotection-keys` 挂在两个副本的同一路径上。
  **单节点** k3s 上这样就够了（`ReadWriteOnce` 的语义是"单节点"，不是"单 Pod"）；
  **多节点**必须 RWX 卷，或改用 `PersistKeysToStackExchangeRedis`
  （代码里留了这条路，当前**未启用**）。
- ⚠ **不要**用 `sessionAffinity: ClientIP` 去"解决"这个 401：粘性会话只会掩盖配置错误，
  并且在滚动更新/扩缩容时再次炸出来（`deploy/README.md` 第 11.2 节第 1 行就是这么写的）。
- 密钥环是敏感材料：能读这个卷的人可以伪造令牌，别和导出产物共用一个"谁都挂得上"的卷。

### 6.2 导出/打印产物

- 产物写在容器本地磁盘，任务行里存的是**相对路径**（`IExportArtifactStore` 的实现）。
- 多副本不共享时：**任务在 A 生成、下载请求打到 B → 404**。
  这一条在单节点上用共享 PVC 可以绕过（所有副本看到同一份文件），
  多节点必须 RWX 共享卷，或把 `IExportArtifactStore` 换成对象存储实现
  （接口就是为这件事留的）。
- 验证方法见验收清单第 6 条：**故意反复刷新下载**，看它有没有被打到不同副本上。

---

## 7. 验收清单（**每一项都还没打勾**）

前置：`kubectl -n xjgl get pods` 全部 Running / Ready。

| # | 要验证什么 | 怎么做 | 期望 | 不符意味着什么 |
|---|---|---|---|---|
| 0 | `$(VAR)` 展开真的生效（本清单唯一的 K8s 魔法） | `kubectl -n xjgl exec deploy/api -- printenv ConnectionStrings__Default` | 连接串里 `Password=` 是**真实口令**，不是字面量 `$(POSTGRES_PASSWORD)` | 集群不接受 env value 里的 `$(VAR)`（本仓库未验证过该行为）：改用 Secret 里的一整个连接串键（第 4 节的说明） |
| 1 | 配置真的被读到 | `kubectl -n xjgl logs deploy/api \| grep -E '内容根\|没有 appsettings.json'` | 内容根是 `/app/api`，且**没有**"内容根下没有 appsettings.json"告警 | 配置没挂上：应用会用代码默认值安静地跑（照片路径、字体、学籍号规则一起换值） |
| 2 | Redis 真的在用（不是内存兜底） | `kubectl -n xjgl logs deploy/api \| grep '缓存主实现不可用'` + `kubectl -n xjgl exec deploy/redis -- sh -c 'redis-cli -a "$REDIS_PASSWORD" --scan --pattern "ratelimit:*"'`（`$REDIS_PASSWORD` 必须让**容器里的 sh** 展开，不能由你本地的 shell 展开） | 日志里**没有**该告警；限流键存在 | 静默降级成单机缓存：缓存与限流都不再跨副本共享（配置错了不会报错，见 `deploy/README.md` 第 6 章） |
| 3 | 健康检查语义 | `curl -H 'Host: <域名>' http://<节点IP>/health`；再 `kubectl -n xjgl scale statefulset/postgres --replicas=0`（验完记得 scale 回 1） | 前者 `database=up` / 200；后者变 503 而**不是 500**（探针必须挂在管道最前面） | 拿到 500/一页异常堆栈：探针走到了 ABP 的中间件之后，容器与负载均衡据此判断是否放流量，会做出错误决定 |
| 4 | 登录链路 + 令牌 | 浏览器打开 `http://<域名>/`，`admin` / `1q2w3E*` 登录 | 进入工作台；`/connect/token` 返回 `access_token` | 见第 2 节第 1 行（`App__SelfUrl` / `Authority` / `ClientUrl` 不一致） |
| 5 | **密钥环共享**（多副本最现实的坑之一） | 连续多次 `kubectl -n xjgl delete pod -l app=xjgl-api`（每次只删一个）后，在浏览器里**保持会话刷新页面**；或删掉"当前那个副本"的 Pod | 会话不丢，不出现 401 | 401 = 密钥环没共享（或多节点上 RWO 卷只挂到了一个节点） |
| 6 | **导出产物共享**（最现实的坑，`README §11.2`） | 提交一次导出/打印任务，然后**反复刷新下载**（会被负载均衡到不同副本），至少 10 次 | 每次都下到文件（`%PDF-` / zip 完整） | 间歇性 404 = 产物没共享，或多节点上卷只挂到一个节点 |
| 7 | 限流桶跨副本共享 | `kubectl -n xjgl exec deploy/redis -- sh -c 'redis-cli -a "$REDIS_PASSWORD" --scan --pattern "ratelimit:*"' \| head`；再从两个不同副本各打几次接口 | 键是同一批、计数一起涨 | 各副本各算一套 → 限流精度约为 N 倍（多副本的已知打折项） |
| 8 | 逐个 kill 副本不抖 | 一边 `while true; do curl -s -o /dev/null -w '%{http_code}\n' -H 'Host: <域名>' http://<节点IP>/health; sleep 1; done`，一边逐个删 api Pod | 不应出现 200/503 抖动（滚动期间 `maxUnavailable: 0`） | 抖动 = 探针/滚动策略没起作用，或副本数不足（把 replicas 调回 2 再看） |
| 9 | 生僻字打印（字体目录真的生效） | 按 [`../fonts.md`](../fonts.md) 的"怎么验证生效"四步做（含**空目录反向对照**） | 装了字体：能打出含 `𠮷` 的学籍卡且 PDF 里嵌入了该字体；没装：接口**明确报错**而不是出方块 | 出方块 = 违背设计（`ThrowOnMissingTextGlyphs` 没生效）；报"缺少可用中文字体" = 字体目录/家族名没对上 |
| 10 | 深链回落 | 在浏览器里直接刷新 `/student/list`、`/photo/list`、`/system/org`（不是点菜单） | 页面正常 | 404 = web 里的 `nginx.conf` 没生效，或 Ingress 的 catch-all 没指到 web（见 `ingress.yaml` 注释） |
| 11 | 大文件上传不被入口掐掉 | 用前端「批量上传」传一个接近 200MB 的文件，或直接对 `/api/photos/upload` 发一个同样大小的请求 | 不出现 413 | 413 = 入口的 body-size 没配（ingress-nginx 注解 / Traefik Middleware） |
| 12 | 迁移只跑了一次 | `kubectl -n xjgl get job xjgl-migrate -o jsonpath='{.status.succeeded}'` | `1` | 大于 1 或不稳定：迁移被并发执行了（多副本场景，见 `migrate-job.yaml` 注释） |
| 13 | 降级照片区（**行为变更项**） | 把 NAS 目录改成不可写（或在冒烟环境里把 `Xjgl__Photo__NasRoot` 指向一个只读路径），上传一张照片，看两副本日志 | 照片落到 `/data/photo-fallback`，日志出现「照片补偿」相关记录，NAS 恢复后回放成功 | 若两副本同时回放同一条记录、日志刷「降级副本缺失」：说明**共享降级区与 §11.2 的口径不同**，多节点应改为每副本独立卷 |
| 14 | 压测（可选，但决定 replicas） | 按 `deploy/README.md` 第 12 章的 k6 脚本 | 见第 12 章的"不合格线" | 别在没有基线的状态下加副本：多副本会同时放大已知的四个打折项 |

> 第 3、5、6、7、13 条是本目录里**唯一能证明"多副本真的配对了"**的证据。
> 只看到 `kubectl get pods` 全绿是不够的：本系统的 Redis 故障、配置漏挂、
> 字体缺失**都不会让 Pod 不健康**（这是刻意的设计），必须按上面的方法主动验。

---

## 8. 已知取舍与"尚未验证"的点（写清单时就想清楚的）

1. **`$(VAR)` 在 env value 里的展开**（`api-deployment.yaml` / `migrate-job.yaml`）：
   这是 kubelet 的"依赖环境变量"行为，本仓库**没有在真实集群上验证过**。
   验收清单第 0 条就是专门验它的。不接受时的退路见第 4 节末尾。
2. **liveness 也探 `/health`**：`deploy/README.md` 第 11.3 节要求探针挂在管道最前面，
   于是数据库不可用会把 Pod 重启（重启治不了数据库）。若现场更想"只摘流量、不重启"，
   把 `livenessProbe` 换成 `tcpSocket: { port: http }` —— 清单把取舍写出来，不替你决定。
3. **Traefik 的注解不等于 ingress-nginx 的注解**：k3s 默认入口是 Traefik，
   直接 apply 那份带 nginx 注解的 Ingress，`proxy-body-size` / 超时**都不生效**，
   而症状（413、长请求超时）看起来像后端 bug。`ingress.yaml` 末尾给了 Middleware 的写法，
   但那段同样**没有在真实集群上验证过**。
4. **共享降级照片区是行为变更**：`deploy/README.md` 第 11.2 节写的是"降级区每副本各自一份"，
   compose 的命名卷只有一个消费者所以看不出来；K8s 上共享一个 PVC 会让多个副本
   对同一批 `FallbackPending` 记录做回放（幂等性没有实测过）。见 `pvc.yaml` 的注释。
5. **k3s 默认存储 `local-path` 只有 RWO**：单节点 + 2 副本没问题，
   **多节点**必须换 RWX 存储类（NFS/CephFS）或对象存储，否则新副本会 Pending。
6. **所有 `resources` 都是拍的**：没有压测基线（`deploy/README.md` 第 12 章）。
7. **刻意没有加的东西**：HPA（没有基线可依据）、PDB（2 副本 + `maxUnavailable: 0`
   已经够用，且单节点上 PDB 可能挡住驱逐）、NetworkPolicy（会挡住 NAS/数据库，
   需要按现场网络写）、Pod 反亲和（单节点上会把第二个副本顶成 Pending）。
8. **`postgres.yaml` / `redis.yaml` 只适合冒烟**：单副本、无备份、无高可用。
   生产请换成托管服务，并把 `configmap.yaml` 里的地址指过去。
9. **静态核对会被 `tools/verify-deploy.mjs` 继续守着**：它现在也会检查
   `deploy/k8s/`（配置键是否齐全、PVC 与探针是否存在、入口是否把未知路径交给 web、
   初始化 SQL 的两处副本是否漂移、Secret 示例里有没有真实口令）。
   **它能防"改名/漏配"这类手写错误，防不了"集群上跑不起来"** —— 后者只能靠第 7 节。

---

## 9. 建议的回填方式

第一次在真实集群上跑完之后，请在本文件里：

1. 把第 7 节表格里的每一栏补上**实测结果**（通过的写"✅ 实测通过（k3s v1.xx，日期）"，
   失败的把**报错原文**贴进来，不要只写"失败"）；
2. 把第 8 节里"未验证"的条目改成实测结论（包括"验证后确认不成立"）；
3. 补一节"踩坑记录"（这个仓库的文档风格：把踩过的坑与**为什么**写下来，
   比一份看起来完整的清单有用得多）。

只有第 7 节 15 项都拿到实测证据之后，ROADMAP 第 14 条的验收标准
（"在任意单节点 K8s 上完整部署并跑通冒烟"）才算真正完成。
