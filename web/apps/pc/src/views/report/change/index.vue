<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'

import { changeTypeApi, orgApi, reportApi, ChangeStatDimensions } from '@/api'
import type {
  ChangeStatDimension,
  ChangeType,
  OrgDto,
  StatisticItem,
  StatisticResult
} from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import SchoolSelect from '@/components/SchoolSelect.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 异动统计。
 *
 * 【与"学生统计"共享的三个判断】（数据范围说明、截断提示、码值翻译）见
 * report/student/index.vue 的注释，这里只说**异动特有的两点**：
 *
 *   1) **日期上界是"含当天"**：用户选"到 3 月 31 日"时期望包含当天。
 *      后端会把上界补到 23:59:59（见 StatisticsAppService），
 *      前端这里只需老老实实传 `yyyy-MM-dd`，**不能自己拼时间** ——
 *      自己拼一个 `T00:00:00` 过去就会把当天的申请全部漏掉。
 *
 *   2) **"按转出校 / 按转入校"是两个不同的口径**：一次转学申请同时有一个
 *      转出学校和一个转入学校。这两个维度必须分别看，因为
 *      "我校转出 30 人" 与 "我校转入 30 人" 是完全不同的两件事，
 *      而总数上它们长得一样。
 *
 * 【演示库暂无数据是正常的】
 * 异动申请是通过「异动申请」页提交的，演示种子不造申请记录
 * （见会话交接单的待决策第 2 条）。所以这个页面第一次打开很可能显示 0 ——
 * 这不是坏了。界面上要明确说出来，否则会被当成缺陷排查半天。
 */
const auth = useAuthStore()

const canQuery = computed(() => auth.can(MetadataPermissions.Rpt.Change.Query))

const loading = ref(false)
const schools = ref<OrgDto[]>([])
const types = ref<ChangeType[]>([])
const result = ref<StatisticResult | null>(null)

/** 申请日期区间。Element Plus 的 daterange 给的是 `[start, end]` 或 null。 */
const dateRange = ref<[string, string] | null>(null)

const query = reactive({
  dimension: ChangeStatDimensions.ChangeType as ChangeStatDimension,
  schoolOrgId: undefined as string | undefined,
  includeDescendantOrgs: true,
  changeTypeId: undefined as string | undefined,
  status: undefined as string | undefined
})

const dimensions: { value: ChangeStatDimension; label: string; hint: string }[] = [
  { value: ChangeStatDimensions.ChangeType, label: '按异动类型', hint: '转学 / 休学 / 复学 … 各有多少' },
  { value: ChangeStatDimensions.Status, label: '按审批状态', hint: '待审核 / 审核中 / 已通过 / 已驳回 …' },
  { value: ChangeStatDimensions.Month, label: '按申请月份', hint: '趋势看这个，键是 yyyy-MM' },
  { value: ChangeStatDimensions.SourceSchool, label: '按转出学校', hint: '谁转出去的' },
  { value: ChangeStatDimensions.TargetSchool, label: '按转入学校', hint: '谁转进来的 —— 与转出是两个口径' }
]

const currentHint = computed(() => dimensions.find((d) => d.value === query.dimension)?.hint ?? '')

/**
 * 模板里要比较维度值，所以把常量在本组件里取一个短名字。
 *
 * `ChangeStatDimensions` 是从 `@/api` 导入的，`<script setup>` 里**导入的绑定
 * 确实可以直接用在模板中**（这点和普通变量一致）；这里另外起名只是为了
 * 让模板读起来短一点，避免一行里出现两次长名字。
 */
const Dimensions = ChangeStatDimensions

// 异动申请状态与异动类型都来自码表 / 类型表，不在前端硬编码
const statusCodes = useCodeOptions(CodeCategories.ChangeApplyStatus)

/**
 * 分组键 → 展示名。
 *
 * `changeType` 维度后端已经给了类型名称；
 * `status` 维度给的是 `Submitted` 这类状态码，用码表翻译；
 * `month` 维度是 `yyyy-MM` 字符串，本身就可读。
 */
function displayName(item: StatisticItem): string {
  if (query.dimension === ChangeStatDimensions.Status) {
    return item.name ?? statusCodes.nameOf(item.key) ?? item.key
  }

  return item.name ?? item.key
}

