import { computed, ref, type ComputedRef, type Ref } from 'vue'

import { codeApi } from '@/api'
import type { CodeItemLookup } from '@/api/types'

/**
 * 微代码下拉项 / 码值翻译（债 12）。
 *
 * 【为什么必须有它】
 * 前端手写码值这件事已经真实害过人两次，而且两次都**不报错**：
 *   1) 学籍状态过滤写成 `{ value: 'Studying', label: '在读' }`，
 *      而后端存的是码值 `01` → 后端拿 'Studying' 去比对，**静默返回空列表**；
 *   2) 异动类型的"异动后状态"存成 'Studying'，审批通过后**直接写进学生的学籍状态** ——
 *      主档被写坏，而且要到打印学籍卡时才发现。
 * 这类缺陷的共同点是"前端一套值、后端一套值"，任何类型检查都看不出来。
 *
 * 因此把「取值」收敛到这一个函数：下拉框的 value 永远是码表里的 `code`，
 * 想手写字面量都没有地方可写。
 *
 * 【为什么不缓存到 Pinia / 不每次请求】
 * 码表是**极低频变更**的（只有"微代码管理"页面会改），但同一个页面里
 * 常有多处要用（例如统计页的"学籍状态"下拉与结果表里的状态翻译）。
 * 这里做**模块级缓存**（同一 categoryCode 只请求一次，并发调用共享同一个 Promise），
 * 而不是塞进全局 store：码表是纯只读的展示数据，没有跨页面的响应式联动需求，
 * 用 store 反而要额外处理"刷新"与"登出清理"。
 *
 * 【用法】
 * ```ts
 * const status = useCodeOptions('STUDENT_STATUS')
 *
 * onMounted(status.load)
 *
 * // 下拉框
 * // <el-option v-for="o in status.options.value" :key="o.value" :value="o.value" :label="o.label" />
 *
 * // 表格里把码值翻成中文
 * // status.nameOf(row.studentStatusCode)
 * ```
 */

export interface CodeOption {
  /** **码表里的码值**（如 `01`）—— 下拉框必须用它，不能自己编。 */
  value: string
  label: string
}

interface CacheEntry {
  items: Ref<CodeItemLookup[]>
  loaded: Ref<boolean>
  error: Ref<string | null>
  /** 进行中的请求。用它去重：两个组件同时 load 只会发一次请求。 */
  pending: Promise<void> | null
  /** 是否正在请求（响应式，用于下拉框的 loading）。 */
  loading: Ref<boolean>
}

/** 模块级缓存：一个 categoryCode 一份。 */
const cache = new Map<string, CacheEntry>()

function entryOf(categoryCode: string): CacheEntry {
  let entry = cache.get(categoryCode)

  if (!entry) {
    entry = {
      items: ref<CodeItemLookup[]>([]),
      loaded: ref(false),
      error: ref<string | null>(null),
      pending: null,
      loading: ref(false)
    }

    cache.set(categoryCode, entry)
  }

  return entry
}

export interface UseCodeOptionsReturn {
  /** 下拉框选项（value 来自码表，不是手写字面量）。 */
  options: ComputedRef<CodeOption[]>
  /** 是否正在请求（响应式）。 */
  loading: Ref<boolean>
  /** 是否已经有数据（含缓存命中）。 */
  loaded: Ref<boolean>
  /** 载入失败的原因；成功时为 null。 */
  error: Ref<string | null>
  /** 载入（幂等：已载入时直接返回，并发调用共享同一个请求）。 */
  load: () => Promise<void>
  /**
   * 码值 → 名称。
   *
   * 查不到时返回 `undefined` 而不是原样返回码值：调用方通常要写
   * `nameOf(code) ?? code ?? '—'`，把"码表里没有这个值"与"值本身就是空"
   * 两种情况的处理分开。**码表里查不到是要显式暴露的异常**（说明库里存了
   * 一个码表上不存在的值），所以这里不静默兜底成空串。
   */
  nameOf: (code?: string | null) => string | undefined
  /** 强制重新拉取（码表刚被改过时用；正常情况下不需要）。 */
  refresh: () => Promise<void>
}

/**
 * @param categoryCode 码类别编码，如 `STUDENT_STATUS` / `GENDER` / `CHANGE_APPLY_STATUS`。
 *   这些编码是后端 `CodeDataSeedContributor` 里种下的，**不能在前端拼**。
 */
