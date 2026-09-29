#!/usr/bin/env node
// 生产部署编排的一致性核对
// ---------------------------------------------------------------------------
// 为什么需要它：
//   部署文件的错误有个共同特征 —— **本机跑得好好的，上线才炸**：
//     - compose 里的环境变量名写错一个字母（`Xjgl__Print__DefaultFontFamilly`），
//       容器照样起来，只是打印功能在运行期报"缺少中文字体"；
//     - `.env.example` 少写一个变量，`${VAR:?}` 直接让 compose 起不来；
//     - nginx 少配 `client_max_body_size`，大文件上传在 nginx 层被 413 拒掉
//       （接口层的 200MB 限制反而成了摆设）。
//   这些都不是"代码 bug"，代码覆盖率再高也测不到，因此单独用一个脚本守。
//
// 检查方式：以 `src/Xjgl.HttpApi.Host/appsettings.json` 为**唯一事实来源**，
// 反推生产 compose 必须提供的环境变量（`A:B` → `A__B`），逐个核对。
//
// 用法：node tools/verify-deploy.mjs
// ---------------------------------------------------------------------------

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)))

const problems = []
const notes = []

function read(relativePath) {
  const full = path.join(repoRoot, relativePath)

  if (!fs.existsSync(full)) {
    problems.push(`缺少文件：${relativePath}`)

    return ''
  }

  return fs.readFileSync(full, 'utf8')
}

function check(condition, message) {
  if (!condition) {
    problems.push(message)
  }
}

// ---------------------------------------------------------------------------
// 0) 必需文件
// ---------------------------------------------------------------------------
const compose = read('deploy/docker-compose.prod.yml')
const apiDockerfile = read('deploy/api.Dockerfile')
const webDockerfile = read('deploy/web.Dockerfile')
const nginx = read('deploy/nginx.conf')
const envExample = read('deploy/.env.example')
const readme = read('deploy/README.md')
const appSettings = read('src/Xjgl.HttpApi.Host/appsettings.json')

// ---------------------------------------------------------------------------
// 1) appsettings 是事实来源：列出生产必须覆盖的配置键
// ---------------------------------------------------------------------------
// 说明：这里列的是"生产环境**必须**与开发不同"的键。
// 其余键（SqlSugar、Serilog、熔断参数…）有可用的默认值，允许不覆盖。
const REQUIRED_KEYS = [
  'ConnectionStrings:Default',
  'Redis:Configuration',
  'App:SelfUrl',
  'App:ClientUrl',
  'App:CorsOrigins',
  'AuthServer:Authority',
  'OpenIddict:Applications:Xjgl_App:RootUrl',
  'Xjgl:Print:DefaultFontFamily',
  'Xjgl:Print:RareCharacterFontDirectory',
  'Xjgl:Photo:NasRoot',
  'Xjgl:Photo:FallbackRoot'
]

// 1a) 这些键必须真的还存在于 appsettings（改了名就说明部署文件要跟着改）
if (appSettings) {
  const parsed = JSON.parse(appSettings)

  for (const key of REQUIRED_KEYS) {
    const segments = key.split(':')
    let cursor = parsed
    let found = true

    for (const segment of segments) {
      if (cursor && typeof cursor === 'object' && segment in cursor) {
        cursor = cursor[segment]
      } else {
        found = false
        break
      }
    }

    check(found, `appsettings.json 里找不到配置键 ${key}（部署脚本的核对清单需要同步更新）`)
  }
}

// 1b) compose 的 api 服务必须逐个提供（约定：配置键 A:B → 环境变量 A__B）
const apiBlock = compose.split(/^\s{2}api:/m)[1]?.split(/^\s{2}\w[\w-]*:/m)[0] ?? ''

check(apiBlock.length > 0, 'compose 里找不到 api 服务')

for (const key of REQUIRED_KEYS) {
  const envName = key.replaceAll(':', '__')

  check(
    apiBlock.includes(`${envName}:`) || apiBlock.includes(`${envName} `) || apiBlock.includes(`${envName}\n`),
    `api 服务缺少环境变量 ${envName}（对应配置键 ${key}）—— 少一个就会退回开发默认值`
  )
}

// ---------------------------------------------------------------------------
// 2) 镜像：字体与运行期依赖
// ---------------------------------------------------------------------------
check(
  apiDockerfile.includes('fonts-noto-cjk'),
  'api.Dockerfile 必须安装中文字体（fonts-noto-cjk）：学籍卡/花名册 PDF 要靠它画中文'
)

