<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'

import { orgApi, reportApi, StudentStatDimensions } from '@/api'
import type { OrgDto, StatisticItem, StatisticResult, StudentStatDimension } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import GradeClassSelect from '@/components/GradeClassSelect.vue'
import SchoolSelect from '@/components/SchoolSelect.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 学生统计。
 *
 * 【这个页面唯一真正难的判断：一个数字到底代表什么】
 * 汇总数字本身是敏感信息（一个县的学生总数、各校人数对比），所以有三件事
 * 必须在界面上说清楚，**否则用户会得出错误结论**：
 *
 *   1) **数据范围**：县区用户选"某市"时，后端不会拒绝，而是**收敛**到他自己
 *      的范围（见 StatisticsAppService.ResolveScopeAsync）。如果界面不显示
 *      `scopeDescription`，他就以为"这个市只有 10 个学生"——
 *      实际是"你只能看到这 10 个"。所以范围说明放在最显眼的位置。
 *
 *   2) **是否被截断**：按学校统计分组数达到上限（200）时后端只返回前 200 组，
 *      而 `total` 也只是这 200 组之和。界面上"只显示前 200 所"与
 *      "一共就 200 所"长得一模一样，因此 `isTruncated` 必须显式提示。
 *
 *   3) **码值类维度的名称**：学籍状态 / 性别 / 有无照片这三个维度的分组键是
 *      码值（`01`、`1`、`true`），名称由前端用码表翻译 —— 直接显示键的话
 *      用户看到的是 "01 / 02 / true" 这种东西。
 *
 * 【为什么用表格而不是图表库】
 * 本系统没有引入任何图表库，而且统计的用途是"看数、对数、导出去核对"，
 * 条形图只是辅助。用一条 CSS 进度条表达"相对占比"即可，
 * 不为此背一个几百 KB 的依赖。
 */
const auth = useAuthStore()

const canQuery = computed(() => auth.can(MetadataPermissions.Rpt.Student.Query))

const loading = ref(false)
const schools = ref<OrgDto[]>([])
const result = ref<StatisticResult | null>(null)

/** 筛选条件。维度之外的过滤项必须与列表接口口径一致，否则"统计数"与"列表数"对不上。 */
const query = reactive({
  dimension: StudentStatDimensions.School as StudentStatDimension,
  schoolOrgId: undefined as string | undefined,
  includeDescendantOrgs: true,
  academicYear: '',
  // 年级 / 班级：接口一直支持（StudentStatisticsQueryDto.GradeId/ClassId），
  // 但界面此前没有入口 —— 而"按班级统计"不给班级维度加限制等于把全校班级混在一起看。
  gradeId: undefined as string | undefined,
  classId: undefined as string | undefined,
  studentStatusCode: undefined as string | undefined,
  genderCode: undefined as string | undefined
})

/** 年级 / 班级下拉挂在「学校 + 学年」下，学年留空时由组件回落到当前学年。 */
const gradeClassSelect = ref<{ reset: () => void } | null>(null)

/**
 * 维度选项。
 *
 * `codeTranslator` 决定这个维度的分组键怎么翻成中文：
 *   - `null` 表示后端已经给了名称（学校 / 年级 / 班级 / 学年）；
 *   - 否则用对应的码表翻译。
 * 把这张表写在一处，是为了避免"给某个维度忘了配翻译"——忘了的表现是
 * 表格里出现 `01` 这种码值，而它看起来不像是坏掉，像是有意为之。
 */
const dimensions: {
  value: StudentStatDimension
  label: string
  hint: string
}[] = [
  { value: StudentStatDimensions.School, label: '按学校', hint: '各校学生数，最常用的口径' },
  { value: StudentStatDimensions.Grade, label: '按年级', hint: '按年级名称汇总' },
  { value: StudentStatDimensions.Class, label: '按班级', hint: '按班级名称汇总，建议先选学校' },
  { value: StudentStatDimensions.StudentStatus, label: '按学籍状态', hint: '在读 / 转出 / 休学 …' },
  { value: StudentStatDimensions.Gender, label: '按性别', hint: '男 / 女 / 未知' },
  { value: StudentStatDimensions.AcademicYear, label: '按学年', hint: '各学年在册人数' },
  { value: StudentStatDimensions.HasPhoto, label: '按有无照片', hint: '照片采集进度' }
]

const currentHint = computed(
  () => dimensions.find((d) => d.value === query.dimension)?.hint ?? ''
)

