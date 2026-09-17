<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import axios from 'axios'

import { photoApi } from '@/api'
import type { PhotoStorageHealth } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
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
            <el-button v-if="canUploadPhoto" type="primary" plain @click="go('/photo/list')">
              <AppIcon name="UploadFilled" />
              <span>照片上传</span>
            </el-button>

            <el-button plain @click="go('/student/list')">
              <AppIcon name="User" />
              <span>学生管理</span>
            </el-button>

            <el-button plain @click="go('/change/apply')">
              <AppIcon name="EditPen" />
              <span>异动申请</span>
            </el-button>

            <el-button plain @click="go('/change/audit')">
              <AppIcon name="CircleCheck" />
              <span>异动审核</span>
            </el-button>
          </div>

          <el-divider />

          <p class="dashboard__muted">
            左侧菜单由后端「模块管理」的元数据生成 —— 管理员停用某个模块后，
            这里的菜单和路由会一起消失，不需要改前端代码。
          </p>
        </el-card>
      </el-col>
    </el-row>
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
