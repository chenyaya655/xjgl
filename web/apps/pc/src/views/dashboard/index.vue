<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import axios from 'axios'

import { photoApi } from '@/api'
import type { PhotoStorageHealth } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import ExportTaskCenterDialog from '@/components/ExportTaskCenterDialog.vue'
import PermissionDelegationDialog from '@/components/PermissionDelegationDialog.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 工作台。
 *
 * 这里刻意做得「信息密度高、操作少」：县级学籍管理员每天进来第一件事是
 * 看有没有待办、有没有缺照片的学校，而不是看一堆图表。
 */
const auth = useAuthStore()
const router = useRouter()

const systemInfo = ref<Record<string, string> | null>(null)
const health = ref<PhotoStorageHealth | null>(null)

const canUploadPhoto = auth.can(MetadataPermissions.Pho.Photo.Upload)

/**
 * 当前身份**实际进得去的菜单路径**。
 *
 * 【为什么快捷入口必须按它显隐 —— 这是一类"看起来没坏"的缺陷】
 * 之前四个快捷入口是写死的：模块被管理员停用、或者我这个角色没被授权时，
 * 动态路由根本没注册（见 `router/index.ts` 的 `registerDynamicRoutes`），
 * 于是点下去落到**静态兜底路由的 404 页**。
 * 按钮本身看上去完全正常（有图标、能点、有 hover），左侧菜单里也确实没有它 ——
 * 但用户不会把这两件事联系起来，只会觉得"系统坏了"。
 *
 * 菜单树是后端按"模块是否启用 + 我有没有权限（含操作级）"过滤后的结果，
 * 与注册动态路由用的是**同一份数据**，所以按它判断才和左侧菜单一致。
 */
const menuPaths = computed(() => {
  const paths = new Set<string>()

  for (const group of auth.profile?.menus ?? []) {
    for (const item of group.children) {
      if (item.path) {
        paths.add(item.path)
      }
    }
  }

  return paths
})

/** 该路径在当前身份的菜单里存在（= 路由已注册且可见）才给入口。 */
function canGo(path: string): boolean {
  return menuPaths.value.has(path)
}

/**
 * 「我的导出任务」的入口条件。
 *
 * 导出任务本身没有独立的权限码（有权限发起就有权限回看自己的任务），
 * 因此按"这两个页面里至少有一个进得去"来判断 —— 与上面的快捷入口同一套依据。
 */
const canOpenExportTasks = computed(() => canGo('/student/list') || canGo('/photo/list'))

/** 一个快捷入口都没有时，别留一张空卡片。 */
const hasAnyShortcut = computed(
  () =>
    (canGo('/photo/list') && canUploadPhoto) ||
    canGo('/student/list') ||
    canGo('/change/apply') ||
    canGo('/change/audit') ||
    canOpenExportTasks.value
)

const exportTaskVisible = ref(false)

/**
 * 「我的权限委派」的入口**恒定显示，不参与上面的权限/菜单判断**。
 *
 * 【为什么不给它一个权限码，也不挂在"系统管理"菜单下】
 * 委派是"把自己的权限临时借给同事"（典型场景：审核人休假）。
 * 后端刻意不为它定义 `Sys.Delegation.*`：能借什么由**子集约束**决定
 * （只能借出自己确实拥有的），撤销由**归属约束**决定（只有借出人本人能撤）。
 * 于是**任何登录用户**都是它的使用者 —— 一个持有「异动审核」的普通教师
 * 可能一个管理菜单都没有（`sys_module` 里根本没给他授权模块，
 * `menuPaths` 因此是空的），而他要休假时正是最需要这个功能的人。
 * 若把入口塞进 `Sys.User.Query` / `Sys.Permission.*` 这类管理员权限背后，
 * 最需要它的人恰恰永远看不到它。
 *
 * 工作台则是"每个人都到得了的那一页"：它不依赖任何模块授权，
 * 左侧菜单一个都没有时也照样能进（路由是静态注册的）。
 *
 * 注意它**不并入 hasAnyShortcut**：那句"没有开通任何业务模块"说的是业务模块，
 * 与委派入口是否显示无关 —— 两者同时出现恰恰是最该出现的组合。
 */
const delegationVisible = ref(false)

onMounted(async () => {
  try {
    // 系统信息接口是匿名可访问的探活接口，用它顺便确认前后端连通
    const { data } = await axios.get<Record<string, string>>('/api/system/info')
    systemInfo.value = data
  } catch {
    systemInfo.value = null
  }

  try {
    health.value = await photoApi.getStorageHealth()
  } catch {
    health.value = null
  }
})

/** 把「我有哪些模块」直接铺成入口卡片，省掉在左侧菜单里找的动作。 */
function go(path?: string | null): void {
  if (path) {
    void router.push(path)
  }
}
</script>

