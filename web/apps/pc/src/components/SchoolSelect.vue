<script setup lang="ts">
import { computed } from 'vue'

import type { OrgDto } from '@/api/types'

/**
 * 学校下拉框（**虚拟滚动**）。
 *
 * 【为什么不能用 el-select + el-option】
 * 本系统是全省统一部署：组织树里的学校数量是"几万"这个量级
 * （本机测试库实测 **6,789 所**）。
 * `el-select` 会把每个选项渲染成真实 DOM —— 实测打开「批量上传照片」对话框后
 * 页面里多出 **6,799 个 `.el-select-dropdown__item`**、总节点从 1,231 涨到 29,867，
 * **主线程被卡住 100 秒以上**；表现是"对话框点不开、页面假死"，
 * 而接口、类型检查、集成测试全都是绿的 —— 只有真在浏览器里点一遍才看得见。
 *
 * `el-select-v2` 用虚拟滚动只渲染可见的十几项，DOM 与学校总数无关（常数级）。
 *
 * 【为什么不做成"服务端搜索"】
 * "选学校"的可选范围本来就由权限范围（数据范围）限定，是有限集合，
 * 本地 `filterable` 过滤只是内存里的字符串匹配，反而比往返接口更快，
 * 也不引入新的接口与分页语义。真正需要服务端搜索的是"按姓名/辅号选人"那种无边界集合。
 *
 * 【为什么包一层组件而不是各处写 el-select-v2】
 * 本项目有 6 处"选学校"（学生列表、照片台账缺照片名单、照片批量上传、异动申请、
 * 异动类型预览 ×2）。这种"性能正确性"不应该靠每个页面作者记得，
 * 抽成一个组件后，**以后新增页面只要用 SchoolSelect 就自动是对的**。
 */
const model = defineModel<string | undefined>()

const props = withDefaults(
  defineProps<{
    /** 已经拉平好的学校列表（见 utils/org.ts 的 flattenSchools） */
    schools: OrgDto[]
    placeholder?: string
    clearable?: boolean
    disabled?: boolean
  }>(),
  {
    placeholder: '请选择学校',
    clearable: true,
    disabled: false
  }
)

const emit = defineEmits<{ change: [value: string | undefined] }>()

/** select-v2 要的是 { label, value } 结构；只做映射，过滤交给组件内部。 */
const options = computed(() => props.schools.map((s) => ({ label: s.name, value: s.id })))

/**
 * 清空时 Element Plus 给的值在不同版本/不同清空路径下可能是 `''` 或 `undefined`，
 * 统一归一成 `undefined`：后端的"不筛选"语义是"这个字段不传"，不是传空串
 * （空串会被当成"找一个 Id 为空串的学校"，结果是空列表）。
 */
const inner = computed<string | undefined>({
  get: () => model.value ?? undefined,
  set: (value) => {
    model.value = value === '' || value === null ? undefined : value
  }
})

function onChange(): void {
  emit('change', inner.value)
}
</script>

<template>
  <el-select-v2
    v-model="inner"
    :options="options"
    :placeholder="placeholder"
    :clearable="clearable"
    :disabled="disabled"
    filterable
    @change="onChange"
  />
</template>
