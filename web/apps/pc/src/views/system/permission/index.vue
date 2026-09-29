<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'

import { describeError, identityApi, isJsonParseError, permissionApi } from '@/api'
import type { IdentityRole, IdentityUser, PermissionGroup } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 权限管理：给「角色」或「用户」授予权限。
 *
 * 为什么同时支持角色与用户两级授权（而不是只有角色）：
 *   学籍系统的实际人事情况很杂 —— 某个县只有一位老师兼管学籍，
 *   给他单独加个"导出"权限比新建一个只有一个人的角色更好管理。
 *   ABP 的 PermissionManagement 原生支持 R（角色）/ U（用户）两种 provider，
 *   直接复用即可。
 *
 * 界面上两个必要的能力：
 *   1) **搜索**：全部权限有 40 多个分组、几百项，不搜索根本找不到；
 *   2) **只提交变化项**：一次全量提交会把几百项都写一遍（还容易误改）。
 */
const auth = useAuthStore()
const route = useRoute()

const canAssign = computed(() => auth.can(MetadataPermissions.Sys.Permission.Assign))

const providerName = ref<'R' | 'U'>('R')
const providerKey = ref<string>('')

const roles = ref<IdentityRole[]>([])
const users = ref<IdentityUser[]>([])
const loading = ref(false)
const saving = ref(false)
const groups = ref<PermissionGroup[]>([])
const entityDisplayName = ref('')

/**
 * 「授权对象 = 用户」时一次最多渲染多少条（债 30 / 前端规约 12.2）。
 *
 * 用户（教师账号）是**无边界集合**，动辄上万条：普通 `el-select` 会为每一条
 * 渲染一个真实 DOM，打开一次下拉就把主线程卡死（详见 16.13 的实测）。
 * 规约给了两条许可做法，这里选**关键字服务端检索 + 结果上限** ——
 * 授权对象永远是"某一个人"，比在几万条里滚动更直接。
 * 上限不是码值，写常量即可。
 */
const USER_SEARCH_LIMIT = 30

/**
 * 命中总数 > 渲染条数（被上限截断）：必须明确提示，不能静默截断。
 *
 * 【为什么这一句是必需的】
 *   输入一个"张"可能命中几百个账号。静默只显示 30 条，管理员会得出
 *   "系统里没有这个账号"的错误结论，然后去建一个重复账号。
 *   同一条口径见 H5 `views/change/apply.vue` 与
 *   `components/PermissionDelegationDialog.vue`。
 */
const usersTruncated = ref(false)

/**
 * 已选中的授权对象（用户）。
 *
 * 【为什么要把选中项单独留住】
 *   用户下拉是**远程搜索**：每敲一个关键词，选项列表就被整批换掉。
 *   选完再敲一次关键词时，选中的那条会从列表里消失，而 `el-select`
 *   找不到选项时就**直接显示绑定值（一个 Guid）**—— 界面上突然出现一串
 *   36 位十六进制。因此每次结果回来都把选中项并回去
 *   （与 `components/PermissionDelegationDialog.vue` 的 selectedUser 同一处理）。
 */
const selectedUser = ref<IdentityUser | null>(null)

/**
 * 角色下拉加载失败的原因（非空时显示明确提示，而不是静默留空）。
 *
 * 【为什么必须显式记录 —— 实测踩过】
 *   保留权限的账号打 `/api/identity/roles/all` 返回的是 **302 跳登录页**（不是 403），
 *   浏览器跟随重定向后 axios 拿到 HTML、解 JSON 抛错，而原来的写法是
 *   `catch { roles.value = [] }`：下拉框空白、**一句提示都没有**。
 *   表现是"这个页面没有选择角色的地方"，而不是"我没权限读角色" ——
 *   这属于最难排查的一类问题，必须让失败可见。
 */
const rolesFailed = ref('')