check(
  apiDockerfile.includes('curl'),
  'api.Dockerfile 必须装 curl：compose 里 api 的 healthcheck 用它探测 /health'
)

check(
  /dotnet publish\s+src\/Xjgl\.HttpApi\.Host/.test(apiDockerfile),
  'api.Dockerfile 没有发布 Xjgl.HttpApi.Host'
)

check(
  /dotnet publish\s+src\/Xjgl\.DbMigrator/.test(apiDockerfile),
  'api.Dockerfile 没有发布 Xjgl.DbMigrator：生产上初始化数据库应当用同一个镜像跑迁移'
)

check(
  /ENTRYPOINT\s*\[\s*"dotnet"\s*\]/.test(apiDockerfile),
  'api.Dockerfile 的 ENTRYPOINT 必须是 ["dotnet"]，否则 compose 里的 migrate 服务无法用 command 覆盖启动参数'
)

check(
  webDockerfile.includes('--frozen-lockfile'),
  'web.Dockerfile 必须用 --frozen-lockfile 安装依赖：否则镜像不可复现'
)

check(
  webDockerfile.includes('/usr/share/nginx/html') && webDockerfile.includes('nginx.conf'),
  'web.Dockerfile 要把构建产物与 nginx 配置放进 nginx 镜像'
)

// ---------------------------------------------------------------------------
// 3) nginx：深链回落 / 上传体积 / 反代
// ---------------------------------------------------------------------------
check(
  /try_files\s+\$uri\s+\$uri\/\s+\/index\.html/.test(nginx),
  'nginx 缺少 history 路由回落（try_files ... /index.html）：刷新深链会 404'
)

const sizeMatch = nginx.match(/client_max_body_size\s+(\d+)m/)
check(
  sizeMatch && Number(sizeMatch[1]) >= 200,
  'nginx 的 client_max_body_size 必须 >= 200m（与 PhotosController 的上传上限对齐），否则大文件在 nginx 层被 413'
)

