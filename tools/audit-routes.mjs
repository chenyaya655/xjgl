// 前端调用 vs 后端真实路由/动词 全量核对
// ---------------------------------------------------------------------------
// 为什么需要它（两个真实踩过的缺陷）：
//   1. `StudentImportExportAppService.ExportAsync` 不以 `Get` 开头，
//      ABP 自动 API 约定把它注册成了 **POST**，而前端是带查询串 GET 下载 → 405；
//   2. `PhotoAppService.DeleteManyAsync` 被注册成 **DELETE /api/app/photo/many**
//      （集合参数走请求体），前端却调了 POST → 405。
//
//   这类缺陷有个共同特征：**进程内单元测试永远发现不了**（测试直接调方法，
//   不经过路由），只有拿真实路由表比对、或真的发一次 HTTP 请求才会暴露；
//   而且表现是 405 而不是 404，比"路径写错"更难定位。
//
//   因此把它固化成 CI 步骤：从 swagger.json 取真实路由与动词，
//   再逐个核对 `web/packages/api/src/index.ts` 里的每次调用。
//
// 用法：
//   node tools/audit-routes.mjs                        # 默认读 ./swagger.json
//   node tools/audit-routes.mjs path/to/swagger.json
//   node tools/audit-routes.mjs http://127.0.0.1:44300/swagger/v1/swagger.json
//
// 退出码：0 = 全部一致；1 = 存在不一致或核对数量异常（防止"假绿灯"）。
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const swaggerArg = process.argv[2] ?? path.join(repoRoot, 'swagger.json')

// 期望至少核对的调用数量：脚本解析一旦退化（例如换写法后正则失配），
// 就会"核对 0 项、报告 0 问题"——那是最危险的假绿灯，所以这里硬卡一个下限。
const MIN_EXPECTED_CALLS = 40

async function loadSwagger(src) {
  if (/^https?:\/\//.test(src)) {
    const res = await fetch(src)

    if (!res.ok) throw new Error(`下载 swagger 失败：HTTP ${res.status}`)
    return res.json()
  }
  return JSON.parse(stripBom(fs.readFileSync(src, 'utf8')))
}

/**
 * 去掉 UTF-8 BOM。
 *
 * 【为什么必须有这一步】
 *   `JSON.parse` **不认 BOM**，会直接抛
 *   `SyntaxError: Unexpected token '﻿', "﻿{..." is not valid JSON` ——
 *   报错指向第 1 行第 1 个字符，看起来像"swagger.json 本身是坏的"，
 *   实际文件完全正常，只是被存成了带 BOM 的 UTF-8。
 *
 *   而 Windows 上按 README 操作几乎必然踩到：
 *   `Invoke-WebRequest ... | Out-File swagger.json -Encoding utf8`
 *   在 Windows PowerShell 5.1 里 **默认带 BOM**。
 *   CI 用的是 `curl -o`（不带 BOM），所以这条路只有本机开发者会走到 ——
 *   正是那种"CI 一直是绿的、只有新人第一次跑就报错"的坑。
 */
function stripBom(text) {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text
}

const swagger = await loadSwagger(swaggerArg)
const apiIndexPath = path.join(repoRoot, 'web/packages/api/src/index.ts')
const apiIndex = fs.readFileSync(apiIndexPath, 'utf8')

// ---- 1) 解析 ENDPOINTS 常量表 -------------------------------------------------
const endpointsBlock = apiIndex.split('export const ENDPOINTS')[1]?.split('} as const')[0] ?? ''
const endpoints = {}

for (const m of endpointsBlock.matchAll(/(\w+):\s*'([^']+)'/g)) {
  endpoints[m[1]] = m[2]
}

// ---- 2) 建立 swagger 路由索引（{id} 归一化成 *）--------------------------------
const normalize = (p) => p.replace(/\{[^}]+\}/g, '*').replace(/\/+$/, '')

const routes = Object.entries(swagger.paths).map(([p, ops]) => ({
  key: normalize(p),
  verbs: Object.keys(ops).map((v) => v.toUpperCase())
}))

