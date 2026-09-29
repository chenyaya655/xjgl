<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'

import AppIcon from '@/components/AppIcon.vue'
import { registerDynamicRoutes } from '@/router'
import { useAuthStore } from '@/stores/auth'

/**
 * 登录页。
 *
 * 走的是 OpenIddict 的**密码模式**：本系统是全省统一部署的自有系统，
 * 登录页就是自己的页面，不需要授权码那两次跳转与回调。
 *
 * 登录成功后必须做两件事，缺一不可：
 *   1) 拉取用户上下文（`/api/app/my-profile`）；
 *   2) **用菜单树注册动态路由** —— 否则跳转到业务页面会 404，
 *      因为那些路由本来就只存在于后端元数据里。
 */
const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const LAST_USER_KEY = 'xjgl.last_user'

/** 开发环境才显示默认账号提示，生产构建里这段提示不存在。 */
const isDev = import.meta.env.DEV

const form = ref({
  userName: localStorage.getItem(LAST_USER_KEY) ?? '',
  password: ''
})

const loading = ref(false)
const errorMessage = ref('')
const passwordInput = ref<{ focus: () => void } | null>(null)

onMounted(() => {
  if (form.value.userName) {
    passwordInput.value?.focus()
  }
})

async function handleSubmit(): Promise<void> {
  errorMessage.value = ''

  if (!form.value.userName.trim() || !form.value.password) {
    errorMessage.value = '请输入用户名和密码。'

    return
  }

  loading.value = true

  try {
    await auth.login(form.value.userName.trim(), form.value.password)

    if (!auth.profile) {
      throw new Error('登录成功但未能获取用户信息，请联系系统管理员。')
    }

    // 关键一步：把后端返回的菜单树注册成路由
    registerDynamicRoutes(auth.profile.menus)

    localStorage.setItem(LAST_USER_KEY, form.value.userName.trim())

    ElMessage.success(`欢迎回来，${auth.profile.realName || auth.profile.userName}`)

    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : ''

    if (redirect) {
      /**
       * SSO 回跳：**必须用浏览器整页导航，不能交给 vue-router**。
       *
       * 【为什么】`redirect` 里装的往往是一个带查询串的**服务端地址**
       * （典型是 OpenIddict 的授权端点：`/connect/authorize?client_id=…&redirect_uri=…`）。
       * 如果交给 `router.replace(redirect)`，vue-router 会把这个字符串当**路由路径**解析：
       *   - 问号后面的部分被当成它自己的 query 重新编码；
       *   - 结果是 `%20` 变成 `%2520`、`%3A` 变成 `%253A`（双重编码）。
       * 服务端拿到双重编码的参数就认不出来 —— 实测表现为**授权端点 400**，
       * 而浏览器地址栏看起来"参数都在"，非常难查。
       *
       * 整页导航不会有这个问题：地址原样交给服务端，也正是 OAuth 授权码流程
       * 要求的形态（授权端点由服务端渲染，本来就不该经过 SPA 路由）。
       */
      window.location.replace(redirect)

      return
    }

    await router.replace('/dashboard')
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '登录失败，请稍后重试。'
    form.value.password = ''
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login">
    <div class="login__panel">
      <div class="login__intro">
        <h1>河南省学籍管理系统</h1>
        <p class="login__intro-sub">Student Status Management System</p>
        <ul>
          <li>省 / 市 / 县 / 校 四级组织，数据按权限分级可见</li>
          <li>转学、休学等异动全程线上审批，流程可按业务调整</li>
          <li>照片批量上传，存储异常自动降级、恢复后自动补传</li>
        </ul>
      </div>

      <el-card class="login__card" shadow="always">
        <h2 class="login__title">用户登录</h2>

        <el-form label-position="top" @submit.prevent="handleSubmit">
          <el-form-item label="用户名">
            <el-input
              v-model="form.userName"
              size="large"
              placeholder="请输入用户名"
              autocomplete="username"
            >
              <template #prefix>
                <AppIcon name="User" />
              </template>
            </el-input>
          </el-form-item>

          <el-form-item label="密码">
            <el-input
              ref="passwordInput"
              v-model="form.password"
              type="password"
              size="large"
              placeholder="请输入密码"
              autocomplete="current-password"
              show-password
              @keyup.enter="handleSubmit"
            >
              <template #prefix>
                <AppIcon name="Lock" />
              </template>
            </el-input>
          </el-form-item>

          <el-alert
            v-if="errorMessage"
            :title="errorMessage"
            type="error"
            show-icon
            :closable="false"
            class="login__error"
          />

          <el-button
            type="primary"
            size="large"
            class="login__submit"
            :loading="loading"
            @click="handleSubmit"
          >
            登 录
          </el-button>
        </el-form>

        <!--
          账号激活是**公开页面**（`/activate`，meta.anonymous），但此前没有任何入口 ——
          只能靠手输 URL 才能到达，等于"功能做了但没人找得到"。
          教师的工作流是"管理员预置 → 本人激活 → 才能登录"，所以入口必须放在登录页。
        -->
        <div class="login__actions">
          <el-link type="primary" :underline="false" @click="router.push('/activate')">
            首次使用？点此激活账号
          </el-link>
        </div>

        <p v-if="isDev" class="login__hint">开发环境默认管理员：admin / 1q2w3E*</p>
      </el-card>
    </div>
  </div>
</template>

<style scoped>
.login {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100vh;
  background: linear-gradient(135deg, #1f2d3d 0%, #2c5f8d 100%);
}

.login__panel {
  display: flex;
  align-items: center;
  gap: 60px;
  max-width: 1000px;
  padding: 24px;
}

.login__intro {
  color: #eaf2fb;
  max-width: 420px;
}

.login__intro h1 {
  margin: 0 0 8px;
  font-size: 30px;
  letter-spacing: 2px;
}

.login__intro-sub {
  margin: 0 0 24px;
  opacity: 0.6;
  letter-spacing: 1px;
}

.login__intro ul {
  padding-left: 18px;
  line-height: 2;
  opacity: 0.85;
}

.login__card {
  width: 360px;
}

.login__title {
  margin: 0 0 20px;
  font-size: 20px;
  text-align: center;
}

.login__error {
  margin-bottom: 12px;
}

.login__submit {
  width: 100%;
}

.login__hint {
  margin: 16px 0 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  text-align: center;
}

/* 激活入口：与"开发环境提示"拉开距离，避免两条小字挤在一起看不出哪条能点 */
.login__actions {
  margin-top: 14px;
  text-align: center;
}

@media (max-width: 900px) {
  .login__intro {
    display: none;
  }
}
</style>
