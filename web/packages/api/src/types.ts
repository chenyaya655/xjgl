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
  /**
   * 在左侧菜单里隐藏（模块本身仍可用，路由照常注册）。
   *
   * 后端**照样下发**隐藏模块，正是为了让路由存在 —— 由菜单渲染时跳过。
   * 见 `MenuItemDto.IsHidden` 的说明。
   */
  isHidden: boolean
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

/**
 * 学生详情（对应后端 `StudentDto`，比列表项多出主档的全部字段）。
 *
 * ⚠ **没有身份证号明文**：后端只回 `maskedIdCard`（保留前 6 后 4）。
 *   因此"修改学生"时身份证号必须由用户重新输入 —— 见 `CreateUpdateStudent.idCardNumber`。
 */
export interface StudentDetail extends StudentListItem {
  formerName?: string | null
  pinyin?: string | null
  idCardTypeCode: string
  nationCode?: string | null
  countryCode?: string | null
  politicalCode?: string | null
  nativePlace?: string | null
  birthPlace?: string | null
  householdNatureCode?: string | null
  householdAddress?: string | null
  homeAddress?: string | null
  currentAddress?: string | null
  phoneNumber?: string | null
  postalCode?: string | null
  email?: string | null
  enrollDate?: string | null
  enrollTypeCode?: string | null
  studyModeCode?: string | null
  schoolLengthCode?: string | null
  isMigrantChild: boolean
  migrantTypeCode?: string | null
  isLeftBehindChild: boolean
  leftBehindTypeCode?: string | null
  isPoor: boolean
  isDisabled: boolean
  disabilityCode?: string | null
  healthCode?: string | null
  photoPath?: string | null
  remark?: string | null
}

/**
 * 新增 / 修改学生（对应后端 `CreateUpdateStudentDto`）。
 *
 * 【为什么修改也要传身份证号】
 * 后端 `UpdateAsync` 会拿它重算摘要并做查重，而详情接口**刻意不返回明文**
 * （身份证是加密存储的，回显就等于把加密白做了）。所以编辑表单要求重新输入，
 * 并在界面上写明原因，而不是假装这个字段可以省略。
 */
export interface CreateUpdateStudent {
  name: string
  formerName?: string | null
  genderCode: string
  birthDate: string
  idCardTypeCode: string
  idCardNumber: string
  schoolOrgId: string
  academicYear: string
  gradeId?: string | undefined
  classId?: string | undefined
  classStudentNo?: string | null
  nationCode?: string | null
  countryCode?: string | null
  politicalCode?: string | null
  nativePlace?: string | null
  birthPlace?: string | null
  householdNatureCode?: string | null
  householdAddress?: string | null
  homeAddress?: string | null
  currentAddress?: string | null
  phoneNumber?: string | null
  postalCode?: string | null
  email?: string | null
  enrollDate?: string | null
  enrollTypeCode?: string | null
  studyModeCode?: string | null
  schoolLengthCode?: string | null
  studentStatusCode?: string | null
  isMigrantChild: boolean
  migrantTypeCode?: string | null
  isLeftBehindChild: boolean
  leftBehindTypeCode?: string | null
  isPoor: boolean
  isDisabled: boolean
  disabilityCode?: string | null
  healthCode?: string | null
  remark?: string | null
}

/** 重复学籍检测请求（对应后端 `CheckStudentDuplicateDto`）。 */
export interface CheckStudentDuplicate {
  name: string
  idCardNumber: string
  birthDate?: string | null
  /** 修改场景传自己，避免"和自己重复"。 */
  excludeId?: string | null
}

/** 重复学籍检测结果（对应后端 `StudentDuplicateCheckResultDto`）。 */
export interface StudentDuplicateCheckResult {
  hasDuplicate: boolean
  reasons: string[]
  matched: StudentListItem[]
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
// 组织（学校名录）批量导入
// ---------------------------------------------------------------------------

/**
 * ⚠ 列说明**复用学生的 `ImportColumn`**，这里刻意不再定义一个 `OrganizationImportColumn`：
 *   后端两个接口（`StudentImportExportAppService.GetColumns` 与
 *   `OrganizationImportExportAppService.GetColumns`）返回的都是同一个
 *   `ImportColumnDto`（name / required / remark），形状完全一致。
 *   另立一份只是多一处"后端加字段时可能忘了同步"的字段表 ——
 *   而漏同步的表现是运行期 undefined，前端静默少显示一列，不会报编译错。
 *   列名一律以后端 `getColumns()` 的返回为准，前端**不写死类型清单**
 *   （导入模板的「组织类型」列接受中文别名，见 index.ts 里 organizationImportApi 的说明）。
 */

/** 组织导入的单行失败明细（对应后端 `ImportOrganizationErrorDto`）。 */
export interface ImportOrganizationError {
  /**
   * CSV 行号（与用户在 Excel 里看到的行号一致，表头是第 1 行）。
   *
   * 后端按这个字段升序返回，界面直接按原顺序展示即可 ——
   * 处理顺序是按层级重排过的，只有行号能让用户对上自己的表格。
   */
  rowIndex: number

