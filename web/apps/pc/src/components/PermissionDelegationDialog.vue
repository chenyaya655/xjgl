<script setup lang="ts">
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { describeError, identityApi, isJsonParseError, permissionDelegationApi } from '@xjgl/api'
import type { DelegatablePermission, IdentityUser, PermissionDelegation } from '@xjgl/api'
import AppIcon from '@/components/AppIcon.vue'

/**
 * 「我的权限委派」对话框。
 *
 * 【为什么它必须挂在每个登录用户都到得了的地方，而不是"系统管理"菜单下】
 *   委派是"把我自己的权限临时借给同事"（典型场景：审核人休假）。
 *   后端刻意不为它定义一个 `Sys.Delegation.*`：能借什么由**子集约束**决定
 *   （只能借出自己确实拥有的），撤销由**归属约束**决定（只有借出人本人能撤）。
 *   于是**任何登录用户**都是这个功能的使用者 —— 一个持有「异动审核」的普通教师
 *   可能一个管理菜单都没有（`sys_module` 里没有给他授权的模块），
 *   而他要休假时正是最需要它的人。所以入口挂在工作台的「快捷入口」里，
 *   并且**不按 `Sys.User.Query` / `Sys.Permission.*` 之类的管理员权限显隐**。
 *
 * 【三条必须让用户看见的领域事实（否则这个功能会被用错）】
 *   1. 委派只授予**能力**，不改动**数据范围**：对方拿到的是调用接口的资格，
 *      能看到哪些数据仍由他自己所在组织的范围决定 —— 借出一条权限，
 *      不等于把你能看到的范围一起借出去。这条最容易被误解，因此放在最前面；
 *   2. 只能借出**自己拥有、且允许委派**的权限：系统管理类（`Sys.*`）、
 *      导出类（`*.Export`）、照片存储补偿这类运维动作由后端判定为不可委派，
 *      下拉框里不会出现它们（数据源是后端的 `GetDelegatablePermissionsAsync`，
 *      前端一条权限都不写死）；
 *   3. 到期时间**留空 = 长期有效，需要手动撤销**：填了到期的由后台任务自动回收，
 *      留空的没有兜底。界面上必须把"没有自动回收"这句话说出来。
 *
 * 【为什么不做轮询】
 *   与「我的导出任务」同一个理由：委派台账是"回看与收尾"用的，
 *   打开时拉一次 + 手动刷新就够；自动轮询只会让"打开看一眼"的用户一直打接口。
 */
const visible = defineModel<boolean>({ default: false })

/** 被委派人下拉一次取多少条（按姓名/用户名远程搜索，不是一次拉全量用户）。 */
const USER_PAGE_SIZE = 100

// ---------------------------------------------------------------------------
// 台账
// ---------------------------------------------------------------------------

const loading = ref(false)
const rows = ref<PermissionDelegation[]>([])
/** 正在撤销的行 Id：把 loading 精确到行，避免整表转圈。 */
const actingId = ref<string | null>(null)

/**
 * 加载失败的原因（非空时对话框里显示明确提示）。
 *
 * 拦截器已经弹过提示（见 `http.ts` 的错误处理约定），这里**不再弹第二次**，
 * 但要留下文字并清空列表：否则失败后界面还挂着上一次的结果，
 * 用户会以为自己看到的是最新的。
 */
const loadFailed = ref('')

/**
 * 我借出去的条数。
 *
 * 两个方向混在同一张表里（后端就是这么返回的），因此给一个计数 ——
 * "我到底借出去几条"是这个对话框最常被问的问题。
 */
const mineCount = computed(() => rows.value.filter((row) => row.isMine && row.isActive).length)

