import axios, { AxiosError, AxiosHeaders, type AxiosInstance } from 'axios'
import { ElMessage } from 'element-plus'

import type { TokenResponse } from './types'

/**
 * 给 axios 的请求配置补两个自定义字段。
 *
 * 用声明合并而不是在每个调用点 `as never`：类型安全的自定义配置项
 * 应该由库的类型定义承载，散落的强制转换迟早会掩盖真正的类型错误。
 */
declare module 'axios' {
  export interface AxiosRequestConfig {
    /** 静默请求：失败时不弹提示（用于后台轮询、健康探针等）。 */
    silent?: boolean
    /** 内部标记：该请求已经因为 401 重放过一次，避免无限循环。 */
    _retried?: boolean
  }
}

/**
 * HTTP 客户端 —— 全站唯一的网络出口。
 *
 * 这里集中处理四件在每个页面重复出现的事：
 *   1) **带上令牌**：请求拦截器统一注入 Bearer；
 *   2) **令牌过期自动续期**：401 时用 refresh_token 换新令牌并**重放原请求**，
 *      并发请求只触发一次刷新（用一个共享 Promise 去重）——
 *      否则用户打开一个有 5 个并发请求的页面，会同时发起 5 次刷新，
 *      后 4 次因为 refresh_token 已被旋转而全部失败，表现成「随机掉线」；
 *   3) **ABP 错误信封解包**：ABP 的报错在 `error.message` / `error.details`
 *      / `error.validationErrors` 里，直接弹 `err.message` 只会得到
 *      "Request failed with status code 403"，对用户毫无意义；
 *   4) **错误提示只在这里弹**：页面不需要各自写一遍，避免同一个错误弹两次。
 */

const ACCESS_TOKEN_KEY = 'xjgl.access_token'
const REFRESH_TOKEN_KEY = 'xjgl.refresh_token'

/** OpenIddict 令牌端点。走相对路径，开发时由 Vite 代理转发。 */
export const TOKEN_ENDPOINT = '/connect/token'

/** 本系统自己的客户端（密码模式）。与后端 OpenIddict 种子数据里的 ClientId 一致。 */
export const CLIENT_ID = 'Xjgl_App'

/** 请求的 scope。offline_access 是拿到 refresh_token 的前提。 */
export const SCOPE = 'Xjgl offline_access'

export const tokenStorage = {
  get accessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY)
  },
  get refreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_KEY)
  },
  save(token: TokenResponse): void {
    localStorage.setItem(ACCESS_TOKEN_KEY, token.access_token)

    if (token.refresh_token) {
      localStorage.setItem(REFRESH_TOKEN_KEY, token.refresh_token)
    }
  },
  clear(): void {
    localStorage.removeItem(ACCESS_TOKEN_KEY)
    localStorage.removeItem(REFRESH_TOKEN_KEY)
  }
}

/** 令牌失效时的回调，由路由层注册（避免这里 import router 造成循环依赖）。 */
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler
}

export const http: AxiosInstance = axios.create({
  timeout: 30000
})

http.interceptors.request.use((config) => {
  const token = tokenStorage.accessToken

  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }

  return config
})

/** 进行中的刷新，用于把并发 401 合并成一次刷新。 */
let refreshing: Promise<string | null> | null = null

async function refreshAccessToken(): Promise<string | null> {
  const refreshToken = tokenStorage.refreshToken

  if (!refreshToken) {
    return null
  }

  try {
    const body = new URLSearchParams({
      grant_type: 'refresh_token',
      client_id: CLIENT_ID,
      refresh_token: refreshToken
    })

    const { data } = await axios.post<TokenResponse>(TOKEN_ENDPOINT, body, {
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' }
    })

    tokenStorage.save(data)

    return data.access_token
  } catch {
    // 刷新令牌也失效了（过期 / 被撤销 / 用户被停用）→ 只能重新登录
    tokenStorage.clear()

    return null
  }
}

interface RetriableConfig {
  _retried?: boolean
  silent?: boolean
}

http.interceptors.response.use(
  (response) => response,
  async (error: AxiosError) => {
    const config = (error.config ?? {}) as typeof error.config & RetriableConfig
    const status = error.response?.status

    // ---- 401：尝试续期并重放一次 ----
    if (status === 401 && !config._retried && !config.url?.includes(TOKEN_ENDPOINT)) {
      config._retried = true

      refreshing ??= refreshAccessToken().finally(() => {
        refreshing = null
      })

      const newToken = await refreshing

      if (newToken) {
        config.headers = new AxiosHeaders({
          ...config.headers,
          Authorization: `Bearer ${newToken}`
        })

        return http.request(config)
      }

      onUnauthorized?.()

      return Promise.reject(error)
    }

    if (!config.silent) {
      ElMessage.error(describeError(error))
    }

    return Promise.reject(error)
  }
)

/**
 * 把 axios 错误翻译成能给用户看的一句话。
 *
 * ABP 的错误信封长这样：
 *   { "error": { "code": "...", "message": "...", "details": "...",
 *                "validationErrors": [{ "message": "...", "members": ["Name"] }] } }
 * 其中 validationErrors 是数据校验失败（用户能自己改），
 * details 往往是业务原因的详细说明，两者优先级都高于 message。
 */
export function describeError(error: unknown): string {
  if (!axios.isAxiosError(error)) {
    return error instanceof Error ? error.message : '发生未知错误。'
  }

  const data = error.response?.data as
    | {
        error?: {
          message?: string
          details?: string
          validationErrors?: { message?: string; members?: string[] }[]
        }
      }
    | undefined

  const envelope = data?.error

  if (envelope?.validationErrors?.length) {
    return envelope.validationErrors
      .map((v) => v.message ?? '')
      .filter(Boolean)
      .join('；')
  }

  if (envelope?.details) {
    return envelope.details
  }

  if (envelope?.message) {
    return envelope.message
  }

  if (error.code === 'ECONNABORTED') {
    return '请求超时，请检查网络后重试。'
  }

  if (!error.response) {
    return '无法连接服务器，请确认后端服务已启动。'
  }

  switch (error.response.status) {
    case 403:
      return '您没有执行该操作的权限，请联系管理员。'
    case 404:
      return '请求的资源不存在。'
    case 429:
      return '操作过于频繁，请稍后再试。'
    case 500:
      return '服务器内部错误，请联系系统管理员。'
    default:
      return `请求失败（HTTP ${error.response.status}）。`
  }
}