  /** 文件里原样写的组织类型文本（可能是"学挍"这种错别字，界面照原样回显，不翻译）。 */
  orgType?: string | null
  code?: string | null
  name?: string | null

  /** 后端给出的失败原因，**原样展示**（用户要照着它改文件）。 */
  message: string
}

export interface ImportOrganizationsResult {
  total: number
  succeeded: number
  failed: number
  errors: ImportOrganizationError[]
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

  /** 学籍状态码（如 01）。 */
  status?: string | null

  /** 学籍状态中文名（后端翻译，展示用这个）。 */
  statusName?: string | null
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

/** 审核级别（码值；中文名从 `APPROVE_LEVEL` 码表取）。 */
export const ApproveLevels = {
  School: 'School',
  County: 'County',
  City: 'City',
  Province: 'Province'
} as const

// `ApproveLevelNames` 已删除：中文名与 `APPROVE_LEVEL` 码表重复，
// 属于"两份事实"。显示名统一走 `useCodeOptions(CodeCategories.ApproveLevel)`。

/**
 * 异动申请单状态码值（与后端 `ChangeApplyStatus` 一一对应）。
 *
 * 【为什么要有这组常量】
 * 页面里原来直接写 `row.status === 'Submitted'` 这类字面量。
 * 手写码值的危险不是打错字，而是**后端改了码值、前端照样"看起来对"**：
 * 比较永远为 false，按钮静默消失或永远出现，测试也照绿。
 * 显示名走码表（`CHANGE_APPLY_STATUS`），**比较**走这里的常量。
 */
export const ChangeApplyStatuses = {
  Draft: 'Draft',
  Submitted: 'Submitted',
  InProgress: 'InProgress',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
  Archived: 'Archived'
} as const

/** 流程待办状态码值（与后端 `ChangeTaskStatus` 一一对应）。 */
export const ChangeTaskStatuses = {
  Pending: 'Pending',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Skipped: 'Skipped',
  Revoked: 'Revoked'
} as const

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
  /**
   * 是否必须上传证明材料。
   *
   * 这个开关**真的拦审批**（`ChangeFlowAppService.EnsureAttachmentRequirementAsync`）：
   * 勾上之后，申请单在没有任何附件时无法通过第一次批准。
   * 校验点在"批准前"而不是"提交时"：提交那一刻申请单还不存在，没有 applyId 可挂附件。
   */
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
  /** 是否必须上传证明材料；见 `ChangeType.requireAttachment` 的说明。 */
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
  /** 是否允许驳回（后端真的校验，关闭后该节点只能「通过」）。 */
  allowReject: boolean
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

  /**
   * 证明材料（附件）相关的三个字段。
   *
   * 为什么后端**同时**给"数量"和"是否已满足"而不是让前端自己数：
   * 审核人关心的是"能不能批"，而"要不要附件"是异动类型上的配置
   * （`RequireAttachment`），"不需要附件的类型"恒为已满足 ——
   * 这套判断放前端就会出现"两种页面各写一遍、其中一份忘了判 requireAttachment"，
   * 那种缺陷的表现是**审核页显示"缺材料"、申请页显示"已齐"**。
   */
  attachmentCount: number
  requireAttachment: boolean
  attachmentRequirementSatisfied: boolean

