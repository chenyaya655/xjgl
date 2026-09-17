<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

/**
 * 模块占位页。
 *
 * 什么时候会走到这里：管理员在「模块管理」里新建了一个模块并配了路径，
 * 但对应的前端页面还没开发（或 component 配错了名字）。
 *
 * 为什么要有这个页面而不是直接 404：
 *   模块是**运行期数据**，随时可能先于代码存在。此时给用户一个
 *   写清楚「哪个模块、什么路径、该找谁」的页面，比一个 404 有用得多；
 *   对开发者来说，这里显示的 component 字符串就是排查线索。
 */
const route = useRoute()

const title = computed(() => (route.meta.title as string | undefined) ?? '未知模块')
const groupName = computed(() => (route.meta.groupName as string | undefined) ?? '')
const component = computed(() => route.path)
</script>

<template>
  <el-card shadow="never">
    <el-result icon="info" :title="`「${title}」页面尚未实现`">
      <template #sub-title>
        <p v-if="groupName">所属子系统：{{ groupName }}</p>
        <p>访问路径：{{ component }}</p>
      </template>

      <template #extra>
        <el-alert
          type="info"
          :closable="false"
          show-icon
          title="模块元数据里已配置该菜单，但对应的前端页面还不存在。"
          description="开发人员需要在 src/views 下按模块元数据的 component 字段创建对应页面，或修正该字段。"
        />
      </template>
    </el-result>
  </el-card>
</template>
