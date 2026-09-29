/**
 * 导出/打印任务的**过程文案**（债 23：真进度百分比）单测。
 *
 * 【为什么这个函数值得单独测】
 * 它是八个导出入口 + 任务中心对话框共用的唯一一处实现，而其中最容易写错、
 * 后果又最难看的恰恰是**回退分支**：`total` 为 0 时若照算百分比，
 * 会得到 `Infinity`/`NaN`，界面显示「已处理 0 / 0（0%）」—— 一条**永远不动的
 * 进度条**。用户看到它只会得出"卡死了"这个结论，然后关掉重来。
 * 后端确实存在"结构上给不出总数"的种类（组织、微代码是一次性序列化的，
 * 见 `ExportTaskRunner` 里那份名单），所以这条回退不是理论边界，而是**常态路径**。
 *
 * 【为什么用 node:test 而不是引 vitest】
 * 同 `csvEncoding.test.ts`：被测函数是纯函数、零依赖，被测文件也刻意不放
 * axios（在 `packages/api/src/progress.ts` 而不是 `index.ts`），
 * 因此 Node 22.7+ 的内置类型擦除 + `node:test` 就够，新增依赖为零。
 *
 * 运行：`node --test apps/pc/src/utils/exportProgress.test.ts`（在 `web/` 下）
 *   —— `@xjgl/pc` 的 `test:unit` 已封装整目录。
 *
 * 【为什么是相对路径跨包 import 而不是 `from '@xjgl/api'`】
 * 走包名会解析到 `packages/api/src/index.ts`，而那个文件 import 了 axios，
 * 于是单测要连带拉起 axios 那一串依赖（还可能因为 `.ts` 扩展名解析失败）。
 * 直接指向零依赖的那个文件更稳 —— 这也是把它单独拆成一个文件的理由。
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'

import { describeExportProgress } from '../../../../packages/api/src/progress.ts'
import type { ExportTask } from '../../../../packages/api/src/types.ts'

/** 造一个只关心进度字段的任务（其余字段用不到，但类型要求齐全）。 */
function task(overrides: Partial<ExportTask>): ExportTask {
  return {
    id: '00000000-0000-0000-0000-000000000000',
    kind: 'StudentCsv',
    kindName: '学生名单导出',
    status: 'Running',
    fileSize: 0,
    total: 0,
    rowCount: 0,
    processed: 0,
    isTruncated: false,
    creationTime: '2026-01-01T00:00:00',
    canDownload: false,
    ...overrides
  } as ExportTask
}

// ---------------------------------------------------------------------------
// 核心不变式：总数为 0（或未知）时**绝不能**显示百分比
// ---------------------------------------------------------------------------

test('总数为 0 时不显示百分比，回退到「正在生成…」', () => {
  const text = describeExportProgress(task({ status: 'Running', total: 0, processed: 0 }))

  assert.equal(text, '正在生成…')
  assert.ok(!text.includes('%'), `不能出现任何百分比，实际：${text}`)
  assert.ok(!text.includes('0%'), '「0%」正是要避免的那条误导信息（看起来卡住了）')
})

test('总数为 0 但已处理了几百条时，仍然不显示百分比', () => {
  // 组织/微代码这类"一次性序列化"的产物就是这种形态：有产出、但没有分母。
  // 此时若硬算 (500 / 0)，会显示 Infinity% 或 0%。
  const text = describeExportProgress(task({ status: 'Running', total: 0, processed: 500 }))

  assert.equal(text, '正在生成…')
  assert.ok(!text.includes('%'))
})

test('总数已知但还没有任何产出时，也不显示 0%', () => {
  // 任务刚被领取、第一页还没回来：这时显示「已处理 0 / 12,000（0%）」
  // 与"卡住"在界面上长得一模一样，等真有产出再显示。
  const text = describeExportProgress(task({ status: 'Running', total: 12000, processed: 0 }))

  assert.equal(text, '正在生成…')
  assert.ok(!text.includes('0%'))
})

// ---------------------------------------------------------------------------
// 真百分比
// ---------------------------------------------------------------------------

test('已知总数时给出千分位与整百分比', () => {
  const text = describeExportProgress(task({ status: 'Running', total: 12000, processed: 3000 }))

  assert.equal(text, '已处理 3,000 / 12,000（25%）')
})

test('百分比向下取整 —— 99.6% 不能显示成 100%', () => {
  // 显示 100% 会让用户以为已经好了，然后去点下载（产物那时还没落盘）。
  // 宁可停在 99%，也不要谎报完成。
  const text = describeExportProgress(task({ status: 'Running', total: 1000, processed: 996 }))

  assert.equal(text, '已处理 996 / 1,000（99%）')
  assert.ok(!text.includes('100%'))
})

test('接近一半时向下取整，不四舍五入', () => {
  const text = describeExportProgress(task({ status: 'Running', total: 3, processed: 2 }))

  assert.equal(text, '已处理 2 / 3（66%）', '四舍五入会得到 67%，那是凭空多出来的进度')
})

test('脏数据 processed > total 不会算出超过 100%', () => {
  // 后端理论上不会给出这种组合（上报时会夹一次），但前端绝不该把它原样显示成 105%。
  const text = describeExportProgress(task({ status: 'Running', total: 100, processed: 105 }))

  assert.ok(text.includes('（100%）'), `实际：${text}`)
})

// ---------------------------------------------------------------------------
// 排队与终态
// ---------------------------------------------------------------------------

test('排队中是「排队中…」，即使带着上一轮的进度数字', () => {
  // 排队与"正在生成但暂时没有数字可显示"是两件事：混成一句会让用户在队列里
  // 干等时以为后台已经跑起来了。
  const text = describeExportProgress(task({ status: 'Pending', total: 12000, processed: 3000 }))

  assert.equal(text, '排队中…')
})

test('终态下不抛错，给一句无害的兜底文案', () => {
  // 终态的说明由 describeTaskFailure / describeTruncation 负责，这个函数只回答
  // "还在跑的时候到哪儿了"。调用点在轮询中拿到终态时不该因为这里崩掉。
  for (const status of ['Succeeded', 'Failed', 'Canceled', 'Expired']) {
    const text = describeExportProgress(task({ status, total: 12000, processed: 12000 }))

    assert.ok(text.length > 0, `${status} 下不能返回空串`)
  }
})
