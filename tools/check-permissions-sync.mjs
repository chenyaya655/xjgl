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
 *   4~6. 导出任务种类、数据范围码值、异动状态码值（后来陆续补的，理由写在各自的段落里）。
 *   7. 权限码**没有任何执行点**：声明了、也授了，却没有 `[Authorize]` / `IsGrantedAsync`
 *      指向它 —— 权限管理页于是出现"勾了却不影响任何行为"的开关。
 *      （本项目真实发生过：一次 8 条码，见第 7 节的长注释。）
 *
 * eslint 管不了这些事（它们是跨语言、跨文件的约定），所以单独写一个核对脚本。
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
// 7) 每个权限码都必须有「执行点」（否则就是"勾了却不影响任何行为"）
// ---------------------------------------------------------------------------

// 【为什么这条值得机械核对 —— 它是本项目真实栽过的跟头】
//   本项目曾把 8 条权限码（Sys.User.Import/Export、Sys.Org.Import/Export、
//   Sys.Code.Import/Export、Chg.Apply.Export、Chg.Audit.Export）种进元数据、
//   也授给了 admin，但**前后端都没有任何实现**。后果不是报错，而是权限管理页
//   出现"勾了却不影响任何行为"的权限项 —— 管理员据此以为已经能导入了。
//   后来是实现了才重新声明的（见 MetadataPermissions.cs 的注释）。
//
//   这件事此前只靠人记得。这条核对把它变成 CI 里会红的一步，而且是**双向**的：
//     · 声明了却没有任何执行点 → 失败（要么补实现，要么别声明）；
//     · 白名单里的码**变成有执行点**了也失败 —— 否则白名单会一路掩盖后续回归，
//       白名单只应该装"确实是菜单级"的那些码。
//
// 【什么是「执行点」】这个仓库里权限码有三种落地方式，脚本三种都认：
//     1. `[Authorize(MetadataPermissions.X.Y)]` —— 绝大多数接口；
//     2. `IsGrantedAsync(MetadataPermissions.X.Y)` —— 表达不了在 attribute 里的场景
//        （如异动附件的读路径要"任一权限即可"）；
//     3. 赋给 `private const string XxxPermission` 后在方法体里判 —— 2 的变体。
//   做法是按"**所在文件**里有没有 `[Authorize(` 或 `IsGrantedAsync(`"来判定，
//   而不是逐行匹配调用点：这样 1/2/3 三种写法都被覆盖，代价是个别误判的可能
//   （所以下面还要求"没有执行点的码必须带理由进白名单"，误判会在白名单评审时暴露）。
//
//   导出类权限码**不写在方法上**：它们集中在
//   `ExportTaskPermissions.GetRequiredPermissions` 里，由「入队」与「下载」两处消费
//   （已核对 ExportTaskAppService 与 ExportTaskDownloadService 各有一处遍历）。
//   所以那个映射表文件也算执行点。
//
// 【白名单为什么必须一条一条写理由】
//   菜单级权限码是**有意为之**，不是漏了：它们的数据被低权限页面复用
//   （码值下拉、学校下拉、异动类型），把读接口按 Query 码卡住，会让
//   "没有后台管理权限、但要录学生/传照片"的教师一起 403 ——
//   那是把"能不能进后台页"错当成了"能不能读这份参照数据"。
//   写清每条的理由，下一个人才能判断"我这条该不该进白名单"。
const MENU_ONLY_PERMISSIONS = new Map([
  // ---- 本系统自己的读接口：只要求登录（能看多少另有数据范围约束）----
  [
    'Sys.Module.Query',
    '模块管理菜单；/api/app/module/tree 是该页的数据源，接口只要求登录'
  ],
  [
    'Sys.Code.Query',
    '微代码菜单；码值下拉（useCodeOptions）几乎每个页面都要用，卡接口会让下拉一起变空'
  ],
  [
    'Sys.Org.Query',
    '组织管理菜单；学校/组织下拉被学生、照片、异动等页面复用'
  ],
  [
    'Chg.Type.Query',
    '异动类型菜单；异动申请页需要读类型列表'
  ],

  // ---- 接口在 ABP 自带的控制器上，判的是 AbpIdentity.* / 权限管理权限 ----
  //
  // 这三个页面操作的是 ABP 框架表（AbpUsers / AbpRoles / AbpPermissionGrants），
  // 走的是 ABP 自带的 /api/identity/*、/api/permission-management/* 控制器，
  // 那些控制器上的 [Authorize] 判的是 AbpIdentity.* —— 本组业务码只决定菜单能否看见。
  // 授权时必须**两个都给**，否则出现"菜单看得见、点进去 302"（理由详见
  // MetadataPermissions.cs 里 Sys.Role 的注释与 DemoDataSeedContributor 的说明）。
  ['Sys.Role.Create', '菜单级；接口是 ABP 自带 /api/identity/roles（判 AbpIdentity.Roles*）'],
  ['Sys.Role.Delete', '同上'],
  ['Sys.User.Query', '菜单级；接口是 ABP 自带 /api/identity/users（判 AbpIdentity.Users*）'],
  ['Sys.User.Create', '同上'],
  ['Sys.User.Update', '同上'],
  ['Sys.User.Delete', '同上'],
  ['Sys.Permission.Query', '菜单级；接口是 ABP 自带 /api/permission-management/*'],
  ['Sys.Permission.Assign', '同上']
])

