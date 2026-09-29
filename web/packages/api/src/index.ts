import axios from 'axios'

import { CLIENT_ID, http, SCOPE, TOKEN_ENDPOINT, tokenStorage } from './http'

// ExportTaskStatuses 是**值**（枚举式常量对象），不能放在下面的 import type 里 ——
// 那样只能当类型用，用作值会报 TS1361。
import { ExportTaskStatuses } from './types'
import type {
  ActivateUser,
  ActivateUserResult,
  ChangeApply,
  ChangeApplyListItem,
  ChangeApplyQuery,
  ChangeAttachment,
  ChangeAuditAction,
  ChangeFlowTemplate,
  ChangeStatisticsQuery,
  ChangeTodoQuery,
  ChangeType,
  ClassDto,
  CodeCategory,
  CodeItem,
  CodeItemLookup,
  CodeOption,
  CreatePredefineUser,
  CreatePermissionDelegation,
  CreateRole,
  CreateUpdateChangeType,
  CreateUpdateCodeCategory,
  CreateUpdateCodeItem,
  CreateUpdateModule,
  CreateUpdateModuleAction,
  CreateUpdateOrg,
  CreateUpdateSubsystem,
  CreateUser,
  CreateExportTask,
  DelegatablePermission,
  ExportTask,
  ExportTaskQuery,
  Guardian,
  GuardianInput,
  GradeDto,
  IdentityRole,
  IdentityUser,
  ImportPredefineResult,
  ImportColumn,
  ImportCodesResult,
  ImportOrganizationsResult,
  ImportStudentsResult,
  MissingPhotoQuery,
  MissingPhotoStudent,
  ModuleAction,
  MyProfile,
  OrgDto,
  OrgQuery,
  OrgTreeNode,
  PagedResult,
  Physical,
  PhysicalInput,
  PredefineUser,
  PredefineUserQuery,
  PermissionDelegation,
  PermissionResult,
  PhotoCompensationResult,
  PhotoDto,
  PhotoQuery,
  PhotoStorageHealth,
  PhotoUploadResult,
  PreviewFlowResult,
  Punishment,
  PunishmentInput,
  Resume,
  ResumeInput,
  Reward,
  RewardInput,
  RoleDataScope,
  SaveFlowTemplate,
  SetRoleDataScope,
  StatisticResult,
  CheckStudentDuplicate,
  CreateUpdateStudent,
  StudentDetail,
  StudentDuplicateCheckResult,
  StudentListItem,
  StudentStatisticsQuery,
  StudentChangeLog,
  StudentChangeLogQuery,
  StudentQuery,
  SubmitChangeApply,
  Subsidy,
  SubsidyInput,
  Subsystem,
  SysModule,
  TokenResponse,
  UpdateRole,
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
 *     - 路径对了动词还可能错：不以 `Get*` 开头的方法默认是 POST，
 *       所以「导出」「批量删除」这类方法的动词必须回 swagger 核对（已踩过 405）；
 *     - 光看代码猜路径风险很高，而且猜错的表现可能是 405 而不是 404，更难定位。
 */
export const ENDPOINTS = {
  profile: '/api/app/my-profile',

  org: '/api/app/org',
  orgTree: '/api/app/org/tree',

  /**
   * 年级与班级。
   *
   * ⚠ 路由形态**不能靠签名推断**，必须回 swagger 看。实际结果是：
   *   - `GET /api/app/grade-class/grades/{schoolOrgId}?academicYear=`
   *   - `GET /api/app/grade-class/classes?schoolOrgId=&academicYear=&gradeId=`
   *
   * 我第一版按"参数都进路径"写成 `/grades/{schoolOrgId}/{academicYear}`，
   * 被 `tools/audit-routes.mjs` 当场拦下（"后端: (无此路由)"）——
   * 否则运行期表现为年级下拉框永远是空的。
   * 两个方法名的第一个参数进了路径、其余（含那个**必填的 Guid**）都走查询串，
   * 所以 `classes` 连 schoolOrgId 都是查询参数。这类差异只能实测。
   */
  gradeClass: '/api/app/grade-class',

  /** ABP Identity 自带用户/角色接口（不是我们自己的应用服务） */
  identityUsers: '/api/identity/users',
  identityRoles: '/api/identity/roles',
  identityRolesAll: '/api/identity/roles/all',

  /** ABP PermissionManagement 自带授权接口 */
  permissions: '/api/permission-management/permissions',

  /**
   * 角色级数据范围（ROADMAP 第 9 条）。
   *
   * 本项目自己的应用服务（不是 ABP 自带的），路径按 `RoleDataScopeAppService`
   * 的约定推出来，**动词必须回 swagger 核对**：
   *   - `GetListAsync`    → `GET  /api/app/role-data-scope`（全部角色及其范围）
   *   - `GetOptionsAsync` → `GET  /api/app/role-data-scope/options`（下拉框选项）
   *   - `SetAsync`        → `POST /api/app/role-data-scope/set`（设置 / 清除）
   *
   * ⚠ 最后一条**踩过一次真的坑**：这里原来写的是 `POST /api/app/role-data-scope`，
   *   而 ABP 把不以 Get/Create/Update/Delete 开头的方法按**方法名**补一段路径
   *   （`SetAsync` → `/set`），于是"保存数据范围"在真机上必然 404/405 ——
   *   集成测试直接调应用服务、绕过了 HTTP，所以一直是绿的。
   *   抓出它的是 `tools/audit-routes.mjs`：**方法名推路径的东西必须回 swagger 核对**。
   */
  roleDataScope: '/api/app/role-data-scope',
  roleDataScopeSet: '/api/app/role-data-scope/set',
  roleDataScopeOptions: '/api/app/role-data-scope/options',

  code: '/api/app/code',
  codeCategories: '/api/app/code/categories',
  codeItems: '/api/app/code/items',

  module: '/api/app/module',
  userProvision: '/api/app/user-provision',

  student: '/api/app/student',
  studentImport: '/api/app/student-import-export',

  /**
   * 学生档案子表（监护人 / 简历 / 奖励 / 处分 / 资助 / 体检）。
   *
   * ⚠ 路径段顺序是 ABP 约定推出来的**实测结果**，不是猜的：
   *   查询与新增把动作名放在前面（`/guardians/{studentId}`、`/guardian/{studentId}`），
   *   而修改与删除把 id 放在前面（`/{id}/guardian`）。
   *   这类"看着别扭但就是这样"的路由必须回 swagger 核对（见 tools/audit-routes.mjs）。
   */
  studentProfile: '/api/app/student-profile',

  /**
   * 学籍变更流水（留痕）。查询走 `student-logs/{studentId}`（与子表同一套约定：
   * 查/增把动作名放前面），全局查询是 `GET /api/app/student-change-log`。
   */
  studentChangeLog: '/api/app/student-change-log',

  /**
   * 导出 / 打印后台任务的入口。
   *
   * ⚠ 原来这里是三条**同步**下载接口（`/api/print/*`、`/api/app/student-import-export/export`、
   * `/api/photos/export`）：请求里直接渲染 PDF / 打包 zip / 拼几千行 CSV。
   * 上千人的花名册会把请求线程占满，而且浏览器先超时、服务端还在跑 ——
   * 用户看到"失败"，文件其实已经生成，他再点一次，于是白干两遍。
   * 现在统一改成「提交任务 → 轮询 → 下载」，那三条旧接口已删除（返回 404）。
   */
  exportTask: '/api/app/export-task',

  /**
   * 产物下载走**手写控制器**，所以路径里**没有 `/app`**。
   *
   * ⚠ 这两个前缀不同不是笔误，与照片那套完全同源（见下面 photoFile 的说明）：
   *   - `/api/app/export-task` 是 ABP 约定式应用服务（入队、查单条、列表、取消、删除，JSON）；
   *   - `/api/export-task` 是手写控制器（下载二进制产物，流式响应，不经过 JSON 序列化）。
   *   我第一次把下载写成 `/api/app/export-task/{id}/download`，
   *   被 `tools/audit-routes.mjs` 当场拦下（"后端: (无此路由)"）——
   *   否则线上表现就是点下载 404，而前端把它变成一个模糊的失败提示。
   */
  exportTaskDownload: '/api/export-task',

  photo: '/api/app/photo',

  /**
   * 照片的**二进制**接口（缩略图 / 原图）。
   *
   * ⚠ 注意这里**没有 `/app`**：照片管理有两套入口 ——
   *   - `/api/app/photo` 是约定式应用服务（台账查询、删除等，返回 JSON）；
   *   - `/api/photos` 是手写控制器（上传、取图片，收发二进制）。
   * 两者路径不同不是笔误。我第一次写成了 `/api/app/photo/{id}/thumbnail`，
   * 被 `tools/audit-routes.mjs` 当场拦下（否则运行期是 404，
   * 而前端的 catch 会把它变成"缩略图全是空的"，很难查）。
   */
  photoFile: '/api/photos',
  photoMissingList: '/api/app/photo/missing-photo-list',
  photoStorageHealth: '/api/app/photo/storage-health',
  photoCompensate: '/api/app/photo/compensate',
  photoDeleteMany: '/api/app/photo/many',
  photoUpload: '/api/photos/upload',

  changeType: '/api/app/change-type',
  changeFlow: '/api/app/change-flow',

  /**
   * 异动附件（手写控制器，收发二进制 / multipart）。
   *
   * ⚠ 与照片同一个理由**没有 `/app`**：ABP 约定式控制器不会为
   *   `Stream` 参数/返回值生成接口，所以上传与下载都在手写控制器里
   *   （`ChangeAttachmentController`），路径是 `/api/change-attachment`。
   */
  changeAttachment: '/api/change-attachment',

  /**
   * 统计报表。
   *
   * 后端是一个应用服务 `StatisticsAppService` 里的两个方法
   * （`GetStudentStatisticsAsync` / `GetChangeStatisticsAsync`），
   * 按 ABP 约定它们分别落到 `/student-statistics` 与 `/change-statistics`。
   *
   * 与导出下载那套不同，这里**是纯 JSON 的约定式接口**，所以前缀带 `/api/app`。
   * 两条路径都在 `tools/audit-routes.mjs` 的核对范围里 —— 这类"方法名推路径"
   * 的东西一旦改名就会静默 404，只有核对脚本能拦住。
   */
  report: '/api/app/statistics'
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
   * 按条件查组织（`OrgQueryDto` 的四个条件：类型 / 上级 / 关键字 / 是否只看启用）。
   *
   * 【为什么需要它 —— `getTree()` 在真实数据量下不能用】
   * `getTree()` 一次返回**整棵组织树**，它自己的注释就写着"上万所学校时不要用"。
   * 「按名字找一所学校」这类场景不需要树：教师知道学校叫什么，不知道它在哪个县。
   * 所以给一个关键字查询，只回匹配的少量节点。
   *
   * ⚠ 返回的是 `List<OrgDto>`，**不是分页**（后端 `GetListAsync` 返回 List）：
   *   没有 `totalCount`、也没有 `skipCount`，调用方拿不到"总共多少条"，
   *   更翻不了页。关键字越短命中越多（后端做的是 `Name.Contains(keyword)`），
   *   因此调用方**必须自己限制渲染条数并提示用户继续输入** ——
   *   见 H5 `views/change/apply.vue` 的 20 条上限。
   *
   * ⚠ `onlyEnabled` 后端默认就是 **true**（`OrgQueryDto.OnlyEnabled`），
   *   这里照实转发，不写就不能像别的接口那样"不传等于不过滤"。
   */
  async getList(query: OrgQuery): Promise<OrgDto[]> {
    const { data } = await http.get<OrgDto[]>(ENDPOINTS.org, {
      params: {
        orgType: query.orgType,
        parentId: query.parentId,
        keyword: query.keyword,
        onlyEnabled: query.onlyEnabled,
        includeDescendants: query.includeDescendants
      }
    })

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
  },

  /**
   * 某校某学年的年级列表。
   *
   * ⚠ 只有 `schoolOrgId` 进路径，`academicYear` 走查询串（已回 swagger 核对）。
   * 两个参数都必填：年级是「学校 + 学年」维度的，只给学年会得到全省所有学校的年级。
   */
  async getGrades(schoolOrgId: string, academicYear: string): Promise<GradeDto[]> {
    const { data } = await http.get<GradeDto[]>(`${ENDPOINTS.gradeClass}/grades/${schoolOrgId}`, {
      params: { academicYear }
    })

    return data
  },

  /**
   * 班级列表（可按学年 / 年级过滤）。
   *
   * ⚠ 三个参数**全部走查询串**，连必填的 `schoolOrgId` 也是 ——
   * 写成 `/classes/{schoolOrgId}` 会被核对脚本拦下（后端无此路由）。
   */
  async getClasses(query: {
    schoolOrgId: string
    academicYear?: string
    gradeId?: string
  }): Promise<ClassDto[]> {
    const { data } = await http.get<ClassDto[]>(`${ENDPOINTS.gradeClass}/classes`, {
      params: {
        schoolOrgId: query.schoolOrgId,
        academicYear: query.academicYear,
        gradeId: query.gradeId
      }
    })

    return data
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
  },

  /**
   * 分页取角色（角色管理页用，带总数）。
   *
   * 与 `getRoles()` 的区别不只是多一个 totalCount：
   *   - `/roles/all` 返回**全部**角色的精简列表，用于下拉框（如"给用户选角色"）；
   *   - `/roles` 是分页列表，用于管理页面。
   * 两个接口**要求的权限也不同**，所以不能互相替代 —— 实测：
   * 没有 `AbpIdentity.Roles` 的账号打这两个都是 **302 跳登录页**（不是 403）。
   */
  async getRolePage(query: {
    filter?: string
    skipCount?: number
    maxResultCount?: number
    sorting?: string
  }): Promise<PagedResult<IdentityRole>> {
    const { data } = await http.get<PagedResult<IdentityRole>>(ENDPOINTS.identityRoles, {
      params: query
    })

    return data
  },

  async createRole(input: CreateRole): Promise<IdentityRole> {
    const { data } = await http.post<IdentityRole>(ENDPOINTS.identityRoles, input)

    return data
  },

  async updateRole(id: string, input: UpdateRole): Promise<IdentityRole> {
    const { data } = await http.put<IdentityRole>(`${ENDPOINTS.identityRoles}/${id}`, input)

    return data
  },

  async deleteRole(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.identityRoles}/${id}`)
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
// 角色级数据范围（ROADMAP 第 9 条）
// ---------------------------------------------------------------------------

/**
 * 角色级数据范围配置。
 *
 * 【它与「权限」是什么关系】
 *   权限（`Sys.Role.*` / `Stu.Student.*`）决定**能做什么**；
 *   数据范围决定**能对谁做**。两者正交，都挂在角色上。
 *
 * 【为什么权限沿用角色管理的 Sys.Role.*】
 *   配数据范围本来就是"管角色"的一部分，另立一个权限码只会多一处
 *   "这个码勾了到底管不管用"的疑问（后端 DTO 的注释同此）。
 *   因此：读列表 / 读选项要 `Sys.Role.Query`，写要 `Sys.Role.Update`。
 *
 * 【⚠ 一个必须在界面上讲清楚的语义：多个角色取最宽】
 *   一个用户可能同时属于多个角色；只要其中任意一个角色配了更宽的范围，
 *   他就按最宽的那个看（与权限"任一角色授予即拥有"完全一致，这是刻意的）。
 *   代价是"给某人加一个宽范围的角色"= 扩权 —— 所以界面上要把每个角色
 *   配了什么范围显示清楚，别让它只存在于数据库里。
 */
export const roleDataScopeApi = {
  /**
   * 全部角色及其数据范围（**未配置的角色也在列表里**，`scope` 为 null）。
   *
   * 一次取回所有角色（通常十几个），前端按 roleId 对齐即可，不必按角色逐个查。
   * 权限：`Sys.Role.Query`。
   */
  async getList(): Promise<RoleDataScope[]> {
    const { data } = await http.get<RoleDataScope[]>(ENDPOINTS.roleDataScope)

    return data
  },

  /**
   * 可选的数据范围（下拉框数据源）。权限：`Sys.Role.Query`。
   *
   * 【为什么选项要问后端，而不是前端写死四个】
   *   可配置的范围由后端 `DataScopes.Configurable` 决定，而且**刻意不含**
   *   `Custom`（自定义组织集合）：各查询的 SQL 谓词按"单一根路径"生成，
   *   配了它会静默退化成"本级及下级"—— 那**比配置更宽**，属于"配了却放开了"。
   *   前端抄一份码值表，就会既抄不准、又在后端增删范围时静默不一致：
   *   表现是"下拉里少一项"或"选了一个后端不认的值"，两种都不会报编译错。
   */
  async getOptions(): Promise<CodeOption[]> {
    const { data } = await http.get<CodeOption[]>(ENDPOINTS.roleDataScopeOptions)

    return data
  },

  /**
   * 设置 / 清除某个角色的数据范围。权限：`Sys.Role.Update`。
   *
   * ⚠ `scope` 传 null = **清除配置**（后端删掉这一行，回到按组织层级推导），
   *   不是"这个字段忘了填"。界面上「恢复默认」发的就是这一条。
   */
  async set(input: SetRoleDataScope): Promise<RoleDataScope> {
    const { data } = await http.post<RoleDataScope>(ENDPOINTS.roleDataScopeSet, input)

    return data
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
    await http.delete<void>(`${ENDPOINTS.code}/${id}/category`)
  },

  async getItems(categoryId: string, onlyEnabled = false, asTree = false): Promise<CodeItem[]> {
    const { data } = await http.get<CodeItem[]>(`${ENDPOINTS.code}/items/${categoryId}`, {
      params: { onlyEnabled, asTree }
    })

    return data
  },

  /**
   * 按**类别编码**取码项（下拉框用）。
   *
   * ⚠ 参数是查询串而不是路径段：ABP 约定只把名为 id 的参数放进路径，
   *    `categoryCode` 会被当作查询参数（已用真实请求确认：
   *    `GET /api/app/code/items-by-code?categoryCode=GUARDIAN_RELATION` → 200）。
   *
   * 为什么要有它：下拉框里的"在读/休学"必须对应数据库里的码值（01/02…）。
   * 前端手写 `{ value: 'Studying', label: '在读' }` 这类选项，
   * 后端拿到的是一个它不认识的字符串 —— 过滤时会静默返回空结果，
   * 更糟的是异动类型会把 AfterStatusCode 存成 'Studying'，
   * 审批通过后**直接写进学生的学籍状态**（实测踩过，见提交说明）。
   */
  async getItemsByCode(categoryCode: string, onlyEnabled = true): Promise<CodeItemLookup[]> {
    const { data } = await http.get<CodeItemLookup[]>(`${ENDPOINTS.code}/items-by-code`, {
      params: { categoryCode, onlyEnabled }
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
    await http.delete<void>(`${ENDPOINTS.code}/${id}/item`)
  },

  /** 码值变更后清缓存（一般写操作已自动失效，这里给一个手动入口）。 */
  async refreshCache(): Promise<void> {
    await http.post(`${ENDPOINTS.code}/refresh-cache`, {})
  }
}

// ---------------------------------------------------------------------------
// 微代码（码表 / 码项）批量导入
// ---------------------------------------------------------------------------

/**
 * 微代码批量导入 —— 导出**不在这里**：导出走后台任务
 * （`exportTaskApi.run({ kind: ExportTaskKinds.CodeCsv, codeQuery })`），
 * 与组织名单、学生名单、照片打包同一条链路。
 *
 * 权限：三个方法都要求 `Sys.Code.Import`（导出要求 `Sys.Code.Export`）。
 *
 * 【这份文件唯一需要理解的约定：**一行 = 一个码项**】
 *   类别信息在每一行里**重复写**（类别编码 + 类别名称），后端模板就是这么给的：
 *   同一个类别的两行示例（EXAM_TYPE / 考试类型 / 01、02）是刻意的 ——
 *   只给一行的话，用户根本看不出"类别编码要重复写"。
 *   不做"类别一小节、码项一块"的嵌套格式：CSV 里没有嵌套，要表达嵌套就得
 *   发明缩进/分隔符约定，而那些约定在 Excel 里会被改坏。
 *   因此前端也**不提供**任何"类别 → 码项"的嵌套视图（列说明一律取 `getColumns()`）。
 *
 * 【为什么三个 URL 写成**全字面量**，不像微代码主接口那样用 `${ENDPOINTS.xxx}` 拼】
 *   与 `organizationImportApi` 同一个理由：`tools/audit-routes.mjs` 逐条核对
 *   每个调用的第一个实参，写全的路径它可以直接拿去和 swagger 的路径表比对，
 *   拼出来的得先靠它解析模板 —— 省下几个字符，换来的是路径写错时没人拦得住
 *   （表现是 404/405，而不是编译错，见文件头 ENDPOINTS 的说明）。
 *
 * ⚠ 导入**不比手工新增宽松**：后端复用 `CodeAppService.CreateCategoryAsync` /
 *   `CreateItemAsync`，所以"同类别下码值唯一"等校验走的是同一条路；
 *   反过来，已存在的类别**既不重建也不改名**（按类别编码命中后直接沿用，
 *   连「类别名称」列都不再看）—— 这是有意为之，改名会让引用它的地方
 *   看到的中文悄悄变掉。导入也不会建出树形结构：树形码表导出时就已平铺，
 *   ParentId 不在文件里（见 `CodeCsvExportBuilder` 的取舍说明）。
 *
 * ⚠ `Logs/swagger.json` 那份快照**早于这三个接口**，核对脚本会把它们报成 ✗
 *   （"后端: (无此路由)"）—— 那是快照过时，不是路径写错。
 */
export const codeImportApi = {
  /**
   * 导入模板（CSV 文本，含表头与**同一类别的两行**示例）。
   *
   * 模板里的 BOM 由后端 `Csv.WriteForExcel` 加上（Excel 不认无 BOM 的 UTF-8），
   * 前端**不再自己补一个**。
   */
  async getTemplate(): Promise<string> {
    const { data } = await http.get<string>('/api/app/code-import-export/import-template')

    return data
  },

  /**
   * 列说明（前端做字段提示用）。
   *
   * 返回类型复用学生的 `ImportColumn`：后端 `StudentImportExport` /
   * `OrganizationImportExport` / `CodeImportExport` 三个 `GetColumns` 返回的
   * 都是同一个 `ImportColumnDto`（name / required / remark），形状完全一致 ——
   * 另立一份只是多一处"后端加字段时可能忘了同步"的字段表。
   */
  async getColumns(): Promise<ImportColumn[]> {
    const { data } = await http.get<ImportColumn[]>('/api/app/code-import-export/columns')

    return data
  },

  /**
   * 批量导入。
   *
   * 逐行容错：单行失败不影响其它行，返回的 `errors[].rowIndex` 是
   * **CSV 行号**（表头是第 1 行）、`message` 是给用户照着改文件的原因，
   * 因此必须原样展示，不能汇总成一句"导入失败"。
   * `result.message` 里带**新建类别数**（码表导入会顺带建类别），要一起显示。
   */
  async import(content: string): Promise<ImportCodesResult> {
    const { data } = await http.post<ImportCodesResult>('/api/app/code-import-export/import', {
      content
    })

    return data
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

/**
 * 学生主档。
 *
 * 【为什么这里曾经只有 getList】
 * 学生只能通过 CSV 批量导入建档，界面上「新增学生」是一个没有 @click 的按钮，
 * 主档也没有编辑/删除入口 —— 而后端 `StudentAppService` 的
 * Create / Update / Delete / CheckDuplicate 四个接口一直是齐的。
 * 补齐前端这一层，缺陷才算真的修掉（按钮画出来不等于功能存在）。
 *
 * 路由逐个显式写出（不用模板拼接）：`tools/audit-routes.mjs` 只能核对
 * 字面量 URL，拼出来的路径在核对脚本里是盲区。
 */
export const studentApi = {
  async getList(query: StudentQuery): Promise<PagedResult<StudentListItem>> {
    const { data } = await http.get<PagedResult<StudentListItem>>(ENDPOINTS.student, {
      params: query
    })

    return data
  },

  async get(id: string): Promise<StudentDetail> {
    const { data } = await http.get<StudentDetail>(`${ENDPOINTS.student}/${id}`)

    return data
  },

  async create(input: CreateUpdateStudent): Promise<StudentDetail> {
    const { data } = await http.post<StudentDetail>(ENDPOINTS.student, input)

    return data
  },

  async update(id: string, input: CreateUpdateStudent): Promise<StudentDetail> {
    const { data } = await http.put<StudentDetail>(`${ENDPOINTS.student}/${id}`, input)

    return data
  },

  /** 软删除（可在回收站恢复）。后端会连带级联软删 6 张子表与留痕。 */
  async remove(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.student}/${id}`)
  },

  /**
   * 重复学籍检测（身份证摘要 + 姓名 + 出生日期，跨校跨区）。
   *
   * 后端在写入前也会查一次并拒绝，这里先查一次是为了**在提交前**把
   * "这个人已经有学籍了"讲清楚，而不是让用户填完一整张表单再吃一个报错。
   */
  async checkDuplicate(input: CheckStudentDuplicate): Promise<StudentDuplicateCheckResult> {
    const { data } = await http.post<StudentDuplicateCheckResult>(
      `${ENDPOINTS.student}/check-duplicate`,
      input
    )

    return data
  }
}

