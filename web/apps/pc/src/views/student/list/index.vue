<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'

import { orgApi, studentApi } from '@/api'
import type { OrgDto, StudentListItem } from '@/api/types'
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
            <el-button v-if="auth.can(MetadataPermissions.Stu.Student.Export)" plain>
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
        <el-table-column prop="idCardMasked" label="身份证号" width="180" />
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
            <el-tag v-if="row.photoPath" size="small" type="success" effect="plain">已上传</el-tag>
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
</style>