  tasks: ChangeFlowTask[]
  logs: ChangeFlowLog[]
}

/**
 * 异动证明材料（附件）的元数据。
 *
 * 刻意不含存储路径：那是服务端的物理路径，前端拿它没用，
 * 泄露出去只会让"改个参数试试别的文件"有迹可循 —— 下载一律走 Id。
 */
export interface ChangeAttachment {
  id: string
  applyId: string
  fileName: string
  contentType: string
  fileSize: number
  uploadedByUserId?: string | null
  uploadedByName?: string | null
  uploadedAt: string
  remark?: string | null
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

// 【这里曾有一个手写的 `OrgTypeNames`（Province=省厅 …），已删除】
// 它与后端的 `ORG_TYPE` 码表各存一份中文名 —— 改了一边另一边不会报错，
// 属于"两份事实"。中文名统一从码表取：
//   PC 端 `useCodeOptions(CodeCategories.OrgType)`；
//   H5 端 `@/composables/useCodeOptions`。
// 码值本身仍由上面的 `OrgTypes` 常量提供（那是值，不是显示名）。

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
  /** 乐观并发戳：修改时必须原样回传（见 UpdateRole 的注释）。 */
  concurrencyStamp: string
  creationTime: string
}

/**
 * 新增 / 修改角色。字段与 ABP 的 `IdentityRoleCreateDto` / `IdentityRoleUpdateDto` 对齐。
 *
 * ⚠ `isStatic` **不在**这里：静态角色（`admin`）由框架保护，改不动也删不掉。
 *   界面据此把这类角色的按钮禁用，而不是让用户点了才报错。
 */
export interface CreateRole {
  name: string
  isDefault: boolean
  isPublic: boolean
}

export interface UpdateRole {
  name: string
  isDefault: boolean
  isPublic: boolean
  /**
   * 并发戳。
   *
   * 【为什么必须原样回传】ABP 的更新接口带乐观并发校验：不回传（或回传旧值）
   * 会报 `AbpConcurrencyCheck` 之类的错误，而用户看到的只是一句
   * "数据已被他人修改" —— 在一个不可能有人同时改的环境里出现这句提示，
   * 极易被当成系统故障。列表接口返回什么，这里就带回去什么。
   */
  concurrencyStamp: string
}

// ---------------------------------------------------------------------------
// 角色级数据范围（ROADMAP 第 9 条）
//
// 与上面两个「角色」类型的区别：**角色本身**走 ABP 自带的 /api/identity/roles，
// 而数据范围是本项目自己的应用服务（/api/app/role-data-scope）。
// 权限决定「能做什么」，数据范围决定「能对谁做」，两者正交。
// ---------------------------------------------------------------------------

/**
 * 通用下拉项（字段与后端 `CodeOption` 一致）。
 *
 * 【为什么又写一个，而不是复用现成的】
 * 共享包里形似的只有 `CodeItemLookup`，但它对应的是**码表里的码项**
 * （字段叫 code / name，还带 parentCode / treeLevel），语义是"码值 → 中文名"；
 * 拿它当这里的类型，会逼调用方去读一个后端根本没返回的 `name` 字段。
 * PC / H5 的 `composables/useCodeOptions.ts` 里那两个局部 `CodeOption` 与本类型
 * **结构完全相同**，只是 shared 包不能反向依赖应用 —— 形状一致即互相可传，无需转换。
 */
export interface CodeOption {
  value: string
  label: string
  /**
   * 一句话解释"选它意味着什么"（后端下发，可能为空串）。
   *
   * 说明文案**必须由后端给**：前端自己写一份"码值 → 文案"的映射，
   * 就是第二份码值 —— 码值改名或新增时它不会报错，只会静默少一行说明。
   */
  description?: string
}

/**
 * 某个角色当前的数据范围（对应后端 `RoleDataScopeDto`）。
 *
 * ⚠ `scope` 为 null 表示**没有配置**：此时该角色按组织层级推导
 *   （省厅=全省，市/县/校=本级及下级），也就是"加这个功能之前的行为"。
 *   null **不是**某个"默认取值"，所以不要拿 `scope` 去和码值比较来判断"没配置"。
 */
export interface RoleDataScope {
  roleId: string
  roleName: string
  /** 配置的范围码值；null = 未配置。码值只应来自 `roleDataScopeApi.getOptions()`。 */
  scope?: string | null
  /** 范围的中文名；未配置时为 null。 */
  scopeName?: string | null
  /**
   * 是否已显式配置。
   *
   * 【为什么必须用它，而不是自己判 `scope` 有没有值】
   * 界面上要靠它区分两件**必须长得不一样**的事：
   *   - 没配置：含义由组织层级规则**实时推导**（省厅看到全省，且规则将来会演进）；
   *   - 显式配成"本级及下级"：含义被**钉死**在配置的那一刻。
   * 两者今天的效果可能完全相同（市级角色默认就是"本级及下级"），所以只看"值等不等于
   * 默认值"会把它们画成一个样子 —— 而将来默认规则一调整，前者跟着变、后者不变，
   * 那时就会出现"我明明配过，怎么显示成没配"。判定口径只应有一处：后端给什么就用什么。
   */
  isConfigured: boolean
  /** 备注：为什么给这个角色配这个范围。 */
  remark?: string | null
}

/**
 * 设置 / 清除某个角色的数据范围（对应后端 `SetRoleDataScopeDto`）。
 *
 * ⚠ `scope` 传 null（或空串）表示**清除配置**，回到按组织层级推导 ——
 *   这不是"一个必填项没填"，而是「恢复默认」这个动作本身。
 */
export interface SetRoleDataScope {
  roleId: string
  scope?: string | null
  remark?: string | null
}

// 【这里曾有一份 `DataScopeHints`（码值 → 解释文案），已删除】
// 它虽然只用于显示、不参与任何判断，但它就是**第二份码值**：
// 后端把 `OrgOnly` 改名或新增一个范围时，这份表不会报错，只会静默少一行说明。
// 现在解释文案由后端连选项一起下发（`CodeOption.Description`，
// 文案源头是 `DataScopes.GetDescription`），前端一个码值都不写。

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
// 微代码（码表 / 码项）批量导入导出（ROADMAP 第 17 条）
// ---------------------------------------------------------------------------

/**
 * 微代码导入的单行失败明细（对应后端 `ImportCodeErrorDto`）。
 *
 * 【为什么不复用组织/学生那份错误类型】
 *   三边的字段只对得上一半：组织是 `orgType`、学生是 `idCardNumber`，
 *   这里是 `categoryCode`。把它们合成一个"字段都可选"的大类型，等于把
 *   "哪一行该有哪个字段"从类型上抹掉 —— 而这张表是给用户**照着改文件**用的，
 *   少显示一列就是让他自己去猜哪一行错了。
 *
 * 【类别编码 / 码值 / 码名称一律原样回显，不查码表翻译】
 *   这一行装的可能是用户自己写错的编码（如把 `EXAM_TYPE` 写成 `EXAM_TYPO`），
 *   翻译过的名字会与他文件里的那一行对不上 —— 而这张表存在的唯一意义
 *   就是让他拿着文件从上往下改。
 */
export interface ImportCodeError {
  /** CSV 行号（表头是第 1 行、数据从第 2 行开始）；后端已按它升序返回。 */
  rowIndex: number

