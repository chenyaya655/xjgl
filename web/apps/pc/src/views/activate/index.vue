<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

import { provisionApi } from '@/api'
import type { ActivateUserResult } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'

/**
 * 账号激活页（**公开页面，不需要登录**）。
 *
 * 这是「用户预置 + 本人激活」流程的教师侧入口：
 * 管理员预置姓名/手机号/所属学校/角色并生成激活码，教师凭
 * 「手机号 + 姓名 + 激活码」自己设置密码 —— 管理员全程不接触任何人的密码。
 *
 * 为什么不放在登录后的页面里：教师还没有账号，登录不了。
 * 所以这条路由是 `meta.anonymous = true`（见 router/index.ts），
 * 并且这个页面只调用一个匿名接口（`POST /api/app/user-provision/activate`）。
 */
const router = useRouter()

const form = ref({
  phoneNumber: '',
  realName: '',
  idCardNumber: '',
  activationCode: '',
  password: '',
  confirmPassword: ''
})

const loading = ref(false)
const errorMessage = ref('')
const result = ref<ActivateUserResult | null>(null)

async function handleSubmit(): Promise<void> {
  errorMessage.value = ''

  if (!form.value.phoneNumber.trim() || !form.value.realName.trim()) {
    errorMessage.value = '请填写手机号与姓名（要与管理员预置时的一致）。'

    return
  }

  if (!form.value.activationCode.trim()) {
    errorMessage.value = '请填写激活码。'

    return
  }

  if (form.value.password.length < 6) {
    errorMessage.value = '密码至少 6 位，且需要同时包含大写字母、小写字母、数字与特殊字符。'

    return
  }

  if (form.value.password !== form.value.confirmPassword) {
    errorMessage.value = '两次输入的密码不一致。'

    return
  }

  loading.value = true

  try {
    result.value = await provisionApi.activate({
      phoneNumber: form.value.phoneNumber.trim(),
      realName: form.value.realName.trim(),
      idCardNumber: form.value.idCardNumber?.trim() || null,
      activationCode: form.value.activationCode.trim(),
      password: form.value.password
    })
  } catch (error) {
    // 激活是匿名接口，用裸 axios 调用，因此这里自己取 ABP 的错误信封
    const envelope = (error as { response?: { data?: { error?: { message?: string; data?: Record<string, unknown> } } } })
      ?.response?.data?.error

    const reason = envelope?.data?.reason

    errorMessage.value =
      (typeof reason === 'string' && reason) || envelope?.message || '激活失败，请检查信息是否与预置时一致。'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="activate">
    <el-card class="activate__card" shadow="always">
      <h2 class="activate__title">账号激活</h2>

      <!-- ---------------- 激活成功 ---------------- -->
      <template v-if="result">
        <el-result icon="success" title="激活成功">
          <template #sub-title>
            <p>您的登录账号是：<b class="activate__account">{{ result.userName }}</b></p>
            <p v-if="result.roles?.length">已分配角色：{{ result.roles.join('、') }}</p>
            <p class="activate__hint">请用该账号与刚刚设置的密码登录。</p>
          </template>
          <template #extra>
            <el-button type="primary" @click="router.replace('/login')">去登录</el-button>
          </template>
        </el-result>
      </template>

      <!-- ---------------- 激活表单 ---------------- -->
      <template v-else>
        <p class="activate__intro">
          请填写管理员预置时登记的姓名与手机号，并输入收到的激活码，然后设置自己的登录密码。
        </p>

        <el-form label-position="top" @submit.prevent="handleSubmit">
          <el-form-item label="手机号">
            <el-input v-model="form.phoneNumber" size="large" placeholder="预置时登记的手机号">
              <template #prefix>
                <AppIcon name="Iphone" />
              </template>
            </el-input>
          </el-form-item>

          <el-form-item label="姓名">
            <el-input v-model="form.realName" size="large" placeholder="与预置时一致的真实姓名">
              <template #prefix>
                <AppIcon name="User" />
              </template>
            </el-input>
          </el-form-item>

          <el-form-item label="身份证号（若预置时填写过）">
            <el-input v-model="form.idCardNumber" size="large" placeholder="选填" />
          </el-form-item>

          <el-form-item label="激活码">
            <el-input v-model="form.activationCode" size="large" placeholder="管理员发给您的激活码">
              <template #prefix>
                <AppIcon name="Key" />
              </template>
            </el-input>
          </el-form-item>

          <el-form-item label="设置密码">
            <el-input
              v-model="form.password"
              type="password"
              size="large"
              show-password
              placeholder="至少 6 位，含大小写字母、数字与特殊字符"
            />
          </el-form-item>

          <el-form-item label="确认密码">
            <el-input
              v-model="form.confirmPassword"
              type="password"
              size="large"
              show-password
              @keyup.enter="handleSubmit"
            />
          </el-form-item>

          <el-alert
            v-if="errorMessage"
            :title="errorMessage"
            type="error"
            show-icon
            :closable="false"
            class="activate__error"
          />

          <el-button
            type="primary"
            size="large"
            class="activate__submit"
            :loading="loading"
            @click="handleSubmit"
          >
            激活账号
          </el-button>
        </el-form>

        <div class="activate__footer">
          <el-link type="primary" @click="router.replace('/login')">已有账号？返回登录</el-link>
        </div>
      </template>
    </el-card>
  </div>
</template>

<style scoped>
.activate {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  background: linear-gradient(135deg, #1f2d3d 0%, #2c5f8d 100%);
  padding: 24px;
}

.activate__card {
  width: 420px;
}

.activate__title {
  margin: 0 0 16px;
  font-size: 20px;
  text-align: center;
}

.activate__intro {
  margin: 0 0 16px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.activate__error {
  margin-bottom: 12px;
}

.activate__submit {
  width: 100%;
}

.activate__footer {
  margin-top: 16px;
  text-align: center;
}

.activate__account {
  font-family: Consolas, Monaco, monospace;
  font-size: 16px;
}

.activate__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