<template>
  <div class="dashboard">
    <el-card shadow="never" class="dashboard__welcome">
      <div class="dashboard__welcome-main">
        <h2>
          {{ auth.profile?.realName || auth.profile?.userName }}，欢迎使用学籍管理系统
        </h2>
        <p>
          <el-tag v-if="auth.profile?.orgType" size="small" effect="plain">
            {{ auth.profile.orgName }}
          </el-tag>
          <el-tag v-for="role in auth.profile?.roles ?? []" :key="role" size="small" type="info" effect="plain">
            {{ role }}
          </el-tag>
        </p>
      </div>

      <el-descriptions v-if="systemInfo" :column="1" size="small" border class="dashboard__system">
        <el-descriptions-item label="应用">
          {{ systemInfo.application }}（{{ systemInfo.version }}）
        </el-descriptions-item>
        <el-descriptions-item label="运行环境">
          {{ systemInfo.framework }} · {{ systemInfo.environment }}
        </el-descriptions-item>
        <el-descriptions-item label="服务器时间">
          {{ systemInfo.serverTime }}
        </el-descriptions-item>
      </el-descriptions>
    </el-card>

    <el-row :gutter="16" class="dashboard__row">
      <el-col :span="12">
        <el-card shadow="never">
          <template #header>
            <div class="dashboard__card-header">
              <span>照片存储状态</span>
              <el-button v-if="auth.can(MetadataPermissions.Pho.Photo.Query)" text @click="go('/photo/list')">
                查看台账
              </el-button>
            </div>
          </template>

          <el-empty v-if="!health" description="暂时获取不到存储状态（后端未启动或 Redis/存储不可达）" :image-size="60" />

          <template v-else>
            <el-descriptions :column="1" size="small" border>
              <el-descriptions-item label="主存储（NAS）">
                <el-tag :type="health.primaryAvailable ? 'success' : 'danger'" size="small">
                  {{ health.primaryAvailable ? '可写' : '不可写' }}
                </el-tag>
                <span class="dashboard__muted">{{ health.primaryName }}</span>
              </el-descriptions-item>

              <el-descriptions-item label="熔断状态">
                {{ health.circuitStateText }}
              </el-descriptions-item>

              <el-descriptions-item label="降级待补传">
                <el-tag :type="health.fallbackPendingCount > 0 ? 'warning' : 'info'" size="small">
                  {{ health.fallbackPendingCount }} 张
                </el-tag>
              </el-descriptions-item>

              <el-descriptions-item label="处理失败">
                <el-tag :type="health.failedCount > 0 ? 'danger' : 'info'" size="small">
                  {{ health.failedCount }} 张
                </el-tag>
              </el-descriptions-item>
            </el-descriptions>

            <p class="dashboard__muted dashboard__checked">检测时间：{{ health.checkedTime }}</p>
          </template>
        </el-card>
      </el-col>

      <el-col :span="12">
        <el-card shadow="never">
          <template #header>快捷入口</template>

          <div class="dashboard__shortcuts">
            <el-button
              v-if="canGo('/photo/list') && canUploadPhoto"
              type="primary"
              plain
              @click="go('/photo/list')"
            >
              <AppIcon name="UploadFilled" />
              <span>照片上传</span>
            </el-button>

            <el-button v-if="canGo('/student/list')" plain @click="go('/student/list')">
              <AppIcon name="User" />
              <span>学生管理</span>
            </el-button>

            <el-button v-if="canGo('/change/apply')" plain @click="go('/change/apply')">
              <AppIcon name="EditPen" />
              <span>异动申请</span>
            </el-button>

            <el-button v-if="canGo('/change/audit')" plain @click="go('/change/audit')">
              <AppIcon name="CircleCheck" />
              <span>异动审核</span>
            </el-button>

            <!--
              导出/打印已经改成任务式：提交之后可以离开页面，回头再来这里下载或清理。
              少了这个入口，用户就只能在自己那个页面干等 —— 超时或关掉页面之后，
              任务其实还在后台跑，而界面上再也找不到它。
            -->
            <el-button v-if="canOpenExportTasks" plain @click="exportTaskVisible = true">
              <AppIcon name="Tickets" />
              <span>我的导出任务</span>
            </el-button>

            <!--
              权限委派：把自己的权限临时借给同事（审核人休假这类场景）。
              它**刻意不加 v-if** —— 任何登录用户都能管理"自己"的委派，
              没有管理员权限的老师才是最需要它的人（详见 delegationVisible 的说明）。
            -->
            <el-button plain @click="delegationVisible = true">
              <AppIcon name="Share" />
              <span>我的权限委派</span>
            </el-button>

            <p v-if="!hasAnyShortcut" class="dashboard__muted">
              你当前的角色没有开通任何业务模块，请联系管理员分配权限。
            </p>
          </div>

          <el-divider />

          <p class="dashboard__muted">
            左侧菜单由后端「模块管理」的元数据生成 —— 管理员停用某个模块后，
            这里的菜单和路由会一起消失，不需要改前端代码。
          </p>
        </el-card>
      </el-col>
    </el-row>

    <!-- 我的导出任务（学生名单 / 学籍卡 / 花名册 / 照片 zip 的提交记录与产物） -->
    <ExportTaskCenterDialog v-model="exportTaskVisible" />

    <!-- 我的权限委派（我借出去的 + 别人借给我的；任何登录用户都可用） -->
    <PermissionDelegationDialog v-model="delegationVisible" />
  </div>
</template>

<style scoped>
.dashboard__welcome {
  margin-bottom: 16px;
}

.dashboard__welcome-main h2 {
  margin: 0 0 12px;
  font-size: 18px;
}

.dashboard__welcome-main p {
  display: flex;
  gap: 8px;
  margin: 0;
}

.dashboard__system {
  margin-top: 16px;
}

.dashboard__card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.dashboard__muted {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.dashboard__checked {
  margin: 12px 0 0;
  display: block;
}

.dashboard__shortcuts {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.dashboard__shortcuts .el-button {
  margin-left: 0;
}
</style>
