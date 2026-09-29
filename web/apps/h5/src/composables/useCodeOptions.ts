import { computed, ref } from 'vue'
import type { ComputedRef, Ref } from 'vue'

import { codeApi } from '@xjgl/api'
import type { CodeItemLookup } from '@xjgl/api'

/**
 * H5 端的码值下拉 / 名称翻译（与 PC 端 `@/composables/useCodeOptions` 同一契约）。
 *
 * 【为什么 H5 也需要它】
 * H5 原来是**完全不看码表**的：直接渲染后端给的码值。表现是"我的"页面
 * 单位层级显示英文 `School` / `County`；一旦哪个页面要加状态下拉，
 * 又会变成"手写一套中文 + 后端另一套码值"的老问题。
 *
 * 【为什么不直接复用 PC 那份】
 * PC 的实现在 `web/apps/pc` 里，跨应用 import 会绑死两个应用的构建。
 * 真正的公共层是 `@xjgl/api`，而它**不依赖 vue**（只有 axios / element-plus）。
 * 把一个用到 `ref`/`computed` 的组合式函数塞进去，会把整个包变成"必须有 vue"，
 * 影响面比这几十行大得多 —— 所以这里按同一契约写一份薄的，改动范围可控。
 *
 * 【缓存口径与 PC 一致】按码类别缓存 + 并发去重，失败不抛
 * （下拉为空、名称回落到码值），绝不让"码表挂了"变成白屏。
 */
export interface CodeOption {
  value: string
  label: string
}

interface CacheEntry {
  items: Ref<CodeItemLookup[]>
  loading: Ref<boolean>
  loaded: Ref<boolean>
  pending: Promise<void> | null
}

const cache = new Map<string, CacheEntry>()

function entryOf(categoryCode: string): CacheEntry {
  let entry = cache.get(categoryCode)

  if (!entry) {
    entry = {
      items: ref<CodeItemLookup[]>([]),
      loading: ref(false),
      loaded: ref(false),
      pending: null
    }

    cache.set(categoryCode, entry)
  }

  return entry
}

export interface UseCodeOptionsReturn {
  options: ComputedRef<CodeOption[]>
  loading: Ref<boolean>
  loaded: Ref<boolean>
  load: () => Promise<void>
  /** 码值 → 中文名；查不到返回 undefined（调用方决定回落到码值还是 '—'）。 */
  nameOf: (code?: string | null) => string | undefined
}

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
    entry.loading.value = true

    try {
      entry.items.value = await codeApi.getItemsByCode(categoryCode)
      entry.loaded.value = true
    } catch {
      // 失败不抛：码表拉不到只应导致"下拉为空 / 名称显示为码值"这种可降级后果，
      // 不该让整个页面白屏
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

  function nameOf(code?: string | null): string | undefined {
    if (!code) {
      return undefined
    }

    return index.value.get(code)
  }

  return { options, loading: entry.loading, loaded: entry.loaded, load, nameOf }
}

/** 码类别编码常量（与后端 `CodeDataSeedContributor` 一一对应）。 */
export const CodeCategories = {
  /** 组织层级：Province / City / County / School。 */
  OrgType: 'ORG_TYPE',
  /** 流程待办状态。 */
  FlowTaskStatus: 'FLOW_TASK_STATUS',
  /** 异动申请状态。 */
  ChangeApplyStatus: 'CHANGE_APPLY_STATUS',
  /** 学籍状态。 */
  StudentStatus: 'STUDENT_STATUS'
} as const
