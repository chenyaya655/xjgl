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
// 报告
// ---------------------------------------------------------------------------
console.log('部署编排核对：')
console.log(`  核对配置键 ${REQUIRED_KEYS.length} 个、compose 变量 ${referenced.size} 个`)
console.log(`  检查文件：docker-compose.prod.yml / api.Dockerfile / web.Dockerfile / nginx.conf / .env.example / README.md`)

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
