<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { changeFlowApi, changeTypeApi, orgApi, studentApi } from '@/api'
import type {
  ChangeApplyListItem,
  ChangeType,
  OrgDto,
  StudentListItem
} from '@/api/types'
import ChangeApplyDetail from '@/components/ChangeApplyDetail.vue'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 异动申请：提起申请、查看进度、撤回。
 *
 * 三个交互上的要点：
 *   1) **只能选"已发布流程"的异动类型** —— 没配流程就提交，会生成一个
 *      没有任何审核节点的申请，卡死在"待审核"且没人能处理；
 *   2) 提交前把**实际会走的步骤**展示给申请人看（复用流程预览），
 *      让"我这个申请要跑几个单位"一目了然，减少催办电话；
 *   3) 是否跨校决定要不要选目标学校 —— 休学/退学这类不换学校的异动不需要。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Chg.Apply.Create))
const canDelete = computed(() => auth.can(MetadataPermissions.Chg.Apply.Delete))

const loading = ref(false)
const rows = ref<ChangeApplyListItem[]>([])
const total = ref(0)
const types = ref<ChangeType[]>([])
const schools = ref<OrgDto[]>([])

const query = reactive({
  changeTypeId: undefined as string | undefined,
  status: undefined as string | undefined,
  keyword: '',
  maxResultCount: 20
})

const page = ref(1)

const statusOptions = [
  { value: 'Submitted', label: '待审核' },
  { value: 'InProgress', label: '审核中' },
  { value: 'Approved', label: '已通过' },
  { value: 'Rejected', label: '已驳回' },
  { value: 'Withdrawn', label: '已撤回' }
]

async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await changeFlowApi.getApplies({
      changeTypeId: query.changeTypeId,
      status: query.status,
      keyword: query.keyword || undefined,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'ApplyDate DESC'
    })

    rows.value = result.items
    total.value = result.totalCount
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

function statusTagType(status: string): 'success' | 'danger' | 'warning' | 'info' {
  switch (status) {
    case 'Approved':
      return 'success'
    case 'Rejected':
      return 'danger'
    case 'Submitted':
    case 'InProgress':
      return 'warning'
    default:
      return 'info'
  }
}

// ---------------------------------------------------------------------------
// 新建申请
// ---------------------------------------------------------------------------

const submitVisible = ref(false)
const submitting = ref(false)
const studentKeyword = ref('')
const studentOptions = ref<StudentListItem[]>([])
const searchingStudent = ref(false)

const submitForm = reactive({
  changeTypeId: undefined as string | undefined,
  studentId: undefined as string | undefined,
  targetSchoolOrgId: undefined as string | undefined,
  reason: '',
  remark: ''
})

/** 可选的异动类型：启用 + 已发布流程。 */
const selectableTypes = computed(() => types.value.filter((t) => t.isEnabled && t.hasPublishedFlow))

const selectedType = computed(() =>
  selectableTypes.value.find((t) => t.id === submitForm.changeTypeId)
)

/** 只有"允许跨校"的异动类型才需要选目标学校。 */
const needTargetSchool = computed(() => selectedType.value?.allowCrossSchool ?? false)

async function searchStudents(): Promise<void> {
  if (!studentKeyword.value.trim()) {
    ElMessage.warning('请输入学籍辅号或姓名。')

    return
  }

  searchingStudent.value = true

  try {
    const result = await studentApi.getList({
      keyword: studentKeyword.value.trim(),
      maxResultCount: 20,
      sorting: 'StudentNo ASC'
    })

    studentOptions.value = result.items
  } finally {
    searchingStudent.value = false
  }
}

function openSubmit(): void {
  submitForm.changeTypeId = undefined
  submitForm.studentId = undefined
  submitForm.targetSchoolOrgId = undefined
  submitForm.reason = ''
  submitForm.remark = ''
  studentKeyword.value = ''
  studentOptions.value = []
  submitVisible.value = true
}

async function doSubmit(): Promise<void> {
  if (!submitForm.changeTypeId || !submitForm.studentId) {
    ElMessage.warning('请选择异动类型与学生。')

    return
  }

  if (needTargetSchool.value && !submitForm.targetSchoolOrgId) {
    ElMessage.warning('该异动类型需要选择目标学校。')

    return
  }

  if (!submitForm.reason.trim()) {
    ElMessage.warning('请填写申请理由。')

    return
  }

  submitting.value = true

  try {
    const apply = await changeFlowApi.submit({
      changeTypeId: submitForm.changeTypeId,
      studentId: submitForm.studentId,
      targetSchoolOrgId: submitForm.targetSchoolOrgId ?? null,
      reason: submitForm.reason.trim(),
      remark: submitForm.remark || null
    })

    ElMessage.success(`申请已提交（${apply.applyNo}），共 ${apply.tasks.length} 步审核`)

    submitVisible.value = false
    await load()
  } finally {
    submitting.value = false
  }
}

// ---------------------------------------------------------------------------
// 详情 / 撤回 / 删除
// ---------------------------------------------------------------------------

const detailVisible = ref(false)
const detailId = ref<string | null>(null)

function openDetail(row: ChangeApplyListItem): void {
  detailId.value = row.id
  detailVisible.value = true
}

async function withdraw(row: ChangeApplyListItem): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定撤回申请「${row.applyNo}」吗？撤回后已产生的待办会一并作废。`,
      '撤回确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await changeFlowApi.withdraw(row.id)
  ElMessage.success('已撤回')
  await load()
}

async function remove(row: ChangeApplyListItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定删除申请「${row.applyNo}」吗？`, '删除确认', { type: 'warning' })
  } catch {
    return
  }

  await changeFlowApi.remove(row.id)
  ElMessage.success('已删除')
  await load()
}