  categoryCode?: string | null
  code?: string | null
  name?: string | null

  /** 后端给出的失败原因，**原样展示**（用户要照着它改文件）。 */
  message: string
}

/**
 * 微代码导入结果（对应后端 `ImportCodesResultDto`）。
 *
 * 【为什么不能复用 `ImportStudentsResult` / `ImportOrganizationsResult`】
 *   形状差两处，都不是能靠"多一个可选字段"糊过去的：
 *     1) 这里多一个 `createdCategories` —— 码表导入会**顺带建类别**，
 *        而后端的 `message` 里明确写着"新建类别 N 个"，这个数字必须能显示出来：
 *        类别是会被别处引用的（能不能删、改不改得动都看它）；
 *     2) `errors[]` 的元素是 `ImportCodeError`（见上，字段不同）。
 *   硬套那两个类型，前端就只能把 `createdCategories` 丢掉或读成 undefined。
 *
 * ⚠ 导入**永远不会重命名、也不会重建已存在的类别**：后端按类别编码命中已有类别后
 *   直接沿用，连「类别名称」列都不再看（这是有意为之 —— 改名会让引用它的地方
 *   看到的中文悄悄变掉）。所以"导入之后类别名还是老样子"是**设计如此**。
 */
export interface ImportCodesResult {
  total: number

  /** 本次**新建**的类别数（已存在的类别不重复建）。 */
  createdCategories: number