const rows = computed(() => {
  const items = result.value?.items ?? []
  const max = items.reduce((m, i) => Math.max(m, i.count), 0)

  const mapped = items.map((item) => ({
    key: item.key,
    name: displayName(item),
    count: item.count,
    percent: max > 0 ? Math.round((item.count / max) * 100) : 0
  }))

  // 月份维度按时间正序（看趋势），其余按数量倒序（看重点）
  return query.dimension === ChangeStatDimensions.Month
    ? mapped.sort((a, b) => a.key.localeCompare(b.key))
    : mapped.sort((a, b) => b.count - a.count)
})

async function load(): Promise<void> {
  if (!canQuery.value) {
    return
  }

  loading.value = true

  try {
    result.value = await reportApi.getChangeStatistics({
      dimension: query.dimension,
      schoolOrgId: query.schoolOrgId,
      includeDescendantOrgs: query.includeDescendantOrgs,
      changeTypeId: query.changeTypeId,
      status: query.status,
      // 直接传 yyyy-MM-dd：后端负责把上界补到当天 23:59:59。
      // 前端自己拼时间会把"含当天"写成"不含当天"。
      from: dateRange.value?.[0],
      to: dateRange.value?.[1]
    })
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '统计失败，请稍后重试')
  } finally {
    loading.value = false
  }
}

function reset(): void {
  query.schoolOrgId = undefined
  query.includeDescendantOrgs = true
  query.changeTypeId = undefined
  query.status = undefined
  dateRange.value = null
  void load()
}

