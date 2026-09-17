import { computed, ref } from 'vue'
import { defineStore } from 'pinia'

import { authApi, profileApi } from '@xjgl/api'
import type { MyProfile } from '@xjgl/api'

/**
 * 移动端登录状态。
 *
 * 与 PC 端的 store 是同一套思路（权限判断只依据后端返回的权限码集合，
 * 前端不做角色推断），差别只有一个：**H5 不需要菜单树** ——
 * 底部三个页签是固定的，不走元数据驱动的动态路由。
 *
 * 为什么不给 H5 也做动态路由：动态路由解决的是"PC 上模块会增删、菜单要跟着变"，
 * 而移动端只承载两三个高频场景，把它做成"什么都能进"反而会让教师在手机上
 * 面对一屏看不懂的菜单。**端不同，产品定位就该不同。**
 */
export const useAuthStore = defineStore('auth', () => {
  const profile = ref<MyProfile | null>(null)
  const loading = ref(false)

  const isAuthenticated = computed(() => !!profile.value)

  const permissionSet = computed(() => new Set(profile.value?.permissions ?? []))

  function can(permission: string): boolean {
    return permissionSet.value.has(permission)
  }

  function canAny(permissions: string[]): boolean {
    return permissions.some((p) => permissionSet.value.has(p))
  }

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

  return { profile, loading, isAuthenticated, can, canAny, login, loadProfile, logout }
})