async function load(): Promise<void> {
  loading.value = true
  loadFailed.value = ''

  try {
    const list = await permissionDelegationApi.getList()

    if (disposed) {
      return
    }

    rows.value = list
  } catch (error) {
    if (disposed) {
      return
    }

    rows.value = []
    loadFailed.value = `读取委派台账失败：${describeError(error)}`
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------------------
// 显示辅助
// ---------------------------------------------------------------------------

function formatTime(value?: string | null): string {
  return value ? value.replace('T', ' ').slice(0, 19) : '—'
}

/**
 * 权限的显示文案。
 *
 * 优先用后端翻译好的中文名（取自模块元数据的操作名）；查不到时后端会回落成权限码，
 * 前端跟着回落 —— 显示一个权限码总比显示空白强（空白会被读成"这条委派没有权限"）。
 */
function permissionLabel(row: PermissionDelegation): string {
  return row.permissionDisplayName || row.permissionName
}

/**
 * 可委派权限下拉项的文案。
 *
 * 与后端 `DelegatablePermissionDto.DisplayName` 的约定一致：可能为 null，
 * 此时回落成权限码。**不在这里做任何码值/权限映射**。
 */
function permissionOptionLabel(option: DelegatablePermission): string {
  return option.displayName || option.permissionName
}

function userLabel(user: IdentityUser): string {
  return user.name ? `${user.name}（${user.userName}）` : user.userName
}

// ---------------------------------------------------------------------------
// 撤销
// ---------------------------------------------------------------------------

/**
 * 撤销一条委派。
 *
 * 只有「我借出的 + 仍在生效」才给按钮：别人借给我的那条，撤销权在**借出人**手上
 * （后端也会拒），与其放一个点了必然报错的按钮，不如把这句话写在操作列里。
 */
async function revoke(row: PermissionDelegation): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定撤销「${permissionLabel(row)}」给「${row.delegateeName ?? '该用户'}」的委派吗？` +
        '撤销后对方通过这条委派获得的能力立即回收；如果他的角色本来就有这条权限，' +
        '或者还有别的生效委派，那部分不受影响。',
      '撤销委派',
      { type: 'warning', confirmButtonText: '确定撤销', cancelButtonText: '不撤销' }
    )
  } catch {
    return // 用户取消
  }

  actingId.value = row.id

  try {
    await permissionDelegationApi.revoke(row.id)
    ElMessage.success('已撤销')
  } catch {
    // 拦截器已提示（例如"只能撤销自己委派出去的权限"）；不关对话框
  } finally {
    actingId.value = null
    // 无论成功与否都刷新：失败时也让用户看到这条委派此刻的真实状态
    await load()
  }
}

// ---------------------------------------------------------------------------
// 新建委派
// ---------------------------------------------------------------------------

const formVisible = ref(false)
const saving = ref(false)

const form = reactive({
  delegateeUserId: '',
  permissionName: '',
  /** 留空 = 长期有效。Element Plus 清空日期时给的是 null，这里统一按 null 提交。 */
  expiresAt: null as string | null,
  remark: ''
})

/** 被委派人下拉（远程搜索）。 */
const users = ref<IdentityUser[]>([])
const usersLoading = ref(false)
const usersFailed = ref('')

/**
 * 命中总数 > 渲染条数（被 `USER_PAGE_SIZE` 截断）：必须明确提示，不能静默截断。
 *
 * 【为什么这一句是必需的（债 30 / 规约 12.2）】
 *   教师账号动辄上万条，所以这里只能"关键字服务端检索 + 硬上限"，
 *   而不是把全部账号塞进下拉。但上限一旦存在就必须说出来：
 *   搜"张"命中几百人却只显示 100 条时，界面若无提示，用户会得出
 *   "系统里没有这个老师"的错误结论 —— 这与 H5 `views/change/apply.vue`
 *   的"结果较多，请继续输入以缩小范围"是同一条口径。
 */
const usersTruncated = ref(false)

/**
 * 已选中的被委派人。
 *
 * 【为什么要把选中项单独留住】
 *   被委派人下拉是**远程搜索**：每敲一个关键词，选项列表就被整批换掉。
 *   如果选完之后再敲一次关键词，选中的那条会从列表里消失，
 *   而 `el-select` 找不到选项时就**直接显示绑定值（一个 Guid）**——
 *   界面上突然出现一串 36 位的十六进制，没有人能看出那是"已选中的人"。
 *   因此每次搜索结果回来都把选中项并回去（见 loadUsers）。
 */
const selectedUser = ref<IdentityUser | null>(null)

/** 可委派权限下拉（数据源只能是后端）。 */
const permissions = ref<DelegatablePermission[]>([])
const permissionsLoading = ref(false)
const permissionsFailed = ref('')

/**
 * 加载失败与"过期响应"的守卫。
 *
 * 两件事真的会出问题，所以这里必须有一个 disposed/序号式的守卫：
 *   1) **远程搜索的竞态**：先敲 "zh" 再敲 "zhang"，两个请求的返回顺序不保证，
 *      后回来的旧结果会把新结果覆盖掉 —— 下拉里显示的是上一个关键词的人。
 *      用自增序号丢弃过期响应（只认最后一次请求）。
 *   2) 组件卸载（工作台被换掉）后不该再回写状态；否则下次打开会先闪一下
 *      上一次的报错或半截数据。
 */
let disposed = false
let userSearchSeq = 0

onUnmounted(() => {
  disposed = true
})

/**
 * 拉取被委派人下拉。
 *
 * 【这里的失败必须**说出来**，不能表现成空下拉】
 *   用户列表走的是 ABP 自带的 `/api/identity/users`，它要求框架权限
 *   `AbpIdentity.Users`。没有这个权限时接口返回的是 **302 跳登录页**（不是 403），
 *   浏览器跟随重定向后 axios 拿到的是登录页 HTML、解 JSON 抛错 ——
 *   如果这里只写 `catch {}`，用户看到的就是**一个空的"被委派人"下拉**，
 *   结论必然是"系统里没有别的老师"，而不是"我这个账号缺一个框架权限"。
 *   因此把原因原文写在表单里（拦截器的提示也照常弹，这里不重复弹）。
 */
async function loadUsers(filter?: string): Promise<void> {
  const seq = ++userSearchSeq

  usersLoading.value = true

  try {
    const result = await identityApi.getUsers({
      filter: filter?.trim() || undefined,
      maxResultCount: USER_PAGE_SIZE,
      sorting: 'userName'
    })

    // 过期响应直接丢掉，避免旧关键词的结果盖掉新结果
    if (disposed || seq !== userSearchSeq) {
      return
    }

    const items = result.items ?? []

    // 截断判定只能用**服务端返回的条数**：算上下面并回来的"选中项"，
    // 会在"刚好只多一条"时把提示吞掉（那就又变成静默截断了）
    usersTruncated.value = result.totalCount > items.length

    const selected = selectedUser.value

    // 把选中项并回列表，否则选完再搜索时下拉会显示成 Guid（见 selectedUser 的说明）
    if (selected && !items.some((item) => item.id === selected.id)) {
      items.unshift(selected)
    }

    users.value = items
    usersFailed.value = ''
  } catch (error) {
    if (disposed || seq !== userSearchSeq) {
      return
    }

    users.value = []
    usersTruncated.value = false
    usersFailed.value = isJsonParseError(error)
      ? `读取用户列表失败：接口未授权时被重定向到登录页（返回的不是 JSON）。` +
        `请确认当前账号拥有框架权限 AbpIdentity.Users；没有它就无法选择被委派人。`
      : `读取用户列表失败：${describeError(error)}`
  } finally {
    if (seq === userSearchSeq) {
      usersLoading.value = false
    }
  }
}

/** 选中项变化时记住它（见 selectedUser 的说明）。 */
function rememberUser(id: string): void {
  selectedUser.value = users.value.find((user) => user.id === id) ?? null
}

/**
 * 拉取"我可以委派的权限"。
 *
 * 数据源**只能是后端**：它返回的是「我拥有的 ∩ 允许委派的」。
 * 这条接口对任何登录用户都可用（不需要管理员权限），所以拉到空数组
 * 只有一个含义 —— 我确实没有可借出的权限，界面要把这句话说清楚。
 */
async function loadPermissions(): Promise<void> {
  permissionsLoading.value = true

  try {
    const list = await permissionDelegationApi.getDelegatablePermissions()

    if (disposed) {
      return
    }

    permissions.value = list
    permissionsFailed.value = ''
  } catch (error) {
    if (disposed) {
      return
    }

    permissions.value = []
    permissionsFailed.value = `读取可委派权限失败：${describeError(error)}`
  } finally {
    permissionsLoading.value = false
  }
}

/**
 * 过去的时间不给选。
 *
 * 后端会以"到期时间必须晚于当前时间"拒绝过去的时间点 —— 与其让用户填完再吃一个
 * 报错，不如一开始就选不到（今天仍然可选，因为只禁到"今天 00:00"之前）。
 */
function disablePastDate(date: Date): boolean {
  const todayStart = new Date()

  todayStart.setHours(0, 0, 0, 0)

  return date.getTime() < todayStart.getTime()
}

/**
 * 展开「新建委派」表单。
 *
 * 下拉数据**在这里才拉**，不在打开对话框时拉：被委派人那一项需要框架权限
 * `AbpIdentity.Users`，而很多人（正是本功能的典型用户）没有 ——
 * 一打开就报错的对话框会让人以为整个功能坏了。
 */
function openForm(): void {
  formVisible.value = true
  form.delegateeUserId = ''
  form.permissionName = ''
  form.expiresAt = null
  form.remark = ''
  selectedUser.value = null
  usersFailed.value = ''
  usersTruncated.value = false
  permissionsFailed.value = ''

  void loadUsers()
  void loadPermissions()
}

function closeForm(): void {
  formVisible.value = false
  usersFailed.value = ''
  permissionsFailed.value = ''
}

/**
 * 提交委派。
 *
 * 提交成功后**重新拉一次台账**，而不是把后端返回的那条塞进本地数组：
 * 台账里"我借出的/别人借给我的"、生效状态都由后端算，
 * 前端自己拼一条迟早会与下一次刷新不一致。
 */
async function submit(): Promise<void> {
  if (!form.delegateeUserId || !form.permissionName) {
    ElMessage.warning('请先选择被委派人和要委派的权限。')
    return
  }

  saving.value = true

  try {
    await permissionDelegationApi.create({
      delegateeUserId: form.delegateeUserId,
      permissionName: form.permissionName,
      // 空串统一成 null：留空就是"长期有效"，不是"填了一个空时间"
      expiresAt: form.expiresAt || null,
      remark: form.remark.trim() || null
    })

    ElMessage.success('委派已生效，对方现在就可以使用这项权限了')
    closeForm()
    await load()
  } catch {
    // 拦截器已提示（例如"你没有权限…不能把它委派给别人""这条权限已经委派给该用户"）；
    // 不关表单，让用户能直接改
  } finally {
    saving.value = false
  }
}

// 每次打开都重新拉一次：委派在对话框关闭期间会到期、会被撤销
watch(visible, (opened) => {
  if (opened) {
    formVisible.value = false
    selectedUser.value = null
    users.value = []
    permissions.value = []
    void load()
  }
})
</script>

<template>
  <el-dialog v-model="visible" title="我的权限委派" width="1080px" top="6vh" destroy-on-close>
    <el-alert type="warning" :closable="false" show-icon class="delegation__tip">
      <template #title>委派只授予「能力」，不改动「数据范围」</template>

      <div class="delegation__tip-body">
        <p>
          对方拿到的是调用相应功能的资格；他能看到哪些数据，仍然由<strong>他自己所在组织</strong>的范围决定 ——
          借出一条权限，不等于把你能看到的范围一起借出去。
        </p>
        <p>
          只能借出自己确实拥有、且允许委派的权限：系统管理类、导出类、存储补偿等不在下拉里
          （导出等于把数据带走，事后无从追溯），后端也不接受。
        </p>
        <p>
          到期时间留空表示长期有效，<strong>不会</strong>被自动回收，将来必须回到这里手动撤销；
          填了到期时间的由后台任务自动回收。
        </p>
      </div>
    </el-alert>

    <el-alert
      v-if="loadFailed"
      type="error"
      :closable="false"
      show-icon
      class="delegation__tip"
      :title="loadFailed"
    />

    <div class="delegation__toolbar">
      <el-button :loading="loading" @click="load">
        <AppIcon name="Refresh" />
        <span>刷新</span>
      </el-button>

      <el-button v-if="!formVisible" type="primary" @click="openForm">
        <AppIcon name="Plus" />
        <span>新建委派</span>
      </el-button>

      <span class="delegation__muted">
        共 {{ rows.length }} 条，其中我借出且生效中 {{ mineCount }} 条
      </span>
    </div>

    <!-- ---------------- 新建委派 ---------------- -->
    <el-card v-if="formVisible" shadow="never" class="delegation__form">
      <el-form label-width="90px">
        <el-form-item label="被委派人" required>
          <el-alert
            v-if="usersFailed"
            type="error"
            :closable="false"
            show-icon
            class="delegation__form-tip"
            :title="usersFailed"
          />

          <!--
            远程搜索而不是一次拉全量用户：教师账号动辄上万条，
            一次拉回来既慢又会把下拉塞满；这里按姓名/用户名向后端要一页。
            这一页是**有硬上限**的（USER_PAGE_SIZE），所以下面必须把"被截断了"说出来。
          -->
          <el-select
            v-model="form.delegateeUserId"
            filterable
            remote
            reserve-keyword
            :remote-method="loadUsers"
            :loading="usersLoading"
            :disabled="!!usersFailed"
            placeholder="输入姓名 / 用户名搜索"
            style="width: 100%"
            @change="rememberUser"
          >
            <el-option v-for="u in users" :key="u.id" :label="userLabel(u)" :value="u.id" />
          </el-select>

          <!-- 限了条数就必须说出来：静默截断会让人以为"系统里没有这个老师" -->
          <div v-if="usersTruncated && !usersFailed" class="delegation__truncated">
            结果较多，请继续输入以缩小范围。
          </div>
        </el-form-item>

        <el-form-item label="权限" required>
          <el-select
            v-model="form.permissionName"
            filterable
            :loading="permissionsLoading"
            :disabled="!!permissionsFailed || permissions.length === 0"
            placeholder="只列出你拥有、且允许委派的权限"
            style="width: 100%"
          >
            <el-option
              v-for="p in permissions"
              :key="p.permissionName"
              :label="permissionOptionLabel(p)"
              :value="p.permissionName"
            />
          </el-select>

          <div v-if="permissionsFailed" class="delegation__hint delegation__hint--error">
            {{ permissionsFailed }}
          </div>
          <div v-else-if="!permissionsLoading && permissions.length === 0" class="delegation__hint">
            你没有可委派的权限：只能借出自己确实拥有、且属于"业务办理"的那部分
            （系统管理类、导出类、存储补偿不属于可委派范围）。
          </div>
        </el-form-item>

        <el-form-item label="到期时间">
          <el-date-picker
            v-model="form.expiresAt"
            type="datetime"
            value-format="YYYY-MM-DDTHH:mm:ss"
            :disabled-date="disablePastDate"
            placeholder="留空 = 长期有效（需手动撤销）"
            style="width: 100%"
          />

          <div class="delegation__hint">
            留空表示长期有效：它不会被自动回收，将来必须回到这里手动撤销。
            到期时间必须晚于当前时间，否则后端会拒绝。
          </div>
        </el-form-item>

        <el-form-item label="备注">
          <!-- 长度上限与后端 `CreatePermissionDelegationDto.Remark` 的 StringLength(256) 一致 -->
          <el-input
            v-model="form.remark"
            type="textarea"
            :rows="2"
            maxlength="256"
            show-word-limit
            placeholder="为什么借出（如：休假期间由同事代审）"
          />
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="saving" :disabled="!!usersFailed" @click="submit">
            提交委派
          </el-button>
          <el-button @click="closeForm">取消</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <!-- ---------------- 台账（两个方向） ---------------- -->
    <el-table
      v-loading="loading"
      :data="rows"
      border
      stripe
      max-height="50vh"
      empty-text="还没有与你相关的权限委派"
    >
      <el-table-column label="权限" min-width="260">
        <template #default="{ row }">
          <span class="delegation__perm">{{ permissionLabel(row) }}</span>
          <!--
            方向必须一眼看得出来：我借出的（我能撤销）与别人借给我的（我只能看）
            在数据上只差一个 isMine，而界面上长成一样的话，用户会点撤销然后吃一个报错。
          -->
          <el-tag
            size="small"
            effect="plain"
            :type="row.isMine ? 'warning' : 'info'"
            class="delegation__dir"
          >
            {{ row.isMine ? '我借出的' : '别人借我的' }}
          </el-tag>
          <div v-if="row.permissionDisplayName" class="delegation__muted">{{ row.permissionName }}</div>
        </template>
      </el-table-column>

      <el-table-column label="借出人" width="130" show-overflow-tooltip>
        <template #default="{ row }">
          <span>{{ row.delegatorName ?? '—' }}</span>
          <span v-if="row.isMine" class="delegation__muted">（我）</span>
        </template>
      </el-table-column>

      <el-table-column label="借给人" width="130" show-overflow-tooltip>
        <template #default="{ row }">
          <span>{{ row.delegateeName ?? '—' }}</span>
          <span v-if="!row.isMine" class="delegation__muted">（我）</span>
        </template>
      </el-table-column>

      <el-table-column label="到期" width="190">
        <template #default="{ row }">
          <span v-if="row.expiresAt">{{ formatTime(row.expiresAt) }}</span>
          <!-- 留空的没有自动回收兜底，必须写出来，否则这条委派会一直留在对方身上 -->
          <span v-else class="delegation__forever">长期有效（需手动撤销）</span>
        </template>
      </el-table-column>

      <el-table-column label="状态" width="150" align="center">
        <template #default="{ row }">
          <!-- 生效与否由后端的 isActive 决定，不拿 expiresAt 和本地时间比（时区/时钟不一定一致） -->
          <el-tag size="small" :type="row.isActive ? 'success' : 'info'">
            {{ row.isActive ? '生效中' : '已失效' }}
          </el-tag>
          <div v-if="!row.isActive && row.revokedReason" class="delegation__muted">
            {{ row.revokedReason }}
          </div>
        </template>
      </el-table-column>

      <el-table-column label="备注" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">{{ row.remark ?? '—' }}</template>
      </el-table-column>

      <el-table-column label="操作" width="150" align="center" fixed="right">
        <template #default="{ row }">
          <el-button
            v-if="row.isMine && row.isActive"
            link
            type="danger"
            :loading="actingId === row.id"
            @click="revoke(row)"
          >
            撤销
          </el-button>

          <span v-else-if="!row.isMine && row.isActive" class="delegation__muted">
            由借出人撤销
          </span>

          <span v-else class="delegation__muted">—</span>
        </template>
      </el-table-column>
    </el-table>

    <template #footer>
      <el-button @click="visible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.delegation__tip {
  margin-bottom: 12px;
}

.delegation__tip-body p {
  margin: 4px 0;
  line-height: 1.6;
}

.delegation__toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.delegation__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.delegation__form {
  margin-bottom: 12px;
}

.delegation__form-tip {
  margin-bottom: 8px;
}

.delegation__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}

.delegation__hint--error {
  color: var(--el-color-danger);
}

/*
 * 「结果较多」用警告色而不是灰色说明：它不是错误（列表仍可用），
 * 但漏看它的后果是"以为系统里没有这个老师"，必须比普通提示更显眼。
 */
.delegation__truncated {
  margin-top: 4px;
  color: var(--el-color-warning);
  font-size: 12px;
  line-height: 1.6;
}

.delegation__perm {
  margin-right: 8px;
}

.delegation__dir {
  transform: scale(0.9);
}

.delegation__forever {
  color: var(--el-color-warning);
}
</style>
