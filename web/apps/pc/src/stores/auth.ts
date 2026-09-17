import { computed, ref } from 'vue'
import { defineStore } from 'pinia'

import { authApi, profileApi } from '@/api'
import type { MyProfile } from '@/api/types'

/**
 * 登录状态与权限上下文。
 *
 * 设计要点：**权限判断的唯一依据是后端返回的权限码集合**，
 * 前端不做任何角色推断（不写 `if (role === 'admin')` 这种代码）。
 * 原因是本系统的权限是元数据驱动的，角色与权限的对应关系完全在后端，
 * 前端一旦自己推断角色，就会在管理员调整授权后与后端不一致 ——
 * 表现为「按钮看得见，点了报 403」这种最难排查的问题。
 *
 * 注意：前端隐藏按钮只是体验优化，**真正的拦截永远在后端**。
 */
export const useAuthStore = defineStore('auth', () => {
  const profile = ref<MyProfile | null>(null)
  const loading = ref(false)

  const isAuthenticated = computed(() => !!profile.value)

  const permissionSet = computed(() => new Set(profile.value?.permissions ?? []))

  /** 是否拥有某个权限码，如 `Stu.Student.Create`。 */
  function can(permission: string): boolean {
    return permissionSet.value.has(permission)
  }

  /** 拥有其中任意一个即可。 */
  function canAny(permissions: string[]): boolean {
    return permissions.some((p) => permissionSet.value.has(p))
  }

  /** 登录 + 拉取上下文（两步都要成功才算登录成功）。 */
  async function login(userName: string, password: string): Promise<void> {
    await authApi.login(userName, password)
    await loadProfile()
  }

  async function loadProfile(): Promise<void> {
    loading.value = true

    try {
      profile.value = await profileApi.get()
    } finally {
      loading.value = false
    }
  }

  function logout(): void {
    authApi.logout()
    profile.value = null
  }

  return {
    profile,
    loading,
    isAuthenticated,
    can,
    canAny,
    login,
    loadProfile,
    logout
  }
})
