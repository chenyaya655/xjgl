<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'

import { orgApi, studentApi, studentImportApi } from '@xjgl/api'
import type { ImportColumn, ImportStudentsResult, OrgDto, StudentListItem } from '@xjgl/api'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 学生列表。
 *
 * 两个必须做对的点：
 *   1) **生僻字**：姓名直接渲染即可，字体由全局样式里的 unicode-range 兜住；
 *      这里额外给含生僻字的记录打标记，方便排查「为什么这个学生打印出来是方块」。
 *   2) **数据权限在后端**：前端只按选中的学校过滤，能查到的学生范围由后端
 *      按当前用户的组织与数据权限决定 —— 前端传了越权的 schoolOrgId 也查不到。
 */
const auth = useAuthStore()

const loading = ref(false)
const rows = ref<StudentListItem[]>([])
const total = ref(0)
const schools = ref<OrgDto[]>([])

const query = reactive({
  schoolOrgId: undefined as string | undefined,
  keyword: '',
  studentStatusCode: undefined as string | undefined,
  maxResultCount: 20
})

/** 当前页码（1 起）。分页组件用的是页码，接口要的是 skipCount，两者在这里换算。 */
const page = ref(1)

const statusOptions = [
  { value: 'Studying', label: '在读' },
  { value: 'Suspended', label: '休学' },
  { value: 'Transferred', label: '转出' },
  { value: 'Graduated', label: '毕业' },
  { value: 'Dropped', label: '退学' }
]

async function loadSchools(): Promise<void> {
  try {
    const tree = await orgApi.getTree()
    schools.value = flattenSchools(tree)
  } catch {
    // 组织接口失败时不阻塞列表：用户仍可用关键字查询（受后端数据权限约束）
    schools.value = []
  }
}