// ---------------------------------------------------------------------------
// 学生批量导入导出
// ---------------------------------------------------------------------------

export const studentImportApi = {
  /** 导入模板（CSV 文本，含表头与一行示例）。 */
  async getTemplate(): Promise<string> {
    const { data } = await http.get<string>(`${ENDPOINTS.studentImport}/import-template`)

    return data
  },

  /** 列说明（前端做字段提示用）。 */
  async getColumns(): Promise<ImportColumn[]> {
    const { data } = await http.get<ImportColumn[]>(`${ENDPOINTS.studentImport}/columns`)

    return data
  },

  /**
   * 批量导入。
   *
   * 逐行容错：返回成功数与**每一行的失败原因**，因此前端必须把错误明细展示出来 ——
   * 只告诉用户"导入失败"等于让他拿着几百行数据自己找问题。
   */
  async import(content: string): Promise<ImportStudentsResult> {
    const { data } = await http.post<ImportStudentsResult>(`${ENDPOINTS.studentImport}/import`, {
      content
    })

    return data
  }
}

// ---------------------------------------------------------------------------
// 组织（学校名录）批量导入
// ---------------------------------------------------------------------------

/**
 * 组织（省 / 市 / 县 / 校）批量导入 —— 导出**不在这里**：
 * 导出走后台任务（`exportTaskApi.run({ kind: ExportTaskKinds.OrganizationCsv })`），
 * 与学生名单、照片打包同一条链路（上万所学校的 CSV 不该在请求线程里拼）。
 *
 * 权限：三个方法都要求 `Sys.Org.Import`（导出要求 `Sys.Org.Export`）。
 *
 * 【为什么三个 URL 写成**全字面量**，不像学生那套用 `${ENDPOINTS.xxx}` 拼】
 *   `tools/audit-routes.mjs` 是逐条核对每个调用的第一个实参的：
 *   写全的路径它能直接拿去和 swagger 的路径表比对，拼出来的得先靠它解析模板。
 *   这类"少写几个字符"换来的代价，是路径写错时没人拦得住
 *   （表现还是 404/405，而不是编译错，见文件头 ENDPOINTS 的说明）。
 *
 * ⚠ 组织导入**不比手工新增宽松**：后端复用 `OrgAppService.CreateAsync`，
 *   所以编码唯一、层级（省 → 市 → 县 → 校）都必须对 —— 层级错会让
 *   数据范围与审核级别**静默算错**，这正是导入最该防的。
 *
 * ⚠ 「组织类型」列在后端由 `OrgTypes.TryParse` 解析，**接受中文别名**
 *   （省厅 / 地市 / 县区 / 学校，也接受 Province / City / …）。
 *   因此前端**不提供、也不校验**一份写死的类型清单：列名与说明一律取
 *   `getColumns()`，否则后端改名或增加别名时，前端会先一步把合法文件拦下。
 *
 * ⚠ `Logs/swagger.json` 那份快照**早于这三个接口**，核对脚本会把它们报成 ✗
 *   （"后端: (无此路由)"）—— 那是快照过时，不是路径写错。
 */
