import axios from 'axios'

import { CLIENT_ID, http, SCOPE, TOKEN_ENDPOINT, tokenStorage } from './http'
import type {
  ActivateUser,
  ActivateUserResult,
  ChangeApply,
  ChangeApplyListItem,
  ChangeApplyQuery,
  ChangeAuditAction,
  ChangeFlowTemplate,
  ChangeTodoQuery,
  ChangeType,
  CodeCategory,
  CodeItem,
  CreatePredefineUser,
  CreateUpdateChangeType,
  CreateUpdateCodeCategory,
  CreateUpdateCodeItem,
  CreateUpdateModule,
  CreateUpdateModuleAction,
  CreateUpdateOrg,
  CreateUpdateSubsystem,
  CreateUser,
  IdentityRole,
  IdentityUser,
  ImportPredefineResult,
  MissingPhotoQuery,
  MissingPhotoStudent,
  ModuleAction,
  MyProfile,
  OrgDto,
  OrgTreeNode,
  PagedResult,
  PredefineUser,
  PredefineUserQuery,
  PermissionResult,
  PhotoCompensationResult,
  PhotoDto,
  PhotoQuery,
  PhotoStorageHealth,
  PhotoUploadResult,
  PreviewFlowResult,
  SaveFlowTemplate,
  StudentListItem,
  StudentQuery,
  SubmitChangeApply,
  Subsystem,
  SysModule,
  TokenResponse,
  UpdateUser
} from './types'

// 让包根入口同时导出全部类型与 HTTP 封装：各端只需 `import ... from '@xjgl/api'`，
// 不必去记 types / http 这些子路径（子路径仍然可用，见 package.json 的 exports）。
// 注意 http 这一行不能省：路由层要用 setUnauthorizedHandler 注册 401 回调，
// 少了它就会出现"接口都正常，只有登录失效跳转不生效"这种局部缺失。
export * from './http'
export * from './permissions'
export * from './types'

/**
 * 后端接口地址集中定义。
 *
 * 这些路径是 ABP **约定式控制器**生成的（应用服务名去掉 AppService 转 kebab-case，
 * 方法名同理），集中放在这里的原因很直接：万一某个方法名改了导致路径变化，
 * 只需要改这一处，而不用在整个前端里翻字符串。
 *
 * ⚠ 这些路径**不是靠推断写出来的，是启动后端后从 /swagger/v1/swagger.json
 *   实际取出来核对过的**。踩过的坑：
 *     - 方法名去掉动词前缀后的剩余部分才作为路径段，
 *       所以 `GetTypesAsync` → `/types`、`GetApplyAsync(id)` → `/{id}/apply`；
 *     - `DeleteManyAsync` → `/many`，**不是** `/delete-many`（我第一版就猜错了）；
 *     - 光看代码猜路径风险很高，而且猜错的表现可能是 405 而不是 404，更难定位。
 */
