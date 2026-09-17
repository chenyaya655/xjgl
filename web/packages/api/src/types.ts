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

/** 组织节点（含下级；对应后端 OrgDto）。 */
export interface OrgDto {
  id: string
  parentId?: string | null
  orgType: string
  orgTypeName: string
  code: string
  name: string
  shortName?: string | null
  /** 物化路径，层级与子树查询都靠它 */
  pathCode: string
  treeLevel: number
  sort: number
  isEnabled: boolean
  remark?: string | null
  children: OrgDto[]
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
  age: number
  /** 脱敏后的身份证号（后端字段名是 MaskedIdCard） */
  maskedIdCard?: string | null
  schoolOrgId: string
  schoolName?: string | null
  academicYear: string
  gradeId?: string | null
  gradeName?: string | null
  classId?: string | null
  className?: string | null
  studentStatusCode: string
  studentStatusName?: string | null
  /**
   * 是否已有照片（后端给的是布尔值 HasPhoto，**不是**照片路径）。
   *
   * 这里曾写成 `photoPath`，于是学生列表的"照片"列永远显示"缺失" ——
   * 类型定义与实际返回值不一致的表现就是这样：界面静默显示错误信息，
   * 不报错、也不崩，只能靠对照后端 DTO 才能发现。
   */
  hasPhoto: boolean
  hasRareCharacter: boolean
}

export interface StudentQuery extends PagedRequest {
  schoolOrgId?: string
  includeDescendantOrgs?: boolean
  academicYear?: string
  gradeId?: string
  classId?: string
  studentStatusCode?: string
  genderCode?: string
  keyword?: string
}

// ---------------------------------------------------------------------------
// 学生批量导入导出
// ---------------------------------------------------------------------------

export interface ImportColumn {
  name: string
  required: boolean
  remark?: string | null
}

export interface ImportStudentError {
  /** CSV 行号（与用户在 Excel 里看到的行号一致） */
  rowIndex: number
  name?: string | null
  idCardNumber?: string | null
  message: string
}

