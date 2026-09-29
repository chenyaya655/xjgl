#!/usr/bin/env node
/**
 * 前后端「约定一致性」核对。
 *
 * 【为什么需要这个脚本】
 * 这个项目里最贵的一类缺陷不是"报错"，而是**双方约定不一致、谁都不报错**：
 *
 *   1. 权限码：后端在 `MetadataPermissions.cs` 定义、前端在 `permissions.ts` 抄一份。
 *      只改一边的后果是"按钮永远不显示"或"接口永远 403" —— 没有编译错误、
 *      没有运行时异常，只能靠人记得。CONTRIBUTING 里专门写了一条，
 *      但"写进文档"对下一轮改动没有约束力。
 *   2. 码类别：前端写 `useCodeOptions('PHOTO_STATUS')`，后端码表里叫别的名字。
 *      查一个不存在的类别，接口返回 **200 + 空数组** → 下拉框静默变空。
 *   3. 模块操作的 ActionType：元数据种子用 `Action("Assign", ...)`，
 *      而 `ACTION_TYPE` 码表里没有它 → "操作类型"下拉选不到、编辑后显示为空。
 *      （这一条真的发生过：前端手写的列表与码表各自漂移，一边有 Assign/Compensate、
 *      另一边有 Download。）
 *
 * eslint 管不了这三件事（它们是跨语言、跨文件的约定），所以单独写一个核对脚本。
 * 与 `tools/audit-routes.mjs`、`tools/verify-deploy.mjs` 同一性质：
 * **把"只能靠记得"变成 CI 里会红的一步**。
 *
 * 用法：node tools/check-permissions-sync.mjs
 */

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..')

const problems = []
const notes = []

function read(relPath) {
  const full = path.join(repoRoot, relPath)

  if (!fs.existsSync(full)) {
    problems.push(`找不到文件：${relPath}`)
    return ''
  }

  return fs.readFileSync(full, 'utf8')
}

/** 收集某个文件里所有匹配正则的分组 1。 */
function collectAll(text, regex) {
  const out = new Set()

  for (const match of text.matchAll(regex)) {
    out.add(match[1])
  }

  return out
}

function diff(label, backend, frontend, hint) {
  const onlyBackend = [...backend].filter((x) => !frontend.has(x)).sort()
  const onlyFrontend = [...frontend].filter((x) => !backend.has(x)).sort()

  if (onlyBackend.length === 0 && onlyFrontend.length === 0) {
    return true
  }

  if (onlyBackend.length > 0) {
    problems.push(`${label}：后端有、前端缺 → ${onlyBackend.join(', ')}\n    （后果：${hint}）`)
  }

  if (onlyFrontend.length > 0) {
    problems.push(`${label}：前端有、后端缺 → ${onlyFrontend.join(', ')}\n    （后果：${hint}）`)
  }

  return false
}

// ---------------------------------------------------------------------------
// 1) 权限码：MetadataPermissions.cs ↔ permissions.ts
// ---------------------------------------------------------------------------

const permissionsCs = read('src/Xjgl.Application.Contracts/Permissions/MetadataPermissions.cs')
const permissionsTs = read('web/packages/api/src/permissions.ts')