{
  // 解析 MetadataPermissions.cs：嵌套类路径 + 常量名 → 符号名；同时取权限码字面量
  const stack = []
  const declared = []

  for (const line of permissionsCs.split('\n')) {
    const cls = line.match(/^\s*public\s+static\s+class\s+(\w+)/)
    if (cls) {
      stack.push(cls[1])
      continue
    }

    if (/^\s*\}/.test(line)) {
      if (stack.length > 0) stack.pop()
      continue
    }

    const konst = line.match(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"/)
    if (konst) {
      // stack[0] 是外层类名 MetadataPermissions，因此拼出来就是源码里的符号名
      declared.push({ symbol: `${stack.join('.')}.${konst[1]}`, code: konst[2] })
    }
  }

  /** 递归收集 src 下的 .cs（跳过 obj/bin，避免把生成代码里的引用也算进来）。 */
  function walkCs(dir) {
    const out = []

    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (entry.name === 'obj' || entry.name === 'bin') continue
        out.push(...walkCs(path.join(dir, entry.name)))
      } else if (entry.name.endsWith('.cs')) {
        out.push(path.join(dir, entry.name))
      }
    }

    return out
  }

  const definitionFile = path.join(
    repoRoot,
    'src/Xjgl.Application.Contracts/Permissions/MetadataPermissions.cs'
  )
  const exportMappingFile = path.join(
    repoRoot,
    'src/Xjgl.Application.Contracts/Exporting/ExportTaskDtos.cs'
  )

  const enforced = new Set()
  const referenced = new Set()

  if (declared.length === 0) {
    problems.push(
      '没有从 MetadataPermissions.cs 解析出权限码常量（含符号名）—— 正则可能已过时，请修脚本。'
    )
  } else {
    for (const file of walkCs(path.join(repoRoot, 'src'))) {
      if (file === definitionFile) continue

      const text = fs.readFileSync(file, 'utf8')
      const hasCheckSite =
        /\[Authorize\(|IsGrantedAsync\(/.test(text) || file === exportMappingFile

      for (const { symbol, code } of declared) {
        if (!text.includes(symbol)) continue

        referenced.add(code)
        if (hasCheckSite) enforced.add(code)
      }
    }

    const noEnforcement = declared.filter((d) => !enforced.has(d.code))

    for (const d of noEnforcement) {
      if (MENU_ONLY_PERMISSIONS.has(d.code)) continue

      problems.push(
        `权限码没有任何执行点：${d.code}（${d.symbol}）\n` +
          '    （后果：权限管理页出现"勾了却不影响任何行为"的权限项，管理员据此以为已经能用了。' +
          '要就整条链路一起做，不要只声明；若确实只做菜单级，请连同理由加进脚本的 MENU_ONLY_PERMISSIONS）'
      )
    }

    for (const code of MENU_ONLY_PERMISSIONS.keys()) {
      if (!declared.some((d) => d.code === code)) {
        problems.push(`白名单里的权限码已经不存在：${code}\n    （请在脚本里删掉这一条）`)
        continue
      }

      if (enforced.has(code)) {
        problems.push(
          `白名单里的权限码已经有执行点了：${code}\n` +
            '    （请把它从 MENU_ONLY_PERMISSIONS 删掉 —— 白名单只应装"确实是菜单级"的码，' +
            '否则它会一路掩盖后续回归）'
        )
      }
    }

    const orphan = noEnforcement.filter((d) => !referenced.has(d.code)).map((d) => d.code)

    notes.push(
      `权限码执行点：${declared.length} 条中 ${declared.length - noEnforcement.length} 条有执行点，` +
        `${noEnforcement.length} 条为菜单级（白名单 ${MENU_ONLY_PERMISSIONS.size} 条${
          orphan.length > 0 ? `；其中 ${orphan.length} 条在 src 里连引用都没有：${orphan.join(', ')}` : ''
        }）`
    )
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

console.log(
  '\n✅ 七处约定一致：权限码 / 码类别 / 操作类型 / 导出种类 / 数据范围 / 异动状态 / 权限码执行点。'
)