export const organizationImportApi = {
  /** 导入模板（CSV 文本，含表头与四行示例，覆盖四个层级；带 UTF-8 BOM）。 */
  async getTemplate(): Promise<string> {
    const { data } = await http.get<string>('/api/app/organization-import-export/import-template')

    return data
  },

  /**
   * 列说明（前端做字段提示用）。
   *
   * 返回类型复用学生的 `ImportColumn`：后端两个接口返回的都是同一个
   * `ImportColumnDto`（name / required / remark），形状完全一致 ——
   * 另立一份只是多一处"后端加字段时可能忘了同步"的字段表。
   */
  async getColumns(): Promise<ImportColumn[]> {
    const { data } = await http.get<ImportColumn[]>('/api/app/organization-import-export/columns')

    return data
  },

  /**
   * 批量导入。
   *
   * 逐行容错：单行失败不影响其它行，返回的 `errors[].rowIndex` 是
   * **Excel 行号**（表头为第 1 行）、`message` 是给用户照着改文件的原因，
   * 因此必须原样展示，不能汇总成一句"导入失败"。
   * 行序不用用户操心：后端按「省 → 市 → 县 → 校」重排后再处理。
   */
  async import(content: string): Promise<ImportOrganizationsResult> {
    const { data } = await http.post<ImportOrganizationsResult>(
      '/api/app/organization-import-export/import',
      { content }
    )

    return data
  }
}