  succeeded: number
  failed: number
  errors: ImportCodeError[]
  message: string
}

/**
 * 微代码导出的筛选条件（对应后端 `CodeExportQueryDto`；都不填 = 导出全部）。
 *
 * ⚠ `onlyEnabled` 后端默认 **false**（连停用的码项一起导出）—— 与组织导出
 *   **正好相反**（`OrgQueryDto.OnlyEnabled` 默认 true）。这里的用途是
 *   "测试环境 ↔ 生产环境搬运"，漏掉停用的那几条只会在某处下拉框少一个选项时
 *   才被发现。传 `undefined` 与 `false` 结果相同，区别只在读代码的人要不要去翻 DTO。
 */
export interface CodeExportQuery {
  /** 只导出某个类别（**类别编码**，如 `EXAM_TYPE`，不是类别 Id）。 */
  categoryCode?: string | null

  /** 关键字（匹配类别编码 / 类别名称 / 码值 / 码名称）。 */
  keyword?: string | null

  /** 是否只导出启用的；不传 = false = 连停用的一起导出。 */
  onlyEnabled?: boolean
}

// ---------------------------------------------------------------------------
// 年级与班级
//
// 它们**不在组织树里**（见后端 SysGrade 的注释）：年级挂在「学校 + 学年」下，
// 所以每年升级只是一次数据操作，不动组织与权限。
// ---------------------------------------------------------------------------

/** 年级。 */
export interface GradeDto {
  id: string
  schoolOrgId: string
  academicYear: string
  gradeCode: string
  gradeName: string
  headTeacherUserId?: string | null
  sort: number
  isEnabled: boolean
  remark?: string | null
  /** 该年级下的班级数。 */
  classCount: number
}

/** 班级。 */
export interface ClassDto {
  id: string
  schoolOrgId: string
  academicYear: string
  gradeId: string
  gradeName: string
  classNo: string
  className: string
  headTeacherUserId?: string | null
  classroom?: string | null
  sort: number
  isEnabled: boolean
}

// ---------------------------------------------------------------------------
// 统计报表
// ---------------------------------------------------------------------------
/**
 * 学生统计维度。
 *
 * ⚠ 取值必须与后端 `StudentStatDimensions` **逐字一致**（大小写敏感的那部分是
 * `studentStatus` / `academicYear` 这种驼峰）。写错了不会得到空结果，
 * 而是抛 `ValidationFailed`（后端的 IsKnown 校验），表现为页面上一句"未知的统计维度"。
 */
export const StudentStatDimensions = {
  /** 按学校。 */
  School: 'school',
  Grade: 'grade',
  Class: 'class',
  StudentStatus: 'studentStatus',
  Gender: 'gender',
  AcademicYear: 'academicYear',
  HasPhoto: 'hasPhoto'
} as const

export type StudentStatDimension = (typeof StudentStatDimensions)[keyof typeof StudentStatDimensions]

/** 异动统计维度（与后端 `ChangeStatDimensions` 一致）。 */
export const ChangeStatDimensions = {
  ChangeType: 'changeType',
  Status: 'status',
  /** 申请月份，`yyyy-MM`。 */
  Month: 'month',
  SourceSchool: 'sourceSchool',
  TargetSchool: 'targetSchool'
} as const

export type ChangeStatDimension = (typeof ChangeStatDimensions)[keyof typeof ChangeStatDimensions]

/** 统计结果的单个分组。 */
export interface StatisticItem {
  /** 分组键（Id 或码值）。 */
  key: string
  /**
   * 可直接展示的名称；**可能为 null**。
   *
   * 为 null 说明这个维度是码值（学籍状态 / 性别 / 异动状态 / 有无照片），
   * 名称要由前端用码表翻译 —— 后端不替前端再查一遍码表（见后端 DTO 的注释）。
   */
  name?: string | null
  count: number
}

/** 统计结果。 */
export interface StatisticResult {
  dimension: string
  /**
   * 各分组计数之和。
   *
   * ⚠ 它**不是**"库里的总数"：分组被上限（200）截断时，它只覆盖返回的分组。
   * 判断"数字是不是完整的"要看 `isTruncated`，不能拿它去对账。
   */
  total: number
  /**
   * 是否因为分组数达到上限而被截断。
   *
   * 必须展示出来：按学校统计时"只显示前 200 所"与"一共就 200 所"
   * 在界面上长得一模一样，而两者对使用者的含义完全不同。
   */
  isTruncated: boolean
  /** 本次统计实际用的数据范围（人类可读），用于解释"为什么我的数字比别人小"。 */
  scopeDescription: string
  items: StatisticItem[]
}

/** 学生统计的请求条件。 */
export interface StudentStatisticsQuery {
  dimension: StudentStatDimension
  schoolOrgId?: string
  includeDescendantOrgs?: boolean
  academicYear?: string
  gradeId?: string
  classId?: string
  studentStatusCode?: string
  genderCode?: string
}

/** 异动统计的请求条件。 */
export interface ChangeStatisticsQuery {
  dimension: ChangeStatDimension
  schoolOrgId?: string
  includeDescendantOrgs?: boolean
  changeTypeId?: string
  status?: string
  /** 申请日期下界（含），`yyyy-MM-dd`。 */
  from?: string
  /** 申请日期上界（含当天，后端补到 23:59:59）。 */
  to?: string
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
  /** 姓名 / 学籍辅号模糊匹配（服务端过滤，不是"只筛当前页"）。 */
  keyword?: string | null
  action?: string | null
  from?: string | null
  to?: string | null
  skipCount?: number
  maxResultCount?: number
  sorting?: string
}

// ---------------------------------------------------------------------------
// 打印（学籍卡 / 花名册）
// ---------------------------------------------------------------------------

export interface StudentRosterQuery {
  schoolOrgId?: string | null
  includeDescendantOrgs?: boolean
  academicYear?: string | null
  gradeId?: string | null
  classId?: string | null
  studentStatusCode?: string | null
  keyword?: string | null