export const ENDPOINTS = {
  profile: '/api/app/my-profile',

  org: '/api/app/org',
  orgTree: '/api/app/org/tree',

  /** ABP Identity 自带用户/角色接口（不是我们自己的应用服务） */
  identityUsers: '/api/identity/users',
  identityRoles: '/api/identity/roles',
  identityRolesAll: '/api/identity/roles/all',

  /** ABP PermissionManagement 自带授权接口 */
  permissions: '/api/permission-management/permissions',

  code: '/api/app/code',
  codeCategories: '/api/app/code/categories',
  codeItems: '/api/app/code/items',

  module: '/api/app/module',
  userProvision: '/api/app/user-provision',

  student: '/api/app/student',

  photo: '/api/app/photo',
  photoMissingList: '/api/app/photo/missing-photo-list',
  photoStorageHealth: '/api/app/photo/storage-health',
  photoCompensate: '/api/app/photo/compensate',
  photoDeleteMany: '/api/app/photo/many',
  photoUpload: '/api/photos/upload',

  changeType: '/api/app/change-type',
  changeFlow: '/api/app/change-flow'
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
  /** 整棵组织树。**上万所学校时不要用**，优先用 getRoots + getChildren 逐层加载。 */
  async getTree(): Promise<OrgDto[]> {
    const { data } = await http.get<OrgDto[]>(ENDPOINTS.orgTree)

    return data
  },

  /**
   * 取根节点（省厅）。
   *
   * 为什么不用「parentId 传空」的写法：路由是 `/org/children/{parentId}`，
   * 空段会变成 404。改用列表接口按类型过滤，语义还更清楚。
   */
  async getRoots(): Promise<OrgDto[]> {
    const { data } = await http.get<OrgDto[]>(ENDPOINTS.org, {
      params: { orgType: 'Province', onlyEnabled: false }
    })

    return data
  },

  /** 逐层加载某个节点的直接下级（大组织树的标准打开方式）。 */
  async getChildren(parentId: string, onlyEnabled = false): Promise<OrgTreeNode[]> {
    const { data } = await http.get<OrgTreeNode[]>(`${ENDPOINTS.org}/children/${parentId}`, {
      params: { onlyEnabled }
    })

    return data
  },

  async get(id: string): Promise<OrgDto> {
    const { data } = await http.get<OrgDto>(`${ENDPOINTS.org}/${id}`)

    return data
  },

  async create(input: CreateUpdateOrg): Promise<OrgDto> {
    const { data } = await http.post<OrgDto>(ENDPOINTS.org, input)

    return data
  },

  async update(id: string, input: CreateUpdateOrg): Promise<OrgDto> {
    const { data } = await http.put<OrgDto>(`${ENDPOINTS.org}/${id}`, input)

    return data
  },

  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.org}/${id}`)
  },

  /** 移动节点（会级联重写整棵子树的物化路径）。 */
  async move(id: string, newParentId?: string | null): Promise<void> {
    await http.post(`${ENDPOINTS.org}/${id}/move`, { newParentId: newParentId ?? null })
  }
}

// ---------------------------------------------------------------------------
// 身份：用户与角色（走 ABP Identity 自带接口）
// ---------------------------------------------------------------------------

export const identityApi = {
  async getUsers(query: {
    filter?: string
    skipCount?: number
    maxResultCount?: number
    sorting?: string
  }): Promise<PagedResult<IdentityUser>> {
    const { data } = await http.get<PagedResult<IdentityUser>>(ENDPOINTS.identityUsers, {
      params: query
    })

    return data
  },

  async getUser(id: string): Promise<IdentityUser> {
    const { data } = await http.get<IdentityUser>(`${ENDPOINTS.identityUsers}/${id}`)

    return data
  },

  /**
   * 取某个用户的角色。
   *
   * **必须用这个接口，不能读 user.roleNames** —— ABP 10 的 IdentityUserDto
   * （无论列表还是详情）都不包含 roleNames 字段。曾经想当然地读它，
   * 拿到 undefined → 界面上角色是空的 → 保存时把空数组回写 →
   * **用户角色被静默清空**。角色是"谁有什么权限"的唯一入口，这类静默丢失很致命。
   */
  async getUserRoles(id: string): Promise<IdentityRole[]> {
    const { data } = await http.get<{ items: IdentityRole[] }>(
      `${ENDPOINTS.identityUsers}/${id}/roles`
    )

    return data.items
  },

  async createUser(input: CreateUser): Promise<IdentityUser> {
    const { data } = await http.post<IdentityUser>(ENDPOINTS.identityUsers, input)

    return data
  },

  async updateUser(id: string, input: UpdateUser): Promise<IdentityUser> {
    const { data } = await http.put<IdentityUser>(`${ENDPOINTS.identityUsers}/${id}`, input)

    return data
  },

  async deleteUser(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.identityUsers}/${id}`)
  },

  /** 全部角色（用于角色下拉与用户角色分配）。 */
  async getRoles(): Promise<IdentityRole[]> {
    const { data } = await http.get<{ items: IdentityRole[] }>(ENDPOINTS.identityRolesAll)

    return data.items
  }
}

// ---------------------------------------------------------------------------
// 权限分配（走 ABP PermissionManagement 自带接口）
// ---------------------------------------------------------------------------