// ---------------------------------------------------------------------------
// 照片
// ---------------------------------------------------------------------------

/**
 * 学生档案子表。
 *
 * 六张子表的接口形状完全一致（列表 / 新增 / 修改 / 删除），因此**刻意写成平铺的显式方法**而不是
 * 工厂函数：显式写出来的每个 URL 都能被 `tools/audit-routes.mjs` 逐个核对，
 * 而用 `${action}` 拼出来的 URL 是核对不了的（脚本只能看到模板变量）。
 * 这类"少写几十行"换来的代价，是路由写错时没人发现。
 *
 * 注意 delete 也带了 `<void>` 泛型：核对脚本按 `http.<verb><T>(...)` 匹配，
 * 不带泛型的调用会被静默跳过。
 */
export const studentProfileApi = {
  // ---- 监护人 ----

  async getGuardians(studentId: string): Promise<Guardian[]> {
    const { data } = await http.get<Guardian[]>(`${ENDPOINTS.studentProfile}/guardians/${studentId}`)

    return data
  },

  async createGuardian(studentId: string, input: GuardianInput): Promise<Guardian> {
    const { data } = await http.post<Guardian>(`${ENDPOINTS.studentProfile}/guardian/${studentId}`, input)

    return data
  },

  async updateGuardian(id: string, input: GuardianInput): Promise<Guardian> {
    const { data } = await http.put<Guardian>(`${ENDPOINTS.studentProfile}/${id}/guardian`, input)

    return data
  },

  async deleteGuardian(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/guardian`)
  },

  // ---- 学习简历 ----

  async getResumes(studentId: string): Promise<Resume[]> {
    const { data } = await http.get<Resume[]>(`${ENDPOINTS.studentProfile}/resumes/${studentId}`)

    return data
  },

  async createResume(studentId: string, input: ResumeInput): Promise<Resume> {
    const { data } = await http.post<Resume>(`${ENDPOINTS.studentProfile}/resume/${studentId}`, input)

    return data
  },

  async updateResume(id: string, input: ResumeInput): Promise<Resume> {
    const { data } = await http.put<Resume>(`${ENDPOINTS.studentProfile}/${id}/resume`, input)

    return data
  },

  async deleteResume(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/resume`)
  },

  // ---- 奖励 ----

  async getRewards(studentId: string): Promise<Reward[]> {
    const { data } = await http.get<Reward[]>(`${ENDPOINTS.studentProfile}/rewards/${studentId}`)

    return data
  },

  async createReward(studentId: string, input: RewardInput): Promise<Reward> {
    const { data } = await http.post<Reward>(`${ENDPOINTS.studentProfile}/reward/${studentId}`, input)

    return data
  },

  async updateReward(id: string, input: RewardInput): Promise<Reward> {
    const { data } = await http.put<Reward>(`${ENDPOINTS.studentProfile}/${id}/reward`, input)

    return data
  },

  async deleteReward(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/reward`)
  },

  // ---- 处分 ----

  async getPunishments(studentId: string): Promise<Punishment[]> {
    const { data } = await http.get<Punishment[]>(`${ENDPOINTS.studentProfile}/punishments/${studentId}`)

    return data
  },

  async createPunishment(studentId: string, input: PunishmentInput): Promise<Punishment> {
    const { data } = await http.post<Punishment>(`${ENDPOINTS.studentProfile}/punishment/${studentId}`, input)

    return data
  },

  async updatePunishment(id: string, input: PunishmentInput): Promise<Punishment> {
    const { data } = await http.put<Punishment>(`${ENDPOINTS.studentProfile}/${id}/punishment`, input)

    return data
  },

  async deletePunishment(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/punishment`)
  },

  // ---- 资助 ----

  async getSubsidies(studentId: string): Promise<Subsidy[]> {
    const { data } = await http.get<Subsidy[]>(`${ENDPOINTS.studentProfile}/subsidies/${studentId}`)

    return data
  },

  async createSubsidy(studentId: string, input: SubsidyInput): Promise<Subsidy> {
    const { data } = await http.post<Subsidy>(`${ENDPOINTS.studentProfile}/subsidy/${studentId}`, input)

    return data
  },

  async updateSubsidy(id: string, input: SubsidyInput): Promise<Subsidy> {
    const { data } = await http.put<Subsidy>(`${ENDPOINTS.studentProfile}/${id}/subsidy`, input)

    return data
  },

  async deleteSubsidy(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/subsidy`)
  },

  // ---- 体质健康（体检）----

  async getPhysicals(studentId: string): Promise<Physical[]> {
    const { data } = await http.get<Physical[]>(`${ENDPOINTS.studentProfile}/physicals/${studentId}`)

    return data
  },

  async createPhysical(studentId: string, input: PhysicalInput): Promise<Physical> {
    const { data } = await http.post<Physical>(`${ENDPOINTS.studentProfile}/physical/${studentId}`, input)

    return data
  },

  async updatePhysical(id: string, input: PhysicalInput): Promise<Physical> {
    const { data } = await http.put<Physical>(`${ENDPOINTS.studentProfile}/${id}/physical`, input)

    return data
  },

  async deletePhysical(id: string): Promise<void> {
    await http.delete<void>(`${ENDPOINTS.studentProfile}/${id}/physical`)
  }
}

/**
 * 打印（学籍卡 / 花名册）。
 *
 * ⚠ 原来的 `studentPrintApi`（同步返回 Blob）已**删除**：渲染 PDF 是 CPU 活，
 *   同步做会把请求线程占满，上千人的花名册尤其明显。
 *   改用 `exportTaskApi.run({ kind: 'StudentCardPdf' | 'StudentRosterPdf' })`
 *   提交任务，完成后 `exportTaskApi.download(id)` 取回同样的 PDF。
 */

// ---------------------------------------------------------------------------
// 导出 / 打印后台任务
// ---------------------------------------------------------------------------

/** 轮询间隔与总超时。超时不是"任务失败"，只是前端不再等（任务仍在后台跑）。 */
const EXPORT_POLL_INTERVAL_MS = 1500
const EXPORT_POLL_TIMEOUT_MS = 5 * 60 * 1000

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export const exportTaskApi = {
  /** 提交任务（入队）。服务端会做权限校验与参数快照，这里只负责把条件传过去。 */
  async create(input: CreateExportTask): Promise<ExportTask> {
    const { data } = await http.post<ExportTask>(ENDPOINTS.exportTask, input)

    return data
  },

  async get(id: string): Promise<ExportTask> {
    const { data } = await http.get<ExportTask>(`${ENDPOINTS.exportTask}/${id}`)

    return data
  },

  /** 我发起过的任务（后端恒定按发起人过滤，看不到别人的）。 */
  async list(query: ExportTaskQuery = {}): Promise<PagedResult<ExportTask>> {
    const { data } = await http.get<PagedResult<ExportTask>>(ENDPOINTS.exportTask, { params: query })

    return data
  },

  async cancel(id: string): Promise<ExportTask> {
    const { data } = await http.post<ExportTask>(`${ENDPOINTS.exportTask}/${id}/cancel`, {})

    return data
  },

  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.exportTask}/${id}`)
  },

  /**
   * 下载产物。
   *
   * **必须用 axios（http 实例）取 Blob，不能改成 `window.open(url)`**：
   * 下载接口要求 Bearer 令牌，而浏览器直接导航不会带 Authorization 头，
   * 那样必然 401 —— 而且表现为"点下载没反应"或跳到一页 JSON 错误，很难往令牌上想。
   */
  async download(id: string): Promise<Blob> {
    const { data } = await http.get<Blob>(`${ENDPOINTS.exportTaskDownload}/${id}/download`, {
      responseType: 'blob'
    })

    return data
  },

  /**
   * 轮询到终态。
   *
   * 三个必须有的终止条件（少一个就会留下一个永远在转的定时器）：
   *   1) 状态进入终态；
   *   2) 总超时；
   *   3) `shouldStop()` —— 页面卸载/组件销毁时由调用方置位。
   *
   * 注意超时**不抛错**：任务还在后台跑，抛错会把"还在排队"误报成"失败"。
   */
  async waitUntilDone(
    id: string,
    options: {
      intervalMs?: number
      timeoutMs?: number
      shouldStop?: () => boolean
      onProgress?: (task: ExportTask) => void
    } = {}
  ): Promise<ExportTask> {
    const intervalMs = options.intervalMs ?? EXPORT_POLL_INTERVAL_MS
    const deadline = Date.now() + (options.timeoutMs ?? EXPORT_POLL_TIMEOUT_MS)

    for (;;) {
      const task = await this.get(id)

      options.onProgress?.(task)

      if (task.status === ExportTaskStatuses.Succeeded || task.status === ExportTaskStatuses.Failed
        || task.status === ExportTaskStatuses.Canceled || task.status === ExportTaskStatuses.Expired) {
        return task
      }

      if (options.shouldStop?.() || Date.now() >= deadline) {
        return task
      }

      await sleep(intervalMs)
    }
  },

  /**
   * 提交并等到终态。四个调用点都用它，避免各自写一遍轮询。
   *
   * 失败一律抛错（带上后端给的原因），成功返回可下载的任务 ——
   * 调用方只需要决定文件名与提示语。
   */
  async run(
    input: CreateExportTask,
    options: {
      shouldStop?: () => boolean
      onProgress?: (task: ExportTask) => void
    } = {}
  ): Promise<ExportTask> {
    const created = await this.create(input)

    const done = await this.waitUntilDone(created.id, options)

    if (done.status === ExportTaskStatuses.Succeeded && done.canDownload) {
      return done
    }

    throw new Error(describeTaskFailure(done))
  }
}