  /** 姓名脱敏（对外提供名单时使用；校内打印不需要）。 */
  maskNames?: boolean

  /** 最大行数（默认 2000，上限 10000）。 */
  maxRows?: number
}

// ---------------------------------------------------------------------------
// 照片打包导出
// ---------------------------------------------------------------------------

export interface PhotoExportQuery {
  schoolOrgId?: string | null
  batchId?: string | null
  status?: string | null
  keyword?: string | null

  /** 最多导出张数（默认 500，上限 2000）。 */
  maxCount?: number

  /** 导出缩略图而不是原图（体积小得多）。 */
  useThumbnail?: boolean

  /** 是否附一份「缺照片名单.csv」（默认附）。 */
  includeMissingList?: boolean
}

// ---------------------------------------------------------------------------
// 导出 / 打印后台任务
// ---------------------------------------------------------------------------

/** 任务种类。值与后端 ExportTaskKinds 一致。 */
export const ExportTaskKinds = {
  StudentCsv: 'StudentCsv',
  StudentCardPdf: 'StudentCardPdf',
  StudentRosterPdf: 'StudentRosterPdf',
  PhotoZip: 'PhotoZip',

  /**
   * 组织（省 / 市 / 县 / 校）名单导出。
   *
   * ⚠ 这个种类**永远不会截断**（`isTruncated` 恒为 false）：组织是参考数据，
   *   上万条也只是几千行 CSV，后端没有设行数上限。调用方仍然照常调
   *   `describeTruncation`（少一条都不能接受的是学生名单，不是这里），
   *   只是对这一个 kind 它必然返回 null —— 不要为此在调用点上写死判断，
   *   否则以后组织量真的触到上限时，那句如实交代就没地方说了。
   */
  OrganizationCsv: 'OrganizationCsv',

  /**
   * 微代码（码表 / 码项）导出。
   *
   * 与组织导出同一条链路（提交任务 → 轮询 → 下载），所需权限是
   * `Sys.Code.Export`（后端 `ExportTaskPermissions.GetRequiredPermissions`）。
   *
   * 产物与导入模板**列完全一致**（类别编码 / 类别名称 / 码值 / 码名称 / 排序 / 是否启用），
   * 因此可以"导出 → 改一改 → 导入"，这是码表在测试与生产之间搬运最省事的形态。
   * ⚠ 一行 = 一个码项：同一类别的每一行都重复写类别编码与类别名称
   *   （CSV 里没有嵌套，见 types 里 `ImportCodesResult` 与 index.ts 的说明）。
   *
   * 同样**永远不截断**（码表总量只有几千条，后端没有行数上限）：
   * 调用方照常调 `describeTruncation`，只是它必然返回 null —— 不要为此写死判断。
   */
  CodeCsv: 'CodeCsv',

  /**
   * 异动申请单导出（一个人一行）与**异动审核流水**导出（一个审批节点一行）。
   *
   * ⚠ 两者是**两份不同的产物**、权限也分开（申请页要清单，审核页要台账）：
   *   - `ChangeApplyCsv`   → 需要 `Chg.Apply.Export`
   *   - `ChangeAuditCsv`   → 需要 `Chg.Audit.Export`
   * 审核流水那份含处理人 / 处理时间 / 审核意见 / 合并来源序号 ——
   * 它在申请单清单里表达不出来（一份申请有多个节点），而复核与纪检要的正是它。
   *
   * 这两种**会截断**（申请单没有天然上限，后端按 `Xjgl:Export:MaxRows` 收口），
   * 所以调用方必须照常展示 `describeTruncation` 的结果。
   *
   * 用哪一个决定列：两者共用同一份查询条件（`changeQuery`）。
   */
  ChangeApplyCsv: 'ChangeApplyCsv',
  ChangeAuditCsv: 'ChangeAuditCsv'
} as const

export type ExportTaskKind = (typeof ExportTaskKinds)[keyof typeof ExportTaskKinds]

export const ExportTaskStatuses = {
  Pending: 'Pending',
  Running: 'Running',
  Succeeded: 'Succeeded',
  Failed: 'Failed',
  Canceled: 'Canceled',
  Expired: 'Expired'
} as const

export interface ExportTask {
  id: string
  kind: ExportTaskKind
  kindName: string
  status: string
  fileName?: string
  contentType?: string
  fileSize: number