/** 权限名 → 是否授权（当前界面上的值）。 */
const granted = reactive(new Map<string, boolean>())
/** 权限名 → 是否被改动过。 */
const dirty = reactive(new Set<string>())

const keyword = ref('')
const onlyGranted = ref(false)
const onlyBusiness = ref(false)

/** 变化数量，用于保存按钮的提示。 */
const dirtyCount = computed(() => dirty.size)

/**
 * 只显示本系统的业务权限（`Xxx.Yyy...` 形式），还是连同 ABP 框架自带的
 * （`AbpIdentity.*`、`AbpTenantManagement.*` 等）一起显示。
 *
 * 默认**只显示业务权限**：一个县区管理员看到 `AbpIdentity.Users.Delete`
 * 这种权限会一头雾水，而且误勾会带来真实风险。
 */
function isBusinessPermission(name: string): boolean {
  return /^(Sys|Stu|Chg|Pho)\./.test(name)
}

const visibleGroups = computed(() => {
  const kw = keyword.value.trim().toLowerCase()

  return groups.value
    .map((g) => ({
      ...g,
      permissions: g.permissions.filter((p) => {
        if (onlyBusiness.value && !isBusinessPermission(p.name)) {
          return false
        }

        if (onlyGranted.value && !granted.get(p.name)) {
          return false
        }

        if (!kw) {
          return true
        }

        return (
          p.name.toLowerCase().includes(kw) ||
          (p.displayName ?? '').toLowerCase().includes(kw) ||
          g.displayName.toLowerCase().includes(kw)
        )
      })
    }))
    .filter((g) => g.permissions.length > 0)
})

const grantedCount = computed(() => groups.value.reduce(
  (sum, g) => sum + g.permissions.filter((p) => granted.get(p.name)).length,
  0
))

// ---------------------------------------------------------------------------
// 加载
// ---------------------------------------------------------------------------

async function loadPermissions(): Promise<void> {
  if (!providerKey.value) {
    groups.value = []

    return
  }

  loading.value = true
  dirty.clear()

  try {
    const result = await permissionApi.get(providerName.value, providerKey.value)

    entityDisplayName.value = result.entityDisplayName
    groups.value = result.groups

    granted.clear()

    for (const group of result.groups) {
      for (const permission of group.permissions) {
        granted.set(permission.name, permission.isGranted)
      }
    }
  } finally {
    loading.value = false
  }
}

function onProviderChanged(): void {
  providerKey.value = ''

  // 换了授权对象类型，就不要再记住上一个人：否则下次搜索会把一个
  // "当前并未选中"的账号并回列表顶部（见 selectedUser 的说明）
  selectedUser.value = null
  usersTruncated.value = false

  if (providerName.value === 'R' && roles.value.length > 0) {
    providerKey.value = roles.value[0].name
  }

  void loadPermissions()
}

function toggle(name: string, value: boolean): void {
  const original = groups.value
    .flatMap((g) => g.permissions)
    .find((p) => p.name === name)

  granted.set(name, value)

  if (original && original.isGranted === value) {
    dirty.delete(name)
  } else {
    dirty.add(name)
  }
}

/** 整组全选 / 全不选。 */
function toggleGroup(group: PermissionGroup, value: boolean): void {
  for (const permission of group.permissions) {
    if (permission.isEditable) {
      toggle(permission.name, value)
    }
  }
}

function groupGrantedCount(group: PermissionGroup): number {
  return group.permissions.filter((p) => granted.get(p.name)).length
}