/** 把任务终态翻译成一句人话。 */
export function describeTaskFailure(task: ExportTask): string {
  switch (task.status) {
    case ExportTaskStatuses.Failed:
      return task.failureReason || '任务执行失败'
    case ExportTaskStatuses.Canceled:
      return '任务已取消'
    case ExportTaskStatuses.Expired:
      return '产物已过保留期并被清理，请重新导出'
    case ExportTaskStatuses.Succeeded:
      return '产物不可下载（可能已被清理）'
    default:
      // 走到这里说明是前端等超时了，而任务其实还在后台排队/执行
      return `${task.kindName}仍在后台执行（当前状态：${task.status}），稍后可在「导出任务」里查看并下载`
  }
}

/**
 * 截断提示。返回 null 表示没有被截断。
 *
 * 单独抽出来是因为它必须**在每个调用点都被展示**：名单类产出少人
 * 是最不能接受的失败方式，而后端已经如实算出"少了几行"，
 * 前端如果因为"成功"就只弹一个绿色提示，那份如实交代就白做了。
 */
export function describeTruncation(task: ExportTask): string | null {
  if (!task.isTruncated) {
    return null
  }

  const missing = Math.max(0, task.total - task.rowCount)

  return `${task.kindName}已生成，但**只包含 ${task.rowCount} 条（共 ${task.total} 条，少 ${missing} 条）**。${
    task.message ?? ''
  }`
}

