<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'

import { MetadataPermissions } from '@xjgl/api'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { useAuthStore } from '@/stores/auth'

/**
 * 我的：身份信息、业务入口与退出。
 *
 * 移动端把"当前是谁、属于哪个单位、有哪些角色"直接摊开显示 ——
 * 教师经常被问"你用的是哪个学校的账号"，能一眼看到比让他去翻设置有用。
 *
 * 【"业务入口"为什么放在这里，而不是并进「待办」页】
 *   「待办」的语义是**"轮到我审的任务"**（页签上那个角标就是在回答
 *   "有没有轮到我审"）。"发起异动申请"是**反方向**的业务：我是申请人，
 *   接下来轮到别人审。若把它混进待办列表，教师看到一条记录的第一件事
 *   就变成判断"这条是让我审的、还是我提的"，而这个判断一旦出错，
 *   后果是漏审或重复审 —— 语义混用的代价。而「我的」是**每个人都有的
 *   功能入口页**，把"我能发起的业务"放在这里，不依赖任何审核角色。
 */
const auth = useAuthStore()
const router = useRouter()

const profile = computed(() => auth.profile)

/**
 * 发起异动申请需要 `Chg.Apply.Create`。
 *
 * 无权限时**整个「业务入口」区都不显示**，而不是显示一个点进去报 403 的按钮：
 * 一个空的"业务入口"标题比没有这一块更让人困惑（点不动的东西没法解释）。
 * 这也与 PC 端 `views/change/apply/index.vue` 里 `v-if="canCreate"` 同一口径。
 */
const canApplyChange = computed(() => auth.can(MetadataPermissions.Chg.Apply.Create))

function openChangeApply(): void {
  void router.push({ name: 'change-apply' })
}

/**
 * 单位层级的中文名走码表（ORG_TYPE）。
 *
 * 这里原来直接渲染 `profile.orgType`，于是界面上显示的是英文
 * `School` / `County` —— 教师看不懂，而这恰恰是"我属于哪一级"这个问题的答案。
 */
const orgTypeCodes = useCodeOptions(CodeCategories.OrgType)

void orgTypeCodes.load()

async function logout(): Promise<void> {
  try {
    await ElMessageBox.confirm('确定要退出登录吗？', '提示', {
      confirmButtonText: '退出',
      cancelButtonText: '取消'
    })
  } catch {
    return
  }

  auth.logout()
  ElMessage.success('已退出登录')
  await router.replace('/login')
}
</script>

<template>
  <div class="h5-page">
    <h2 class="h5-title">我的</h2>

    <el-card shadow="never" class="mine__card">
      <div class="mine__head">
        <div class="mine__avatar">{{ (profile?.realName || profile?.userName || '?').slice(0, 1) }}</div>
        <div>
          <div class="mine__name">{{ profile?.realName || profile?.userName }}</div>
          <div class="h5-muted">账号：{{ profile?.userName }}</div>
        </div>
      </div>

      <el-descriptions :column="1" size="small" border class="mine__info">
        <el-descriptions-item label="所属单位">
          {{ profile?.orgName || '未关联单位' }}
        </el-descriptions-item>
        <el-descriptions-item label="单位层级">
          {{ orgTypeCodes.nameOf(profile?.orgType) ?? profile?.orgType ?? '—' }}
        </el-descriptions-item>
        <el-descriptions-item label="角色">
          {{ profile?.roles?.join('、') || '未分配' }}
        </el-descriptions-item>
        <el-descriptions-item label="手机号">
          {{ profile?.phoneNumber || '—' }}
        </el-descriptions-item>
        <el-descriptions-item label="已授予权限">
          {{ profile?.permissions?.length ?? 0 }} 项
        </el-descriptions-item>
      </el-descriptions>
    </el-card>

    <!-- ---------------- 业务入口 ---------------- -->
    <el-card v-if="canApplyChange" shadow="never" class="mine__card">
      <div class="mine__section">业务入口</div>

      <!-- 整张卡片都是触摸目标（≥44px 高），右侧 › 提示可进入，不靠 hover -->
      <div class="mine__entry" @click="openChangeApply">
        <div class="mine__entry-main">
          <div class="mine__entry-title">发起异动申请</div>
          <div class="h5-muted">转学 / 休学 / 复学等学籍异动，提交后按流程自动流转审核</div>
        </div>
        <span class="mine__entry-go">›</span>
      </div>
    </el-card>

    <el-card shadow="never" class="mine__card">
      <el-button type="danger" plain class="mine__logout" @click="logout">退出登录</el-button>
    </el-card>

    <p class="h5-muted mine__tip">
      提示：如需修改密码或调整权限，请联系本单位管理员；<br />
      账号激活与权限调整在电脑端完成。
    </p>
  </div>
</template>

<style scoped>
.mine__card {
  margin-bottom: 12px;
}

.mine__head {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 14px;
}

.mine__avatar {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: var(--el-color-primary);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 20px;
  font-weight: 600;
}

.mine__name {
  font-size: 16px;
  font-weight: 600;
}

.mine__info {
  margin-top: 4px;
}

.mine__logout {
  width: 100%;
}

.mine__section {
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 10px;
}

.mine__entry {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 44px;
  padding: 10px 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  cursor: pointer;
}

/* 长文案（学校名、说明）必须允许收缩与断行，否则 320px 宽下会横向溢出 */
.mine__entry-main {
  min-width: 0;
  overflow-wrap: anywhere;
}

.mine__entry-title {
  font-size: 16px;
  font-weight: 600;
  margin-bottom: 2px;
}

.mine__entry-go {
  color: var(--el-text-color-secondary);
  font-size: 20px;
  line-height: 1;
}

.mine__tip {
  margin-top: 16px;
  line-height: 1.7;
  text-align: center;
}
</style>