onMounted(async () => {
  try {
    types.value = await changeTypeApi.getTypes(false)
  } catch {
    types.value = []
  }

  try {
    schools.value = flattenSchools(await orgApi.getTree())
  } catch {
    schools.value = []
  }

  await load()
})
</script>

<template>
  <div class="chg-apply">
    <el-card shadow="never" class="chg-apply__filter">
      <el-form :inline="true" @submit.prevent="search">
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

        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 130px">
            <el-option v-for="s in statusOptions" :key="s.value" :label="s.label" :value="s.value" />
          </el-select>
        </el-form-item>

        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="申请单号 / 学生姓名"
            clearable
            style="width: 200px"
            @keyup.enter="search"
          />
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="loading" @click="search">查询</el-button>
          <el-button @click="((query.changeTypeId = undefined), (query.status = undefined), (query.keyword = ''), search())">
            重置
          </el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <template #header>
        <div class="chg-apply__header">
          <span>异动申请（共 {{ total }} 条）</span>
          <el-button v-if="canCreate" type="primary" @click="openSubmit">
            <AppIcon name="Plus" />
            <span>发起异动申请</span>
          </el-button>
        </div>
      </template>

      <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 330px)">
        <el-table-column prop="applyNo" label="申请单号" width="180" />
        <el-table-column prop="changeTypeName" label="异动类型" width="110" />
        <el-table-column label="学生" width="150">
          <template #default="{ row }">
            {{ row.studentName }}
            <span class="chg-apply__muted">{{ row.studentNo }}</span>
          </template>
        </el-table-column>
        <el-table-column label="学校变动" min-width="260">
          <template #default="{ row }">
            {{ row.sourceSchoolName || '—' }}
            <span v-if="row.targetSchoolName"> → {{ row.targetSchoolName }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="statusTagType(row.status)">{{ row.statusName }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="当前环节" min-width="200">
          <template #default="{ row }">
            <span v-if="row.currentSeq">
              第 {{ row.currentSeq }} 步 · {{ row.currentNodeName }}
              <div class="chg-apply__muted">{{ row.currentOrgName }}</div>
            </span>
            <span v-else class="chg-apply__muted">—</span>
          </template>
        </el-table-column>
        <el-table-column label="申请时间" width="160" align="center">
          <template #default="{ row }">{{ row.applyDate.replace('T', ' ').slice(0, 16) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="180" align="center" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openDetail(row)">详情</el-button>
            <el-button
              v-if="row.status === 'Submitted' || row.status === 'InProgress'"
              link
              type="warning"
              @click="withdraw(row)"
            >
              撤回
            </el-button>
            <el-button
              v-if="canDelete && (row.status === 'Withdrawn' || row.status === 'Rejected')"
              link
              type="danger"
              @click="remove(row)"
            >
              删除
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="chg-apply__pager"
        :total="total"
        layout="total, sizes, prev, pager, next"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------- 新建申请 ---------- -->
    <el-dialog v-model="submitVisible" title="发起异动申请" width="680px">
      <el-form label-width="110px">
        <el-form-item label="异动类型" required>
          <el-select v-model="submitForm.changeTypeId" placeholder="请选择" style="width: 100%">
            <el-option
              v-for="t in selectableTypes"
              :key="t.id"
              :label="`${t.name}（最高${t.maxApproveLevelName}审核）`"
              :value="t.id"
            />
          </el-select>
          <div v-if="selectableTypes.length === 0" class="chg-apply__hint">
            没有可用的异动类型：请先在「异动类型管理」里创建类型并**发布流程**。
          </div>
        </el-form-item>

        <el-form-item label="学生" required>
          <div class="chg-apply__student-picker">
            <el-input
              v-model="studentKeyword"
              placeholder="输入学籍辅号或姓名"
              style="width: 240px"
              @keyup.enter="searchStudents"
            />
            <el-button :loading="searchingStudent" @click="searchStudents">查找</el-button>
            <el-select v-model="submitForm.studentId" placeholder="选择学生" filterable style="width: 240px">
              <el-option
                v-for="s in studentOptions"
                :key="s.id"
                :label="`${s.name}（${s.studentNo}）`"
                :value="s.id"
              />
            </el-select>
          </div>
        </el-form-item>

        <el-form-item v-if="needTargetSchool" label="目标学校" required>
          <el-select
            v-model="submitForm.targetSchoolOrgId"
            placeholder="请选择转入学校"
            filterable
            style="width: 100%"
          >
            <el-option v-for="s in schools" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>
        </el-form-item>

        <el-form-item label="申请理由" required>
          <el-input v-model="submitForm.reason" type="textarea" :rows="3" placeholder="如：家庭搬迁至XX区" />
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="submitForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="提交后系统会按该异动类型的已发布流程自动生成审核待办；同一单位只会审一次。"
      />

      <template #footer>
        <el-button @click="submitVisible = false">取消</el-button>
        <el-button type="primary" :loading="submitting" @click="doSubmit">提交申请</el-button>
      </template>
    </el-dialog>

    <!-- ---------- 详情 ---------- -->
    <el-drawer v-model="detailVisible" title="异动申请详情" size="52%">
      <ChangeApplyDetail :apply-id="detailId" />
    </el-drawer>
  </div>
</template>

<style scoped>
.chg-apply__filter {
  margin-bottom: 16px;
}

.chg-apply__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.chg-apply__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.chg-apply__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.chg-apply__hint {
  color: var(--el-color-danger);
  font-size: 12px;
}

.chg-apply__student-picker {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.chg-apply__pager {
  margin-top: 16px;
  justify-content: flex-end;
}
</style>