/**
 * 用 Blob URL 触发下载。
 *
 * 集中到这里（原来在三个 .vue 里各写了一遍）：`URL.revokeObjectURL` 漏掉一次
 * 就会一直占着这份 Blob 的内存，而导出件动辄几十 MB。
 */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')

  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

/**
 * 学籍变更流水（只读）。
 *
 * 没有写入接口是**刻意的**：能被改的审计日志等于没有审计。
 * 流水由后端在业务操作的工作单元里写入。
 */
export const studentChangeLogApi = {
  /** 某个学生的变更历史（按时间倒序）。 */
  async getStudentLogs(studentId: string, maxResultCount = 200): Promise<StudentChangeLog[]> {
    const { data } = await http.get<{ items: StudentChangeLog[] }>(
      `${ENDPOINTS.studentChangeLog}/student-logs/${studentId}`,
      { params: { maxResultCount } }
    )

    return data.items ?? []
  },

  /** 全局查询（按动作 / 时间过滤），受数据范围约束。 */
  async getList(query: StudentChangeLogQuery): Promise<PagedResult<StudentChangeLog>> {
    const { data } = await http.get<PagedResult<StudentChangeLog>>(ENDPOINTS.studentChangeLog, {
      params: query
    })

    return data
  }
}

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

  /**
   * 批量删除照片。
   *
   * ⚠ 动词必须是 DELETE：后端 `DeleteManyAsync` 被 ABP 约定注册成
   * `DELETE /api/app/photo/many`（集合参数走请求体）。原先这里写的 `http.post`
   * 在运行时会直接 405 —— 而这只有真的发一次请求才会暴露，
   * 因此仓库里保留了 `.tools/audit-routes.mjs` 做全量核对。
   */
  async deleteMany(ids: string[]): Promise<number> {
    const { data } = await http.delete<number>(ENDPOINTS.photoDeleteMany, { data: ids })

    return data
  },

  /**
   * 取学生照片的**缩略图**（列表页用）。
   *
   * 为什么不用 `<img src="...">` 直接指向接口：接口要求 Bearer 令牌，
   * 而浏览器发图片请求时不会带上 Authorization 头。
   * 因此统一用 axios 取回 Blob，再用 `URL.createObjectURL` 交给 `<img>`；
   * 用完必须 `URL.revokeObjectURL`，否则整页浏览下来会一直占着内存。
   */
  async getThumbnail(studentId: string): Promise<Blob> {
    const { data } = await http.get<Blob>(`${ENDPOINTS.photoFile}/${studentId}/thumbnail`, {
      responseType: 'blob'
    })

    return data
  },

  /** 取学生照片的**原图**（查看/核对用）。 */
  async getContent(studentId: string): Promise<Blob> {
    const { data } = await http.get<Blob>(`${ENDPOINTS.photoFile}/${studentId}/content`, {
      responseType: 'blob'
    })

    return data
  },

  /**
   * 打包导出照片（zip，内含「照片清单.csv」与可选的「缺照片名单.csv」）。
   *
   * ⚠ 原来的 `exportPhotos`（同步返回 Blob）已**删除**：一次要读上千个文件再压缩，
   *   同步做会把请求线程占满，而且 zip 全在内存里。
   *   改用 `exportTaskApi.run({ kind: 'PhotoZip', photoQuery: {...} })`。
   */

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