check(
  /location\s+~?\s*\^?\/?\(?api/.test(nginx) && nginx.includes('http://api:8080'),
  'nginx 必须把 /api 反代到 api:8080'
)

check(
  nginx.includes('/connect'),
  'nginx 必须反代 /connect（登录令牌端点），否则前端登录会失败'
)

const timeoutMatch = nginx.match(/proxy_read_timeout\s+(\d+)s/)
check(
  timeoutMatch && Number(timeoutMatch[1]) >= 120,
  'nginx 的 proxy_read_timeout 太小：导出 zip 与打印花名册是同步生成，可能几十秒'
)

check(
  /Cache-Control\s+"no-store/.test(nginx),
  'index.html 必须禁止缓存（no-store）：否则发版后用户仍加载旧壳子、引用已删除的 chunk'
)

// ---------------------------------------------------------------------------
// 4) compose：健康检查、数据卷、profile
// ---------------------------------------------------------------------------
check(
  /depends_on:\s*\n\s+postgres:\s*\n\s+condition:\s*service_healthy/.test(compose),
  'api 必须 depends_on postgres 的 service_healthy：数据库没起来就启动会直接崩'
)

check(
  compose.includes('/health'),
  'api 的 healthcheck 必须探 /health（容器编排与负载均衡都依赖它）'
)

check(
  /xjgl-photofallback:/.test(compose),
  '降级照片目录必须是命名卷：NAS 熔断期间暂存的照片不能因为重建容器而丢'
)

check(
  /RARE_FONT_DIR[^\n]*:\/app\/fonts\/rare/.test(compose),
  'compose 必须把生僻字字体目录挂到 /app/fonts/rare（与 api.Dockerfile 的默认目录一致）'
)

check(
  compose.includes('profiles: ["mq"]'),
  'RabbitMQ 必须以 profile 形式提供：当前版本没有注册 MQ 模块，默认起来只会让人误解'
)

check(
  compose.includes('Outbox') || compose.includes('未注册'),
  'RabbitMQ 服务附近必须写明"当前未接入（需先做 Outbox）"的原因，避免运维以为它已经在工作'
)

// compose 里引用的文件必须真实存在（build context / dockerfile / 初始化脚本 / 字体目录）
const referencedFiles = [
  ...[...compose.matchAll(/dockerfile:\s*(\S+)/g)].map((m) => m[1]),
  ...[...compose.matchAll(/^\s*-\s+(\.\/\S+?):/gm)].map((m) => m[1]),
  ...[...webDockerfile.matchAll(/COPY\s+(deploy\/\S+)\s/g)].map((m) => m[1])
]

for (const file of new Set(referencedFiles)) {
  const target = file.startsWith('deploy/') ? file : `deploy/${file.replace(/^\.\//, '')}`

  check(fs.existsSync(path.join(repoRoot, target)), `部署文件里引用了不存在的文件：${target}`)
}

// ---------------------------------------------------------------------------
// 5) .env.example 必须覆盖 compose 里用到的每个变量
// ---------------------------------------------------------------------------
// 注意：要**先去掉注释行**再提取 —— compose 头部注释里就写了 `${VAR:?提示}` 这个用法说明，
// 不去注释会把 "VAR" 当成一个真的变量（本脚本第一版就是这么误报的）。
const composeCode = compose
  .split('\n')
  .filter((line) => !line.trimStart().startsWith('#'))
  .join('\n')

const referenced = new Set()

for (const match of composeCode.matchAll(/\$\{([A-Z0-9_]+)/g)) {
  referenced.add(match[1])
}

// 带默认值的（${VAR:-default}）可以不给，但为了可读性我们仍然要求列出
const missingInExample = [...referenced].filter((name) => !new RegExp(`^${name}=`, 'm').test(envExample))

check(
  missingInExample.length === 0,
  `.env.example 里缺少这些变量（compose 会用到，缺失时 ${'${VAR:?}'} 会直接让 compose 起不来）：${missingInExample.join('、')}`
)

// ---------------------------------------------------------------------------
// 6) 生产文件里不允许出现开发口令
// ---------------------------------------------------------------------------
// 口令必须来自 .env：这里只认"YAML 行首的键 + 字面量值"（`${...}` 是允许的）。
// 不能用 `POSTGRES_PASSWORD:\s*[^$\s]` 这种写法：它会误命中 `${POSTGRES_PASSWORD:?提示}` 里的
// "POSTGRES_PASSWORD:" + "?"（脚本第一版就误报过一次）。
const literalSecret = /^\s*(POSTGRES_PASSWORD|REDIS_PASSWORD|SEQ_ADMIN_PASSWORD|RABBITMQ_PASSWORD):\s*(?!\$\{)\S/m

check(
  !compose.includes('Xjgl@Dev2026'),
  '生产 compose 里出现了开发口令 Xjgl@Dev2026：所有口令都必须从 .env 注入'
)

check(
  !literalSecret.test(composeCode),
  'compose 里的数据库/Redis 口令必须是 ${...} 变量引用，不能写字面量'
)

// ---------------------------------------------------------------------------
// 7) 文档
// ---------------------------------------------------------------------------
check(
  readme.includes('fonts-noto-cjk') || readme.includes('生僻字'),
  'deploy/README.md 必须说明字体（中文字体随镜像、生僻字字体需另行放置）'
)

check(
  readme.includes('QuestPDF') || readme.includes('ImageSharp') || readme.includes('许可证'),
  'deploy/README.md 必须说明第三方库许可证（QuestPDF / ImageSharp 商用需自行购买）'
)

// ---------------------------------------------------------------------------
// 8) deploy/k8s/：Kubernetes 编排清单的**文本级**核对
// ---------------------------------------------------------------------------
// 为什么要核对它：compose 与 K8s 清单是**同一批配置键的两个消费者** ——
// "compose 改了、清单忘了改"不会有任何报错，只会在集群上以
// "登录成功但接口全 401""大文件上传 413""导出下载间歇 404"的形式暴露。
//
// ⚠ 必须说清楚这个检查的边界：它只证明**文本上没有漏配/改名/漂移**，
//   **证明不了清单能在集群上跑起来**。清单本身**从未在真实集群上应用过**
//   （ROADMAP 第 14 条的验收标准仍未完成），那件事只能由人在单节点 k3s 上
//   按 deploy/k8s/README.md 第 7 节的 14 项验收清单走一遍。
const k8sFileNames = [
  'namespace.yaml',
  'configmap.yaml',
  'secret.example.yaml',
  'pvc.yaml',
  'postgres.yaml',
  'redis.yaml',
  'migrate-job.yaml',
  'api-deployment.yaml',
  'api-service.yaml',
  'web-deployment.yaml',
  'web-service.yaml',
  'ingress.yaml',
  'README.md'
]

const k8s = {}

for (const name of k8sFileNames) {
  k8s[name] = read(`deploy/k8s/${name}`)
}

const k8sAll = Object.values(k8s).join('\n')

const configMap = k8s['configmap.yaml']
const secretExample = k8s['secret.example.yaml']
const pvc = k8s['pvc.yaml']
const apiDeployment = k8s['api-deployment.yaml']
const apiService = k8s['api-service.yaml']
const webService = k8s['web-service.yaml']
const ingress = k8s['ingress.yaml']
const postgresYaml = k8s['postgres.yaml']

/** 取 ConfigMap 里某个键的值（值一律带引号，取不到时返回 undefined）。 */
function configValue(key) {
  const match = configMap.match(new RegExp(`^\\s*${key}:\\s*"?([^"\\n]+?)"?\\s*$`, 'm'))

  return match?.[1]?.trim()
}

// 8a) 同一批"生产必须覆盖"的配置键，K8s 清单里也必须都供齐
//     （两种写法都认：ConfigMap 的 `KEY: value`，以及 env 的 `- name: KEY`）
for (const key of REQUIRED_KEYS) {
  const envName = key.replaceAll(':', '__')

  check(
    new RegExp(`^\\s*${envName}:`, 'm').test(k8sAll) || k8sAll.includes(`name: ${envName}`),
    `deploy/k8s/ 里没有为配置键 ${key} 提供环境变量 ${envName}（与 compose 是同一批键，漏一个就会退回开发默认值）`
  )
}

// 8b) 口令只能走 Secret：ConfigMap 是明文的（kubectl get cm -o yaml 就能看到）
//     注意要**先去掉注释行**再找：清单的注释里就会提到 `Redis__Password`
//     （"口令走 Secret 的 Redis__Password"）—— 这与第 5 节处理 `${VAR}` 是同一个坑。
const configMapCode = configMap
  .split('\n')
  .filter((line) => !line.trimStart().startsWith('#'))
  .join('\n')

for (const name of ['ConnectionStrings__Default', 'Redis__Password']) {
  check(
    !configMapCode.includes(name),
    `deploy/k8s/configmap.yaml 里出现了 ${name}：口令/连接串必须走 Secret，ConfigMap 是明文的`
  )
}

// 8c) Secret **示例**里不能出现真实口令（这个文件会进仓库）
check(
  !secretExample.includes('Xjgl@Dev2026'),
  'deploy/k8s/secret.example.yaml 里出现了开发口令 Xjgl@Dev2026'
)

const stringDataBlock = secretExample.split(/^stringData:\s*$/m)[1] ?? ''

for (const match of stringDataBlock.matchAll(/^\s+([A-Za-z_][A-Za-z0-9_]*):\s*(.+)$/gm)) {
  const [, key, value] = match

  check(
    /请改成|CHANGE_ME|占位/.test(value),
    `deploy/k8s/secret.example.yaml 的 ${key} 看起来不是占位符（示例文件会进仓库，绝不能放真实口令）`
  )
}

check(
  /POSTGRES_PASSWORD/.test(stringDataBlock) && /REDIS_PASSWORD/.test(stringDataBlock),
  'deploy/k8s/secret.example.yaml 必须给出 POSTGRES_PASSWORD 与 REDIS_PASSWORD 两个键（api 与 postgres/redis 都从这一处取口令）'
)

// 8d) 探针：必须探 /health（200 = 进程活着且数据库连得上，503 = 数据库连不上）
check(
  /readinessProbe:[\s\S]{0,200}?path:\s*\/*health/.test(apiDeployment),
  'api-deployment.yaml 的 readinessProbe 必须探 /health（编排与负载均衡据此决定放不放流量）'
)

check(
  /livenessProbe:[\s\S]{0,200}?path:\s*\/*health/.test(apiDeployment),
  'api-deployment.yaml 的 livenessProbe 必须探 /health'
)

// 8e) 需要持久化/共享的三个目录：PVC 存在 + api 真的挂上了 + 路径与配置键一致
check(/kind:\s*PersistentVolumeClaim/.test(pvc), 'pvc.yaml 里没有 PersistentVolumeClaim')

const requiredClaims = [
  ['xjgl-exports', '导出/打印产物', '多副本不共享时"任务在 A 生成、下载打到 B"就是 404'],
  ['xjgl-photo-fallback', '降级照片暂存区', 'NAS 熔断期间暂存的原始照片不能因为重建 Pod 而丢'],
  ['xjgl-dataprotection-keys', '数据保护密钥环', '不共享的表现是"在 A 副本登录、请求落到 B 副本就 401"']
]

for (const [claim, what, why] of requiredClaims) {
  check(pvc.includes(`name: ${claim}`), `pvc.yaml 里缺少 PVC ${claim}（${what}）：${why}`)
  check(
    apiDeployment.includes(`claimName: ${claim}`),
    `api-deployment.yaml 没有挂载 PVC ${claim}（${what}）：${why}`
  )
}

for (const mountPath of [
  '/app/api/App_Data/exports',
  '/data/photo-fallback',
  '/app/api/App_Data/DataProtection-Keys',
  '/app/fonts/rare',
  '/mnt/nas/xjgl'
]) {
  check(
    apiDeployment.includes(`mountPath: ${mountPath}`),
    `api-deployment.yaml 没有把卷挂到 ${mountPath}（容器内路径必须与配置键的值一致）`
  )
}

check(
  configValue('Xjgl__Photo__FallbackRoot') === '/data/photo-fallback',
  'configmap.yaml 的 Xjgl__Photo__FallbackRoot 必须与 api-deployment.yaml 的挂载点 /data/photo-fallback 一致'
)

check(
  configValue('Xjgl__Export__ArtifactRoot') === '/app/api/App_Data/exports',
  'configmap.yaml 的 Xjgl__Export__ArtifactRoot 必须与 api-deployment.yaml 的挂载点 /app/api/App_Data/exports 一致（否则产物写到"挂不住卷"的目录，容器一重建就丢）'
)

check(
  configValue('Xjgl__Print__RareCharacterFontDirectory') === '/app/fonts/rare',
  'configmap.yaml 的 Xjgl__Print__RareCharacterFontDirectory 必须与 api-deployment.yaml 的挂载点 /app/fonts/rare 一致'
)

check(
  configValue('Xjgl__Photo__NasRoot') === '/mnt/nas/xjgl',
  'configmap.yaml 的 Xjgl__Photo__NasRoot 必须与 api-deployment.yaml 的 NAS 挂载点 /mnt/nas/xjgl 一致（compose 的约定是"同路径进容器"，K8s 也照做）'
)

// 8f) 对外地址必须互相对得上：改了 App__SelfUrl 忘了 Authority 的经典错
//     症状是"登录成功但所有接口 401/403"，看起来像权限问题
const selfUrl = configValue('App__SelfUrl')
const clientUrl = configValue('App__ClientUrl')

check(!!selfUrl, 'configmap.yaml 里找不到 App__SelfUrl')
check(!!clientUrl, 'configmap.yaml 里找不到 App__ClientUrl')

check(
  !selfUrl || selfUrl === configValue('AuthServer__Authority'),
  `configmap.yaml 的 App__SelfUrl（${selfUrl}）与 AuthServer__Authority（${configValue('AuthServer__Authority')}）不一致：令牌签发者与校验用的 Authority 不是一个字符串，表现为"登录成功但接口全 401"`
)

check(
  !selfUrl || selfUrl === configValue('OpenIddict__Applications__Xjgl_Swagger__RootUrl'),
  'configmap.yaml 的 Swagger RootUrl 必须等于 App__SelfUrl'
)

check(
  !clientUrl || clientUrl === configValue('OpenIddict__Applications__Xjgl_App__RootUrl'),
  'configmap.yaml 的 Xjgl_App RootUrl 必须等于 App__ClientUrl（前端回调地址）'
)

// 8g) Service 名必须与 deploy/nginx.conf 的反代目标一致
check(
  /^  name: api$/m.test(apiService),
  'api Service 必须叫 api：deploy/nginx.conf 里写的是 proxy_pass http://api:8080，改名后前端会"静态页能开、所有接口 502"'
)

check(
  /^  name: web$/m.test(webService),
  'web Service 必须叫 web：Ingress 的 catch-all 指向它'
)

check(
  /port:\s*8080/.test(apiService),
  'api Service 必须暴露 8080（与 nginx.conf 的 proxy_pass 端口一致）'
)

check(/port:\s*80/.test(webService), 'web Service 必须暴露 80（web 镜像里 nginx 监听 80）')

// 8h) Ingress：深链回落的**前半段**（后半段在 web 容器里的 nginx.conf）
check(/kind:\s*Ingress/.test(ingress), 'ingress.yaml 里没有 Ingress')

check(
  /-\s*path:\s*\/\s*\n\s*pathType:\s*Prefix[\s\S]{0,200}?name:\s*web/.test(ingress),
  'Ingress 必须有 catch-all（path: / → Service web）：前端是 history 路由，未知路径必须落到 web，再由它容器里的 nginx.conf 做 try_files → index.html；缺了它"点菜单正常、刷新深链 404"'
)

check(
  /path:\s*\/api\b[\s\S]{0,200}?name:\s*api/.test(ingress),
  'Ingress 必须把 /api 转发给 Service api'
)

check(
  /path:\s*\/connect\b/.test(ingress),
  'Ingress 必须转发 /connect（登录令牌端点），否则前端登录会失败'
)

check(
  /ingressClassName:/.test(ingress),
  'Ingress 必须显式写 ingressClassName（k3s 默认入口是 traefik，写成 nginx 或不写都可能没有任何控制器接管它）'
)

check(
  /proxy-body-size/.test(ingress),
  'Ingress 必须放行大请求体（与 nginx.conf 的 client_max_body_size 210m 对齐），否则照片批量上传在入口被 413'
)

check(
  /proxy-read-timeout/.test(ingress),
  'Ingress 必须有长请求超时（导出 zip / 花名册是同步生成，可能几十秒）'
)

// 8i) 初始化 SQL 的两处副本不能漂移（事实来源是 deploy/initdb/01-extensions.sql）
const initSql = read('deploy/initdb/01-extensions.sql')
const initSqlBlock = (postgresYaml.split(/^\s*01-extensions\.sql:\s*\|\s*$/m)[1] ?? '').split(/^---\s*$/m)[0]

check(initSqlBlock.length > 0, 'deploy/k8s/postgres.yaml 里找不到 01-extensions.sql 的 ConfigMap 内容')

if (initSql && initSqlBlock) {
  const blockLines = new Set(
    initSqlBlock
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
  )

  const missingLines = initSql
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .filter((line) => !blockLines.has(line))

  check(
    missingLines.length === 0,
    `deploy/k8s/postgres.yaml 里的初始化 SQL 与 deploy/initdb/01-extensions.sql 漂移了（${missingLines.length} 行对不上，例如「${missingLines[0]}」）：扩展与排序规则只在这个脚本里，两处不一致会让 K8s 建出的库与 compose 建出的库不一样`
  )
}

// 8j) 环境变量展开这个"魔法"必须在文档里被标成未验证
check(
  /\(POSTGRES_PASSWORD\)/.test(apiDeployment) && /未验证|没有在真实集群上验证/.test(k8sAll),
  'api-deployment.yaml 用 $(VAR) 展开拼连接串，而清单里必须**明确写出**"这个行为没有在真实集群上验证过"（见 secret.example.yaml / README）'
)

notes.push(
  'deploy/k8s/：只做了文本级核对 —— 清单**从未在真实集群上应用过**，' +
    'ROADMAP 第 14 条"在任意单节点 K8s 上完整部署并跑通冒烟"仍未完成（验收清单见 deploy/k8s/README.md 第 7 节）'
)

// ---------------------------------------------------------------------------
// 报告
// ---------------------------------------------------------------------------
console.log('部署编排核对：')
console.log(`  核对配置键 ${REQUIRED_KEYS.length} 个、compose 变量 ${referenced.size} 个`)
console.log(`  检查文件：docker-compose.prod.yml / api.Dockerfile / web.Dockerfile / nginx.conf / .env.example / README.md`)
console.log(`  另核对 deploy/k8s/ 的 ${k8sFileNames.length} 个文件（文本级，未在集群上验证）`)

for (const note of notes) {
  console.log(`  · ${note}`)
}

if (problems.length === 0) {
  console.log('\n✅ 部署编排与应用的期望一致。')
  process.exit(0)
}

console.error('\n❌ 发现以下问题：')

for (const problem of problems) {
  console.error(`  - ${problem}`)
}

process.exit(1)
