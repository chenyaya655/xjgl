<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'

import { describeError, identityApi, isJsonParseError, roleDataScopeApi } from '@/api'
import type { CodeOption, IdentityRole, RoleDataScope } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 角色管理：角色的增删改。
 *
 * 【为什么不并进「权限管理」页】
 *   两者是**两件事**：角色管理是"有哪些角色"，权限管理是"某个角色能做哪些事"。
 *   合成一页会让"新建角色"这个动作藏在一个以"授权"为主题的页面里 ——
 *   实际后果就是有人找不到它（这一页正是因此才补出来的）。
 *   但两者关系很近，所以每一行都给了「分配权限」直达入口。
 *
 * 【⚠ 本页涉及两个权限层次，是本系统唯一一处这样的地方】
 *   - **菜单能否看见** → 业务权限 `Sys.Role.*`（元数据定义，正常授权即可）；
 *   - **接口能否调用** → ABP 内置 `AbpIdentity.Roles*`（框架控制器自带的 [Authorize]）。
 *
 *   原因：本页调的是 ABP 自带的 `/api/identity/roles`，它判的是框架权限；
 *   而菜单过滤只认元数据快照里的权限名，`AbpIdentity.*` 不在快照里。
 *   所以**授权时两个都要给**，只给 `Sys.Role.*` 会出现"菜单看得见、点进来报错"。
 *   没有 `AbpIdentity.Roles` 时接口返回的是 **302 跳登录页**（不是 403）——
 *   见下面 loadFailed 的处理。
 */
const auth = useAuthStore()
const router = useRouter()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Role.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Role.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Role.Delete))

/**
 * 是否显示「分配权限」入口。
 *
 * 【为什么必须判权限 —— 这是一个真实踩到的 404】
 *   权限页（`/system/permission`）这个**路由**是由元数据里 `Sys.Permission`
 *   模块的权限决定的：没有 `Sys.Permission` 权限，路由就不会被注册。
 *   而角色管理只看 `Sys.Role.*`。两者可以独立授权 —— 于是存在
 *   "能管角色、不能配权限"的账号（实测：演示账号就是这样）。
 *   此时若无条件显示这个按钮，点下去会直接掉进 **404 页面**。
 *   本仓库其余页面（用户管理、学生管理…）都遵循"按权限显隐按钮"的约定，
 *   这里不能例外。
 */
const canAssign = computed(
  () =>
    auth.can(MetadataPermissions.Sys.Permission.Assign) ||
    auth.can(MetadataPermissions.Sys.Permission.Query)
)

/**
 * 是否显示「数据范围」列（并去读数据范围列表）。
 *
 * 【为什么单独判 `Sys.Role.Query`，而不是跟着整页可见性走】
 *   角色列表本身要的是框架权限 `AbpIdentity.Roles`，而数据范围走的是本项目自己的
 *   `/api/app/role-data-scope`，判的是元数据权限 `Sys.Role.Query` —— 两者可以独立授权。
 *   于是存在"能看角色列表、没有 Sys.Role.Query"的账号：不判就发请求，只会白吃一个 403
 *   （还带一句用户无法处理的提示），而这一列对他本来也没有意义。
 *   与上面「分配权限」的 canAssign 同一个道理。
 *   「数据范围」的**写**入口另外判 `Sys.Role.Update`（见模板里的 canUpdate）。
 */
const canQueryScope = computed(() => auth.can(MetadataPermissions.Sys.Role.Query))

const loading = ref(false)
const rows = ref<IdentityRole[]>([])
const total = ref(0)
const page = ref(1)

/**
 * 加载失败的原因（非空时页面上显示一条明确提示）。
 *
 * 【为什么必须显式记录失败，而不是像以前那样 `catch { rows = [] }`】
 *   实测过这个坑：演示账号没有 `AbpIdentity.Roles` 时请求返回 302，
 *   浏览器跟随重定向后拿到登录页 HTML，axios 解 JSON 抛错（"Unexpected token '<'"），
 *   而原来的语句是 `catch { roles.value = [] }` —— 列表空白、**一句提示都没有**。
 *   使用者的结论是"这个功能不存在"，而不是"我没权限"。
 *   把失败记下来并显示，是这个页面必须有的东西，不是锦上添花。
 */
const loadFailed = ref('')

const query = reactive({
  filter: '',
  maxResultCount: 20
})

