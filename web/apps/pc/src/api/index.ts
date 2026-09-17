import axios from 'axios'

import { CLIENT_ID, http, SCOPE, TOKEN_ENDPOINT, tokenStorage } from './http'
import type {
  MissingPhotoQuery,
  MissingPhotoStudent,
  MyProfile,
  OrgDto,
  PagedResult,
  PhotoCompensationResult,
  PhotoDto,
  PhotoQuery,
  PhotoStorageHealth,
  PhotoUploadResult,
  StudentListItem,
  StudentQuery,
  TokenResponse
} from './types'

/**
 * 后端接口地址集中定义。
 *
 * 这些路径是 ABP **约定式控制器**生成的（应用服务名去掉 AppService 转 kebab-case，
 * 方法名同理），集中放在这里的原因很直接：万一某个方法名改了导致路径变化，
 * 只需要改这一处，而不用在整个前端里翻字符串。
 */
export const ENDPOINTS = {
  profile: '/api/app/my-profile',

  org: '/api/app/org',
  orgTree: '/api/app/org/tree',

  student: '/api/app/student',

  photo: '/api/app/photo',
  photoMissingList: '/api/app/photo/missing-photo-list',
  photoStorageHealth: '/api/app/photo/storage-health',
  photoCompensate: '/api/app/photo/compensate',
  photoDeleteMany: '/api/app/photo/delete-many',
  photoUpload: '/api/photos/upload'
} as const

// ---------------------------------------------------------------------------
// 认证
// ---------------------------------------------------------------------------

export const authApi = {
  /**
   * 密码模式登录。
   *
   * 为什么用密码模式而不是授权码 + 跳转登录页：
   *   本系统是**全省统一部署的自有系统**，登录页就是自己的页面，
   *   没有「第三方应用」的概念，授权码流程只会多两次跳转和一次回调处理。
   *   授权码流程留给 P5 的对外 SSO（学分系统等）场景。
   *
   * 注意：这里用**裸 axios**（不是 http 实例）。http 的拦截器会给请求附加旧令牌、
   * 并在 401 时触发「刷新令牌 + 重放」—— 登录本身不该走那套逻辑，
   * 否则「密码错了」会被误当成「令牌过期」。
   */
  async login(userName: string, password: string): Promise<TokenResponse> {
    const body = new URLSearchParams({
      grant_type: 'password',
      client_id: CLIENT_ID,
      username: userName,
      password,
      scope: SCOPE
    })

    try {
      const { data } = await axios.post<TokenResponse>(TOKEN_ENDPOINT, body, {
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' }
      })

      tokenStorage.save(data)

      return data
    } catch (error) {
      // OpenIddict 的错误体是 OAuth 标准格式（error / error_description），
      // 不是 ABP 的 error 信封，所以不能复用 describeError
      throw new Error(describeTokenError(error))
    }
  },

  logout(): void {
    tokenStorage.clear()
  },

  get isAuthenticated(): boolean {
    return !!tokenStorage.accessToken
  }
}

/**
 * 把 OpenIddict 的 OAuth 错误码翻成教师看得懂的话。
 *
 * 直接显示 `invalid_grant` 对用户毫无意义，而把所有失败都写成
 * 「登录失败」又会让配置问题（客户端没开密码模式、scope 没注册）
 * 变成排查不出来的玄学问题 —— 所以按下表逐条区分。
 */
function describeTokenError(error: unknown): string {
  if (!axios.isAxiosError(error)) {
    return '登录失败，请稍后重试。'
  }

  if (!error.response) {
    return '无法连接服务器，请确认后端服务已启动。'
  }

  const body = error.response.data as { error?: string; error_description?: string } | undefined
  const code = body?.error

  switch (code) {
    case 'invalid_grant':
      return '用户名或密码错误，或账号已被停用。'
    case 'invalid_client':
      return '客户端配置不正确（Xjgl_App 不存在或未启用）。请联系系统管理员。'
    case 'invalid_scope':
      return '登录请求的权限范围不被允许，请联系系统管理员检查 OpenIddict 配置。'
    case 'unsupported_grant_type':
      return '服务端未启用密码登录模式，请联系系统管理员。'
    default:
      return body?.error_description ?? `登录失败（HTTP ${error.response.status}）。`
  }
}

// ---------------------------------------------------------------------------
// 当前用户上下文
// ---------------------------------------------------------------------------

export const profileApi = {
  /** 一次拿到身份、组织、角色、权限码、可见菜单 —— 前端启动只调这一个接口。 */
  async get(): Promise<MyProfile> {
    const { data } = await http.get<MyProfile>(ENDPOINTS.profile)

    return data
  }
}

// ---------------------------------------------------------------------------
// 组织
// ---------------------------------------------------------------------------

export const orgApi = {
  async getTree(): Promise<OrgDto[]> {
    const { data } = await http.get<OrgDto[]>(ENDPOINTS.orgTree)

    return data
  }
}

// ---------------------------------------------------------------------------
// 学生
// ---------------------------------------------------------------------------

export const studentApi = {
  async getList(query: StudentQuery): Promise<PagedResult<StudentListItem>> {
    const { data } = await http.get<PagedResult<StudentListItem>>(ENDPOINTS.student, {
      params: query
    })

    return data
  }
}

// ---------------------------------------------------------------------------
// 照片
// ---------------------------------------------------------------------------

export const photoApi = {
  async getList(query: PhotoQuery): Promise<PagedResult<PhotoDto>> {
    const { data } = await http.get<PagedResult<PhotoDto>>(ENDPOINTS.photo, { params: query })

    return data
  },

  async getMissingList(query: MissingPhotoQuery): Promise<PagedResult<MissingPhotoStudent>> {
    const { data } = await http.get<PagedResult<MissingPhotoStudent>>(ENDPOINTS.photoMissingList, {
      params: query
    })

    return data
  },

  async getStorageHealth(): Promise<PhotoStorageHealth> {
    const { data } = await http.get<PhotoStorageHealth>(ENDPOINTS.photoStorageHealth, {
      // 存储健康度由首页定时轮询，失败时不弹提示（否则 NAS 一抖动就刷屏）
      silent: true
    })

    return data
  },

  async compensate(): Promise<PhotoCompensationResult> {
    const { data } = await http.post<PhotoCompensationResult>(ENDPOINTS.photoCompensate)

    return data
  },

  async deleteMany(ids: string[]): Promise<number> {
    const { data } = await http.post<number>(ENDPOINTS.photoDeleteMany, ids)

    return data
  },

  /**
   * 批量上传。
   *
   * 用 multipart 而不是 base64 JSON：一张 200KB 的照片 base64 之后是 270KB，
   * 50 张的批量上传会白白多传 3.5MB —— 这在县级学校的网络上很要命。
   *
   * studentIds 与 files **顺序必须一一对应**，后端按索引配对。
   */
  async upload(
    files: File[],
    studentIds: string[],
    batchId: string,
    onProgress?: (percent: number) => void
  ): Promise<PhotoUploadResult> {
    const form = new FormData()

    files.forEach((file) => form.append('files', file))
    studentIds.forEach((id) => form.append('studentIds', id))
    form.append('batchId', batchId)

    const { data } = await http.post<PhotoUploadResult>(ENDPOINTS.photoUpload, form, {
      timeout: 0,
      onUploadProgress: (event) => {
        if (onProgress && event.total) {
          onProgress(Math.round((event.loaded / event.total) * 100))
        }
      }
    })

    return data
  }
}
