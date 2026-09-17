import type { OrgDto } from '@/api/types'

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
    if (node.orgType === 'School') {
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