async function load(): Promise<void> {
  loading.value = true
  loadFailed.value = ''

  try {
    const result = await identityApi.getRolePage({
      filter: query.filter.trim() || undefined,
      skipCount: (page.value - 1) * query.maxResultCount,
      maxResultCount: query.maxResultCount,
      sorting: 'name'
    })

    rows.value = result.items
    total.value = result.totalCount
  } catch (error) {
    rows.value = []
    total.value = 0
    loadFailed.value = describeRoleLoadFailure(error)
  } finally {
    loading.value = false
  }
}

/**
 * 把"读不到角色列表"翻译成一句能行动的话。
 *
 * 302 与 JSON 解析失败这两种形态**都不是权限问题的常见长相**，
 * 但在这个接口上它们**恰恰就是**权限问题（缺 `AbpIdentity.Roles`）。
 * 所以这里刻意把结论直接写出来，而不是只显示 "Unexpected token '<'"。
 */
function describeRoleLoadFailure(error: unknown): string {
  if (isJsonParseError(error)) {
    return (
      '读取角色列表失败：服务端返回的不是 JSON —— 未授权时接口会重定向到登录页，' +
      '重定向后的内容是 HTML。请确认当前账号拥有框架权限 AbpIdentity.Roles。'
    )
  }

  return `读取角色列表失败：${describeError(error)}`
}

function search(): void {
  page.value = 1
  void load()
}

/** 跳到「权限管理」并预选这个角色 —— 两步紧邻的操作，不该让人重新找一遍。 */
function gotoPermissions(row: IdentityRole): void {
  void router.push({ path: '/system/permission', query: { role: row.name } })
}

// ---------------------------------------------------------------------------
// 新增 / 编辑 / 删除
// ---------------------------------------------------------------------------

const dialogVisible = ref(false)
const saving = ref(false)
/** 非空表示编辑中；同时保存并发戳，更新时必须原样回传。 */
const editingId = ref<string | null>(null)
const editingStamp = ref('')

const form = reactive({
  name: '',
  isDefault: false,
  isPublic: false
})

function openCreate(): void {
  editingId.value = null
  editingStamp.value = ''
  form.name = ''
  form.isDefault = false
  form.isPublic = false
  dialogVisible.value = true
}

function openEdit(row: IdentityRole): void {
  editingId.value = row.id
  editingStamp.value = row.concurrencyStamp
  form.name = row.name
  form.isDefault = row.isDefault
  form.isPublic = row.isPublic
  dialogVisible.value = true
}

