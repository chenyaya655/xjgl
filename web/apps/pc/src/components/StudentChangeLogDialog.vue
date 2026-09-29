<script setup lang="ts">
import { reactive, ref, watch } from 'vue'

import { studentChangeLogApi } from '@xjgl/api'
import type { StudentChangeLog } from '@xjgl/api'
import AppIcon from '@/components/AppIcon.vue'

/**
 * 全局变更留痕查询对话框。
 *
 * 【为什么做成学生列表页上的对话框，而不是新开一个菜单/路由】
 * 本系统的路由与菜单完全由后端元数据（`sys_module`）驱动（见 `router/index.ts`），
 * 独立页面就必须在「模块管理」里新增一条模块记录、再给每个角色授权 ——
 * 而"查留痕"在业务上不是一个新的功能入口，它就是学生管理的一部分
 * （同 `StudentProfileDrawer` 里那个按学生查的签页）。做成对话框，
 * 用户既有的权限（`Stu.Student.Query`）就能直接用上。
 *
 * 【为什么全局查询是必要的（而不是只在学生详情里看）】
 * 留痕要回答的两类问题只有全局查询答得了：
 *   1) **某个操作人**在某个时间段改过什么（审计抽查）；
 *   2) 某个学生的档案在**转学之前**的变更（那时的记录属于原学校，
 *      从现在的学校进去翻学生详情同样看得到，但前提是知道去翻谁）。
 * 因此这里按「动作 + 时间」在**数据范围内**全局检索。
 */
const visible = defineModel<boolean>({ default: false })

const loading = ref(false)
const rows = ref<StudentChangeLog[]>([])
const total = ref(0)
const page = ref(1)

/** 申请日期区间。Element Plus 的 daterange 给的是 `[start, end]` 或 null。 */
const dateRange = ref<[string, string] | null>(null)

const query = reactive({
  keyword: '',
  action: undefined as string | undefined,
  maxResultCount: 20
})

/**
 * 动作类型下拉项。
 *
 * 【为什么这里没有走 `useCodeOptions`】
 * 变更动作（Create / Update / Delete / Change / Import）是后端的**领域常量**
 * `StudentChangeActions`（`src/Xjgl.Domain/Students/StuChangeLog.cs`），
 * **它不在码表里**：`CodeDataSeedContributor` 没有对应类别，后端也没有暴露
 * "有哪些动作"的查询接口。给 `CodeCategories` 硬加一个码表上不存在的类别，
 * 结果正是 `useCodeOptions` 注释里警告过的失败形态 —— 查一个不存在的类别，
 * 接口 200 但返回空数组，下拉框**静默变空**（而这次连后端种子都不能改）。
 *
 * 因此只能在此镜像"值 + 中文名"，与后端 `GetName` 一一对应。
 * 注意表格里显示的始终是后端回来的 `actionName`，所以后端改了文案时
 * 表格不会漂移，只有这个下拉的文案需要同步。
 */
const CHANGE_ACTIONS: { value: string; label: string }[] = [
  { value: 'Create', label: '新建学籍' },
  { value: 'Update', label: '修改学籍' },
  { value: 'Delete', label: '删除学籍' },
  { value: 'Change', label: '学籍异动' },
  { value: 'Import', label: '批量导入' }
]

/**
 * 关键字（学生姓名 / 学籍辅号）走**服务端**过滤。
 *
 * 【这里原本是一个"当前页过滤"的权宜之计，现在换掉了】
 * 最初后端 `GetListAsync` 根本没读 `StudentId`、也没有姓名关键字，
 * 所以只能在前端对"当前这一页"做过滤，并在界面上写明"只过滤本页"——
 * 那是为了不制造"接口 200、结果一条不少"的假象。
 *
 * 后来后端补齐了 `StudentId` 与 `Keyword`（`StudentChangeLogQueryDto`），
 * 于是这里恢复成真正的服务端过滤：`totalCount` 也是命中总数，
 * 分页、翻页都不会漏。表格直接绑 `rows`，不再需要"本页过滤"那一层。
 */

/**
 * 拉取一页留痕。
 *
 * 失败**不弹自己的提示**（拦截器已经弹过，见 `http.ts` 的错误处理约定），
 * 但要把列表清空：否则查询失败后界面还挂着上一次的结果，
 * 用户会以为自己看到的是新条件的结果。
 */