// 码表：学籍状态、性别、有无照片（后者是布尔，不走码表）
const statusCodes = useCodeOptions(CodeCategories.StudentStatus)
const sexCodes = useCodeOptions(CodeCategories.Sex)

/**
 * 分组键 → 展示名。
 *
 * ⚠ `hasPhoto` 维度后端返回的键是 `'yes'` / `'no'`（见
 * `SqlSugarStatisticsQuery` 里的 `case when s.photo_path is null then 'no' else 'yes' end`），
 * **不是** `true` / `false`。这里最初就是按布尔字面量写的，结果是表格里
 * 出现两行分别叫 "yes" 和 "no" —— 它看起来不像坏掉，像是有意为之，很容易漏。
 */
function displayName(item: StatisticItem): string {
  if (query.dimension === StudentStatDimensions.HasPhoto) {
    return item.key === 'yes' ? '有照片' : item.key === 'no' ? '缺照片' : item.key
  }

  if (query.dimension === StudentStatDimensions.StudentStatus) {
    return item.name ?? statusCodes.nameOf(item.key) ?? item.key
  }

  if (query.dimension === StudentStatDimensions.Gender) {
    return item.name ?? sexCodes.nameOf(item.key) ?? item.key
  }

  // 学校 / 年级 / 班级 / 学年：后端已经给了名称
  return item.name ?? item.key
}

/**
 * 结果行。
 *
 * `percent` 用于画条形图：**以最大分组为 100%**，而不是以总数为 100%。
 * 以总数为分母时，学校多起来每一行都短得看不见（40 个学生分到 8 所学校，
 * 最大的一所也只占 25%），条形图就失去了"看出差异"的作用。
 */
const rows = computed(() => {
  const items = result.value?.items ?? []
  const max = items.reduce((m, i) => Math.max(m, i.count), 0)

  return items
    .map((item) => ({
      key: item.key,
      name: displayName(item),
      count: item.count,
      percent: max > 0 ? Math.round((item.count / max) * 100) : 0
    }))
    .sort((a, b) => b.count - a.count)
})

/** 是否用了码表翻译（用于在码表没载入时给出提示，而不是干显示码值）。 */
const needsCodeTable = computed(
  () =>
    query.dimension === StudentStatDimensions.StudentStatus ||
    query.dimension === StudentStatDimensions.Gender
)

const codeTableError = computed(() => statusCodes.error.value ?? sexCodes.error.value)

