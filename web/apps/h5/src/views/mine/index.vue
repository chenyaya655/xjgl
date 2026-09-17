<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'

import { useAuthStore } from '@/stores/auth'

/**
 * 我的：身份信息与退出。
 *
 * 移动端把"当前是谁、属于哪个单位、有哪些角色"直接摊开显示 ——
 * 教师经常被问"你用的是哪个学校的账号"，能一眼看到比让他去翻设置有用。
 */
const auth = useAuthStore()
const router = useRouter()

const profile = computed(() => auth.profile)

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
          {{ profile?.orgType || '—' }}
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

.mine__tip {
  margin-top: 16px;
  line-height: 1.7;
  text-align: center;
}
</style>
