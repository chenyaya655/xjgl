import type { OrgDto } from '@/api/types'
import { OrgTypes } from '@/api/types'

/**
 * 组织树相关的纯函数工具。
 *
 * 抽出来的原因很实际：学生列表、照片管理、异动申请、流程预览四个页面
 * 都需要「把组织树拉平成学校下拉框」，各写一份迟早会有一份忘了滤掉
 * 非学校节点（教育局），于是下拉框里出现"某某县教育局"这种不该选中的项。
 */

/** 递归把所有学校节点拉平成一维数组。 */
export function flattenSchools(nodes: OrgDto[], result: OrgDto[] = []): OrgDto[] {
  for (const node of nodes) {
    if (node.orgType === OrgTypes.School) {
      result.push(node)
    }

    if (node.children?.length) {
      flattenSchools(node.children, result)
    }
  }

  return result
}

/** 在树里按 Id 找节点（用于显示层级信息）。 */
export function findOrg(nodes: OrgDto[], id: string): OrgDto | undefined {
  for (const node of nodes) {
    if (node.id === id) {
      return node
    }

    if (node.children?.length) {
      const hit = findOrg(node.children, id)

      if (hit) {
        return hit
      }
    }
  }

  return undefined
}

/** 把 Id 列表映射成「名称」用于表格展示。 */
export function nameOf(map: Map<string, string>, id?: string | null): string {
  return (id && map.get(id)) || '—'
}

/**
 * 当前学年（`2025-2026` 形式）。
 *
 * 【为什么需要一个"当前学年"】
 * 年级与班级挂在「学校 + 学年」下（见后端 `SysGrade`），所以任何"按年级/班级筛选"
 * 的查询都必须带学年。而用户的心智模型是"我要看现在的六年级"，
 * 不会主动想到"得先声明是哪一学年"。
 *
 * 这个推导刻意做得很朴素：**按自然年滚动**（9 月开学 → 学年跨两个自然年）。
 * 不引入"开学日期可配置"之类的机制 —— 学年的真实边界由各区县自行决定，
 * 前端猜不准；所以它只作为**默认值**，界面上始终可以手改（`academicYear` 是可选输入）。
 * 猜错的代价因此只是"默认值不对，改一下就行"，而不是"筛不出数据且不知道为什么"。
 */
export function currentAcademicYear(now: Date = new Date()): string {
  const year = now.getFullYear()

  // 8 月及以前算上一个学年（上学期还没开始），9 月起进入新学年。
  // 用 8 月而不是 9 月做分界：8 月已经在准备新学年的数据（分班、录新生）。
  const start = now.getMonth() + 1 >= 9 ? year : year - 1

  return `${start}-${start + 1}`
}
