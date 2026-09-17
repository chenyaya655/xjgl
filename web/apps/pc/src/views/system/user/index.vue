<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { identityApi } from '@/api'
import type { IdentityRole, IdentityUser } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 用户管理。
 *
 * 走的是 ABP Identity 自带的 `/api/identity/users`，而不是自己再写一套 ——
 * 用户/角色/权限的数据本来就在 ABP 的框架表里（`AbpUsers` 等），
 * 自己写只会与框架的密码策略、锁定策略、并发戳机制打架。
 *
 * 两处界面上的取舍：
 *   1) **列表不显示角色**：列表接口不返回 roleNames（要每个用户单独查一次），
 *      与其为了一个列打 20 次请求，不如把角色放在编辑弹窗里显示 —— 那是真正
 *      需要看角色的地方。
 *   2) **编辑时不能改密码**：ABP 的更新接口不含密码字段。这符合安全惯例
 *      （管理员不该知道用户密码），本系统另外提供了「用户预置 + 本人激活」的流程。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.User.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.User.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.User.Delete))

const loading = ref(false)
const rows = ref<IdentityUser[]>([])
const total = ref(0)
const roles = ref<IdentityRole[]>([])

const query = reactive({
  filter: '',
  maxResultCount: 20
})

const page = ref(1)