  /** 符合条件的总记录数。 */
  total: number

  /**
   * 实际写进产物的记录数。
   *
   * `total !== rowCount` 就表示**被上限截断**了 —— 必须让用户看见，
   * 因为名单少人是最不能接受的失败方式（后端也会在 message 里写明差多少）。
   */
  rowCount: number

  processed: number
  isTruncated: boolean
  message?: string
  failureReason?: string
  startedTime?: string
  finishedTime?: string
  expiresTime?: string
  creationTime: string

  /** 由后端判定，前端不要自己按 status 推断（否则加了新状态两边就会不一致）。 */
  canDownload: boolean
}

export interface CreateExportTask {
  kind: ExportTaskKind
  studentQuery?: StudentQuery
  rosterQuery?: StudentRosterQuery
  studentId?: string
  photoQuery?: PhotoExportQuery

  /**
   * 组织导出（`ExportTaskKinds.OrganizationCsv`）用；留空 = 导出全部组织。
   *
   * ⚠ 复用 `OrgQuery`（上面「组织」一节里那个类型），不是新写一个形状相同的接口：
   *   它与后端 `OrgQueryDto` 本来就是同一个 DTO。
   *
   * ⚠ 后端 `OrgQueryDto.OnlyEnabled` 默认 **true**（只导启用的），
   *   所以"全量导出"必须显式传 `onlyEnabled: false` —— 传 undefined 是"少了一半组织"，
   *   而文件照样生成成功，只有对账时才会发现。
   *
   * ⚠ 导出实现（`OrganizationCsvExportBuilder.Matches`）只认
   *   `orgType` / `keyword` / `onlyEnabled` 三个条件：`parentId` 与
   *   `includeDescendants` 会被**静默忽略**。界面上因此不要暴露它们
   *   （"选了按某县导出、结果导出了全省"是最糟的静默失败）。
   */
  orgQuery?: OrgQuery

  /**
   * 微代码导出（`ExportTaskKinds.CodeCsv`）用；留空 = 导出全部码表。
   *
   * ⚠ 这里**不能**照抄组织那一段的"必须显式传 onlyEnabled: false"：
   *   后端 `CodeExportQueryDto.OnlyEnabled` 默认就是 **false**（连停用的一起导出），
   *   两个 DTO 的默认值**正好相反**。显式写 `false` 只是让读代码的人
   *   不必去翻后端，而不是在补一个会被漏掉的字段。
   *
   * ⚠ 导出实现只认 `categoryCode` / `keyword` / `onlyEnabled` 三个条件
   *   （见 `CodeCsvExportBuilder.BuildAsync`），别的字段传了也会被静默忽略 ——
   *   界面上因此只暴露这三个。
   */
  codeQuery?: CodeExportQuery

