<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { codeApi, studentChangeLogApi, studentProfileApi } from '@xjgl/api'
import type {
  CodeItemLookup,
  Guardian,
  GuardianInput,
  Physical,
  PhysicalInput,
  Punishment,
  PunishmentInput,
  Resume,
  ResumeInput,
  Reward,
  RewardInput,
  StudentChangeLog,
  StudentListItem,
  Subsidy,
  SubsidyInput
} from '@xjgl/api'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 学生详情抽屉（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）。
 *
 * 【为什么是抽屉而不是独立路由】
 * 本系统的路由完全由后端元数据（`sys_module`）驱动（见 router/index.ts 的注释）。
 * 详情页如果写成独立路由，就必须在"模块管理"里给它建一条模块记录，
 * 否则刷新页面会 404 —— 而"学生详情"显然不是一个可以独立授权的功能菜单，
 * 它只是学生列表的一部分。因此按本仓库既有的做法（`ChangeApplyDetail.vue`）做成组件。
 *
 * 【为什么表单是"字段描述 + 一套模板"而不是六个手写表单】
 * 六张子表的表单结构完全一样（文本/数字/日期/下拉/开关/备注），
 * 手写六遍的话，改一个校验规则要改六处，而且必然有漏。
 * 因此把"有哪些字段"描述成数据（见 fields），模板只写一遍。
 */
const props = defineProps<{
  modelValue: boolean
  student: StudentListItem | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  /** 子表数据发生变化（用于让父页面刷新，比如"是否有照片"之类的汇总列）。 */
  changed: []
}>()

const auth = useAuthStore()

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value)
})

const canEdit = computed(() => auth.can(MetadataPermissions.Stu.Student.Update))

const studentId = computed(() => props.student?.id ?? '')

// ---------------------------------------------------------------------------
// 标签页
// ---------------------------------------------------------------------------

type TabKey = 'guardian' | 'resume' | 'reward' | 'punishment' | 'subsidy' | 'physical' | 'changeLog'

const tabs: { key: TabKey; label: string }[] = [
  { key: 'guardian', label: '监护人' },
  { key: 'resume', label: '学习简历' },
  { key: 'reward', label: '奖励' },
  { key: 'punishment', label: '处分' },
  { key: 'subsidy', label: '资助' },
  { key: 'physical', label: '体质健康' },
  // 只读：能被改的审计日志等于没有审计，因此这一页没有新增/编辑
  { key: 'changeLog', label: '变更记录' }
]

const activeTab = ref<TabKey>('guardian')
const loading = ref(false)

// ---------------------------------------------------------------------------
// 数据
// ---------------------------------------------------------------------------

const guardians = ref<Guardian[]>([])
const resumes = ref<Resume[]>([])
const rewards = ref<Reward[]>([])
const punishments = ref<Punishment[]>([])
const subsidies = ref<Subsidy[]>([])
const physicals = ref<Physical[]>([])
const changeLogs = ref<StudentChangeLog[]>([])

