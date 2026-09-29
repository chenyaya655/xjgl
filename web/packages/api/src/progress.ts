import type { ExportTask } from './types'

/**
 * 任务状态。值与后端 `ExportTaskStatuses` 一致。
 *
 * ⚠ 这个常量**刻意放在本文件**（而不是 `types.ts`）：本文件要能被 `node --test`
 *   直接跑（见下面的单测说明），而 Node 的 ESM 解析**不补后缀** ——
 *   只要这里 import 了 './types'（运行时值），单测就会以
 *   `ERR_MODULE_NOT_FOUND: .../src/types` 失败；写成 './types.ts' 又要给
 *   整个包开 `allowImportingTsExtensions`。放在这里之后本文件**零运行时依赖**，
 *   单测只 import 它一个就够。`types.ts` 会把它再导出一遍，包根入口的用法不变。
 */
export const ExportTaskStatuses = {
  Pending: 'Pending',
  Running: 'Running',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  Canceled: 'Canceled',
  Expired: 'Expired'
} as const

/**
 * 导出/打印任务的**过程文案**（债 23：真进度百分比）。
 *
 * 【为什么必须只有这一处实现】
 *   导出入口有八个（学生名单、组织、微代码、异动申请、异动审核、用户、预置名单、
 *   照片打包），每个都要在轮询回调里把任务状态翻成一句人话。以前这句话是
 *   `t.status === 'Running' ? '正在生成…' : '排队中…'`，**八份复制**。
 *   复制品的问题是它对"漏改"完全免疫：本次要让进度带上百分比，
 *   若在八个地方各改一遍，必然有两处被忘掉 —— 而那两处不会报错、不会变红，
 *   只是永远显示旧文案，等到有人截图问"为什么这个页面没有百分比"才会发现。
 *   所以抽成这个函数，调用点只写 `describeExportProgress(t)`。
 *
 * 【为什么 total / processed 为 0 时必须回退到"正在生成…"，而不是显示 0%】
 *   后端的 `Total` 在某些种类里**结构上就是 0**（组织、微代码是一次性序列化的，
 *   过程里根本没有"总数"这个概念；见 `ExportTaskRunner` 里那份名单）。
 *   此时若照算 `processed / total`，会得到 `Infinity` 或 `NaN`，
 *   显示成「已处理 0 / 0（0%）」—— 用户看到的是一个**停在 0% 的进度条**，
 *   他会以为任务卡死了，然后去点第二次导出。这比不显示百分比糟糕得多：
 *   一个不动的百分比是一条明确误导人的信息，而"正在生成…"至少是诚实的。
 *   同一条理由也适用于 `total > 0 但 processed === 0` 的头几秒：
 *   那时百分比必然是 0%，同样是"卡在 0%"。等真有产出再显示。
 *
 * 【为什么不在这里处理终态文案】
 *   终态（失败原因、截断提示、过期说明）分别由 `describeTaskFailure` 与
 *   `describeTruncation` 负责，它们的信息量比这里大得多。这个函数只回答
 *   "还在跑的时候，现在跑到哪儿了"，因此在终态下它只给一句无害的兜底文案 ——
 *   调用点本来也只在轮询过程中用它。
 */
export function describeExportProgress(task: ExportTask): string {
  // 排队是**真的没开始**，与"正在生成但暂时没有可显示的数字"是两件事，
  // 混成一句会让用户在队列里干等时以为后台已经在跑了。
  if (task.status === ExportTaskStatuses.Pending) {
    return '排队中…'
  }

  const total = task.total ?? 0
  const processed = task.processed ?? 0

  if (total > 0 && processed > 0) {
    // 向下取整而不是四舍五入：99.6% 时显示 100% 会让用户以为已经好了，
    // 然后去点下载（此时产物还没落盘）。宁可停在 99%，也不要谎报"完成"。
    const percent = Math.min(100, Math.floor((processed / total) * 100))

    return `已处理 ${formatCount(processed)} / ${formatCount(total)}（${percent}%）`
  }

  return '正在生成…'
}

/**
 * 千分位。任务条数是 12000 这种量级，不加分隔符时
 * 「已处理 12000 / 12000」在表格里几乎读不出来是几位数。
 *
 * 用 `toLocaleString('en-US')` 而不是自己拼正则：分隔符只有一处实现，
 * 且不受运行环境默认区域影响（`toLocaleString()` 不带参数在中文区域下
 * 分隔符仍是逗号，但那属于"碰巧对了"，不该依赖）。
 */
function formatCount(value: number): string {
  return Math.max(0, Math.trunc(value)).toLocaleString('en-US')
}