  /**
   * `ChangeApplyCsv` / `ChangeAuditCsv` 用（两者共用同一份条件）。
   *
   * ⚠ 复用「异动」一节里的 `ChangeApplyQuery`（上面那个类型），不是新写一个形状相同的接口：
   *   它与后端 `ChangeApplyQueryDto` 本来就是同一个 DTO，而导出走的正是**列表查询**那条路
   *   —— 因此"能导出哪些"与"界面上能看到哪些"由同一处逻辑决定（含数据范围）。
   */
  changeQuery?: ChangeApplyQuery
}

export interface ExportTaskQuery extends PagedRequest {
  kind?: ExportTaskKind | null
  status?: string | null
}

// ---------------------------------------------------------------------------
// 权限委派（ROADMAP 第 19 条）
// ---------------------------------------------------------------------------

/**
 * 一条权限委派（对应后端 `PermissionDelegationDto`）。
 *
 * 【两个方向共用一份列表，而且"方向"由后端判定】
 *   `isMine` 是后端按当前登录人算好的：true = **我借出去的**（我能撤销），
 *   false = **别人借给我的**（我不能撤销）。
 *   前端不要用 `delegatorUserId === 当前用户 Id` 自己推：那要求前端再实现一遍
 *   "我是谁"的口径，而这个口径只应有一处（后端）。
 *
 * 【委派只授予"能力"，不改动"数据范围"】
 *   被委派人拿到的是**调用接口的资格**；他能看到哪些数据仍由他自己所在组织的
 *   范围决定。这一点必须显示给用户看 —— 否则借出「异动审核」的人会以为
 *   对方连自己的可见范围一起借到了。
 */
export interface PermissionDelegation {
  id: string

  delegatorUserId: string
  delegatorName?: string | null

  delegateeUserId: string
  delegateeName?: string | null

  /** 权限码。只用于兜底显示与排错，界面上优先显示 `permissionDisplayName`。 */
  permissionName: string

  /**
   * 权限的中文名（后端由元数据里的操作名翻译而来）。
   *
   * ⚠ 可能为 null（权限已从元数据下线、或名字查不到），后端此时会把内容回落成权限码。
   *   前端仍要按 null 处理：显示一个权限码总比显示空白强 ——
   *   空白会被读成"这条委派没有权限"。
   */
  permissionDisplayName?: string | null

  /**
   * 到期时间；**null = 长期有效，必须手动撤销**。
   *
   * ⚠ 填了到期的由后台任务自动回收，留空的没有兜底 —— 界面上必须把这句话写出来，
   *   否则"我借出去的权限"会永远留在别人身上。
   */
  expiresAt?: string | null

  isRevoked: boolean
  revokedAt?: string | null

  /** 失效原因（"已撤销" / "管理员撤销" / "已到期自动回收"），由后端给，前端不自己拼。 */
  revokedReason?: string | null

  /**
   * 是否仍然生效（未撤销、未到期）。
   *
   * 后端算好给前端用，**不要自己拿 `expiresAt` 和本地时间比**：
   * 服务器与浏览器的时间/时区不一定一致，比出来的是"我这台机器的此刻"。
   */
  isActive: boolean

  /** 是不是"我借出去的"。 */
  isMine: boolean

  remark?: string | null

  creationTime: string
}

/**
 * 新建一条委派（对应后端 `CreatePermissionDelegationDto`）。
 */
export interface CreatePermissionDelegation {
  delegateeUserId: string

  /** 权限码；**必须来自 `permissionDelegationApi.getDelegatablePermissions()`**，不能手写。 */
  permissionName: string

  /** 留空 = 长期有效（需要显式撤销）。 */
  expiresAt?: string | null

  remark?: string | null
}

/**
 * 可委派的权限项（下拉框数据源，对应后端 `DelegatablePermissionDto`）。
 *
 * 【为什么下拉框一条权限都不许前端写死】
 *   "能借出什么"由后端两条规则实时算出来：**我拥有的** ∩ **允许委派的**
 *   （`PermissionDelegationRules`：`Sys.*` 系统管理类、`*.Export` 导出类、
 *   照片存储补偿这类运维动作**刻意不可委派**）。前端抄一份清单，
 *   就会既抄不准、又在后端调整规则时静默不一致 ——
 *   表现是"下拉里出现一个提交必被拒的权限"，或者"少了一项"，两种都不报编译错。
 */
export interface DelegatablePermission {
  permissionName: string

  /** 中文名；后端查不到时为 null（调用方回落显示 `permissionName`）。 */
  displayName?: string | null
}