// ---- 3) 把一次调用的实参文本解析成路径模板 ------------------------------------
function resolvePath(raw) {
  const text = raw.trim()

  // 模板串 / 普通字符串字面量
  const literal = text.match(/^(`[^`]*`|'[^']*'|"[^"]*")/)

  if (literal) {
    return literal[0]
      .slice(1, -1)
      .replace(/\$\{([^}]*)\}/g, (_, expr) => {
        const key = expr.trim()
        const name = key.startsWith('ENDPOINTS.') ? key.slice('ENDPOINTS.'.length) : key

        return endpoints[name] ?? '*'
      })
      .split('?')[0]
      .replace(/\/$/, '')
  }

  // 直接写常量名，如 http.get(ENDPOINTS.profile)
  const bare = text.match(/^ENDPOINTS\.(\w+)/)

  if (bare) {
    return endpoints[bare[1]] ?? null
  }

  return null
}

// ---- 4) 收集前端调用 ---------------------------------------------------------
const calls = []

// 类型参数里可能还有嵌套泛型（`http.get<PagedResult<StudentChangeLog>>(...)`），
// 用 `<[^>]*>` 匹配会漏掉这种写法 —— 漏掉就是"少核对一次"，属于假绿灯。
// 因此这里用 `<[^(]*>` 一直吃到调用的左括号；下面还会用"原始出现次数"做交叉校验。
for (const m of apiIndex.matchAll(/http\.(get|post|put|delete)(?:<[^(]*>)?\(\s*([^\n]*)/g)) {
  calls.push({
    verb: m[1].toUpperCase(),
    path: resolvePath(m[2]),
    raw: m[2].trim(),
    line: apiIndex.slice(0, m.index).split('\n').length
  })
}

// 交叉校验：文件里实际出现了多少次 `http.<verb>`，就必须解析出多少条，
// 否则说明上面的正则漏了某种写法（比如又出现了新的调用形式）
const rawInvocationCount = (apiIndex.match(/\bhttp\.(get|post|put|delete)\s*[<(]/g) ?? []).length

// ---- 5) 逐项核对 -------------------------------------------------------------
const lines = []
const unidentified = []
let checked = 0
let mismatched = 0

for (const call of calls) {
  if (!call.path || !call.path.startsWith('/api/')) {
    unidentified.push(call)
    continue
  }

  checked++

  const key = call.path
  let hit = routes.find((r) => r.key === key)

  if (!hit) {
    const pattern = new RegExp(
      '^' + key.replace(/[.*+?^${}()|[\]\\]/g, (ch) => (ch === '*' ? '[^/]+' : '\\' + ch)) + '$'
    )

    hit = routes.find((r) => pattern.test(r.key))
  }

  const ok = Boolean(hit && hit.verbs.includes(call.verb))

  if (!ok) mismatched++

  lines.push(
    `  ${ok ? '✓' : '✗'} ${call.verb.padEnd(6)} ${call.path.padEnd(56)} → 后端: ${
      hit ? hit.verbs.join('/') : '(无此路由)'
    }`
  )
}

console.log(lines.join('\n'))

if (unidentified.length > 0) {
  console.log('\n以下调用未能自动解析，请人工确认（新写法请同步更新本脚本）：')

  for (const c of unidentified) {
    console.log(`  ? index.ts:${c.line}  ${c.verb}  ${c.raw}`)
  }
}

console.log(`\n实际核对 ${checked} 项 / 共 ${calls.length} 个调用；动词或路由不匹配 ${mismatched} 项`)

if (calls.length !== rawInvocationCount) {
  console.error(
    `\n❌ 解析出 ${calls.length} 个调用，但文件里出现了 ${rawInvocationCount} 次 http.<verb>：` +
      '有调用被正则漏掉了，请检查 index.ts 的写法与上面的正则。'
  )
  process.exit(1)
}

if (checked < MIN_EXPECTED_CALLS) {
  console.error(
    `\n❌ 只核对了 ${checked} 项（期望 >= ${MIN_EXPECTED_CALLS}）：脚本解析很可能失效了，请检查 index.ts 的写法是否变化。`
  )
  process.exit(1)
}

if (mismatched > 0) {
  console.error(`\n❌ 存在 ${mismatched} 项前端调用与后端路由/动词不一致（线上表现为 404/405）。`)
  process.exit(1)
}

console.log('\n✅ 前端调用的路径与动词全部与后端一致。')
