<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import {
  describeError,
  describeExportProgress,
  describeTruncation,
  exportTaskApi,
  identityApi,
  isJsonParseError,
  saveBlob
} from '@/api'
import type { ExportTask, IdentityRole, IdentityUser } from '@/api/types'
import { ExportTaskKinds } from '@/api/types'
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

/**
 * 导出账号清单。
 *
 * 【为什么导出不挂在"能查"上】能翻着看的名单与"能整份落盘带走"不是同一件事，
 * 与组织/异动导出同一条口径（后端 `ExportTaskPermissions` 用的也是这个码）。
 *
 * ⚠ 这一页**没有、也不加数据范围过滤**：用户表是框架数据（`AbpUsers`），
 *   后端导出用的就是本页那条查询。因此"页面上看得见几个，文件里就是几个"——
 *   少了反而会让人以为系统丢了账号。
 */
const canExport = computed(() => auth.can(MetadataPermissions.Sys.User.Export))

const loading = ref(false)
const rows = ref<IdentityUser[]>([])
const total = ref(0)
const roles = ref<IdentityRole[]>([])

/**
 * 加载失败的原因（非空时页面上显示明确提示）。
 *
 * 【为什么不像原来那样只 `catch {}`】
 *   没有 `AbpIdentity.Users` 时 `/api/identity/users` 返回的是 **302 跳登录页**
 *   （不是 403），axios 拿到登录页 HTML、解 JSON 抛错。原来的写法让这个错误
 *   既没有提示、列表也是空的 —— 使用者只会判断成"这个页面坏了/没有数据"。
 *   角色下拉同理（`AbpIdentity.Roles`），见 loadRoles 的注释。
 */
const loadFailed = ref('')
const rolesFailed = ref('')

const query = reactive({
  filter: '',
  maxResultCount: 20
})

const page = ref(1)

async function load(): Promise<void> {
  loading.value = true
  loadFailed.value = ''

  try {
    const result = await identityApi.getUsers({
      filter: query.filter.trim() || undefined,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'userName'
    })

    rows.value = result.items
    total.value = result.totalCount
  } catch (error) {
    rows.value = []
    total.value = 0
    loadFailed.value = isJsonParseError(error)
      ? '读取用户列表失败：接口未授权时被重定向到登录页（返回的不是 JSON）。' +
        '请确认当前账号拥有框架权限 AbpIdentity.Users。'
      : `读取用户列表失败：${describeError(error)}`
  } finally {
    loading.value = false
  }
}

/** 角色下拉：失败要说出来，否则编辑弹窗里的"角色"是一个空下拉、看不出原因。 */
async function loadRoles(): Promise<void> {
  try {
    roles.value = await identityApi.getRoles()
    rolesFailed.value = ''
  } catch (error) {
    roles.value = []
    rolesFailed.value = isJsonParseError(error)
      ? '读取角色列表失败：请确认当前账号拥有框架权限 AbpIdentity.Roles。'
      : `读取角色列表失败：${describeError(error)}`
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

// ---------------------------------------------------------------------------
// 导出（后台任务）
// ---------------------------------------------------------------------------

/**
 * 页面卸载后停止轮询。
 *
 * 少了这个，用户点了导出再立刻切走，定时器仍会每 1.5 秒打一次接口 ——
 * 直到 5 分钟超时为止，而那时他早就不在这个页面了。
 */
let disposed = false
onUnmounted(() => {
  disposed = true
})

const exporting = ref(false)
/** 任务的真实阶段（排队中 / 已处理 N / M（x%） / 正在生成）：只说"转圈"用户不知道卡在哪一步。 */
const exportProgressText = ref('')

/**
 * 提交导出任务并等它完成，然后下载（与组织/微代码/异动导出走完全同一条路径）。
 *
 * **沿用当前关键字**：看到什么就导出什么。留空即全部账号 ——
 * 与页面上"关键字留空 = 列出全部"完全一致。
 */
async function exportUsers(): Promise<void> {
  exporting.value = true
  exportProgressText.value = '排队中…'

  try {
    const filter = query.filter.trim()

    const task = await exportTaskApi.run(
      { kind: ExportTaskKinds.UserCsv, userQuery: { filter: filter || undefined } },
      {
        shouldStop: () => disposed,
        onProgress: (t: ExportTask) => {
          // 账号清单导出是分页累积的，**有**真百分比（已处理 N / M（x%））；
          // 文案只有 `describeExportProgress` 一处实现（债 23）。
          exportProgressText.value = describeExportProgress(t)
        }
      }
    )

    const blob = await exportTaskApi.download(task.id)

    // 文件名以**后端给的**为准（带了日期），后端没给才用兜底名
    saveBlob(blob, task.fileName || `账号清单_${new Date().toISOString().slice(0, 10)}.csv`)

    // 账号数没有天然上限，后端按 Xjgl:Export:MaxRows 收口 ——
    // 触顶必须显式提示，只弹一句"成功"等于把"少了 3000 个账号"藏起来。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(
        task.message ? `${task.kindName}已完成：${task.message}` : `${task.kindName}已完成`
      )
    }
  } catch (e) {
    // 这里的提示**不能省**，但也不能重复弹：
    //   · 入队/轮询/下载的 HTTP 失败已被拦截器弹过（且给出的是后端 reason）；
    //   · 而任务本身 Failed / 被取消 / 前端等超时是 `exportTaskApi.run` 抛的
    //     **业务错误**，它不经过拦截器 —— 不提示就等于"点了导出、转圈停下、
    //     什么都没发生"，用户只会以为功能坏了。
    // 用 `isAxiosError` 这个 axios 自己打的标记把两者分开。
    if (!(e as { isAxiosError?: boolean })?.isAxiosError) {
      ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
    }
  } finally {
    // 无论成功、失败还是超时都要清掉，否则那行提示会一直留在页面上
    exporting.value = false
    exportProgressText.value = ''
  }
}

onMounted(async () => {
  await loadRoles()
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
          <div>
            <!--
              导出按 `Sys.User.Export` 显隐，与后端 `ExportTaskPermissions` 一致：
              少了这层判断，按钮点得动而接口回 403 —— 那是最难解释的失败。
            -->
            <el-button
              v-if="canExport"
              :loading="exporting"
              :disabled="exporting"
              @click="exportUsers"
            >
              <AppIcon name="Download" />
              <span>导出</span>
            </el-button>
            <el-button v-if="canCreate" type="primary" @click="openCreate">
              <AppIcon name="Plus" />
              <span>新建用户</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 导出要等后台生成，只说"转圈"用户不知道卡在哪一步 -->
      <div v-if="exportProgressText" class="user__hint">{{ exportProgressText }}</div>

      <el-alert
        v-if="canExport"
        type="info"
        :closable="false"
        show-icon
        class="user__tip"
        title="导出的是当前关键字下的全部账号（留空即全部），列与列表一致，另含角色与所属组织。账号清单不按数据范围收口 —— 与这一页保持一致。"
      />

      <el-alert
        v-if="loadFailed"
        type="error"
        :closable="false"
        show-icon
        class="user__tip"
        :title="loadFailed"
      />

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
          <div v-if="rolesFailed" class="user__error">{{ rolesFailed }}</div>
          <div v-else class="user__hint">
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

.user__error {
  color: var(--el-color-danger);
  font-size: 12px;
  line-height: 1.5;
}

.user__lockout {
  margin-left: 16px;
}
</style>