// ---------------------------------------------------------------------------
// 异动附件
// ---------------------------------------------------------------------------

/**
 * 异动证明材料（附件）。
 *
 * 权限沿用申请单本身那两条：**看**要 `Chg.Apply.Query`、**传/删**要 `Chg.Apply.Update`
 * （后端 `ChangeAttachmentController` 就是这么挂的）。刻意没有为附件新开权限码 ——
 * "能看这张申请单"与"能看它的材料"本来就是同一件事，
 * 多两个勾选项只会让授权的人多一次猜。
 */
export const changeAttachmentApi = {
  /**
   * 某张申请单的附件列表（按上传时间正序）。
   *
   * 只返回元数据，不含存储路径 —— 下载一律走附件 Id（见后端 DTO 的说明）。
   */
  async getList(applyId: string): Promise<ChangeAttachment[]> {
    const { data } = await http.get<ChangeAttachment[]>(ENDPOINTS.changeAttachment, {
      params: { applyId }
    })

    return data
  },

  /**
   * 上传附件（multipart）。
   *
   * 用 multipart 而不是 base64 JSON：一份扫描件 base64 之后要胖三分之一，
   * 而县级学校的上行带宽是最紧的一环（与照片批量上传同一个理由）。
   *
   * `timeout: 0`：10MB 的扫描件在慢网络上可能超过默认超时，
   * 超时中断会让用户以为"上传失败"，而服务端其实已经收下并入库了。
   */
  async upload(applyId: string, file: File): Promise<ChangeAttachment> {
    const form = new FormData()

    form.append('file', file)
    form.append('applyId', applyId)

    const { data } = await http.post<ChangeAttachment>(`${ENDPOINTS.changeAttachment}/upload`, form, {
      timeout: 0
    })

    return data
  },

  /**
   * 下载附件。
   *
   * ⚠ 必须走 axios 取 Blob，**不能**用 `<a href>` / `window.open` 直接指向接口：
   *   接口要 Bearer 令牌，浏览器自己发起的导航请求不带 Authorization 头，
   *   拿回来的是登录页 HTML（或 401），用户看到的是"下载下来一个打不开的文件"。
   *   与照片缩略图、导出产物下载是同一条规矩。
   */
  async download(id: string): Promise<Blob> {
    const { data } = await http.get<Blob>(`${ENDPOINTS.changeAttachment}/${id}/download`, {
      responseType: 'blob'
    })

    return data
  },

  /** 删除附件（记录与存储里的文件一起删）。 */
  async remove(id: string): Promise<void> {
    await http.delete(`${ENDPOINTS.changeAttachment}/${id}`)
  }
}

