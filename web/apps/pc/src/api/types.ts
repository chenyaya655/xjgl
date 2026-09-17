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

// ---------------------------------------------------------------------------
// 异动（可配置审批流）
// ---------------------------------------------------------------------------

/** 组织解析器：节点「审的是哪一级组织」。前端下拉框用这些值。 */
export const ChangeOrgResolvers = {
  ApplicantSchool: 'ApplicantSchool',
  StudentSchool: 'StudentSchool',
  TargetSchool: 'TargetSchool',
  SourceSchool: 'SourceSchool',
  TargetCounty: 'TargetCounty',
  TargetCity: 'TargetCity',
  SourceCounty: 'SourceCounty',
  SourceCity: 'SourceCity',
  Province: 'Province'
} as const

/** 解析器的中文名（与后端 ChangeOrgResolvers.GetDisplayName 一致）。 */
export const ChangeOrgResolverNames: Record<string, string> = {
  ApplicantSchool: '发起人所在学校',
  StudentSchool: '学生当前学校',
  TargetSchool: '转入学校',
  SourceSchool: '转出学校',
  TargetCounty: '转入学校所在县区',
  TargetCity: '转入学校所在地市',
  SourceCounty: '转出学校所在县区',
  SourceCity: '转出学校所在地市',
  Province: '省级教育行政部门'
}

/** 合并策略：同一组织被多个节点解析到时怎么处理。 */
export const ChangeMergeStrategies = {
  MergeToLast: 'MergeToLast',
  MergeToFirst: 'MergeToFirst',
  NoMerge: 'NoMerge'
} as const

export const ChangeMergeStrategyNames: Record<string, string> = {
  MergeToLast: '合并到最后一个节点（上级终审，推荐）',
  MergeToFirst: '合并到第一个节点',
  NoMerge: '不合并（同一组织出现多个待办）'
}

/** 审核级别。 */
export const ApproveLevels = {
  School: 'School',
  County: 'County',
  City: 'City',
  Province: 'Province'
} as const

export const ApproveLevelNames: Record<string, string> = {
  School: '校级',
  County: '县区级',
  City: '地市级',
  Province: '省级'
}

/** 异动类型。 */
export interface ChangeType {
  id: string
  code: string
  name: string
  categoryCode?: string | null
  beforeStatusCode?: string | null
  afterStatusCode?: string | null
  /** 最高审核级别，超过这个级别的节点在实例化时会被截断 */
  maxApproveLevel: string
  maxApproveLevelName: string
  needPeerSchoolConfirm: boolean
  affectGrade: boolean
  affectClass: boolean
  allowCrossSchool: boolean
  applyWindowStart?: string | null
  applyWindowEnd?: string | null
  allowCrossAcademicYear: boolean
  maxApplyTimesPerYear: number
  gradeScope?: string | null
  requireAttachment: boolean
  flowTemplateId?: string | null
  /** 是否已有发布版本的流程；没有就不允许提交申请 */
  hasPublishedFlow: boolean
  sort: number
  isEnabled: boolean
  remark?: string | null
}

export interface CreateUpdateChangeType {
  code: string
  name: string
  categoryCode?: string | null
  beforeStatusCode?: string | null
  afterStatusCode?: string | null
  maxApproveLevel: string
  needPeerSchoolConfirm: boolean
  affectGrade: boolean
  affectClass: boolean
  allowCrossSchool: boolean
  applyWindowStart?: string | null
  applyWindowEnd?: string | null
  allowCrossAcademicYear: boolean
  maxApplyTimesPerYear: number
  gradeScope?: string | null
  requireAttachment: boolean
  sort: number
  isEnabled: boolean
  remark?: string | null
}

/** 流程节点（只读，来自服务端）。 */
export interface ChangeFlowNode {
  id: string
  templateId: string
  seq: number
  nodeName: string
  orgResolver: string
  orgResolverName: string
  roleCode: string
  mergeStrategy: string
  allowReject: boolean
  allowDelegate: boolean
  timeoutDays: number
  isEnabled: boolean
  remark?: string | null
}