async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await studentChangeLogApi.getList({
      // 服务端按姓名 / 学籍辅号过滤（后端 `StudentChangeLogQueryDto.Keyword`）
      keyword: query.keyword.trim() || undefined,
      action: query.action,
      // 起止都到"秒"：后端用的是 `OccurredAt <= To` 的直接比较，
      // 只传 yyyy-MM-dd 的话，结束日当天的记录会全部被排除掉。
      from: dateRange.value?.[0] ? `${dateRange.value[0]}T00:00:00` : undefined,
      to: dateRange.value?.[1] ? `${dateRange.value[1]}T23:59:59` : undefined,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'OccurredAt DESC'
    })

    rows.value = result.items
    total.value = result.totalCount
  } catch {
    rows.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

function reset(): void {
  query.keyword = ''
  query.action = undefined
  dateRange.value = null
  search()
}

function formatTime(value?: string | null): string {
  return value ? value.replace('T', ' ').slice(0, 19) : '—'
}

// 每次打开都回到第一页重新查：留痕每天都在长，没必要保留上次的位置
watch(visible, (opened) => {
  if (opened) {
    page.value = 1
    void load()
  }
})
</script>

<template>
  <el-dialog v-model="visible" title="变更留痕" width="1100px" top="6vh" destroy-on-close>
    <el-alert
      type="info"
      :closable="false"
      show-icon
      class="change-log__tip"
      title="留痕由系统在业务操作时自动写入，只读不可修改（能被改的审计日志等于没有审计）。查询范围由你的数据权限决定；身份证号只留摘要前缀，明文不会出现在流水里。"
    />

    <el-form :inline="true" class="change-log__filter" @submit.prevent="search">
      <el-form-item label="动作类型">
        <el-select v-model="query.action" placeholder="全部" clearable style="width: 140px">
          <el-option v-for="a in CHANGE_ACTIONS" :key="a.value" :label="a.label" :value="a.value" />
        </el-select>
      </el-form-item>

      <el-form-item label="变更日期">
        <el-date-picker
          v-model="dateRange"
          type="daterange"
          value-format="YYYY-MM-DD"
          unlink-panels
          start-placeholder="开始日期"
          end-placeholder="结束日期"
          style="width: 260px"
        />
      </el-form-item>

      <el-form-item label="学生">
        <el-input
          v-model="query.keyword"
          placeholder="姓名 / 学籍辅号"
          clearable
          style="width: 220px"
          @keyup.enter="search"
        >
          <template #prefix>
            <AppIcon name="Search" />
          </template>
        </el-input>
      </el-form-item>

      <el-form-item>
        <el-button type="primary" :loading="loading" @click="search">查询</el-button>
        <el-button @click="reset">重置</el-button>
      </el-form-item>
    </el-form>

    <!--
      这里原来有一条"关键字只过滤当前页"的警告 —— 因为后端当时既不读 StudentId、
      也没有姓名关键字，只能在前端对当页结果做过滤。
      后端补齐 `Keyword` 之后，过滤已经是服务端的（总数也是命中总数），
      警告随之删除：留着会让用户以为结果不全。
    -->

    <el-table
      v-loading="loading"
      :data="rows"
      border
      stripe
      max-height="56vh"
      empty-text="没有符合条件的变更记录"
    >
      <el-table-column label="变更时间" width="170">
        <template #default="{ row }">{{ formatTime(row.occurredAt) }}</template>
      </el-table-column>

      <el-table-column label="学生" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">
          <span class="change-log__name">{{ row.studentName }}</span>
          <span class="change-log__no">{{ row.studentNo }}</span>
        </template>
      </el-table-column>

      <el-table-column label="动作" width="100" align="center">
        <template #default="{ row }">
          <el-tag size="small" effect="plain">{{ row.actionName ?? row.action }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="字段" width="120" show-overflow-tooltip>
        <template #default="{ row }">{{ row.fieldLabel ?? '—' }}</template>
      </el-table-column>

      <!-- 字段级留痕：真正要看的是"改了哪个字段、从什么值变成什么值" -->
      <el-table-column label="变更（旧值 → 新值）" min-width="280" show-overflow-tooltip>
        <template #default="{ row }">
          <span v-if="row.oldValue || row.newValue">
            <span class="change-log__old">{{ row.oldValue ?? '（空）' }}</span>
            <span class="change-log__arrow">→</span>
            <span class="change-log__new">{{ row.newValue ?? '（空）' }}</span>
          </span>
          <span v-else>—</span>
        </template>
      </el-table-column>

      <el-table-column label="操作人" width="120" show-overflow-tooltip>
        <template #default="{ row }">{{ row.operatorName ?? '系统' }}</template>
      </el-table-column>

      <el-table-column label="来源" width="110" show-overflow-tooltip>
        <template #default="{ row }">{{ row.source ?? '—' }}</template>
      </el-table-column>

      <el-table-column label="原因" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">{{ row.reason ?? '—' }}</template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-model:current-page="page"
      v-model:page-size="query.maxResultCount"
      class="change-log__pager"
      :total="total"
      layout="total, sizes, prev, pager, next, jumper"
      :page-sizes="[20, 50, 100]"
      @current-change="load"
      @size-change="search"
    />

    <template #footer>
      <el-button @click="visible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.change-log__tip {
  margin-bottom: 12px;
}

.change-log__filter :deep(.el-form-item) {
  margin-bottom: 12px;
}

.change-log__name {
  margin-right: 6px;
}

.change-log__no {
  color: var(--el-text-color-secondary);
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
}

.change-log__old {
  color: var(--el-text-color-secondary);
  text-decoration: line-through;
}

.change-log__arrow {
  margin: 0 6px;
  color: var(--el-text-color-secondary);
}

.change-log__new {
  color: var(--el-color-primary);
  font-weight: 600;
}

.change-log__pager {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>