// ---------------------------------------------------------------------------
// 统计报表
// ---------------------------------------------------------------------------

/**
 * 统计报表接口。
 *
 * 【为什么单独一个对象而不是塞进 studentApi】
 * 权限是两套（`Rpt.*` 与 `Stu.*`）：能看名单不等于能看汇总。
 * 混在一起写，调用点就会分不清"这个请求要哪个权限"，
 * 于是出现"按钮按 Stu 权限显示、接口却回 403"这种最难解释的失败。
 *
 * 【返回值的两个坑，调用方必须处理】
 *   1) `isTruncated`：分组数达到上限（200）时后端只返回前 200 组，
 *      界面上"只显示前 200 所"与"一共就 200 所"长得一模一样，**必须显式提示**；
 *   2) `item.name` 可能为 null：码值维度（学籍状态/性别/异动状态/有无照片）
 *      的名称要由前端用码表翻译，不能直接显示 key（那是 "01"、"true" 这种东西）。
 *   3) `scopeDescription`：本次统计实际用的数据范围。县区用户看"全市汇总"时
 *      后端会**收敛**到他的范围，不说明的话用户会以为系统算错了。
 */
export const reportApi = {
  /** 学生统计。 */
  async getStudentStatistics(query: StudentStatisticsQuery): Promise<StatisticResult> {
    const { data } = await http.get<StatisticResult>(`${ENDPOINTS.report}/student-statistics`, {
      params: query
    })

    return data
  },

  /**
   * 异动统计。
   *
   * `from` / `to` 传 `yyyy-MM-dd`：后端会**把上界补到当天 23:59:59**
   * （界面上写着"含当天"，用 00:00 比较会把当天的申请全漏掉）。
   */
  async getChangeStatistics(query: ChangeStatisticsQuery): Promise<StatisticResult> {
    const { data } = await http.get<StatisticResult>(`${ENDPOINTS.report}/change-statistics`, {
      params: query
    })

    return data
  }
}

// ---------------------------------------------------------------------------
// 权限委派（ROADMAP 第 19 条）
// ---------------------------------------------------------------------------

/**
 * 权限委派：把自己的权限**临时借给**同事（典型场景是审核人休假）。
 *
 * 【为什么这里没有任何权限码、也不做任何权限门禁】
 *   委派是"用我自己的权限"：后端用**子集约束**（只能借出自己确实拥有的）
 *   + **归属约束**（只有借出人本人或管理员能撤销）来管，
 *   刻意不额外定义一个 `Sys.Delegation.*`（见后端 DTO 里的说明）。
 *   因此**任何登录用户**都能管理**自己**的委派 —— 前端不要按 `Sys.User.Query`
 *   之类的管理员权限去显隐入口：一个持有「异动审核」的普通教师可能一个管理菜单
 *   都没有，而这正是他最需要这个功能的场景。
 *
 * 【四个 URL 写成全字面量，不用 `${ENDPOINTS.xxx}` 拼】
 *   与 `organizationImportApi` / `codeImportApi` 同一个理由：
 *   `tools/audit-routes.mjs` 逐条核对每个调用的第一个实参，写全的路径它可以直接
 *   拿去和 swagger 的路径表比对，拼出来的得先靠它解析模板 —— 省下几个字符，
 *   换来的是路径写错时没人拦得住（表现是 404/405，而不是编译错）。
 *
 *   路径按 `PermissionDelegationAppService` 的 ABP 约定推出，**动词必须回 swagger 核对**：
 *     - `GetListAsync()`                   → GET  /api/app/permission-delegation
 *     - `GetDelegatablePermissionsAsync()` → GET  /api/app/permission-delegation/delegatable-permissions
 *     - `CreateAsync(input)`               → POST /api/app/permission-delegation
 *     - `RevokeAsync(id)`                  → POST /api/app/permission-delegation/{id}/revoke
 *   最后一条尤其要注意：`RevokeAsync` 不以 `Get` 开头，ABP 约定注册成 **POST**，
 *   写成 PUT 会 405，而 405 比 404 更难往"动词写错了"上想。
 *
 * ⚠ `Logs/swagger.json` 那份快照**早于这四个接口**，核对脚本会把它们报成 ✗
 *   （"后端: (无此路由)"）—— 那是快照过时，不是路径写错。
 */
export const permissionDelegationApi = {
  /**
   * 与我相关的委派：**我借出去的 + 别人借给我的**（后端恒定按当前登录人过滤，
   * 看不到不相干的人）。已撤销 / 已到期的行也在列表里 —— 台账要能回看。
   */
  async getList(): Promise<PermissionDelegation[]> {
    const { data } = await http.get<PermissionDelegation[]>('/api/app/permission-delegation')

    return data
  },

  /**
   * 我可以委派出去的权限（我拥有的 ∩ 允许委派的），给下拉框用。
   *
   * 下拉框的数据源**只能是它**：系统管理类（`Sys.*`）、导出类（`*.Export`）、
   * 照片存储补偿这类运维动作由后端判定为不可委派，前端不重复一遍规则。
   */
  async getDelegatablePermissions(): Promise<DelegatablePermission[]> {
    const { data } = await http.get<DelegatablePermission[]>(
      '/api/app/permission-delegation/delegatable-permissions'
    )

    return data
  },

  /** 新建一条委派（立即生效）。 */
  async create(input: CreatePermissionDelegation): Promise<PermissionDelegation> {
    const { data } = await http.post<PermissionDelegation>('/api/app/permission-delegation', input)

    return data
  },

  /**
   * 撤销一条委派（只有借出人本人或管理员可以）。
   *
   * 后端"回收授权"时是**有条件的**：同一个人同一条权限若还有别的生效委派在，
   * 用户级授权不会被收回（否则撤一条会把另一条一起弄失效）。
   */
  async revoke(id: string): Promise<PermissionDelegation> {
    const { data } = await http.post<PermissionDelegation>(
      `/api/app/permission-delegation/${id}/revoke`,
      {}
    )

    return data
  }
}