// C#：public const string Query = "Stu.Student.Query";
const backendPermissions = collectAll(permissionsCs, /public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"/g)

// TS：Query: 'Stu.Student.Query' —— 只取带点号的码，避免把普通字符串当权限码
const frontendPermissions = collectAll(permissionsTs, /:\s*'([A-Za-z][\w]*\.[\w.]+)'/g)

if (backendPermissions.size === 0) {
  problems.push('没有从 MetadataPermissions.cs 解析出任何权限码 —— 正则可能已过时，请修脚本。')
}

diff(
  '权限码不一致',
  backendPermissions,
  frontendPermissions,
  '按钮不显示 / 授权无效，且不报错。请同步 web/packages/api/src/permissions.ts 与 MetadataPermissions.cs'
)
notes.push(`权限码：后端 ${backendPermissions.size} 条 / 前端 ${frontendPermissions.size} 条`)

// ---------------------------------------------------------------------------
// 2) 码类别：前端 CodeCategories ↔ 后端码表 AddAsync("...")
// ---------------------------------------------------------------------------

const codeSeed = read('src/Xjgl.Domain/Codes/CodeDataSeedContributor.cs')

// 后端：await AddAsync("PHOTO_STATUS", ...)
const backendCategories = collectAll(codeSeed, /AddAsync\(\s*"([A-Z0-9_]+)"/g)

for (const rel of [
  'web/apps/pc/src/composables/useCodeOptions.ts',
  'web/apps/h5/src/composables/useCodeOptions.ts'
]) {
  const text = read(rel)

  // 前端：OrgType: 'ORG_TYPE' —— 只取值形如大写下划线的
  const frontendCategories = collectAll(text, /:\s*'([A-Z][A-Z0-9_]{2,})'/g)

  if (frontendCategories.size === 0) {
    problems.push(`${rel}：没有解析出任何码类别 —— 正则可能已过时，请修脚本。`)
    continue
  }

  const missing = [...frontendCategories].filter((c) => !backendCategories.has(c)).sort()

  if (missing.length > 0) {
    problems.push(
      `${rel} 引用了后端码表里不存在的类别 → ${missing.join(', ')}\n` +
        '    （后果：查一个不存在的类别返回 200 + 空数组，下拉框静默变空、名称回落到码值）'
    )
  }

  notes.push(`${rel}：引用 ${frontendCategories.size} 个码类别，全部存在于后端码表`)
}

// ---------------------------------------------------------------------------
// 3) 元数据种子的 ActionType 必须都在 ACTION_TYPE 码表里
// ---------------------------------------------------------------------------

// 只截取 ACTION_TYPE 那一段的 AddAsync(...) 调用，避免把别的码表内容算进来
const actionTypeBlock = codeSeed.match(/AddAsync\(\s*"ACTION_TYPE"[\s\S]*?isSystem:\s*true\);/)

if (!actionTypeBlock) {
  problems.push('没有在 CodeDataSeedContributor.cs 里找到 ACTION_TYPE 码表 —— 正则可能已过时，请修脚本。')
} else {
  const actionTypes = collectAll(actionTypeBlock[0], /\(\s*"([A-Za-z]+)"\s*,/g)

  const moduleSeed = read('src/Xjgl.Domain/Metadata/ModuleMetadataDataSeedContributor.cs')
  const usedActionTypes = collectAll(moduleSeed, /Action\(\s*"([A-Za-z]+)"/g)

  if (usedActionTypes.size === 0) {
    problems.push('没有从模块元数据种子里解析出任何 Action(...) —— 正则可能已过时，请修脚本。')
  } else {
    const missing = [...usedActionTypes].filter((t) => !actionTypes.has(t)).sort()

    if (missing.length > 0) {
      problems.push(
        `模块元数据用到的操作类型不在 ACTION_TYPE 码表里 → ${missing.join(', ')}\n` +
          '    （后果：「操作类型」下拉选不到该值，编辑已有操作时显示为空）'
      )
    }

    notes.push(
      `操作类型：元数据用了 ${usedActionTypes.size} 种，码表有 ${actionTypes.size} 种（码表需为超集）`
    )
  }
}

// ---------------------------------------------------------------------------
// 4) 导出任务种类：ExportTaskConsts.cs ↔ types.ts 的 ExportTaskKinds
// ---------------------------------------------------------------------------

// 【为什么这条值得机械核对】前端提交导出任务时要写 `kind: ExportTaskKinds.StudentCsv`。
// 两边漂移的表现是**只在运行期**出现：前端写了一个后端不认识的种类 →
// `ExportTaskPermissions.IsKnown` 返回 false → 任务被拒（提示往往只是"未知的导出种类"），
// 而"后端加了种类、前端没跟上"则表现为"新功能在界面上根本用不到"，两边都不报错、也没有测试能覆盖。
{
  const constsCs = read('src/Xjgl.Domain.Shared/Exporting/ExportTaskConsts.cs')
  const kindsBlock = constsCs.match(/class ExportTaskKinds[\s\S]*?\n\}/)

  if (!kindsBlock) {
    problems.push('没有在 ExportTaskConsts.cs 里找到 ExportTaskKinds —— 正则可能已过时，请修脚本。')
  } else {
    const backendKinds = collectAll(kindsBlock[0], /public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"/g)

    const tsKindsBlock = read('web/packages/api/src/types.ts')
      .match(/export const ExportTaskKinds\s*=\s*\{[\s\S]*?\}/)

    if (!tsKindsBlock) {
      problems.push('没有在 types.ts 里找到 ExportTaskKinds —— 正则可能已过时，请修脚本。')
    } else {
      const frontendKinds = collectAll(tsKindsBlock[0], /:\s*'([^']+)'/g)

      if (backendKinds.size === 0 || frontendKinds.size === 0) {
        problems.push('导出任务种类没有解析出内容（一侧为空）—— 正则可能已过时，请修脚本。')
      } else {
        diff(
          '导出任务种类不一致',
          backendKinds,
          frontendKinds,
          '前端会提交一个后端不认识的种类（任务被拒），或后端新增的种类在界面上根本用不到'
        )
        notes.push(`导出任务种类：后端 ${backendKinds.size} 种 / 前端 ${frontendKinds.size} 种`)
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 5) 数据范围码值：DataScopes 常量 ↔ DATA_SCOPE 码表
// ---------------------------------------------------------------------------

// 【为什么这条值得机械核对】`DataScopes.cs` 自己写着"与微代码 DATA_SCOPE 的码值保持一致"，
// 但这条约定此前**没有任何东西在守**。漂移的后果是"角色数据范围下拉里显示码值而不是中文"
// （查询类别的码表里找不到这个码值），而且不会报错。
{
  const scopesCs = read('src/Xjgl.Domain.Shared/Organizations/DataScopes.cs')

  // ⚠ 只取**形如标识符的码值**：`DataScopes` 里还有一个哨兵常量
  //   `ImpossiblePathPrefix = "\u0001"`，它不是范围码值 —— 第一版没排除它，
  //   脚本当场报了一个"后端有、码表没有 \u0001"的假不一致。
  const backendScopes = collectAll(scopesCs, /public\s+const\s+string\s+\w+\s*=\s*"([A-Za-z]+)"/g)

  const dataScopeBlock = codeSeed.match(/AddAsync\(\s*"DATA_SCOPE"[\s\S]*?isSystem:\s*true\);/)

  if (backendScopes.size === 0) {
    problems.push('没有从 DataScopes.cs 里解析出范围码值 —— 正则可能已过时，请修脚本。')
  } else if (!dataScopeBlock) {
    problems.push('没有在 CodeDataSeedContributor.cs 里找到 DATA_SCOPE 码表 —— 正则可能已过时，请修脚本。')
  } else {
    const codeScopes = collectAll(dataScopeBlock[0], /\(\s*"([A-Za-z]+)"\s*,/g)

    if (codeScopes.size === 0) {
      problems.push('DATA_SCOPE 码表没有解析出码项 —— 正则可能已过时，请修脚本。')
    } else {
      diff(
        '数据范围码值不一致（DataScopes ↔ DATA_SCOPE 码表）',
        backendScopes,
        codeScopes,
        '角色数据范围下拉会显示码值而不是中文，且不报错'
      )
      notes.push(`数据范围：DataScopes ${backendScopes.size} 个 / DATA_SCOPE 码表 ${codeScopes.size} 个`)
    }
  }
}

// ---------------------------------------------------------------------------
// 6) 异动状态码值：ChangeApplyStatus / ChangeTaskStatus ↔ types.ts 里的同名常量
// ---------------------------------------------------------------------------

// 【为什么这条值得机械核对】前端到处在用 `row.status === ChangeApplyStatuses.Approved` 这类比较
// （本轮刚从字面量改成常量）。码值一旦对不上，比较**恒为 false**：
// 按钮静默消失、状态标签永远是灰色，而类型检查、编译、集成测试**全都是绿的**。
{
  const changesCs = read('src/Xjgl.Domain/Changes/ChangeApply.cs')
  const statusTs = read('web/packages/api/src/types.ts')

  for (const [className, tsName, label] of [
    ['ChangeApplyStatus', 'ChangeApplyStatuses', '异动申请状态'],
    ['ChangeTaskStatus', 'ChangeTaskStatuses', '流程待办状态']
  ]) {
    const block = changesCs.match(new RegExp(`class ${className}\\s*\\{[\\s\\S]*?\\n\\}`))
    const tsBlock = statusTs.match(new RegExp(`export const ${tsName}\\s*=\\s*\\{[\\s\\S]*?\\}`))

    if (!block || !tsBlock) {
      problems.push(`没有解析出 ${className} / ${tsName} —— 正则可能已过时，请修脚本。`)
      continue
    }

    const backend = collectAll(block[0], /public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"/g)
    const frontend = collectAll(tsBlock[0], /:\s*'([^']+)'/g)

    if (backend.size === 0 || frontend.size === 0) {
      problems.push(`${label}：${className} 或 ${tsName} 没有解析出码值 —— 正则可能已过时，请修脚本。`)
      continue
    }

    diff(
      `${label}码值不一致（${className} ↔ ${tsName}）`,
      backend,
      frontend,
      '前端的 === 比较恒为 false：按钮静默消失、状态标签永远是灰色，而编译与集成测试全绿'
    )
    notes.push(`${label}：后端 ${backend.size} 个 / 前端 ${frontend.size} 个`)
  }
}

// ---------------------------------------------------------------------------

console.log('核对前后端约定一致性…\n')

// 这几行只是**计数**（便于发现"正则失效、其实什么都没解析出来"），
// 所以刻意不用 ✓ —— 否则一旦后面报错，会先看到一串勾再看到失败，很误导。
for (const note of notes) {
  console.log(`  · ${note}`)
}

if (problems.length > 0) {
  console.log('')
  console.error('❌ 发现前后端约定不一致：\n')

  for (const p of problems) {
    console.error(`  · ${p}`)
  }

  process.exit(1)
}

console.log('\n✅ 六处约定一致：权限码 / 码类别 / 操作类型 / 导出种类 / 数据范围 / 异动状态。')