async function load(): Promise<void> {
  if (!canQuery.value) {
    return
  }

  loading.value = true

  try {
    result.value = await reportApi.getStudentStatistics({
      dimension: query.dimension,
      schoolOrgId: query.schoolOrgId,
      includeDescendantOrgs: query.includeDescendantOrgs,
      academicYear: query.academicYear || undefined,
      gradeId: query.gradeId,
      classId: query.classId,
      studentStatusCode: query.studentStatusCode,
      genderCode: query.genderCode
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
  query.academicYear = ''
  query.gradeId = undefined
  query.classId = undefined
  query.studentStatusCode = undefined
  query.genderCode = undefined

  // 一并清掉下拉里的选项列表（只清 v-model 会留下上一所学校的年级）
  gradeClassSelect.value?.reset()

  void load()
}

/**
 * 导出当前统计结果为 CSV。
 *
 * 在**前端**生成而不是走后台导出任务：统计结果最多 200 行，
 * 是已经在内存里的一小块数据，为它排一个后台任务再轮询下载，
 * 对用户来说是"点一下要等十几秒"的负体验。行数有上限，不存在
 * 导出把请求线程占满的问题（这正是名单类导出必须后台化的原因）。
 *
 * 加 BOM：不加的话 Excel 打开中文会是乱码（MS Excel 对无 BOM 的
 * UTF-8 按本地代码页解释）。
 */
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
    // 名称里可能有逗号或引号（学校名带「、」很常见），必须按 CSV 规则转义
    lines.push(`"${row.name.replace(/"/g, '""')}",${row.count}`)
  }

  lines.push(`合计,${data.total}`)

  const blob = new Blob(['\uFEFF' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' })

  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')

  link.href = url
  link.download = `学生统计_${dimensionLabel()}_${new Date().toISOString().slice(0, 10)}.csv`
  link.click()
  URL.revokeObjectURL(url)
}

function dimensionLabel(): string {
  return dimensions.find((d) => d.value === query.dimension)?.label ?? query.dimension
}

onMounted(async () => {
  // 码表与组织树并行拉取：两者互不依赖，串行会白白多等一个往返
  const [, tree] = await Promise.all([
    Promise.all([statusCodes.load(), sexCodes.load()]),
    orgApi.getTree().catch(() => [] as OrgDto[])
  ])

  schools.value = flattenSchools(tree)

  await load()
})
</script>

<template>
  <div class="report-student">
    <el-alert
      v-if="!canQuery"
      type="warning"
      :closable="false"
      show-icon
      title="你没有「学生统计查看」权限（Rpt.Student.Query），无法查看汇总数据。"
    />

    <template v-else>
      <el-card shadow="never" class="report-student__filters">
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
              style="width: 260px"
            />
          </el-form-item>

          <el-form-item label="含下级">
            <el-switch v-model="query.includeDescendantOrgs" />
          </el-form-item>

          <el-form-item label="学年">
            <el-input
              v-model="query.academicYear"
              placeholder="如 2025-2026，留空不限"
              clearable
              style="width: 180px"
            />
          </el-form-item>

          <!--
            年级 / 班级：接口一直支持这两个过滤项，但先前没有入口。
            "按班级统计"不给班级维度任何限制时，全校各届班级会混在一张表里，
            数字看着对、其实没有意义。
          -->
          <el-form-item label="年级 / 班级">
            <GradeClassSelect
              ref="gradeClassSelect"
              v-model:grade-id="query.gradeId"
              v-model:class-id="query.classId"
              :school-org-id="query.schoolOrgId"
              :academic-year="query.academicYear"
              show="both"
            />
          </el-form-item>

          <el-form-item label="学籍状态">
            <el-select
              v-model="query.studentStatusCode"
              placeholder="全部"
              clearable
              style="width: 140px"
            >
              <el-option
                v-for="o in statusCodes.options.value"
                :key="o.value"
                :label="o.label"
                :value="o.value"
              />
            </el-select>
          </el-form-item>

          <el-form-item label="性别">
            <el-select v-model="query.genderCode" placeholder="全部" clearable style="width: 110px">
              <el-option
                v-for="o in sexCodes.options.value"
                :key="o.value"
                :label="o.label"
                :value="o.value"
              />
            </el-select>
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

        <div class="report-student__hint">{{ currentHint }}</div>
      </el-card>

      <el-alert
        v-if="needsCodeTable && codeTableError"
        type="warning"
        :closable="false"
        show-icon
        :title="`码表未载入，下面可能显示码值而不是中文（原因：${codeTableError}）。`"
        class="report-student__warn"
      />

      <!--
        截断提示：这是本页面最不能省的一句话。
        "只显示前 200 所" 与 "一共就 200 所" 在表格里长得一模一样，
        而合计数字也只覆盖已返回的分组。
      -->
      <el-alert
        v-if="result?.isTruncated"
        type="warning"
        :closable="false"
        show-icon
        title="分组数达到上限（200），下面的合计只包含已返回的分组，不是全部数据。"
        class="report-student__warn"
      >
        <template #default>
          请缩小范围（选具体学校或限定学年）后再看，否则会把"只显示了前 200 组"
          当成"一共就这么多"。
        </template>
      </el-alert>

      <el-card shadow="never">
        <div class="report-student__summary">
          <el-statistic title="合计（当前范围与筛选）" :value="result?.total ?? 0" />

          <div class="report-student__scope">
            <div class="report-student__scope-label">数据范围</div>
            <!--
              范围说明必须显眼：县区用户选上级时会**收敛**到自己的范围，
              不说明的话他会以为"这个市只有这么点学生"。
            -->
            <el-tag type="info" effect="plain">{{ result?.scopeDescription ?? '—' }}</el-tag>
          </div>

          <div class="report-student__scope">
            <div class="report-student__scope-label">分组数</div>
            <el-tag effect="plain">{{ rows.length }}</el-tag>
          </div>
        </div>

        <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 460px)">
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

        <div class="report-student__note">
          「占比」条按**最大分组**为 100% 画（这样分组多的时候差异仍然看得出来），
          最右侧的「占合计」才是相对总数的真实百分比。
        </div>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.report-student__filters {
  margin-bottom: 16px;
}

.report-student__filters :deep(.el-form-item) {
  margin-bottom: 8px;
}

.report-student__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.report-student__warn {
  margin-bottom: 12px;
}

.report-student__summary {
  display: flex;
  align-items: center;
  gap: 40px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.report-student__scope-label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  margin-bottom: 4px;
}

.report-student__note {
  margin-top: 12px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