export const permissionApi = {
  /**
   * 取某个 provider（角色 R / 用户 U）的权限树。
   *
   * providerKey：角色传角色名（如 admin），用户传用户 Id。
   */
  async get(providerName: 'R' | 'U', providerKey: string): Promise<PermissionResult> {
    const { data } = await http.get<PermissionResult>(ENDPOINTS.permissions, {
      params: { providerName, providerKey }
    })

    return data
  },

  /** 批量保存。只提交变化过的项，减少无效写入。 */
  async update(
    providerName: 'R' | 'U',
    providerKey: string,
    permissions: { name: string; isGranted: boolean }[]
  ): Promise<void> {
    await http.put(ENDPOINTS.permissions, { permissions }, { params: { providerName, providerKey } })
  }
}

// ---------------------------------------------------------------------------
// 微代码
// ---------------------------------------------------------------------------

export const codeApi = {
  async getCategories(keyword?: string): Promise<CodeCategory[]> {
    const { data } = await http.get<CodeCategory[]>(ENDPOINTS.codeCategories, {
      params: { keyword }
    })

    return data
  },

  async createCategory(input: CreateUpdateCodeCategory): Promise<CodeCategory> {
    const { data } = await http.post<CodeCategory>(`${ENDPOINTS.code}/category`, input)

    return data
  },

  async updateCategory(id: string, input: CreateUpdateCodeCategory): Promise<CodeCategory> {
    const { data } = await http.put<CodeCategory>(`${ENDPOINTS.code}/${id}/category`, input)

    return data
  },

  async removeCategory(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.code}/${id}/category`)
  },

  async getItems(categoryId: string, onlyEnabled = false, asTree = false): Promise<CodeItem[]> {
    const { data } = await http.get<CodeItem[]>(`${ENDPOINTS.code}/items/${categoryId}`, {
      params: { onlyEnabled, asTree }
    })

    return data
  },

  async createItem(input: CreateUpdateCodeItem): Promise<CodeItem> {
    const { data } = await http.post<CodeItem>(`${ENDPOINTS.code}/item`, input)

    return data
  },

  async updateItem(id: string, input: CreateUpdateCodeItem): Promise<CodeItem> {
    const { data } = await http.put<CodeItem>(`${ENDPOINTS.code}/${id}/item`, input)

    return data
  },

  async removeItem(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.code}/${id}/item`)
  },

  /** 码值变更后清缓存（一般写操作已自动失效，这里给一个手动入口）。 */
  async refreshCache(): Promise<void> {
    await http.post(`${ENDPOINTS.code}/refresh-cache`, {})
  }
}

// ---------------------------------------------------------------------------
// 元数据：子系统 / 模块 / 操作
// ---------------------------------------------------------------------------