async function save(): Promise<void> {
  if (dirty.size === 0) {
    ElMessage.info('没有需要保存的改动。')

    return
  }

  saving.value = true

  try {
    const payload = Array.from(dirty).map((name) => ({
      name,
      isGranted: granted.get(name) ?? false
    }))

    await permissionApi.update(providerName.value, providerKey.value, payload)

    ElMessage.success(`已保存 ${payload.length} 项权限改动`)
    await loadPermissions()
    await auth.loadProfile() // 自己的权限可能变了，刷新一下菜单与按钮
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    roles.value = await identityApi.getRoles()
    rolesFailed.value = ''
  } catch (error) {
    roles.value = []
    rolesFailed.value = isJsonParseError(error)
      ? '读取角色列表失败：接口未授权时被重定向到登录页（返回的不是 JSON）。' +
        '请确认当前账号拥有框架权限 AbpIdentity.Roles。'
      : `读取角色列表失败：${describeError(error)}`
  }

  /*
   * 支持从「角色管理」页带参跳转过来：`/system/permission?role=Xxx`。
   *
   * 为什么要有这个入口：建完角色紧接着就要给它配权限，
   * 如果只能自己在下拉框里再找一遍刚建的角色，体验上就是"两件事"，
   * 而实际上它们是一件事的两步。找不到角色下拉时（上面的失败分支）
   * 这里自然也不会命中，提示已经在上面显示了。
   */
  const requested = typeof route.query.role === 'string' ? route.query.role : ''

  if (requested && roles.value.some((r) => r.name === requested)) {
    providerKey.value = requested
  } else if (roles.value.length > 0) {
    providerKey.value = roles.value[0].name
  }

  await loadPermissions()
})

async function searchUsers(text: string): Promise<void> {
  if (!text) {
    return
  }

  const result = await identityApi.getUsers({ filter: text, maxResultCount: USER_SEARCH_LIMIT })
  const found = result.items ?? []

  // 截断判定只能用**服务端返回的条数**：算上下面并回来的"上次选中项"，
  // 会在"刚好只多一条"时把提示吞掉 —— 那就又变成静默截断了
  usersTruncated.value = result.totalCount > found.length

  const items = [...found]
  const selected = selectedUser.value

  if (selected && !items.some((u) => u.id === selected.id)) {
    items.unshift(selected)
  }

  users.value = items
}

/** 选中项变化时记住它（见 selectedUser 的说明）。 */
function rememberUser(id: string): void {
  selectedUser.value = users.value.find((u) => u.id === id) ?? null
}

/** 选完人：先记住他（否则再搜一次下拉会显示成 Guid），再加载他的权限。 */
function pickUser(id: string): void {
  rememberUser(id)
  void loadPermissions()
}
</script>