async function save(): Promise<void> {
  // 防重复提交：`saving` 只把按钮变成 loading，并**不能阻止**再次点击触发第二次提交。
  // 实测（Playwright 里点击在慢接口下重试）会出现两个创建请求 →
  // 第二个因角色名重复而报错，弹窗也不关，用户看到的是一句莫名其妙的"已存在"。
  if (saving.value) {
    return
  }

  const name = form.name.trim()

  if (!name) {
    ElMessage.warning('角色名不能为空。')

    return
  }

  saving.value = true

  try {
    if (editingId.value) {
      await identityApi.updateRole(editingId.value, {
        name,
        isDefault: form.isDefault,
        isPublic: form.isPublic,
        // ⚠ 必须回传列表里拿到的并发戳；传空串会被并发校验拒绝
        concurrencyStamp: editingStamp.value
      })

      ElMessage.success('已保存')
    } else {
      await identityApi.createRole({
        name,
        isDefault: form.isDefault,
        isPublic: form.isPublic
      })

      ElMessage.success('角色已创建')
    }

    dialogVisible.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function remove(row: IdentityRole): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除角色「${row.name}」吗？该操作不可恢复。` +
        '已授予该角色的权限会一并删除；仍属于该角色的用户将失去这些权限。',
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await identityApi.deleteRole(row.id)
  ElMessage.success('已删除')
  await load()
}

// ---------------------------------------------------------------------------
// 数据范围（ROADMAP 第 9 条）
//
// 与「分配权限」的区别：权限决定**能做什么**，数据范围决定**能对谁做**。
// 数据范围来自本项目自己的 `/api/app/role-data-scope`（不是 ABP 的角色接口），
// 所以它有自己的加载状态与失败形态，不能和上面的角色列表混在一起处理。
// ---------------------------------------------------------------------------

const scopes = ref<RoleDataScope[]>([])
const scopeLoading = ref(false)
/** 数据范围列表是否**成功读到**。读失败时列里显示「—」，而不是"默认"。 */
const scopeReady = ref(false)
const scopeError = ref('')

/** roleId → 范围配置。列里按 roleId 对齐两个接口（角色来自 ABP，范围来自本项目）。 */
const scopeById = computed(() => new Map(scopes.value.map((s) => [s.roleId, s])))

/**
 * 取某个角色的范围配置；`undefined` 与"取到一条未配置的记录"是**两件不同的事**：
 * 前者是"没读到"（列表还没回来 / 读失败），后者是"读到了、但它没配置"。
 * 列里必须把这两种画得不一样，否则"没读到"会被当成"按组织层级默认"。
 */
function scopeOf(roleId: string): RoleDataScope | undefined {
  return scopeById.value.get(roleId)
}

/**
 * 列里要显示的范围名；未配置（以及读不到）时返回空串，由模板决定显示什么。
 *
 * 显示名缺失时回落到后端给的码值：宁可显示一串码值也不要显示空白 ——
 * 空白在这张表里意味着"没配置"，那是另一个意思。
 */
function scopeNameOf(roleId: string): string {
  const entry = scopeOf(roleId)

  return entry?.isConfigured ? (entry.scopeName ?? entry.scope ?? '') : ''
}

async function loadScopes(): Promise<void> {
  // 没有查询权限时**不发这个请求**：它必然 403，而拦截器会弹一句用户看不懂、
  // 也无法处理的提示 —— 他本来就看不到这一列（见 canQueryScope）。
  if (!canQueryScope.value) {
    return
  }

  scopeLoading.value = true
  scopeError.value = ''

  try {
    scopes.value = await roleDataScopeApi.getList()
    scopeReady.value = true
  } catch (error) {
    // 拦截器已经统一提示过了，这里只记录状态、不再弹一次。
    // 关键是把 scopeReady 置回 false：读失败**绝不能**被画成"没有配置"，
    // 那会让人以为"默认"就是当前生效的配置（而它其实只是没读到）。
    scopes.value = []
    scopeReady.value = false
    scopeError.value = describeError(error)
  } finally {
    scopeLoading.value = false
  }
}

const scopeDialogVisible = ref(false)
const scopeSaving = ref(false)
/** 正在配置的角色。只留"是谁"，当前值通过 scopeOf 现取，避免两处状态各说各话。 */
const scopeTarget = ref<{ roleId: string; roleName: string } | null>(null)

/**
 * 「恢复默认」在单选组里的值。
 *
 * 用**空串**而不是 null：后端对 `scope` 的语义就是"null / 空串 = 清除配置"，
 * 这里直接用同一个值，提交时不必再翻译一次；而 el-radio 的 `value` 用 null
 * 与 Element Plus 的类型定义不合。
 */
const RESTORE_DEFAULT = ''

const scopeForm = reactive({
  /** 空串 = 恢复默认（见 RESTORE_DEFAULT）。 */
  scope: RESTORE_DEFAULT as string,
  remark: ''
})

const scopeOptions = ref<CodeOption[]>([])
const scopeOptionsLoading = ref(false)
const scopeOptionsError = ref('')

/**
 * 下拉项 + 说明。
 *
 * 值、中文名与**说明文案**全部来自后端（`getOptions()`）—— 这四种范围的码值
 * 不允许在前端再写一遍：可配置的范围是后端定的（而且刻意不含"自定义组织集合"），
 * 抄一份的结果是后端增删范围时前端静默不一致。
 * 说明文案原来在前端有一份"码值 → 文案"的映射，已改为后端下发
 * （`CodeOption.Description`）—— 那份映射虽然只用于显示，但它同样是第二份码值。
 */
const scopeChoices = computed(() =>
  scopeOptions.value.map((option) => ({
    value: option.value,
    label: option.label,
    // 后端没给说明时显示为空（不猜一句），模板里用 v-if 控制
    hint: option.description || undefined
  }))
)

/**
 * 拉取可选范围。只在**首次打开弹窗**时请求：选项由后端常量生成、不会变，
 * 每次开弹窗都请求一次只是白跑一趟。
 *
 * 失败时把原因留下来（所以下次开弹窗会重试），而**不是静默空着** ——
 * 一个空的单选组看起来像"后端没给选项"，会让人以为是自己没权限。
 */
async function loadScopeOptions(): Promise<void> {
  if (scopeOptions.value.length > 0 || scopeOptionsLoading.value) {
    return
  }

  scopeOptionsLoading.value = true
  scopeOptionsError.value = ''

  try {
    scopeOptions.value = await roleDataScopeApi.getOptions()
  } catch (error) {
    // 同上：拦截器已经弹过提示了，这里只把原因留在弹窗里，不关弹窗 ——
    // 关掉弹窗，用户看到的只是"点了没反应"。
    scopeOptionsError.value = describeError(error)
  } finally {
    scopeOptionsLoading.value = false
  }
}

/**
 * 打开「数据范围」弹窗。
 *
 * 回显时以 `isConfigured` 为准，**不是**"看 scope 有没有值"：
 * "显式配置成本级及下级"要显示成它自己，而不是被当成没配置（见 RoleDataScope 的说明）。
 * 未配置时单选组落在「恢复默认」上，而不是留空 —— 留空会让"提交即清除配置"
 * 这件事看起来像"用户还没选"。
 */
function openScope(row: IdentityRole): void {
  const current = scopeOf(row.id)

  scopeTarget.value = { roleId: row.id, roleName: row.name }
  // 回显以 isConfigured 为准（见函数注释）；未配置时落在「恢复默认」上。
  scopeForm.scope = (current?.isConfigured ? current.scope : null) ?? RESTORE_DEFAULT
  scopeForm.remark = current?.remark ?? ''
  scopeDialogVisible.value = true

  void loadScopeOptions()
}

/**
 * 保存数据范围。
 *
 * `scope` 传 null = 清除配置（后端会删掉这一行，回到按组织层级推导），
 * 这正是「恢复默认」要发的请求。保存后**重新拉取**而不是本地改内存：
 * 生效规则（"最宽者胜"）与角色集合都由后端定，本地拼一个"看起来对"的状态
 * 迟早会和后端不一致，而这类不一致没有任何提示。
 */
async function saveScope(): Promise<void> {
  // 与新增角色同样的防重复提交：saving 只让按钮转圈，挡不住第二次点击。
  if (scopeSaving.value || !scopeTarget.value) {
    return
  }

  scopeSaving.value = true

  try {
    await roleDataScopeApi.set({
      roleId: scopeTarget.value.roleId,
      // 空串统一成 null：后端判据是 IsNullOrWhiteSpace，传哪个都一样，
      // 但显式写 null 让"这是清除配置"在请求体里一眼可见。
      scope: scopeForm.scope.trim() || null,
      remark: scopeForm.remark.trim() || null
    })

    ElMessage.success(scopeForm.scope ? '数据范围已保存' : '已恢复为默认（按组织层级）')

    scopeDialogVisible.value = false

    // 两份数据来自两个接口（角色来自 ABP、范围来自本项目），都要刷：
    // 只刷角色列表的话，新配的范围在界面上不会变，看起来像"没保存上"。
    await Promise.all([load(), loadScopes()])
  } catch {
    // 失败**不关弹窗、也不再弹提示**：拦截器已经统一提示过了，
    // 关掉弹窗只会让人重新点一遍、重新填一次备注。
  } finally {
    scopeSaving.value = false
  }
}

onMounted(() => {
  void load()
  void loadScopes()
})
</script>

<template>
  <div class="role">
    <el-card shadow="never" class="role__filter">
      <el-form :inline="true" @submit.prevent="search">
        <el-form-item label="关键字">
          <el-input
            v-model="query.filter"
            placeholder="角色名"
            clearable
            style="width: 240px"
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
        <div class="role__header">
          <span>角色（共 {{ total }} 个）</span>
          <el-button v-if="canCreate" type="primary" @click="openCreate">
            <AppIcon name="Plus" />
            <span>新建角色</span>
          </el-button>
        </div>
      </template>

      <el-alert
        v-if="loadFailed"
        type="error"
        :closable="false"
        show-icon
        class="role__alert"
        :title="loadFailed"
      />

      <!--
        数据范围读失败时给一句明确的话：列里显示「—」是**没读到**，
        不是"没有配置"。不说明的话，看到一串「—」只能猜。
      -->
      <el-alert
        v-if="scopeError"
        type="warning"
        :closable="false"
        show-icon
        class="role__alert"
        title="数据范围读取失败，该列暂显示为「—」（不影响角色的其它操作）。"
      />

      <el-table v-loading="loading || scopeLoading" :data="rows" border stripe>
        <el-table-column prop="name" label="角色名" min-width="200" />
        <el-table-column label="类型" width="110" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.isStatic" size="small" type="warning" effect="plain">内置</el-tag>
            <el-tag v-else size="small" type="info" effect="plain">自定义</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="默认角色" width="110" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.isDefault" size="small" type="success" effect="plain">是</el-tag>
            <span v-else class="role__muted">否</span>
          </template>
        </el-table-column>
        <el-table-column label="公开" width="90" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.isPublic" size="small" effect="plain">是</el-tag>
            <span v-else class="role__muted">否</span>
          </template>
        </el-table-column>
        <!--
          「数据范围」列：权限判的是 Sys.Role.Query，与角色列表本身要的
          AbpIdentity.Roles 是两套（见 canQueryScope 的说明）。
        -->
        <el-table-column v-if="canQueryScope" label="数据范围" min-width="230">
          <template #default="{ row }">
            <span v-if="!scopeReady" class="role__muted">—</span>
            <template v-else-if="scopeNameOf(row.id)">
              <el-tag size="small" effect="plain">{{ scopeNameOf(row.id) }}</el-tag>
              <div
                v-if="scopeOf(row.id)?.remark"
                class="role__hint role__ellipsis"
                :title="scopeOf(row.id)?.remark ?? ''"
              >
                {{ scopeOf(row.id)?.remark }}
              </div>
            </template>
            <!--
              未配置 = 按组织层级推导（省厅=全省，市/县/校=本级及下级）。
              必须与"显式配置成本级及下级"画得不一样：两者今天的效果可能完全相同，
              但前者跟着默认规则走、后者被钉死（见 RoleDataScope.isConfigured 的说明）。
            -->
            <span
              v-else
              class="role__muted"
              title="未配置：按组织层级推导（省厅=全省，市/县/校=本级及下级）"
            >
              默认（按组织层级）
            </span>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="170" align="center">
          <template #default="{ row }">
            {{ (row.creationTime ?? '').replace('T', ' ').slice(0, 19) || '—' }}
          </template>
        </el-table-column>
        <el-table-column label="操作" width="340" align="center" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canAssign" link type="primary" @click="gotoPermissions(row)">分配权限</el-button>
            <!--
              「数据范围」写入口判 Sys.Role.Update（与「编辑」同一个权限码）：
              配数据范围就是"改这个角色"。列本身另判 Query（canQueryScope）。
            -->
            <el-button v-if="canUpdate" link type="primary" @click="openScope(row)">数据范围</el-button>
            <!--
              内置角色（admin）不许改名与删除：ABP 会拒绝，而且它是系统的最后一道门，
              删掉之后没人能进后台。这里直接禁用并说明原因，比让人点了才报错好。
            -->
            <el-button v-if="canUpdate" link type="primary" :disabled="row.isStatic" @click="openEdit(row)">
              编辑
            </el-button>
            <el-button v-if="canDelete" link type="danger" :disabled="row.isStatic" @click="remove(row)">
              删除
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="role__pager"
        :total="total"
        layout="total, sizes, prev, pager, next"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------------- 新增 / 编辑 ---------------- -->
    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑角色' : '新建角色'" width="520px">
      <el-form label-width="100px">
        <el-form-item label="角色名" required>
          <el-input v-model="form.name" placeholder="如 CountyAdmin、SchoolRegistrar" />
          <div class="role__hint">
            建议用英文标识（会写进 JWT 的 role 声明）。中文也能跑通，但如果将来接第三方系统，
            中文角色名在 URL 与日志里容易出现编码问题。
          </div>
        </el-form-item>

        <el-form-item label="默认角色">
          <el-switch v-model="form.isDefault" />
          <div class="role__hint">
            新用户注册/激活时**自动**获得该角色。本系统的账号走「预置 + 激活」创建，
            激活时按预置记录里的角色名授权，因此这里通常保持关闭。
          </div>
        </el-form-item>

        <el-form-item label="公开">
          <el-switch v-model="form.isPublic" />
          <div class="role__hint">仅影响 ABP 自身的语义（可供其它客户端查询），本系统不强依赖。</div>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 数据范围 ---------------- -->
    <el-dialog v-model="scopeDialogVisible" title="数据范围" width="640px">
      <div v-if="scopeTarget">
        <div class="role__scope-role">
          角色：<strong>{{ scopeTarget.roleName }}</strong>
        </div>

        <el-alert
          type="info"
          :closable="false"
          show-icon
          class="role__alert"
          title="数据范围决定「能看谁的数据」，与权限（能做什么）是两件事"
        >
          <div class="role__hint">
            不配置时，范围按组织层级推导：省厅=全省，市/县/校=本级及下级 ——
            也就是加这个功能之前的行为。配置之后就按这里配的来，
            所以「某个市局只看本市、不看下属区县」这类例外现在可以单独配（选「仅本级」）。
          </div>
        </el-alert>

        <el-form label-width="90px">
          <el-form-item label="数据范围">
            <div v-loading="scopeOptionsLoading" class="role__scope-box">
              <el-radio-group v-model="scopeForm.scope" class="role__scope-group">
                <!--
                  「恢复默认」必须是**一个明确的选项**，不能靠"什么都不选"来表达：
                  null 在这套语义里就是"清除配置"这个动作本身，把它画成一个空单选组，
                  管理员会以为"还没填"，而它其实是一个有含义的取值。
                -->
                <el-radio :value="RESTORE_DEFAULT" class="role__scope-item">
                  恢复默认（按组织层级）
                  <span class="role__hint role__scope-hint">
                    清除该角色的数据范围配置，回到按组织层级推导：省厅=全省，市/县/校=本级及下级。
                  </span>
                </el-radio>

                <!--
                  选项、中文名与说明文案一律来自后端 getOptions()，前端不写这四个码值；
                  后端刻意不提供「自定义组织集合」，所以它不会出现在这个列表里。
                -->
                <el-radio
                  v-for="choice in scopeChoices"
                  :key="choice.value"
                  :value="choice.value"
                  class="role__scope-item"
                >
                  {{ choice.label }}
                  <span v-if="choice.hint" class="role__hint role__scope-hint">{{ choice.hint }}</span>
                </el-radio>
              </el-radio-group>

              <div v-if="scopeOptionsError" class="role__hint role__scope-error">
                范围选项没能读取（{{ scopeOptionsError }}）。可以关掉弹窗重开重试；
                已经配好的范围不会因此改变，上面的「恢复默认」也仍然可用。
              </div>
            </div>
          </el-form-item>

          <el-form-item label="备注">
            <el-input
              v-model="scopeForm.remark"
              type="textarea"
              :rows="2"
              maxlength="256"
              show-word-limit
              placeholder="为什么给这个角色配这个范围（半年后没人记得）"
            />
          </el-form-item>
        </el-form>

        <el-alert
          type="warning"
          :closable="false"
          show-icon
          title="多个角色都配了范围时，取最宽的那个（这是刻意的）"
        >
          <div class="role__hint">
            一个人可能同时属于多个角色。权限是加法合并的（任一角色授予即拥有），
            数据范围沿用同一套语义：最宽者胜 —— 否则给某人加一个"全省"的角色，
            他的可见范围反而会变小，那种"权限越多看得越少"的行为没人能理解。
            由此带来两点：给某人加一个范围更宽的角色等于「扩权」，改完要回头看
            他身上的其它角色；反过来想收窄某人的可见范围，只调窄他其中一个角色是没用的。
          </div>
        </el-alert>
      </div>

      <template #footer>
        <el-button @click="scopeDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="scopeSaving" @click="saveScope">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.role__filter {
  margin-bottom: 16px;
}

.role__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.role__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.role__alert {
  margin-bottom: 12px;
}

.role__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.role__muted {
  color: var(--el-text-color-secondary);
}

.role__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

/* 备注可能很长：列宽有限，超出用省略号，完整内容挂 title。 */
.role__ellipsis {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.role__scope-role {
  margin-bottom: 12px;
}

/*
  单选组是**纵向**的，且每一项下面跟一段说明。
  横排会让四个选项的说明互相挤在一起，"仅本级"和"本级及下级"这两项
  正是最容易看错的一对，不能靠"挤一挤"来排版。
*/
.role__scope-box {
  width: 100%;
  min-height: 32px;
}

.role__scope-group {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
}

.role__scope-item {
  height: auto;
  margin-bottom: 10px;
  white-space: normal;
  align-items: flex-start;
}

.role__scope-item :deep(.el-radio__label) {
  line-height: 1.6;
}

.role__scope-hint {
  display: block;
}

.role__scope-error {
  color: var(--el-color-warning);
}
</style>