async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await identityApi.getUsers({
      filter: query.filter.trim() || undefined,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'userName'
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

// ---------------------------------------------------------------------------
// 新增 / 编辑
// ---------------------------------------------------------------------------

const dialogVisible = ref(false)
const saving = ref(false)
const editingId = ref<string | null>(null)
/** 编辑时先把角色单独查出来（列表接口不返回）。 */
const loadingRoles = ref(false)

const form = reactive({
  userName: '',
  name: '',
  surname: '',
  email: '',
  phoneNumber: '',
  password: '',
  isActive: true,
  lockoutEnabled: true,
  roleNames: [] as string[]
})

function openCreate(): void {
  editingId.value = null
  form.userName = ''
  form.name = ''
  form.surname = ''
  form.email = ''
  form.phoneNumber = ''
  form.password = ''
  form.isActive = true
  form.lockoutEnabled = true
  form.roleNames = []
  dialogVisible.value = true
}

async function openEdit(row: IdentityUser): Promise<void> {
  editingId.value = row.id
  form.userName = row.userName
  form.name = row.name ?? ''
  form.surname = row.surname ?? ''
  form.email = row.email ?? ''
  form.phoneNumber = row.phoneNumber ?? ''
  form.password = ''
  form.isActive = row.isActive
  form.lockoutEnabled = row.lockoutEnabled
  form.roleNames = []
  dialogVisible.value = true

  loadingRoles.value = true

  try {
    // 角色必须走 /roles 子资源：用户详情接口不返回 roleNames（见 identityApi.getUserRoles 的注释）
    form.roleNames = (await identityApi.getUserRoles(row.id)).map((r) => r.name)
  } catch {
    form.roleNames = []
  } finally {
    loadingRoles.value = false
  }
}

async function save(): Promise<void> {
  if (!form.userName.trim()) {
    ElMessage.warning('用户名不能为空。')

    return
  }

  if (!editingId.value && form.password.length < 6) {
    ElMessage.warning('初始密码至少 6 位（同时还要满足服务端的密码复杂度策略）。')

    return
  }

  // ABP 的身份模型要求 Email 必填（服务端 DTO 上有 [Required]）。
  // 但学籍系统的实际用户里有大量农村学校教师没有邮箱，为此卡住建档不合理，
  // 因此在留空时生成一个占位邮箱。本系统的账号激活走"预置 + 激活码"，
  // 不依赖邮件送达，所以占位邮箱不会造成功能缺失。
  const email = form.email.trim() || `${form.userName.trim()}@xjgl.local`

  saving.value = true

  try {
    if (editingId.value) {
      await identityApi.updateUser(editingId.value, {
        userName: form.userName.trim(),
        name: form.name.trim() || null,
        surname: form.surname.trim() || null,
        email,
        phoneNumber: form.phoneNumber.trim() || null,
        isActive: form.isActive,
        lockoutEnabled: form.lockoutEnabled,
        roleNames: form.roleNames
      })

      ElMessage.success('已保存')
    } else {
      await identityApi.createUser({
        userName: form.userName.trim(),
        name: form.name.trim() || null,
        surname: form.surname.trim() || null,
        email,
        phoneNumber: form.phoneNumber.trim() || null,
        password: form.password,
        isActive: form.isActive,
        lockoutEnabled: form.lockoutEnabled,
        roleNames: form.roleNames
      })

      ElMessage.success('用户已创建')
    }

    dialogVisible.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function toggleActive(row: IdentityUser): Promise<void> {
  // ⚠ 必须先把角色查出来再一起提交：
  //   ABP 的 UpdateUserDto 会把 roleNames **整体覆盖**，传空数组等于把用户的角色全删了。
  //   而用户详情/列表接口**都不返回** roleNames，所以只能走 /roles 子资源。
  const roleNames = (await identityApi.getUserRoles(row.id)).map((r) => r.name)

  await identityApi.updateUser(row.id, {
    userName: row.userName,
    name: row.name ?? null,
    surname: row.surname ?? null,
    email: row.email ?? null,
    phoneNumber: row.phoneNumber ?? null,
    isActive: !row.isActive,
    lockoutEnabled: row.lockoutEnabled,
    roleNames
  })

  ElMessage.success(row.isActive ? '已停用' : '已启用')
  await load()
}

async function remove(row: IdentityUser): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除用户「${row.userName}」吗？该操作不可恢复（不是停用）。`,
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await identityApi.deleteUser(row.id)
  ElMessage.success('已删除')
  await load()
}

onMounted(async () => {
  try {
    roles.value = await identityApi.getRoles()
  } catch {
    roles.value = []
  }

  await load()
})
</script>

<template>
  <div class="user">
    <el-card shadow="never" class="user__filter">
      <el-form :inline="true" @submit.prevent="search">
        <el-form-item label="关键字">
          <el-input
            v-model="query.filter"
            placeholder="用户名 / 姓名 / 邮箱 / 手机号"
            clearable
            style="width: 260px"
            @keyup.enter="search"
          >
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="loading" @click="search">查询</el-button>
          <el-button @click="((query.filter = ''), search())">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <template #header>
        <div class="user__header">
          <span>用户（共 {{ total }} 个）</span>
          <el-button v-if="canCreate" type="primary" @click="openCreate">
            <AppIcon name="Plus" />
            <span>新建用户</span>
          </el-button>
        </div>
      </template>

      <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 300px)">
        <el-table-column prop="userName" label="用户名" width="180" />
        <el-table-column prop="name" label="姓名" width="130">
          <template #default="{ row }">{{ row.name || '—' }}</template>
        </el-table-column>
        <el-table-column prop="email" label="邮箱" min-width="200">
          <template #default="{ row }">{{ row.email || '—' }}</template>
        </el-table-column>
        <el-table-column prop="phoneNumber" label="手机号" width="140">
          <template #default="{ row }">{{ row.phoneNumber || '—' }}</template>
        </el-table-column>
        <el-table-column label="状态" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isActive ? 'success' : 'danger'">
              {{ row.isActive ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="170" align="center">
          <template #default="{ row }">{{ row.creationTime.replace('T', ' ').slice(0, 19) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="200" align="center" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canUpdate" link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button v-if="canUpdate" link :type="row.isActive ? 'warning' : 'success'" @click="toggleActive(row)">
              {{ row.isActive ? '停用' : '启用' }}
            </el-button>
            <el-button v-if="canDelete" link type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="user__pager"
        :total="total"
        layout="total, sizes, prev, pager, next"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------------- 新增 / 编辑 ---------------- -->
    <el-dialog
      v-model="dialogVisible"
      :title="editingId ? '编辑用户' : '新建用户'"
      width="620px"
    >
      <el-form v-loading="loadingRoles" label-width="100px">
        <el-form-item label="用户名" required>
          <el-input v-model="form.userName" :disabled="!!editingId" placeholder="登录用的账号，如 t_4101020001" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="姓名">
              <el-input v-model="form.name" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="手机号">
              <el-input v-model="form.phoneNumber" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="邮箱">
          <el-input v-model="form.email" placeholder="留空则自动生成占位邮箱" />
          <div class="user__hint">
            服务端要求邮箱必填，但很多教师没有邮箱 —— 留空会自动生成
            <code>用户名@xjgl.local</code>。本系统的账号激活不依赖邮件送达。
          </div>
        </el-form-item>

        <el-form-item v-if="!editingId" label="初始密码" required>
          <el-input v-model="form.password" type="password" show-password placeholder="至少 6 位，需满足复杂度策略" />
        </el-form-item>

        <el-alert
          v-else
          type="info"
          :closable="false"
          show-icon
          class="user__tip"
          title="编辑时不修改密码（管理员不该知道用户密码）。需要重置时，请走「预置与激活」流程让本人设置，或让用户自行找回。"
        />

        <el-form-item label="角色">
          <el-select v-model="form.roleNames" multiple filterable style="width: 100%" placeholder="可多选">
            <el-option v-for="r in roles" :key="r.id" :label="r.name" :value="r.name" />
          </el-select>
          <div class="user__hint">
            角色决定权限集合；具体的权限项在「权限管理」里按角色配置。
          </div>
        </el-form-item>

        <el-form-item label="状态">
          <el-switch v-model="form.isActive" active-text="启用" inactive-text="停用" />
          <el-checkbox v-model="form.lockoutEnabled" class="user__lockout">
            允许因多次登录失败被锁定
          </el-checkbox>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.user__filter {
  margin-bottom: 16px;
}

.user__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.user__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.user__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.user__tip {
  margin-bottom: 16px;
}

.user__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

.user__lockout {
  margin-left: 16px;
}
</style>
