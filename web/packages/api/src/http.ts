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
  (response) => {
    /*
     * ★ 把"本该是 JSON、实际拿到 HTML"的响应**变成异常**。
     *
     * 【为什么必须有这一层 —— 这是个静默到极点的失败】
     *   ABP 自带的身份接口在**未授权**时返回的不是 403，而是 **302 跳登录页**；
     *   浏览器 XHR 会静默跟随重定向，于是调用方最终拿到 **200 + 登录页 HTML**。
     *
     *   关键点：**axios 对这种情况不抛异常**。实测（`200` + `content-type: text/html`）：
     *       NO THROW | typeof data = string | value = "<!DOCTYPE html>..."
     *   它只是把 `transformResponse` 里的 JSON.parse 失败**吞掉**，把原始字符串当 data 返回。
     *
     *   后果是错误会在很远的地方才表现出来，而且完全看不出原因：
     *     `const r = await identityApi.getRolePage(...)` → `r.items` 是 undefined
     *     → 表格空、总数为 NaN/空。页面**没有任何 catch 被触发**，
     *     因为它连异常都没有 —— 我原以为是 `catch {}` 吞掉了错误，实际是根本没进 catch。
     *
     *   所以这里统一兜住：拿到 HTML 就抛一个可识别的错误，
     *   让页面能走到 catch 并给出"缺少框架权限"这类**可行动**的提示。
     *   判据刻意用 `text/html` 而不是"解析失败"：真的返回 JSON 时才解析，
     *   二进制下载（导出文件）走的是另一条路，不会被这里误伤。
     */
    const contentType = String(response.headers?.['content-type'] ?? '')

    if (contentType.includes('text/html')) {
      const error = new SyntaxError(
        `期望 JSON，服务端返回了 ${contentType || '非 JSON 内容'}（通常是未授权被重定向到登录页）。`
      )

      // 交给下面的错误分支统一处理，保持提示与其它错误在同一处产生
      return Promise.reject(error)
    }

    return response
  },
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
      // 非 axios 错误（例如上面主动抛出的"拿到 HTML"）也交给 describeError：
      // 它有 `!axios.isAxiosError` 分支，会退回 Error.message ——
      // 少了这个判断，用户看到的会是毫无信息量的"发生未知错误"。
      ElMessage.error(describeError(error))
    }

    return Promise.reject(error)
  }
)

/**
 * 把 axios 错误翻译成能给用户看的一句话。
 *
 * ABP 的错误信封长这样：
 *   { "error": { "code": "Xjgl:00101", "message": "...", "details": "...",
 *                "data": { "reason": "..." },
 *                "validationErrors": [{ "message": "...", "members": ["Name"] }] } }
 *
 * 取值优先级不是随便定的，每一层都有明确理由：
 *   1) validationErrors —— 数据校验失败，用户自己能改；
 *   2) details          —— 业务原因的详细说明；
 *   3) **data.reason**  —— 我们自己的业务异常用 `.WithData("reason", ...)` 带出的
 *      动态说明（例如"该组织下还有下级组织，请先删除下级"）。这一层不能省：
 *      ABP 在错误码没有本地化文案时会把 message 退化成
 *      "服务器内部错误"，而真正的原因只在 data.reason 里 ——
 *      只读 message 就会给用户看一句谎话（实测踩过）。
 *   4) message          —— 最后才用兜底文案。
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
          data?: Record<string, unknown>
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

  // 业务异常自带的动态说明（reason / message 两个约定键）
  const payload = envelope?.data

  if (payload) {
    for (const key of ['reason', 'message']) {
      const value = payload[key]

      if (typeof value === 'string' && value.trim()) {
        return value
      }
    }
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

/**
 * 判断这个错误是不是"服务端返回了非 JSON，解析失败"。
 *
 * 【为什么需要它 —— 这是一个真实卡过人的失败形态】
 *   ABP 自带的身份接口（`/api/identity/roles`、`/api/identity/users`）在**未授权**时
 *   返回的是 **302 重定向到登录页**，而不是 403。浏览器 XHR 会静默跟随重定向，
 *   于是 axios 拿到的是**登录页的 HTML**，解 JSON 时抛 `SyntaxError`。
 *
 *   这个形态有两个坏后果：
 *     1) axios 的响应拦截器匹配不到它（没有 `error.response`），
 *        所以既不会提示、也不会走 403 文案 —— 页面如果写了 `catch { list = [] }`，
 *        用户看到的就是**一个空列表和一句提示都没有**，结论自然是"功能不存在"；
 *     2) 就算提示了，`"Unexpected token '<'"` 对使用者等于没说，
 *        **看不出真实原因是权限**。
 *
 *   所以页面应当用这个函数把错误归类，然后给出"缺哪个权限"的明确说明。
 */
export function isJsonParseError(error: unknown): boolean {
  if (!(error instanceof Error)) {
    return false
  }

  return (
    error.name === 'SyntaxError' ||
    /unexpected token|not valid json|json/i.test(error.message)
  )
}