async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await studentApi.getList({
      schoolOrgId: query.schoolOrgId,
      keyword: query.keyword || undefined,
      studentStatusCode: query.studentStatusCode,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'StudentNo ASC'
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

function reset(): void {
  query.schoolOrgId = undefined
  query.keyword = ''
  query.studentStatusCode = undefined
  search()
}

function formatDate(value?: string | null): string {
  return value ? value.slice(0, 10) : '—'
}

// ---------------------------------------------------------------------------
// 导入导出
// ---------------------------------------------------------------------------

/** 当前查询条件（导入导出要按同样的条件走，避免"看到的是筛选结果、导出的是全量"）。 */
function currentQuery() {
  return {
    schoolOrgId: query.schoolOrgId,
    keyword: query.keyword || undefined,
    studentStatusCode: query.studentStatusCode,
    sorting: 'StudentNo ASC',
    maxResultCount: 5000
  }
}

const exporting = ref(false)

async function exportStudents(): Promise<void> {
  exporting.value = true

  try {
    const csv = await studentImportApi.export(currentQuery())

    // 导出的文本已带 UTF-8 BOM（否则 Excel 打开是乱码），因此这里按原样写盘
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')

    link.href = url
    link.download = `学生名单_${new Date().toISOString().slice(0, 10)}.csv`
    link.click()
    URL.revokeObjectURL(url)

    ElMessage.success('已导出当前查询条件下的学生名单')
  } finally {
    exporting.value = false
  }
}

const importVisible = ref(false)
const importing = ref(false)
const importColumns = ref<ImportColumn[]>([])
const importResult = ref<ImportStudentsResult | null>(null)
const importFileName = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

async function openImport(): Promise<void> {
  importVisible.value = true
  importResult.value = null
  importFileName.value = ''

  if (importColumns.value.length === 0) {
    try {
      importColumns.value = await studentImportApi.getColumns()
    } catch {
      importColumns.value = []
    }
  }
}

async function downloadTemplate(): Promise<void> {
  const csv = await studentImportApi.getTemplate()
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')

  link.href = url
  link.download = '学生导入模板.csv'
  link.click()
  URL.revokeObjectURL(url)
}

function pickFile(): void {
  fileInput.value?.click()
}

async function doImport(): Promise<void> {
  const input = fileInput.value

  if (!input?.files?.length) {
    ElMessage.warning('请先选择要导入的 CSV 文件。')

    return
  }

  const file = input.files[0]
  importFileName.value = file.name
  importing.value = true

  try {
    // 浏览器直接读出文本（File.text()），不引任何解析库；
    // 编码由后端按 UTF-8 处理，模板本身带 BOM，Excel 另存为也不会乱码
    const content = await file.text()

    importResult.value = await studentImportApi.import(content)

    if (importResult.value.failed === 0) {
      ElMessage.success(importResult.value.message)
    } else {
      ElMessage.warning(importResult.value.message)
    }

    // 导入成功后刷新列表（部分成功也要刷新：成功的那部分已经落库了）
    if (importResult.value.succeeded > 0) {
      await load()
    }
  } finally {
    importing.value = false
    input.value = ''
  }
}

onMounted(async () => {
  await loadSchools()
  await load()
})
</script>

<template>
  <div class="student-list">
    <el-card shadow="never" class="student-list__filter">
      <el-form :inline="true" @submit.prevent="search">
        <el-form-item label="学校">
          <el-select
            v-model="query.schoolOrgId"
            placeholder="全部（按权限范围）"
            clearable
            filterable
            style="width: 240px"
          >
            <el-option v-for="s in schools" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>
        </el-form-item>

        <el-form-item label="学籍状态">
          <el-select v-model="query.studentStatusCode" placeholder="全部" clearable style="width: 140px">
            <el-option v-for="s in statusOptions" :key="s.value" :label="s.label" :value="s.value" />
          </el-select>
        </el-form-item>

        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="姓名 / 学籍辅号"
            clearable
            style="width: 200px"
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
    </el-card>

    <el-card shadow="never">
      <template #header>
        <div class="student-list__header">
          <span>学生信息（共 {{ total }} 人）</span>
          <div>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Import)"
              plain
              @click="openImport"
            >
              <AppIcon name="Upload" />
              <span>导入</span>
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Export)"
              plain
              :loading="exporting"
              @click="exportStudents"
            >
              <AppIcon name="Download" />
              <span>导出</span>
            </el-button>
            <el-button v-if="auth.can(MetadataPermissions.Stu.Student.Create)" type="primary">
              <AppIcon name="Plus" />
              <span>新增学生</span>
            </el-button>
          </div>
        </div>
      </template>

      <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 320px)">
        <el-table-column type="index" label="#" width="55" align="center" />

        <el-table-column prop="studentNo" label="学籍辅号" width="160" />

        <el-table-column label="姓名" min-width="140">
          <template #default="{ row }">
            <span class="student-list__name">{{ row.name }}</span>
            <el-tag v-if="row.hasRareCharacter" size="small" type="warning" effect="plain" class="student-list__rare">
              生僻字
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column prop="genderName" label="性别" width="70" align="center" />
        <el-table-column label="出生日期" width="110" align="center">
          <template #default="{ row }">{{ formatDate(row.birthDate) }}</template>
        </el-table-column>
        <el-table-column prop="maskedIdCard" label="身份证号" width="180" />
        <el-table-column prop="schoolName" label="学校" min-width="180" show-overflow-tooltip />
        <el-table-column prop="gradeName" label="年级" width="90" />
        <el-table-column prop="className" label="班级" width="90" />
        <el-table-column prop="academicYear" label="学年" width="110" align="center" />

        <el-table-column label="学籍状态" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="row.studentStatusCode === 'Studying' ? 'success' : 'info'">
              {{ row.studentStatusName ?? row.studentStatusCode }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="照片" width="80" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.hasPhoto" size="small" type="success" effect="plain">已上传</el-tag>
            <el-tag v-else size="small" type="danger" effect="plain">缺失</el-tag>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="student-list__pager"
        :total="total"
        layout="total, sizes, prev, pager, next, jumper"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------------- 批量导入 ---------------- -->
    <el-dialog v-model="importVisible" title="批量导入学生" width="820px" top="6vh">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="student-list__tip"
        title="导入与手工录入走完全相同的校验（身份证校验位、跨校查重、生僻字姓名、学籍号生成、数据范围），逐行容错：个别行出错不影响其它行。"
      />

      <el-space wrap class="student-list__import-actions">
        <el-button @click="downloadTemplate">
          <AppIcon name="Download" />
          <span>下载模板</span>
        </el-button>

        <el-button type="primary" :loading="importing" @click="pickFile">
          <AppIcon name="Upload" />
          <span>选择 CSV 文件并导入</span>
        </el-button>

        <span v-if="importFileName" class="student-list__filename">{{ importFileName }}</span>

        <input ref="fileInput" type="file" accept=".csv,text/csv" hidden @change="doImport" />
      </el-space>

      <el-collapse v-if="importColumns.length > 0" class="student-list__columns">
        <el-collapse-item :title="`列说明（共 ${importColumns.length} 列，按表头名匹配，列顺序可调）`">
          <el-table :data="importColumns" size="small" border>
            <el-table-column prop="name" label="列名" width="120" />
            <el-table-column label="必需" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.required" size="small" type="danger" effect="plain">必需</el-tag>
                <span v-else class="student-list__muted">可选</span>
              </template>
            </el-table-column>
            <el-table-column prop="remark" label="说明" min-width="280" />
          </el-table>
        </el-collapse-item>
      </el-collapse>

      <template v-if="importResult">
        <el-alert
          :type="importResult.failed === 0 ? 'success' : 'warning'"
          :closable="false"
          show-icon
          class="student-list__tip"
          :title="importResult.message"
        />

        <el-table
          v-if="importResult.errors.length > 0"
          :data="importResult.errors"
          size="small"
          border
          max-height="300"
        >
          <el-table-column prop="rowIndex" label="行号" width="70" align="center" />
          <el-table-column prop="name" label="姓名" width="110" />
          <el-table-column prop="idCardNumber" label="身份证号" width="180" />
          <el-table-column prop="message" label="失败原因" min-width="300" />
        </el-table>
      </template>

      <template #footer>
        <el-button @click="importVisible = false">关闭</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.student-list__filter {
  margin-bottom: 16px;
}

.student-list__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.student-list__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.student-list__name {
  margin-right: 6px;
}

.student-list__rare {
  transform: scale(0.9);
}

.student-list__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.student-list__tip {
  margin-bottom: 12px;
}

.student-list__import-actions {
  margin-bottom: 12px;
}

.student-list__filename {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.student-list__columns {
  margin-bottom: 12px;
}

.student-list__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