/** 导出当前统计结果（前端生成，理由同学生统计页：结果不超过 200 行）。 */
function exportCsv(): void {
  const data = result.value

  if (!data) {
    return
  }

  const lines = [
    `统计维度,${dimensionLabel()}`,
    `数据范围,${data.scopeDescription}`,
    `是否截断,${data.isTruncated ? '是（仅含前 200 组）' : '否'}`,
    '',
    '名称,数量'
  ]

  for (const row of rows.value) {
    lines.push(`"${row.name.replace(/"/g, '""')}",${row.count}`)
  }

  lines.push(`合计,${data.total}`)

  const blob = new Blob(['\uFEFF' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')

  link.href = url
  link.download = `异动统计_${dimensionLabel()}_${new Date().toISOString().slice(0, 10)}.csv`
  link.click()
  URL.revokeObjectURL(url)
}

function dimensionLabel(): string {
  return dimensions.find((d) => d.value === query.dimension)?.label ?? query.dimension
}

onMounted(async () => {
  const [, tree, typeList] = await Promise.all([
    Promise.all([statusCodes.load()]),
    orgApi.getTree().catch(() => [] as OrgDto[]),
    changeTypeApi.getTypes(false).catch(() => [] as ChangeType[])
  ])

  schools.value = flattenSchools(tree)
  types.value = typeList

  await load()
})
</script>

<template>
  <div class="report-change">
    <el-alert
      v-if="!canQuery"
      type="warning"
      :closable="false"
      show-icon
      title="你没有「异动统计查看」权限（Rpt.Change.Query），无法查看汇总数据。"
    />

    <template v-else>
      <el-card shadow="never" class="report-change__filters">
        <el-form :inline="true" @submit.prevent>
          <el-form-item label="统计维度">
            <el-radio-group v-model="query.dimension" @change="load">
              <el-radio-button v-for="d in dimensions" :key="d.value" :value="d.value">
                {{ d.label }}
              </el-radio-button>
            </el-radio-group>
          </el-form-item>

          <el-form-item label="学校">
            <SchoolSelect
              v-model="query.schoolOrgId"
              :schools="schools"
              placeholder="全部学校（按你的数据范围）"
              style="width: 240px"
            />
          </el-form-item>

          <el-form-item label="含下级">
            <el-switch v-model="query.includeDescendantOrgs" />
          </el-form-item>

          <el-form-item label="异动类型">
            <el-select
              v-model="query.changeTypeId"
              placeholder="全部"
              clearable
              style="width: 180px"
            >
              <el-option v-for="t in types" :key="t.id" :label="t.name" :value="t.id" />
            </el-select>
          </el-form-item>

          <el-form-item label="审批状态">
            <el-select v-model="query.status" placeholder="全部" clearable style="width: 140px">
              <el-option
                v-for="o in statusCodes.options.value"
                :key="o.value"
                :label="o.label"
                :value="o.value"
              />
            </el-select>
          </el-form-item>

          <el-form-item label="申请日期">
            <el-date-picker
              v-model="dateRange"
              type="daterange"
              value-format="YYYY-MM-DD"
              range-separator="至"
              start-placeholder="开始日期"
              end-placeholder="结束日期（含当天）"
              style="width: 260px"
            />
          </el-form-item>

          <el-form-item>
            <el-button type="primary" :loading="loading" @click="load">
              <AppIcon name="Search" />
              <span>统计</span>
            </el-button>
            <el-button @click="reset">重置</el-button>
            <el-button :disabled="!result" @click="exportCsv">
              <AppIcon name="Download" />
              <span>导出 CSV</span>
            </el-button>
          </el-form-item>
        </el-form>

        <div class="report-change__hint">
          {{ currentHint }}
          <span v-if="query.dimension === Dimensions.SourceSchool">
            （注意「转出」与「转入」是两个口径，同一次转学会同时计入两边的学校。）
          </span>
        </div>
      </el-card>

      <el-alert
        v-if="statusCodes.error.value"
        type="warning"
        :closable="false"
        show-icon
        :title="`审批状态码表未载入，下面可能显示 Submitted 这类英文码值（原因：${statusCodes.error.value}）。`"
        class="report-change__warn"
      />

      <el-alert
        v-if="result?.isTruncated"
        type="warning"
        :closable="false"
        show-icon
        title="分组数达到上限（200），下面的合计只包含已返回的分组，不是全部数据。"
        class="report-change__warn"
      />

      <el-card shadow="never">
        <div class="report-change__summary">
          <el-statistic title="合计（当前范围与筛选）" :value="result?.total ?? 0" />

          <div class="report-change__scope">
            <div class="report-change__scope-label">数据范围</div>
            <el-tag type="info" effect="plain">{{ result?.scopeDescription ?? '—' }}</el-tag>
          </div>

          <div class="report-change__scope">
            <div class="report-change__scope-label">分组数</div>
            <el-tag effect="plain">{{ rows.length }}</el-tag>
          </div>
        </div>

        <!-- 0 行时明确说明"这不是坏了"，否则会被当成缺陷排查 -->
        <el-empty
          v-if="!loading && result && rows.length === 0"
          description="当前范围与筛选下没有任何异动申请记录。演示库不含异动数据，需先在「异动申请」页提交申请后才有统计。"
        />

        <el-table v-else v-loading="loading" :data="rows" border stripe height="calc(100vh - 500px)">
          <el-table-column type="index" label="#" width="55" align="center" />
          <el-table-column prop="name" label="名称" min-width="220" show-overflow-tooltip />
          <el-table-column prop="count" label="数量" width="120" align="right" sortable />
          <el-table-column label="占比" min-width="240">
            <template #default="{ row }">
              <el-progress
                :percentage="row.percent"
                :stroke-width="14"
                :text-inside="true"
                :show-text="false"
              />
            </template>
          </el-table-column>
          <el-table-column label="占合计" width="100" align="right">
            <template #default="{ row }">
              {{ result && result.total > 0 ? ((row.count / result.total) * 100).toFixed(1) : '0.0' }}%
            </template>
          </el-table-column>
        </el-table>

        <div class="report-change__note">
          日期区间含结束当天（后端把上界补到 23:59:59）；「占比」条按最大分组为 100% 画。
        </div>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.report-change__filters {
  margin-bottom: 16px;
}

.report-change__filters :deep(.el-form-item) {
  margin-bottom: 8px;
}

.report-change__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.report-change__warn {
  margin-bottom: 12px;
}

.report-change__summary {
  display: flex;
  align-items: center;
  gap: 40px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.report-change__scope-label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  margin-bottom: 4px;
}

.report-change__note {
  margin-top: 12px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