export function useCodeOptions(categoryCode: string): UseCodeOptionsReturn {
  const entry = entryOf(categoryCode)

  const options = computed<CodeOption[]>(() =>
    entry.items.value.map((i) => ({ value: i.code, label: i.name }))
  )

  const index = computed(() => {
    const map = new Map<string, string>()

    for (const item of entry.items.value) {
      map.set(item.code, item.name)
    }

    return map
  })

  async function fetchItems(): Promise<void> {
    entry.error.value = null
    entry.loading.value = true

    try {
      entry.items.value = await codeApi.getItemsByCode(categoryCode)
      entry.loaded.value = true
    } catch (e) {
      // 失败**不抛**：调用方（onMounted）不处理异常时页面会白屏，
      // 而码表拉不到只应导致"下拉框为空 / 名称显示为码值"这种可降级的后果。
      // 但要把原因留下来，界面上给一个明确的提示，而不是静默空着。
      entry.error.value = e instanceof Error ? e.message : '码表载入失败'
      entry.loaded.value = false
    } finally {
      entry.loading.value = false
    }
  }

  async function load(): Promise<void> {
    if (entry.loaded.value) {
      return
    }

    // 并发去重：第二个调用者等第一个的请求，而不是再发一次
    entry.pending ??= fetchItems().finally(() => {
      entry.pending = null
    })

    await entry.pending
  }

  async function refresh(): Promise<void> {
    entry.loaded.value = false
    await fetchItems()
  }

  function nameOf(code?: string | null): string | undefined {
    if (!code) {
      return undefined
    }

    return index.value.get(code)
  }

  return {
    options,
    loading: entry.loading,
    loaded: entry.loaded,
    error: entry.error,
    load,
    nameOf,
    refresh
  }
}

/**
 * 码类别编码常量。
 *
 * 与后端 `CodeDataSeedContributor` 里的字面量一一对应 ——
 * 写成常量而不是各处散落的字符串，是因为"码类别写错"的表现是
 * **接口 200 但返回空数组**（查一个不存在的类别），下拉框静默变空，
 * 排查时很难想到是编码拼错了。
 */
export const CodeCategories = {
  /** 学籍状态：01 在读 / 02 转出 / … */
  StudentStatus: 'STUDENT_STATUS',
  /** 性别：1 男 / 2 女 / 0 未知（注意**不是** `GENDER`，类别编码就是 `SEX`）。 */
  Sex: 'SEX',
  /** 异动申请状态：Submitted / InProgress / … */
  ChangeApplyStatus: 'CHANGE_APPLY_STATUS',
  /** 学生异动类型码。 */
  ChangeType: 'CHANGE_TYPE',
  /** 监护人与学生关系（学生档案-监护人）。 */
  GuardianRelation: 'GUARDIAN_RELATION',
  /** 奖励类型 / 级别。 */
  RewardType: 'REWARD_TYPE',
  RewardLevel: 'REWARD_LEVEL',
  /** 处分类型。 */
  PunishType: 'PUNISH_TYPE',
  /** 资助类型。 */
  SubsidyType: 'SUBSIDY_TYPE',
  /** 健康状况。 */
  Health: 'HEALTH',

  // ---- 学生主档（新增 / 修改表单用） ----
  /** 身份证件类型（后端 `CreateUpdateStudentDto.IdCardTypeCode`）。 */
  IdType: 'ID_TYPE',
  /** 民族。 */
  Nation: 'NATION',
  /** 国家 / 地区。 */
  Country: 'COUNTRY',
  /** 政治面貌。 */
  Political: 'POLITICAL',
  /** 户口性质（后端字段是 `HouseholdNatureCode`，类别码是 `HOUSEHOLD`）。 */
  Household: 'HOUSEHOLD',
  /** 入学方式。 */
  EnrollType: 'ENROLL_TYPE',
  /** 就读方式。 */
  StudyMode: 'STUDY_MODE',
  /** 学制。 */
  SchoolLength: 'SCHOOL_LENGTH',
  /** 残疾类型。 */
  Disability: 'DISABILITY',
  /** 随迁子女类型。 */
  Migrant: 'MIGRANT',
  /** 留守儿童类型。 */
  LeftBehind: 'LEFT_BEHIND',

  // ---- 照片 / 元数据 / 审批（本轮补齐：这些此前是前端手写的码值） ----
  /** 照片状态：Pending / Normal / FallbackPending / Failed。 */
  PhotoStatus: 'PHOTO_STATUS',
  /** 组织层级：Province / City / County / School。 */
  OrgType: 'ORG_TYPE',
  /** 模块操作类型（元数据里 action 的 ActionType）。 */
  ActionType: 'ACTION_TYPE',
  /** 审批级别：School / County / City / Province。 */
  ApproveLevel: 'APPROVE_LEVEL',
  /** 流程待办状态：Pending / Approved / Rejected / Skipped。 */
  FlowTaskStatus: 'FLOW_TASK_STATUS'
} as const
