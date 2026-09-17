<script setup lang="ts">
import * as ElementPlusIcons from '@element-plus/icons-vue'
import { computed, type Component } from 'vue'

/**
 * 按名字渲染 Element Plus 图标。
 *
 * 菜单图标名来自后端元数据（`sys_module.icon`，如 `User` / `OfficeBuilding`），
 * 是**运行期字符串**，没法直接当组件用。
 *
 * 为什么不把几百个图标全部 `app.component()` 注册成全局组件：
 *   那样会让每个图标都成为 Vue 的全局解析目标，模板编译期无法 tree-shake，
 *   而且多端（H5 / 小程序）的构建目标对全局组件也更敏感。
 *   这里按需查表，未识别的名字回落到一个默认图标而不是报错 ——
 *   新增模块时忘了配图标名，界面不该白屏。
 */
const props = defineProps<{ name?: string | null }>()

const registry = ElementPlusIcons as unknown as Record<string, Component>

const resolved = computed<Component>(() => {
  const name = props.name?.trim()

  if (name && registry[name]) {
    return registry[name]
  }

  return ElementPlusIcons.Menu
})
</script>

<template>
  <el-icon>
    <component :is="resolved" />
  </el-icon>
</template>