/** 流程节点（提交用）。 */
export interface CreateUpdateFlowNode {
  seq: number
  nodeName: string
  orgResolver: string
  roleCode: string
  mergeStrategy: string
  allowReject: boolean
  allowDelegate: boolean
  timeoutDays: number
}

export interface ChangeFlowTemplate {
  id: string
  changeTypeId: string
  name: string
  version: number
  status: string
  statusName: string
  publishedTime?: string | null
  remark?: string | null
  nodes: ChangeFlowNode[]
}

export interface SaveFlowTemplate {
  changeTypeId: string
  name: string
  remark?: string | null
  nodes: CreateUpdateFlowNode[]
}

/** 流程预览结果 —— 「业务人员改完流程能立刻看到实际会走几步」。 */
export interface PreviewFlowResult {
  changeTypeId: string
  changeTypeCode: string
  changeTypeName: string
  maxApproveLevel: string
  maxApproveLevelName: string
  templateVersion: number
  /** 模板里配了几个节点 */
  configuredNodeCount: number
  /** 按本次输入实例化后实际剩几步 */
  actualStepCount: number
  nodes: PreviewFlowNode[]
  warnings: string[]
}

export interface PreviewFlowNode {
  seq: number
  nodeName: string
  orgId: string
  orgName: string
  orgResolver: string
  orgResolverName: string
  roleCode: string
  approveLevel: string
  approveLevelName: string
  /** 这个节点是由哪几个配置节点合并来的（长度 > 1 说明发生了合并） */
  mergedFromSeqs: number[]
  mergeNote?: string | null
}

export interface SubmitChangeApply {
  changeTypeId: string
  studentId: string
  targetSchoolOrgId?: string | null
  targetGradeId?: string | null
  targetClassId?: string | null
  reason: string
  remark?: string | null
}

export interface ChangeAuditAction {
  applyId: string
  opinion?: string | null
}

export interface ChangeApplyListItem {
  id: string
  applyNo: string
  changeTypeCode: string
  changeTypeName: string
  studentId: string
  studentName: string
  studentNo?: string | null
  applyDate: string
  status: string
  statusName: string
  sourceSchoolName?: string | null
  targetSchoolName?: string | null
  currentSeq?: number | null
  currentNodeName?: string | null
  currentOrgName?: string | null
  finalApprovedTime?: string | null
}

export interface ChangeFlowTask {
  id: string
  seq: number
  nodeName: string
  orgId: string
  orgName: string
  roleCode: string
  approveLevel: string
  approveLevelName: string
  status: string
  statusName: string
  mergedFromSeqs: number[]
  handledByName?: string | null
  handledTime?: string | null
  opinion?: string | null
  dueTime?: string | null
}

export interface ChangeFlowLog {
  id: string
  seq?: number | null
  action: string
  actionName?: string | null
  operatorName?: string | null
  opinion?: string | null
  actionTime: string
}

export interface ChangeApply extends ChangeApplyListItem {
  changeTypeId: string
  sourceSchoolOrgId: string
  sourceGradeId?: string | null
  sourceClassId?: string | null
  sourceStatusCode?: string | null
  targetSchoolOrgId?: string | null
  targetGradeId?: string | null
  targetClassId?: string | null
  targetStatusCode?: string | null
  reason?: string | null
  remark?: string | null
  flowTemplateId: string
  flowTemplateVersion: number
  tasks: ChangeFlowTask[]
  logs: ChangeFlowLog[]
}

export interface ChangeApplyQuery extends PagedRequest {
  changeTypeId?: string
  studentId?: string
  status?: string
  schoolOrgId?: string
  keyword?: string
}

export interface ChangeTodoQuery extends PagedRequest {
  orgId?: string
  roleCode?: string
}