<template>
  <div class="perm">
    <el-card shadow="never" class="perm__picker">
      <el-form :inline="true">
        <el-form-item label="授权对象">
          <el-radio-group v-model="providerName" @change="onProviderChanged">
            <el-radio-button value="R">角色</el-radio-button>
            <el-radio-button value="U">用户</el-radio-button>
          </el-radio-group>
        </el-form-item>

        <el-form-item :label="providerName === 'R' ? '角色' : '用户'">
          <el-select
            v-if="providerName === 'R'"
            v-model="providerKey"
            placeholder="选择角色"
            style="width: 240px"
            @change="loadPermissions"
          >
            <el-option v-for="r in roles" :key="r.id" :label="r.name" :value="r.name" />
          </el-select>

          <el-select
            v-else
            v-model="providerKey"
            placeholder="输入用户名搜索"
            filterable
            remote
            :remote-method="searchUsers"
            style="width: 240px"
            @change="pickUser"
          >
            <el-option
              v-for="u in users"
              :key="u.id"
              :label="`${u.userName}（${u.name || '未填姓名'}）`"
              :value="u.id"
            />
          </el-select>

          <!-- 限了条数就必须说出来：静默截断会让人以为"系统里没有这个账号" -->
          <div v-if="providerName === 'U' && usersTruncated" class="perm__truncated">
            结果较多，请继续输入以缩小范围。
          </div>
        </el-form-item>

        <el-form-item label="搜索权限">
          <el-input v-model="keyword" placeholder="权限码 / 名称" clearable style="width: 200px">
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>
        </el-form-item>

        <el-form-item>
          <el-checkbox v-model="onlyBusiness">只显示业务权限</el-checkbox>
          <el-checkbox v-model="onlyGranted" class="perm__cb">只看已授权</el-checkbox>
        </el-form-item>

        <el-form-item>
          <el-button v-if="canAssign" type="primary" :loading="saving" :disabled="dirtyCount === 0" @click="save">
            保存{{ dirtyCount > 0 ? `（${dirtyCount} 项改动）` : '' }}
          </el-button>
        </el-form-item>
      </el-form>

      <!-- 读不到角色/用户时必须说出来：静默留空会让人以为"这里没有可选项" -->
      <el-alert
        v-if="rolesFailed"
        type="error"
        :closable="false"
        show-icon
        class="perm__alert"
        :title="rolesFailed"
      />
    </el-card>

    <el-card v-loading="loading" shadow="never">
      <template #header>
        <div class="perm__header">
          <span>
            权限配置
            <span v-if="entityDisplayName" class="perm__muted">— {{ entityDisplayName }}</span>
          </span>
          <span class="perm__muted">
            共 {{ groups.length }} 个分组、已授权 {{ grantedCount }} 项
          </span>
        </div>
      </template>

      <el-alert
        v-if="!providerKey"
        type="info"
        :closable="false"
        show-icon
        title="请先选择角色或用户。"
      />

      <el-empty v-else-if="visibleGroups.length === 0" description="没有匹配的权限项" :image-size="70" />

      <el-collapse v-else>
        <el-collapse-item
          v-for="group in visibleGroups"
          :key="group.name"
          :name="group.name"
        >
          <template #title>
            <span class="perm__group-title">
              <b>{{ group.displayName }}</b>
              <el-tag size="small" effect="plain" type="info">{{ group.name }}</el-tag>
              <el-tag size="small" :type="groupGrantedCount(group) > 0 ? 'success' : 'info'" effect="plain">
                已授 {{ groupGrantedCount(group) }} / {{ group.permissions.length }}
              </el-tag>
              <el-button
                v-if="canAssign"
                link
                type="primary"
                size="small"
                @click.stop="toggleGroup(group, true)"
              >
                全选
              </el-button>
              <el-button
                v-if="canAssign"
                link
                type="info"
                size="small"
                @click.stop="toggleGroup(group, false)"
              >
                全不选
              </el-button>
            </span>
          </template>

          <div class="perm__list">
            <div v-for="p in group.permissions" :key="p.name" class="perm__item">
              <el-checkbox
                :model-value="granted.get(p.name) ?? false"
                :disabled="!canAssign || !p.isEditable"
                @change="(v: boolean | string | number) => toggle(p.name, !!v)"
              >
                {{ p.displayName || p.name }}
              </el-checkbox>
              <span class="perm__code">{{ p.name }}</span>
              <el-tag v-if="dirty.has(p.name)" size="small" type="warning" effect="plain">已改动</el-tag>
            </div>
          </div>
        </el-collapse-item>
      </el-collapse>
    </el-card>
  </div>
</template>

<style scoped>
.perm__picker {
  margin-bottom: 16px;
}

.perm__alert {
  margin-top: 12px;
}

.perm__picker :deep(.el-form-item) {
  margin-bottom: 0;
}

.perm__cb {
  margin-left: 12px;
}

/*
 * 「结果较多」必须整行显示、并用警告色：inline 表单里它默认会挤在下拉框
 * 右边被忽略，而漏看这一句的后果是"以为系统里没有这个账号"（见 usersTruncated）。
 */
.perm__truncated {
  flex-basis: 100%;
  color: var(--el-color-warning);
  font-size: 12px;
}

.perm__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.perm__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.perm__group-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.perm__list {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 4px 16px;
}

.perm__item {
  display: flex;
  align-items: center;
  gap: 8px;
}

.perm__code {
  color: var(--el-text-color-placeholder);
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
}
</style>