async function loadRows(): Promise<void> {
  if (!studentId.value) {
    return
  }

  loading.value = true

  try {
    const id = studentId.value

    switch (activeTab.value) {
      case 'guardian':
        guardians.value = await studentProfileApi.getGuardians(id)
        break
      case 'resume':
        resumes.value = await studentProfileApi.getResumes(id)
        break
      case 'reward':
        rewards.value = await studentProfileApi.getRewards(id)
        break
      case 'punishment':
        punishments.value = await studentProfileApi.getPunishments(id)
        break
      case 'subsidy':
        subsidies.value = await studentProfileApi.getSubsidies(id)
        break
      case 'physical':
        physicals.value = await studentProfileApi.getPhysicals(id)
        break
      case 'changeLog':
        changeLogs.value = await studentChangeLogApi.getStudentLogs(id)
        break
    }
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------------------
// 码表选项：**一律从后端码表取，不在前端硬编码**
// ---------------------------------------------------------------------------

interface SelectOption {
  value: string
  label: string
}

const relationOptions = ref<SelectOption[]>([])
const rewardTypeOptions = ref<SelectOption[]>([])
const rewardLevelOptions = ref<SelectOption[]>([])
const punishTypeOptions = ref<SelectOption[]>([])
const subsidyTypeOptions = ref<SelectOption[]>([])
const healthOptions = ref<SelectOption[]>([])

function toOptions(items: CodeItemLookup[]): SelectOption[] {
  return items.map((i) => ({ value: i.code, label: i.name }))
}

let optionsLoaded = false

async function loadOptions(): Promise<void> {
  if (optionsLoaded) {
    return
  }

  const safe = async (categoryCode: string): Promise<CodeItemLookup[]> => {
    try {
      return await codeApi.getItemsByCode(categoryCode)
    } catch {
      // 码表缺失不应让详情打不开：下拉框为空，但其它字段照常可编辑
      return []
    }
  }

  const [relations, rewardTypes, rewardLevels, punishTypes, subsidyTypes, health] = await Promise.all([
    safe('GUARDIAN_RELATION'),
    safe('REWARD_TYPE'),
    safe('REWARD_LEVEL'),
    safe('PUNISH_TYPE'),
    safe('SUBSIDY_TYPE'),
    safe('HEALTH')
  ])

  relationOptions.value = toOptions(relations)
  rewardTypeOptions.value = toOptions(rewardTypes)
  rewardLevelOptions.value = toOptions(rewardLevels)
  punishTypeOptions.value = toOptions(punishTypes)
  subsidyTypeOptions.value = toOptions(subsidyTypes)
  healthOptions.value = toOptions(health)
  optionsLoaded = true
}

// ---------------------------------------------------------------------------
// 字段描述（表单只写一套模板，字段由这里驱动）
// ---------------------------------------------------------------------------

type FieldType = 'text' | 'number' | 'date' | 'select' | 'switch' | 'textarea'

interface Field {
  prop: string
  label: string
  type: FieldType
  options?: SelectOption[]
  required?: boolean
  /** 数字输入用的精度与范围（金额 2 位、身高体重视力 1 位） */
  precision?: number
  min?: number
  max?: number
  placeholder?: string
  /** 新增时的默认值 */
  default?: string | number | boolean | null
}

function today(): string {
  return new Date().toISOString().slice(0, 10)
}

const fields = computed<Field[]>(() => {
  switch (activeTab.value) {
    case 'guardian':
      return [
        { prop: 'name', label: '姓名', type: 'text', required: true },
        { prop: 'relationCode', label: '与学生关系', type: 'select', options: relationOptions.value },
        { prop: 'idCardNumber', label: '身份证号', type: 'text', placeholder: '留空表示不修改' },
        { prop: 'phoneNumber', label: '联系电话', type: 'text' },
        { prop: 'workUnit', label: '工作单位', type: 'text' },
        { prop: 'address', label: '现住址', type: 'text' },
        { prop: 'isPrimary', label: '第一监护人', type: 'switch', default: false },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'resume':
      return [
        { prop: 'schoolName', label: '学校（园）名称', type: 'text', required: true },
        { prop: 'startDate', label: '开始日期', type: 'date' },
        { prop: 'endDate', label: '结束日期', type: 'date', placeholder: '在读可留空' },
        { prop: 'gradeName', label: '年级', type: 'text' },
        { prop: 'className', label: '班级', type: 'text' },
        { prop: 'post', label: '担任职务', type: 'text' },
        { prop: 'referee', label: '证明人', type: 'text' },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'reward':
      return [
        { prop: 'rewardDate', label: '获奖日期', type: 'date', required: true, default: today() },
        { prop: 'rewardName', label: '奖励名称', type: 'text', required: true },
        { prop: 'rewardTypeCode', label: '奖励类型', type: 'select', options: rewardTypeOptions.value },
        { prop: 'rewardLevelCode', label: '奖励级别', type: 'select', options: rewardLevelOptions.value },
        { prop: 'awardOrg', label: '授奖单位', type: 'text' },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'punishment':
      return [
        { prop: 'punishDate', label: '处分日期', type: 'date', required: true, default: today() },
        { prop: 'punishName', label: '处分名称', type: 'text', required: true },
        { prop: 'punishTypeCode', label: '处分类型', type: 'select', options: punishTypeOptions.value },
        { prop: 'punishOrg', label: '决定单位', type: 'text' },
        { prop: 'isRevoked', label: '已撤销', type: 'switch', default: false },
        { prop: 'revokeDate', label: '撤销日期', type: 'date', placeholder: '撤销时自动填写' },
        { prop: 'revokeReason', label: '撤销原因', type: 'text' },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'subsidy':
      return [
        {
          prop: 'academicYear',
          label: '学年',
          type: 'text',
          required: true,
          default: props.student?.academicYear ?? '',
          placeholder: '如 2025-2026'
        },
        { prop: 'subsidyName', label: '资助项目', type: 'text', required: true },
        { prop: 'subsidyTypeCode', label: '资助类型', type: 'select', options: subsidyTypeOptions.value },
        { prop: 'amount', label: '金额（元）', type: 'number', precision: 2, min: 0 },
        { prop: 'subsidyDate', label: '发放日期', type: 'date' },
        { prop: 'fundingOrg', label: '资助方', type: 'text' },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'physical':
      return [
        {
          prop: 'academicYear',
          label: '学年',
          type: 'text',
          required: true,
          default: props.student?.academicYear ?? '',
          placeholder: '一个学年一条'
        },
        { prop: 'checkDate', label: '体检日期', type: 'date', default: today() },
        { prop: 'height', label: '身高（厘米）', type: 'number', precision: 1, min: 0, max: 300 },
        { prop: 'weight', label: '体重（千克）', type: 'number', precision: 1, min: 0, max: 500 },
        { prop: 'leftVision', label: '左眼视力', type: 'number', precision: 1, min: 0, max: 6 },
        { prop: 'rightVision', label: '右眼视力', type: 'number', precision: 1, min: 0, max: 6 },
        { prop: 'healthCode', label: '健康状况', type: 'select', options: healthOptions.value },
        { prop: 'doctorAdvice', label: '医生建议', type: 'textarea' },
        { prop: 'remark', label: '备注', type: 'textarea' }
      ]

    case 'changeLog':
      // 变更记录是只读流水，没有可编辑字段
      return []
  }
})

/** 表格列（各子表差别较大，因此显式写，不用描述驱动）。 */
const currentRows = computed<any[]>(() => {
  switch (activeTab.value) {
    case 'guardian':
      return guardians.value
    case 'resume':
      return resumes.value
    case 'reward':
      return rewards.value
    case 'punishment':
      return punishments.value
    case 'subsidy':
      return subsidies.value
    case 'physical':
      return physicals.value
    case 'changeLog':
      return changeLogs.value
  }
})

// ---------------------------------------------------------------------------
// 新增 / 修改
// ---------------------------------------------------------------------------

const formVisible = ref(false)
const editingId = ref<string | null>(null)
const saving = ref(false)
// 表单模型由"字段描述"驱动，是异构对象，这里用 any 是有意的：
// 写成 Record<string, unknown> 会处处需要断言，而 `v-model` 不接受类型断言表达式
// （`v-model="form[x] as string"` 直接编译不过），最后反而更不安全。
const form = reactive<Record<string, any>>({})

function clearForm(): void {
  for (const key of Object.keys(form)) {
    delete form[key]
  }
}

function openCreate(): void {
  clearForm()
  editingId.value = null

  for (const field of fields.value) {
    form[field.prop] = field.default ?? null
  }

  formVisible.value = true
}

function openEdit(row: Record<string, unknown>): void {
  clearForm()
  editingId.value = String(row.id)

  for (const field of fields.value) {
    // 只取表单里出现的字段：出生日期之类的展示字段不进表单
    form[field.prop] = row[field.prop] ?? null
  }

  if (activeTab.value === 'guardian') {
    // 身份证号只显示掩码，编辑时留空 = 不修改（避免把掩码当新号码存回去）
    form.idCardNumber = ''
  }

  formVisible.value = true
}

async function save(
  create: () => Promise<unknown>,
  update: (id: string) => Promise<unknown>
): Promise<void> {
  saving.value = true

  try {
    if (editingId.value) {
      await update(editingId.value)
      ElMessage.success('已保存')
    } else {
      await create()
      ElMessage.success('已新增')
    }

    formVisible.value = false
    await loadRows()
    emit('changed')
  } finally {
    saving.value = false
  }
}

async function submit(): Promise<void> {
  const id = studentId.value

  switch (activeTab.value) {
    case 'guardian':
      await save(
        () => studentProfileApi.createGuardian(id, form as unknown as GuardianInput),
        (rowId) => studentProfileApi.updateGuardian(rowId, form as unknown as GuardianInput)
      )
      break

    case 'resume':
      await save(
        () => studentProfileApi.createResume(id, form as unknown as ResumeInput),
        (rowId) => studentProfileApi.updateResume(rowId, form as unknown as ResumeInput)
      )
      break

    case 'reward':
      await save(
        () => studentProfileApi.createReward(id, form as unknown as RewardInput),
        (rowId) => studentProfileApi.updateReward(rowId, form as unknown as RewardInput)
      )
      break

    case 'punishment':
      await save(
        () => studentProfileApi.createPunishment(id, form as unknown as PunishmentInput),
        (rowId) => studentProfileApi.updatePunishment(rowId, form as unknown as PunishmentInput)
      )
      break

    case 'subsidy':
      await save(
        () => studentProfileApi.createSubsidy(id, form as unknown as SubsidyInput),
        (rowId) => studentProfileApi.updateSubsidy(rowId, form as unknown as SubsidyInput)
      )
      break

    case 'physical':
      await save(
        () => studentProfileApi.createPhysical(id, form as unknown as PhysicalInput),
        (rowId) => studentProfileApi.updatePhysical(rowId, form as unknown as PhysicalInput)
      )
      break
  }
}

async function removeRow(row: Record<string, unknown>): Promise<void> {
  await ElMessageBox.confirm('删除后不可恢复，确认删除这条记录吗？', '确认删除', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消'
  })

  switch (activeTab.value) {
    case 'guardian':
      await studentProfileApi.deleteGuardian(String(row.id))
      break
    case 'resume':
      await studentProfileApi.deleteResume(String(row.id))
      break
    case 'reward':
      await studentProfileApi.deleteReward(String(row.id))
      break
    case 'punishment':
      await studentProfileApi.deletePunishment(String(row.id))
      break
    case 'subsidy':
      await studentProfileApi.deleteSubsidy(String(row.id))
      break
    case 'physical':
      await studentProfileApi.deletePhysical(String(row.id))
      break
  }

  ElMessage.success('已删除')
  await loadRows()
  emit('changed')
}

function fmtDate(value: unknown): string {
  return typeof value === 'string' && value ? value.slice(0, 10) : '—'
}

function formatTime(value: unknown): string {
  return typeof value === 'string' && value ? value.replace('T', ' ').slice(0, 19) : '—'
}

function fmtText(value: unknown): string {
  return value === null || value === undefined || value === '' ? '—' : String(value)
}

// ---------------------------------------------------------------------------
// 生命周期
// ---------------------------------------------------------------------------

watch(visible, async (opened) => {
  if (!opened) {
    return
  }

  activeTab.value = 'guardian'
  await loadOptions()
  await loadRows()
})

watch(activeTab, async () => {
  if (visible.value) {
    await loadRows()
  }
})

onMounted(loadOptions)
</script>

<template>
  <el-drawer v-model="visible" size="72%" :destroy-on-close="true">
    <template #header>
      <div class="profile__header">
        <span class="profile__title">学生档案</span>
        <span v-if="student" class="profile__subtitle">
          {{ student.name }}
          <span class="profile__no">{{ student.studentNo }}</span>
          <span class="profile__school">{{ student.schoolName }}</span>
        </span>
      </div>
    </template>

    <el-tabs v-model="activeTab">
      <el-tab-pane v-for="tab in tabs" :key="tab.key" :label="tab.label" :name="tab.key" />
    </el-tabs>

    <div class="profile__toolbar">
      <el-button
        v-if="canEdit && activeTab !== 'changeLog'"
        type="primary"
        @click="openCreate"
      >
        新增{{ tabs.find((t) => t.key === activeTab)?.label }}
      </el-button>
      <el-button :loading="loading" @click="loadRows">刷新</el-button>
      <span v-if="activeTab === 'changeLog'" class="profile__hint">
        变更记录由系统自动写入，不可修改（留痕要可信，就不能允许事后编辑）
      </span>
    </div>

    <!-- 监护人 -->
    <el-table v-if="activeTab === 'guardian'" v-loading="loading" :data="guardians" border stripe>
      <el-table-column prop="seq" label="序号" width="70" align="center" />
      <el-table-column prop="name" label="姓名" min-width="110" />
      <el-table-column prop="relationName" label="关系" width="100" />
      <el-table-column label="身份证号" width="180">
        <template #default="{ row }">{{ row.maskedIdCard ?? '—' }}</template>
      </el-table-column>
      <el-table-column prop="phoneNumber" label="联系电话" width="140" />
      <el-table-column prop="workUnit" label="工作单位" min-width="140" show-overflow-tooltip />
      <el-table-column label="第一监护人" width="110" align="center">
        <template #default="{ row }">
          <el-tag v-if="row.isPrimary" size="small" type="success">是</el-tag>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 学习简历 -->
    <el-table v-else-if="activeTab === 'resume'" v-loading="loading" :data="resumes" border stripe>
      <el-table-column label="起止时间" width="210">
        <template #default="{ row }">{{ fmtDate(row.startDate) }} ~ {{ fmtDate(row.endDate) }}</template>
      </el-table-column>
      <el-table-column prop="schoolName" label="学校（园）" min-width="180" show-overflow-tooltip />
      <el-table-column prop="gradeName" label="年级" width="90" />
      <el-table-column prop="className" label="班级" width="90" />
      <el-table-column prop="post" label="职务" width="110" />
      <el-table-column prop="referee" label="证明人" width="110" />
      <el-table-column prop="remark" label="备注" min-width="140" show-overflow-tooltip />
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 奖励 -->
    <el-table v-else-if="activeTab === 'reward'" v-loading="loading" :data="rewards" border stripe>
      <el-table-column label="获奖日期" width="120">
        <template #default="{ row }">{{ fmtDate(row.rewardDate) }}</template>
      </el-table-column>
      <el-table-column prop="rewardName" label="奖励名称" min-width="160" />
      <el-table-column prop="rewardTypeName" label="类型" width="140" />
      <el-table-column prop="rewardLevelName" label="级别" width="110" />
      <el-table-column prop="awardOrg" label="授奖单位" min-width="150" show-overflow-tooltip />
      <el-table-column prop="remark" label="备注" min-width="120" show-overflow-tooltip />
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 处分 -->
    <el-table v-else-if="activeTab === 'punishment'" v-loading="loading" :data="punishments" border stripe>
      <el-table-column label="处分日期" width="120">
        <template #default="{ row }">{{ fmtDate(row.punishDate) }}</template>
      </el-table-column>
      <el-table-column prop="punishName" label="处分名称" min-width="140" />
      <el-table-column prop="punishTypeName" label="类型" width="130" />
      <el-table-column prop="punishOrg" label="决定单位" min-width="150" show-overflow-tooltip />
      <el-table-column label="状态" width="150">
        <template #default="{ row }">
          <el-tag v-if="row.isRevoked" size="small" type="info">
            已撤销（{{ fmtDate(row.revokeDate) }}）
          </el-tag>
          <el-tag v-else size="small" type="danger">生效中</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="revokeReason" label="撤销原因" min-width="140" show-overflow-tooltip />
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 资助 -->
    <el-table v-else-if="activeTab === 'subsidy'" v-loading="loading" :data="subsidies" border stripe>
      <el-table-column prop="academicYear" label="学年" width="120" align="center" />
      <el-table-column prop="subsidyName" label="资助项目" min-width="160" />
      <el-table-column prop="subsidyTypeName" label="类型" width="150" />
      <el-table-column label="金额（元）" width="120" align="right">
        <template #default="{ row }">
          {{ row.amount === null || row.amount === undefined ? '—' : row.amount.toFixed(2) }}
        </template>
      </el-table-column>
      <el-table-column label="发放日期" width="120">
        <template #default="{ row }">{{ fmtDate(row.subsidyDate) }}</template>
      </el-table-column>
      <el-table-column prop="fundingOrg" label="资助方" min-width="150" show-overflow-tooltip />
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 体质健康 -->
    <el-table v-else-if="activeTab === 'physical'" v-loading="loading" :data="physicals" border stripe>
      <el-table-column prop="academicYear" label="学年" width="120" align="center" />
      <el-table-column label="体检日期" width="120">
        <template #default="{ row }">{{ fmtDate(row.checkDate) }}</template>
      </el-table-column>
      <el-table-column label="身高(cm)" width="100" align="right">
        <template #default="{ row }">{{ fmtText(row.height) }}</template>
      </el-table-column>
      <el-table-column label="体重(kg)" width="100" align="right">
        <template #default="{ row }">{{ fmtText(row.weight) }}</template>
      </el-table-column>
      <el-table-column label="左眼视力" width="100" align="right">
        <template #default="{ row }">{{ fmtText(row.leftVision) }}</template>
      </el-table-column>
      <el-table-column label="右眼视力" width="100" align="right">
        <template #default="{ row }">{{ fmtText(row.rightVision) }}</template>
      </el-table-column>
      <el-table-column prop="healthName" label="健康状况" width="110" />
      <el-table-column prop="doctorAdvice" label="医生建议" min-width="160" show-overflow-tooltip />
      <el-table-column v-if="canEdit" label="操作" width="140" align="center" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link type="danger" @click="removeRow(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 变更记录（只读） -->
    <el-table v-else v-loading="loading" :data="changeLogs" border stripe>
      <el-table-column label="时间" width="170">
        <template #default="{ row }">{{ formatTime(row.occurredAt) }}</template>
      </el-table-column>
      <el-table-column prop="actionName" label="操作" width="100" />
      <el-table-column prop="fieldLabel" label="字段" width="120">
        <template #default="{ row }">{{ row.fieldLabel ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="变更" min-width="260">
        <template #default="{ row }">
          <span v-if="row.oldValue || row.newValue">
            <span class="profile__old">{{ row.oldValue ?? '（空）' }}</span>
            <span class="profile__arrow">→</span>
            <span class="profile__new">{{ row.newValue ?? '（空）' }}</span>
          </span>
          <span v-else>—</span>
        </template>
      </el-table-column>
      <el-table-column label="操作人" width="120">
        <template #default="{ row }">{{ row.operatorName ?? '系统' }}</template>
      </el-table-column>
      <el-table-column label="来源" width="110">
        <template #default="{ row }">{{ row.source ?? '—' }}</template>
      </el-table-column>
      <el-table-column prop="reason" label="原因" min-width="180" show-overflow-tooltip>
        <template #default="{ row }">{{ row.reason ?? '—' }}</template>
      </el-table-column>
    </el-table>

    <el-empty v-if="!loading && currentRows.length === 0" description="暂无记录" />

    <!-- 新增 / 编辑（六张子表共用一套模板，字段由 fields 描述驱动） -->
    <el-dialog
      v-model="formVisible"
      :title="editingId ? `修改${tabs.find((t) => t.key === activeTab)?.label}` : `新增${tabs.find((t) => t.key === activeTab)?.label}`"
      width="640px"
      append-to-body
    >
      <el-form label-width="110px">
        <el-form-item v-for="field in fields" :key="field.prop" :label="field.label" :required="field.required">
          <el-input
            v-if="field.type === 'text'"
            v-model="form[field.prop]"
            :placeholder="field.placeholder"
            clearable
          />

          <el-input
            v-else-if="field.type === 'textarea'"
            v-model="form[field.prop]"
            type="textarea"
            :rows="2"
          />

          <el-input-number
            v-else-if="field.type === 'number'"
            v-model="form[field.prop]"
            :precision="field.precision ?? 0"
            :min="field.min"
            :max="field.max"
            controls-position="right"
            style="width: 100%"
          />

          <el-date-picker
            v-else-if="field.type === 'date'"
            v-model="form[field.prop]"
            type="date"
            value-format="YYYY-MM-DD"
            :placeholder="field.placeholder ?? '选择日期'"
            style="width: 100%"
          />

          <el-select
            v-else-if="field.type === 'select'"
            v-model="form[field.prop]"
            clearable
            style="width: 100%"
          >
            <el-option v-for="opt in field.options ?? []" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>

          <el-switch v-else-if="field.type === 'switch'" v-model="form[field.prop]" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="formVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
      </template>
    </el-dialog>
  </el-drawer>
</template>

<style scoped>
.profile__header {
  display: flex;
  align-items: baseline;
  gap: 12px;
}

.profile__title {
  font-size: 16px;
  font-weight: 600;
}

.profile__subtitle {
  color: #606266;
}

.profile__no {
  margin-left: 8px;
  font-family: monospace;
  color: #909399;
}

.profile__school {
  margin-left: 12px;
  color: #909399;
}

.profile__toolbar {
  margin-bottom: 12px;
}

.profile__hint {
  margin-left: 12px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.profile__old {
  color: var(--el-text-color-secondary);
  text-decoration: line-through;
}

.profile__arrow {
  margin: 0 6px;
  color: var(--el-text-color-secondary);
}

.profile__new {
  color: var(--el-color-primary);
  font-weight: 600;
}
</style>
