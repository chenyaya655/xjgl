<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'

import { useAuthStore } from '@/stores/auth'

/**
 * 移动端登录。
 *
 * 与 PC 端共用同一个 OpenIddict 密码模式接口（后端不需要为移动端做任何区分）。
 * 登录后只做一件事：拉取用户上下文（权限码 + 组织），因为 H5 的页签是固定的，
 * 不需要菜单树。
 */
const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const form = ref({
  userName: localStorage.getItem('xjgl.last_user') ?? '',
  password: ''
})

const loading = ref(false)
const errorMessage = ref('')

onMounted(() => {
  if (!form.value.userName) {
    return
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

    localStorage.setItem('xjgl.last_user', form.value.userName.trim())

    ElMessage.success(`欢迎回来，${auth.profile?.realName || auth.profile?.userName}`)

    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/todo'

    await router.replace(redirect)
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
    <div class="login__brand">
      <h1>河南省学籍管理系统</h1>
      <p>移动端 · 教师办事</p>
    </div>

    <el-card class="login__card" shadow="always">
      <el-form label-position="top" @submit.prevent="handleSubmit">
        <el-form-item label="用户名">
          <el-input v-model="form.userName" size="large" placeholder="用户名 / 手机号" clearable />
        </el-form-item>

        <el-form-item label="密码">
          <el-input
            v-model="form.password"
            type="password"
            size="large"
            show-password
            placeholder="请输入密码"
            @keyup.enter="handleSubmit"
          />
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
        【这里原来是一个死链】
        原来写的是 `<el-link @click="router.push('/login')">还没有账号？</el-link>` ——
        而它本身就在登录页上，点下去等于"跳到自己"，什么都不会发生。
        移动端**没有**激活页（激活要核对手机号与身份证，属于电脑端的操作），
        所以这里不给链接，只把"该去哪里办"讲清楚。
      -->
      <div class="login__footer">
        <span class="login__hint">
          还没有账号？账号由学校管理员在「预置与激活」里预置并发放激活码，
          请在<strong>电脑端</strong>打开「账号激活」完成激活后再登录。
        </span>
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.login {
  min-height: 100vh;
  background: linear-gradient(160deg, #1f2d3d 0%, #2c5f8d 100%);
  padding: 60px 20px 20px;
}

.login__brand {
  text-align: center;
  color: #eaf2fb;
  margin-bottom: 28px;
}

.login__brand h1 {
  margin: 0 0 6px;
  font-size: 20px;
  letter-spacing: 1px;
}

.login__brand p {
  margin: 0;
  font-size: 13px;
  opacity: 0.7;
}

.login__card {
  border-radius: 12px;
}

.login__error {
  margin-bottom: 12px;
}

.login__submit {
  width: 100%;
}

.login__footer {
  margin-top: 18px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
}

.login__hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  text-align: center;
  line-height: 1.5;
}
</style>