export interface ImportStudentsResult {
  total: number
  succeeded: number
  failed: number
  errors: ImportStudentError[]
  message: string
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

// ---------------------------------------------------------------------------
// 组织（省 / 市 / 县区 / 学校）
// ---------------------------------------------------------------------------

/** 组织类型的中文名（OrgTypes 常量在文件上方已定义，这里只补显示名）。 */
export const OrgTypeNames: Record<string, string> = {
  Province: '省厅',
  City: '地市',
  County: '县区',
  School: '学校'
}

/** 轻量组织节点，用于逐层懒加载。 */
export interface OrgTreeNode {
  id: string
  parentId?: string | null
  orgType: string
  code: string
  name: string
  shortName?: string | null
  pathCode: string
  treeLevel: number
  sort: number
  isEnabled: boolean
  hasChildren: boolean
  children: OrgTreeNode[]
}

export interface CreateUpdateOrg {
  parentId?: string | null
  orgType: string
  code: string
  name: string
  shortName?: string | null
  sort: number
  isEnabled: boolean
  remark?: string | null
}

export interface OrgQuery {
  orgType?: string
  parentId?: string
  keyword?: string
  onlyEnabled?: boolean
  includeDescendants?: boolean
}

// ---------------------------------------------------------------------------
// 身份（用户 / 角色）—— 走 ABP Identity 自带接口
// ---------------------------------------------------------------------------

export interface IdentityRole {
  id: string
  name: string
  isDefault: boolean
  isStatic: boolean
  isPublic: boolean
}

export interface IdentityUser {
  id: string
  userName: string
  name?: string | null
  surname?: string | null
  email?: string | null
  emailConfirmed: boolean
  phoneNumber?: string | null
  isActive: boolean
  lockoutEnabled: boolean
  accessFailedCount: number
  creationTime: string
  // ⚠ 这里**故意没有** roleNames：ABP 10 的 IdentityUserDto（列表与详情）都不返回它。
  //   角色必须调 `GET /api/identity/users/{id}/roles` 子资源获取。
  //   曾经因为想当然地读 `user.roleNames` 拿到 undefined，
  //   回写时把角色清空了 —— 见 getUserRoles 的注释。
}

export interface CreateUser {
  userName: string
  name?: string | null
  surname?: string | null
  email?: string | null
  phoneNumber?: string | null
  password: string
  isActive: boolean
  lockoutEnabled: boolean
  roleNames: string[]
}

export interface UpdateUser {
  userName: string
  name?: string | null
  surname?: string | null
  email?: string | null
  phoneNumber?: string | null
  isActive: boolean
  lockoutEnabled: boolean
  roleNames: string[]
}

// ---------------------------------------------------------------------------
// 权限分配 —— 走 ABP PermissionManagement 自带接口
// ---------------------------------------------------------------------------

export interface PermissionGrantInfo {
  name: string
  displayName: string
  parentName?: string | null
  isGranted: boolean
  /** 该权限是否允许在当前 provider（角色/用户）上编辑 */
  isEditable: boolean
}

export interface PermissionGroup {
  name: string
  displayName: string
  permissions: PermissionGrantInfo[]
}

export interface PermissionResult {
  entityDisplayName: string
  groups: PermissionGroup[]
}

/** 授权提供方：R=角色，U=用户 */
export const PermissionProviders = {
  Role: 'R',
  User: 'U'
} as const

// ---------------------------------------------------------------------------
// 微代码
// ---------------------------------------------------------------------------

export interface CodeCategory {
  id: string
  code: string
  name: string
  stdSource?: string | null
  isTree: boolean
  isSystem: boolean
  isEnabled: boolean
  cacheable: boolean
  sort: number
  remark?: string | null
  itemCount: number
}

export interface CodeItem {
  id: string
  categoryId: string
  code: string
  name: string
  parentId?: string | null
  pathCode: string
  treeLevel: number
  sort: number
  isEnabled: boolean
  stdCode?: string | null
  extJson?: string | null
  remark?: string | null
  children: CodeItem[]
}

/**
 * 按类别编码取回的码项（下拉框用）。
 *
 * 与 `CodeItem` 的区别：**没有 id**（只有 code/name），
 * 因为它的用途是"把码值翻译成中文"或"给下拉框提供选项"，
 * 这类场景只需要码值与名称。
 */
export interface CodeItemLookup {
  code: string
  name: string
  parentCode?: string | null
  treeLevel: number
  isEnabled: boolean
}

export interface CreateUpdateCodeCategory {
  code: string
  name: string
  stdSource?: string | null
  isTree: boolean
  isEnabled: boolean
  cacheable: boolean
  sort: number
  remark?: string | null
}

export interface CreateUpdateCodeItem {
  categoryId: string
  code: string
  name: string
  parentId?: string | null
  stdCode?: string | null
  sort: number
  isEnabled: boolean
  extJson?: string | null
  remark?: string | null
}

// ---------------------------------------------------------------------------
// 元数据（子系统 / 模块 / 操作）
// ---------------------------------------------------------------------------

export interface ModuleAction {
  id: string
  moduleId: string
  code: string
  name: string
  actionType: string
  /** 权限码：{子系统}.{模块}.{操作} */
  permissionName: string
  sort: number
  isEnabled: boolean
  isDefaultGranted: boolean
}

export interface SysModule {
  id: string
  subsystemId: string
  code: string
  name: string
  /** 权限码：{子系统}.{模块} */
  permissionName: string
  /** 前端路由路径，如 /student/list */
  path?: string | null
  /** 前端组件路径，如 student/list/index */
  component?: string | null
  icon?: string | null
  sort: number
  isEnabled: boolean
  isHidden: boolean
  isExternal: boolean
  /** 允许访问的组织层级，逗号分隔 */
  orgTypes?: string | null
  remark?: string | null
  actions: ModuleAction[]
}

export interface Subsystem {
  id: string
  code: string
  name: string
  icon?: string | null
  sort: number
  isEnabled: boolean
  remark?: string | null
  modules: SysModule[]
}

export interface CreateUpdateSubsystem {
  code: string
  name: string
  icon?: string | null
  sort: number
  isEnabled: boolean
  remark?: string | null
}

export interface CreateUpdateModule {
  subsystemId: string
  code: string
  name: string
  path?: string | null
  component?: string | null
  icon?: string | null
  sort: number
  isEnabled: boolean
  isHidden: boolean
  isExternal: boolean
  orgTypes?: string | null
  remark?: string | null
}

export interface CreateUpdateModuleAction {
  moduleId: string
  code: string
  name: string
  actionType: string
  sort: number
  isEnabled: boolean
  isDefaultGranted: boolean
}

// ---------------------------------------------------------------------------
// 用户预置与激活
// ---------------------------------------------------------------------------

export interface PredefineUser {
  id: string
  realName: string
  phoneNumber: string
  orgId: string
  orgPathCode?: string | null
  /** 逗号分隔的角色名（后端存的是字符串） */
  roleNames?: string | null
  position?: string | null
  status: string
  statusName: string
  activationCode: string
  activationExpiresAt: string
  activatedUserId?: string | null
  activatedAt?: string | null
  batchNo?: string | null
  remark?: string | null
  creationTime: string
}

export interface CreatePredefineUser {
  realName: string
  phoneNumber: string
  idCardNumber?: string | null
  orgId: string
  roleNames?: string | null
  position?: string | null
  remark?: string | null
  /** 激活码有效期（天），默认 30 */
  activationValidDays: number
}

export interface PredefineUserQuery extends PagedRequest {
  orgId?: string
  includeDescendantOrgs?: boolean
  keyword?: string
  status?: string
  batchNo?: string
}

export interface ActivateUser {
  phoneNumber: string
  realName: string
  idCardNumber?: string | null
  activationCode: string
  password: string
}

export interface ActivateUserResult {
  userId: string
  userName: string
  realName: string
  orgId: string
  roles: string[]
}

export interface ImportPredefineError {
  rowIndex: number
  realName?: string | null
  phoneNumber?: string | null
  message: string
}

export interface ImportPredefineResult {
  batchNo: string
  total: number
  succeeded: number
  errors: ImportPredefineError[]
}

// ---------------------------------------------------------------------------
// 学生档案子表（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）
//
// 约定：
//   - 读取类型里的 `xxxName` 是后端翻译好的中文名（前端不做码表映射）；
//   - **身份证号只有 maskedIdCard**：明文只在写入方向出现，
//     且"留空 = 不修改"，所以 Input 类型里的 idCardNumber 是可选的。
// ---------------------------------------------------------------------------

/** 监护人。 */
export interface Guardian {
  id: string
  studentId: string
  seq: number
  name: string
  relationCode?: string | null
  relationName?: string | null
  maskedIdCard?: string | null
  hasIdCard: boolean
  phoneNumber?: string | null
  workUnit?: string | null
  address?: string | null
  isPrimary: boolean
  remark?: string | null
}

export interface GuardianInput {
  name: string
  relationCode?: string | null
  /** 明文；留空表示不修改（界面只显示掩码，用户没改就不该回传）。 */
  idCardNumber?: string | null
  phoneNumber?: string | null
  workUnit?: string | null
  address?: string | null
  isPrimary: boolean
  remark?: string | null
}

/** 学习简历。 */
export interface Resume {
  id: string
  studentId: string
  seq: number
  startDate?: string | null
  endDate?: string | null
  schoolName: string
  gradeName?: string | null
  className?: string | null
  post?: string | null
  referee?: string | null
  remark?: string | null
}

export interface ResumeInput {
  startDate?: string | null
  endDate?: string | null
  schoolName: string
  gradeName?: string | null
  className?: string | null
  post?: string | null
  referee?: string | null
  remark?: string | null
}

/** 奖励。 */
export interface Reward {
  id: string
  studentId: string
  seq: number
  rewardDate: string
  rewardName: string
  rewardTypeCode?: string | null
  rewardTypeName?: string | null
  rewardLevelCode?: string | null
  rewardLevelName?: string | null
  awardOrg?: string | null
  remark?: string | null
}

export interface RewardInput {
  rewardDate: string
  rewardName: string
  rewardTypeCode?: string | null
  rewardLevelCode?: string | null
  awardOrg?: string | null
  remark?: string | null
}

/** 处分。 */
export interface Punishment {
  id: string
  studentId: string
  seq: number
  punishDate: string
  punishName: string
  punishTypeCode?: string | null
  punishTypeName?: string | null
  punishOrg?: string | null
  isRevoked: boolean
  revokeDate?: string | null
  revokeReason?: string | null
  remark?: string | null
}

export interface PunishmentInput {
  punishDate: string
  punishName: string
  punishTypeCode?: string | null
  punishOrg?: string | null
  isRevoked: boolean
  revokeDate?: string | null
  revokeReason?: string | null
  remark?: string | null
}

/** 资助。 */
export interface Subsidy {
  id: string
  studentId: string
  seq: number
  academicYear: string
  subsidyTypeCode?: string | null
  subsidyTypeName?: string | null
  subsidyName: string
  amount?: number | null
  subsidyDate?: string | null
  fundingOrg?: string | null
  remark?: string | null
}

export interface SubsidyInput {
  academicYear: string
  subsidyTypeCode?: string | null
  subsidyName: string
  amount?: number | null
  subsidyDate?: string | null
  fundingOrg?: string | null
  remark?: string | null
}

/** 体质健康（体检）。一个学年一条。 */
export interface Physical {
  id: string
  studentId: string
  academicYear: string
  checkDate?: string | null
  height?: number | null
  weight?: number | null
  leftVision?: number | null
  rightVision?: number | null
  healthCode?: string | null
  healthName?: string | null
  doctorAdvice?: string | null
  remark?: string | null
}

export interface PhysicalInput {
  academicYear: string
  checkDate?: string | null
  height?: number | null
  weight?: number | null
  leftVision?: number | null
  rightVision?: number | null
  healthCode?: string | null
  doctorAdvice?: string | null
  remark?: string | null
}

// ---------------------------------------------------------------------------
// 学籍变更流水（留痕）
// ---------------------------------------------------------------------------

/**
 * 一条变更记录。
 *
 * 一次操作可能产生多行（每个变化的字段一行），它们共享 `batchId`；
 * 界面上可以据此把"一次修改"合并显示。
 * `oldValue`/`newValue` 已是后端翻译好的可读文本（码值形如「在读（01）」），
 * 身份证号只给摘要前缀 —— 明文永远不会出现在流水里。
 */
export interface StudentChangeLog {
  id: string
  studentId: string
  studentNo: string
  studentName: string
  batchId: string
  action: string
  actionName: string
  fieldName?: string | null
  fieldLabel?: string | null
  oldValue?: string | null
  newValue?: string | null
  reason?: string | null
  operatorId?: string | null
  operatorName?: string | null
  occurredAt: string
  source?: string | null
}

export interface StudentChangeLogQuery {
  studentId?: string | null
  action?: string | null
  from?: string | null
  to?: string | null
  skipCount?: number
  maxResultCount?: number
  sorting?: string
}