export const moduleApi = {
  /** 完整元数据树（子系统 → 模块 → 操作）。数量不大，一次取回。 */
  async getTree(onlyEnabled = false): Promise<Subsystem[]> {
    const { data } = await http.get<Subsystem[]>(`${ENDPOINTS.module}/tree`, {
      params: { onlyEnabled }
    })

    return data
  },

  // ---- 子系统 ----
  async createSubsystem(input: CreateUpdateSubsystem): Promise<Subsystem> {
    const { data } = await http.post<Subsystem>(`${ENDPOINTS.module}/subsystem`, input)

    return data
  },

  async updateSubsystem(id: string, input: CreateUpdateSubsystem): Promise<Subsystem> {
    const { data } = await http.put<Subsystem>(`${ENDPOINTS.module}/${id}/subsystem`, input)

    return data
  },

  async removeSubsystem(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.module}/${id}/subsystem`)
  },

  // ---- 模块 ----
  async createModule(input: CreateUpdateModule): Promise<SysModule> {
    const { data } = await http.post<SysModule>(`${ENDPOINTS.module}/module`, input)

    return data
  },

  async updateModule(id: string, input: CreateUpdateModule): Promise<SysModule> {
    const { data } = await http.put<SysModule>(`${ENDPOINTS.module}/${id}/module`, input)

    return data
  },

  async removeModule(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.module}/${id}/module`)
  },

  // ---- 操作 ----
  async createAction(input: CreateUpdateModuleAction): Promise<ModuleAction> {
    const { data } = await http.post<ModuleAction>(`${ENDPOINTS.module}/action`, input)

    return data
  },

  async updateAction(id: string, input: CreateUpdateModuleAction): Promise<ModuleAction> {
    const { data } = await http.put<ModuleAction>(`${ENDPOINTS.module}/${id}/action`, input)

    return data
  },

  async removeAction(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.module}/${id}/action`)
  },

  /** 手工刷新元数据缓存（写操作已自动失效，这里给一个兜底入口）。 */
  async refreshCache(): Promise<void> {
    await http.post(`${ENDPOINTS.module}/refresh-cache`, {})
  }
}

// ---------------------------------------------------------------------------
// 用户预置与激活
// ---------------------------------------------------------------------------

export const provisionApi = {
  async getList(query: PredefineUserQuery): Promise<PagedResult<PredefineUser>> {
    const { data } = await http.get<PagedResult<PredefineUser>>(ENDPOINTS.userProvision, {
      params: query
    })

    return data
  },

  async get(id: string): Promise<PredefineUser> {
    const { data } = await http.get<PredefineUser>(`${ENDPOINTS.userProvision}/${id}`)

    return data
  },

  async create(input: CreatePredefineUser): Promise<PredefineUser> {
    const { data } = await http.post<PredefineUser>(ENDPOINTS.userProvision, input)

    return data
  },

  /** 批量预置（一次几百名教师时用）。 */
  async import(users: CreatePredefineUser[], batchNo?: string): Promise<ImportPredefineResult> {
    const { data } = await http.post<ImportPredefineResult>(`${ENDPOINTS.userProvision}/import`, {
      users,
      batchNo: batchNo ?? null
    })

    return data
  },

  /** 续期激活码（教师一直没激活时用）。 */
  async renew(id: string, validDays = 30): Promise<PredefineUser> {
    const { data } = await http.post<PredefineUser>(
      `${ENDPOINTS.userProvision}/${id}/renew-activation`,
      {},
      { params: { validDays } }
    )

    return data
  },

  /** 吊销（激活码作废，已激活的账号也会被停用）。 */
  async revoke(id: string): Promise<PredefineUser> {
    const { data } = await http.post<PredefineUser>(`${ENDPOINTS.userProvision}/${id}/revoke`, {})

    return data
  },

  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.userProvision}/${id}`)
  },

  /**
   * 本人激活（**匿名接口**，激活页在登录之前使用）。
   *
   * 这里用裸 axios 而不是 http 实例：http 会附加本地可能已过期的令牌，
   * 并在 401 时触发"刷新令牌 + 重放"，而激活本来就不该带令牌 ——
   * 带着过期令牌去调匿名接口，会被拦截器绕进刷新流程，反而报出莫名其妙的错。
   */
  async activate(input: ActivateUser): Promise<ActivateUserResult> {
    const { data } = await axios.post<ActivateUserResult>(
      `${ENDPOINTS.userProvision}/activate`,
      input
    )

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

// ---------------------------------------------------------------------------
// 异动类型与流程模板
// ---------------------------------------------------------------------------

export const changeTypeApi = {
  async getTypes(onlyEnabled = false): Promise<ChangeType[]> {
    const { data } = await http.get<ChangeType[]>(`${ENDPOINTS.changeType}/types`, {
      params: { onlyEnabled }
    })

    return data
  },

  async get(id: string): Promise<ChangeType> {
    const { data } = await http.get<ChangeType>(`${ENDPOINTS.changeType}/${id}/type`)

    return data
  },

  async create(input: CreateUpdateChangeType): Promise<ChangeType> {
    const { data } = await http.post<ChangeType>(`${ENDPOINTS.changeType}/type`, input)

    return data
  },

  async update(id: string, input: CreateUpdateChangeType): Promise<ChangeType> {
    const { data } = await http.put<ChangeType>(`${ENDPOINTS.changeType}/${id}/type`, input)

    return data
  },

  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.changeType}/${id}/type`)
  },

  /**
   * 取当前草稿模板。**没有任何模板时后端返回空响应体**，
   * 这里统一成 null，避免调用方拿到空字符串去做 `.nodes` 而报错。
   */
  async getTemplate(changeTypeId: string): Promise<ChangeFlowTemplate | null> {
    const { data } = await http.get<ChangeFlowTemplate | ''>(
      `${ENDPOINTS.changeType}/template/${changeTypeId}`
    )

    return data || null
  },

  async saveTemplate(input: SaveFlowTemplate): Promise<ChangeFlowTemplate> {
    const { data } = await http.post<ChangeFlowTemplate>(
      `${ENDPOINTS.changeType}/save-template`,
      input
    )

    return data
  },

  async publishTemplate(templateId: string): Promise<ChangeFlowTemplate> {
    const { data } = await http.post<ChangeFlowTemplate>(
      `${ENDPOINTS.changeType}/publish-template/${templateId}`,
      {}
    )

    return data
  },

  /**
   * 流程预览 —— 本系统最有价值的一个接口。
   *
   * 业务人员改完流程后，不必等真实申请跑一遍，直接给一组
   * 「原学校 + 目标学校」就能看到**实际会走哪几步、每步审到哪一级、
   * 哪几步被合并了**。这样"同县区只审一次"这类规则能被当场验证。
   */
  async previewFlow(input: {
    changeTypeId: string
    targetSchoolOrgId: string
    sourceSchoolOrgId?: string
  }): Promise<PreviewFlowResult> {
    const { data } = await http.post<PreviewFlowResult>(
      `${ENDPOINTS.changeType}/preview-flow`,
      input
    )

    return data
  }
}

// ---------------------------------------------------------------------------
// 异动申请与审核
// ---------------------------------------------------------------------------

export const changeFlowApi = {
  async getApplies(query: ChangeApplyQuery): Promise<PagedResult<ChangeApplyListItem>> {
    const { data } = await http.get<PagedResult<ChangeApplyListItem>>(
      `${ENDPOINTS.changeFlow}/applies`,
      { params: query }
    )

    return data
  },

  async getApply(id: string): Promise<ChangeApply> {
    const { data } = await http.get<ChangeApply>(`${ENDPOINTS.changeFlow}/${id}/apply`)

    return data
  },

  async submit(input: SubmitChangeApply): Promise<ChangeApply> {
    const { data } = await http.post<ChangeApply>(`${ENDPOINTS.changeFlow}/submit`, input)

    return data
  },

  async withdraw(id: string): Promise<ChangeApply> {
    const { data } = await http.post<ChangeApply>(`${ENDPOINTS.changeFlow}/${id}/withdraw`, {})

    return data
  },

  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.changeFlow}/${id}`)
  },

  /** 我的待办：只返回"当前轮到我审"的任务。 */
  async getTodoList(query: ChangeTodoQuery): Promise<PagedResult<ChangeApplyListItem>> {
    const { data } = await http.get<PagedResult<ChangeApplyListItem>>(
      `${ENDPOINTS.changeFlow}/todo-list`,
      { params: query }
    )

    return data
  },

  async approve(input: ChangeAuditAction): Promise<ChangeApply> {
    const { data } = await http.post<ChangeApply>(`${ENDPOINTS.changeFlow}/approve`, input)

    return data
  },

  async reject(input: ChangeAuditAction): Promise<ChangeApply> {
    const { data } = await http.post<ChangeApply>(`${ENDPOINTS.changeFlow}/reject`, input)

    return data
  },

  /** 撤销审核：把已通过的任务退回待审，并把学籍状态回滚到申请前。 */
  async revokeApproval(input: ChangeAuditAction): Promise<ChangeApply> {
    const { data } = await http.post<ChangeApply>(
      `${ENDPOINTS.changeFlow}/revoke-approval`,
      input
    )

    return data
  }
}
