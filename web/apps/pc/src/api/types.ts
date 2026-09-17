/**
 * 与后端 DTO 一一对应的类型定义。
 *
 * 为什么手写而不是从 Swagger 自动生成：
 *   后端当前还在快速演进（P1~P4），自动生成的代码每次都会大面积 diff，
 *   code review 时真正的改动会被淹没。等接口稳定后（P5 之后）再切到
 *   `openapi-typescript` 生成，届时这份文件会被替换掉。
 *
 * 约定：字段名与后端 DTO 完全一致（后端序列化为 camelCase）。
 */

/** 统一分页返回。 */
export interface PagedResult<T> {
  totalCount: number
  items: T[]
}

/** 统一分页入参。 */
export interface PagedRequest {
  skipCount?: number
  maxResultCount?: number
  sorting?: string
}

// ---------------------------------------------------------------------------
// 身份与权限
// ---------------------------------------------------------------------------

/** 菜单项（对应后端 MenuItemDto）。 */
export interface MenuItem {
  code: string
  name: string
  path?: string | null
  component?: string | null
  icon?: string | null
  isExternal: boolean
  /** 该模块下当前用户拥有的操作权限码，如 ["Stu.Student.Create"] */
  actions: string[]
}

/** 菜单分组（对应一个子系统）。 */
export interface MenuGroup {
  code: string
  name: string
  icon?: string | null
  children: MenuItem[]
}

/** 当前登录用户的完整上下文 —— 前端启动时只调这一个接口。 */
export interface MyProfile {
  userId: string
  userName: string
  realName?: string | null
  email?: string | null
  phoneNumber?: string | null
  orgId?: string | null
  orgName?: string | null
  orgType?: string | null
  orgPathCode?: string | null
  roles: string[]
  permissions: string[]
  menus: MenuGroup[]
}

/** OpenIddict 令牌响应。 */
export interface TokenResponse {
  access_token: string
  refresh_token?: string
  token_type: string
  expires_in: number
  scope?: string
}

// ---------------------------------------------------------------------------
// 组织
// ---------------------------------------------------------------------------

export const OrgTypes = {
  Province: 'Province',
  City: 'City',
  County: 'County',
  School: 'School'
} as const

export type OrgType = (typeof OrgTypes)[keyof typeof OrgTypes]

export interface OrgDto {
  id: string
  parentId?: string | null
  orgType: string
  orgTypeName?: string | null
  code: string
  name: string
  shortName?: string | null
  pathCode?: string | null
  level: number
  sort: number
  isEnabled: boolean
  children?: OrgDto[]
}

// ---------------------------------------------------------------------------
// 学生
// ---------------------------------------------------------------------------

export interface StudentListItem {
  id: string
  studentNo: string
  nationalStudentNo?: string | null
  classStudentNo?: string | null
  name: string
  genderCode: string
  genderName?: string | null
  birthDate: string
  idCardMasked?: string | null
  schoolOrgId: string
  schoolName?: string | null
  academicYear: string
  gradeId?: string | null
  gradeName?: string | null
  classId?: string | null
  className?: string | null
  studentStatusCode: string
  studentStatusName?: string | null
  photoPath?: string | null
  hasRareCharacter: boolean
}

export interface StudentQuery extends PagedRequest {
  schoolOrgId?: string
  academicYear?: string
  gradeId?: string
  classId?: string
  studentStatusCode?: string
  genderCode?: string
  keyword?: string
}

// ---------------------------------------------------------------------------
// 照片
// ---------------------------------------------------------------------------

export const PhotoStatuses = {
  Pending: 'Pending',
  Normal: 'Normal',
  FallbackPending: 'FallbackPending',
  Failed: 'Failed'
} as const

export interface PhotoDto {
  id: string
  studentId: string
  studentNo?: string | null
  studentName?: string | null
  schoolOrgId: string
  relativePath: string
  thumbnailPath?: string | null
  fileHash: string
  fileSize: number
  width: number
  height: number
  status: string
  statusText: string
  batchId: string
  uploadedTime: string
  uploadedByName?: string | null
  errorMessage?: string | null
  creationTime: string
}

export interface PhotoQuery extends PagedRequest {
  studentId?: string
  schoolOrgId?: string
  batchId?: string
  status?: string
  keyword?: string
}

export interface MissingPhotoStudent {
  studentId: string
  studentNo?: string | null
  name?: string | null
  schoolOrgId: string
  schoolName?: string | null
  gradeName?: string | null
  className?: string | null
  status?: string | null
}

export interface MissingPhotoQuery extends PagedRequest {
  schoolOrgId?: string
  gradeId?: string
  classId?: string
  keyword?: string
}

export interface PhotoUploadItemResult {
  studentId: string
  studentNo?: string | null
  success: boolean
  skipped: boolean
  degraded: boolean
  photoId?: string | null
  relativePath?: string | null
  errorMessage?: string | null
}

export interface PhotoUploadResult {
  batchId: string
  total: number
  succeeded: number
  skipped: number
  degraded: number
  failed: number
  items: PhotoUploadItemResult[]
  message: string
}

export interface PhotoStorageHealth {
  primaryName: string
  primaryAvailable: boolean
  fallbackName: string
  fallbackAvailable: boolean
  circuitState: string
  circuitStateText: string
  fallbackPendingCount: number
  failedCount: number
  checkedTime: string
}

export interface PhotoCompensationResult {
  scanned: number
  replayed: number
  stillPending: number
  failed: number
  aborted: boolean
  message: string
}
