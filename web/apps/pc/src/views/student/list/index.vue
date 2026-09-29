<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import {
  exportTaskApi,
  orgApi,
  studentApi,
  studentImportApi,
  describeExportProgress,
  describeTruncation,
  saveBlob
} from '@xjgl/api'
import type {
  CreateUpdateStudent,
  ExportTask,
  ImportColumn,
  ImportStudentsResult,
  OrgDto,
  StudentDetail,
  StudentDuplicateCheckResult,
  StudentListItem
} from '@xjgl/api'
import type { FormInstance, FormRules } from 'element-plus'
import AppIcon from '@/components/AppIcon.vue'
import GradeClassSelect from '@/components/GradeClassSelect.vue'
import SchoolSelect from '@/components/SchoolSelect.vue'
import StudentChangeLogDialog from '@/components/StudentChangeLogDialog.vue'
import StudentProfileDrawer from '@/components/StudentProfileDrawer.vue'
import { CodeCategories, useCodeOptions } from '@/composables/useCodeOptions'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { currentAcademicYear, flattenSchools } from '@/utils/org'
import { probeCsvEncoding } from '@/utils/csvEncoding'

/**
 * 学生列表。
 *
 * 两个必须做对的点：
 *   1) **生僻字**：姓名直接渲染即可，字体由全局样式里的 unicode-range 兜住；
 *      这里额外给含生僻字的记录打标记，方便排查「为什么这个学生打印出来是方块」。
 *   2) **数据权限在后端**：前端只按选中的学校过滤，能查到的学生范围由后端
 *      按当前用户的组织与数据权限决定 —— 前端传了越权的 schoolOrgId 也查不到。
 */
const auth = useAuthStore()

const loading = ref(false)
const rows = ref<StudentListItem[]>([])
const total = ref(0)
const schools = ref<OrgDto[]>([])

const query = reactive({
  schoolOrgId: undefined as string | undefined,
  keyword: '',
  studentStatusCode: undefined as string | undefined,
  // 年级 / 班级筛选：**后端一直支持**（StudentQueryDto.GradeId/ClassId），
  // 但界面此前没有入口 —— 学校最常用的"看六年级的名单"只能靠关键字凑。
  gradeId: undefined as string | undefined,
  classId: undefined as string | undefined,
  maxResultCount: 20
})

/**
 * 学年：年级与班级都挂在「学校 + 学年」下，所以这两个下拉必须先有学年。
 * 用户不需要为了筛选而主动填它 —— 回落到当前学年即可（可手改）。
 */
const academicYear = ref(currentAcademicYear())

const gradeClassSelect = ref<{ reset: () => void } | null>(null)

/** 当前页码（1 起）。分页组件用的是页码，接口要的是 skipCount，两者在这里换算。 */
const page = ref(1)

/**
 * 学籍状态下拉项**来自码表**（STUDENT_STATUS），不在前端硬编码。
 *
 * 这里踩过一个典型的坑：原来写的是 `{ value: 'Studying', label: '在读' }`，
 * 而后端的学籍状态是**码值**（01/02/…）。后果有两层：
 *   1) 按状态过滤时后端拿 'Studying' 去比对，**静默返回空列表**（HTTP 200，不报错）；
 *   2) 表格里的状态标签也用这个字符串判断，于是所有状态都渲染成灰色。
 * 改成从码表取值，就不会再出现"前端一套值、后端一套值"。
 *
 * 现在这段逻辑收敛到 `useCodeOptions`（债 12）：本页面是第二个写这段代码的地方，
 * 再复制一份必然会漂移（码表拉取方式、并发去重、失败降级都得各写一遍）。
 */
const statusCodes = useCodeOptions(CodeCategories.StudentStatus)
const statusOptions = statusCodes.options

/** 在校状态码（与后端 StudentStatuses.IsInSchool 的含义一致：在读/复学/转入）。 */
const IN_SCHOOL_CODES = ['01', '03', '05']

async function loadSchools(): Promise<void> {
  try {
    const tree = await orgApi.getTree()
    schools.value = flattenSchools(tree)
  } catch {
    // 组织接口失败时不阻塞列表：用户仍可用关键字查询（受后端数据权限约束）
    schools.value = []
  }
}

async function loadStatusOptions(): Promise<void> {
  // 失败不阻塞列表：码表拉不到时下拉框为空，但关键字/学校过滤仍可用
  await statusCodes.load()
}

async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await studentApi.getList({
      schoolOrgId: query.schoolOrgId,
      keyword: query.keyword || undefined,
      studentStatusCode: query.studentStatusCode,
      gradeId: query.gradeId,
      classId: query.classId,
      maxResultCount: query.maxResultCount,
      skipCount: (page.value - 1) * query.maxResultCount,
      sorting: 'StudentNo ASC'
    })

    rows.value = result.items
    total.value = result.totalCount
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

function reset(): void {
  query.schoolOrgId = undefined
  query.keyword = ''
  query.studentStatusCode = undefined
  query.gradeId = undefined
  query.classId = undefined
  academicYear.value = currentAcademicYear()

  // 只清 v-model 是不够的：年级/班级下拉的**选项列表**还留在子组件里，
  // 于是界面会出现"学校已清空、年级里却还列着上一所学校的年级"
  gradeClassSelect.value?.reset()

  search()
}

function formatDate(value?: string | null): string {
  return value ? value.slice(0, 10) : '—'
}

// ---------------------------------------------------------------------------
// 学生档案（子表）
// ---------------------------------------------------------------------------

const profileVisible = ref(false)
const profileStudent = ref<StudentListItem | null>(null)

function openProfile(row: StudentListItem): void {
  profileStudent.value = row
  profileVisible.value = true
}

// ---------------------------------------------------------------------------
// 变更留痕（全局，见 StudentChangeLogDialog 的说明）
// ---------------------------------------------------------------------------

/**
 * 全局留痕做成这个页面上的对话框，而不是新开菜单/路由。
 *
 * 本系统的路由与菜单完全由后端元数据（`sys_module`）驱动，独立页面就必须
 * 在「模块管理」里多一条模块记录、再给每个角色授权；而"查留痕"在业务上
 * 不是一个新的功能入口，它就是学生管理的一部分，权限也正好复用
 * 「能查学生」（`Stu.Student.Query`）—— 能看名单才谈得上核对谁改过什么。
 */
const changeLogVisible = ref(false)

// ---------------------------------------------------------------------------
// 学生主档：新增 / 修改 / 删除
// ---------------------------------------------------------------------------

/**
 * 【这个模块为什么以前不存在】
 * 页面上「新增学生」是一个**没有 @click 的按钮**，行操作里也只有「详情 / 学籍卡」——
 * 而后端 `StudentAppService` 的 Create / Update / Delete / CheckDuplicate 一直是齐的。
 * 后果是学生只能靠 CSV 导入建档、建错了只能改库。
 *
 * 【为什么「修改」必须让用户重新输入身份证号】
 * 身份证是**加密存储**的，详情接口只回 `maskedIdCard`（前 6 后 4），
 * 而 `UpdateAsync` 要用明文重算摘要并查重。界面因此无法"回显原值"，
 * 只能要求重新输入 —— 这句话必须写在界面上，否则用户会以为保存把身份证弄丢了。
 */
type FormMode = 'create' | 'edit'

const formVisible = ref(false)
const formMode = ref<FormMode>('create')
const editingId = ref<string | null>(null)
/** 编辑时回显用的脱敏身份证号（明文拿不到，见上）。 */
const editingMaskedIdCard = ref('')
const saving = ref(false)
const formRef = ref<FormInstance | null>(null)

/** 查重结果：只在**确实查到重复**时提示，正常录入不被它打断。 */
const duplicateChecking = ref(false)
const duplicateResult = ref<StudentDuplicateCheckResult | null>(null)

function emptyForm(): CreateUpdateStudent {
  return {
    name: '',
    formerName: null,
    genderCode: '',
    birthDate: '',
    idCardTypeCode: '1',
    idCardNumber: '',
    // 默认用当前筛选的学校：用户通常是"筛到某校 → 给这校建档"
    schoolOrgId: query.schoolOrgId ?? '',
    academicYear: academicYear.value || currentAcademicYear(),
    // 年级/班级用 undefined（`GradeClassSelect` 的 v-model 类型是 string | undefined），
    // 提交时统一转成 null
    gradeId: undefined,
    classId: undefined,
    classStudentNo: null,
    nationCode: null,
    countryCode: null,
    politicalCode: null,
    nativePlace: null,
    birthPlace: null,
    householdNatureCode: null,
    householdAddress: null,
    homeAddress: null,
    currentAddress: null,
    phoneNumber: null,
    postalCode: null,
    email: null,
    enrollDate: null,
    enrollTypeCode: null,
    studyModeCode: null,
    schoolLengthCode: null,
    // 留空：新增时后端默认「在读」；修改时下面会填入真实值
    studentStatusCode: null,
    isMigrantChild: false,
    migrantTypeCode: null,
    isLeftBehindChild: false,
    leftBehindTypeCode: null,
    isPoor: false,
    isDisabled: false,
    disabilityCode: null,
    healthCode: null,
    remark: null
  }
}

const form = reactive<CreateUpdateStudent>(emptyForm())

const formRules: FormRules = {
  name: [{ required: true, message: '姓名不能为空', trigger: 'blur' }],
  genderCode: [{ required: true, message: '请选择性别', trigger: 'change' }],
  birthDate: [{ required: true, message: '请选择出生日期', trigger: 'change' }],
  idCardTypeCode: [{ required: true, message: '请选择身份证件类型', trigger: 'change' }],
  idCardNumber: [
    { required: true, message: '身份证件号码不能为空', trigger: 'blur' },
    { min: 15, max: 18, message: '身份证件号码长度应为 15~18 位', trigger: 'blur' }
  ],
  schoolOrgId: [{ required: true, message: '请选择学校', trigger: 'change' }],
  academicYear: [
    { required: true, message: '学年不能为空', trigger: 'blur' },
    {
      pattern: /^(\d{4}|\d{4}-\d{4})$/,
      message: '学年格式应为 2025-2026',
      trigger: 'blur'
    }
  ]
}

// 主档用到的码表全部走码值（不手写英文码），与筛选区共用同一个缓存
const genderCodes = useCodeOptions(CodeCategories.Sex)
const idTypeCodes = useCodeOptions(CodeCategories.IdType)
const nationCodes = useCodeOptions(CodeCategories.Nation)
const countryCodes = useCodeOptions(CodeCategories.Country)
const politicalCodes = useCodeOptions(CodeCategories.Political)
const householdCodes = useCodeOptions(CodeCategories.Household)
const enrollTypeCodes = useCodeOptions(CodeCategories.EnrollType)
const studyModeCodes = useCodeOptions(CodeCategories.StudyMode)
const schoolLengthCodes = useCodeOptions(CodeCategories.SchoolLength)
const disabilityCodes = useCodeOptions(CodeCategories.Disability)
const migrantCodes = useCodeOptions(CodeCategories.Migrant)
const leftBehindCodes = useCodeOptions(CodeCategories.LeftBehind)
const healthCodes = useCodeOptions(CodeCategories.Health)

let formCodesLoaded = false

/** 懒加载主档码表：只在第一次打开对话框时拉，失败也不阻塞（下拉为空但可提交必填项）。 */
async function loadFormCodeOptions(): Promise<void> {
  if (formCodesLoaded) {
    return
  }

  formCodesLoaded = true

  await Promise.allSettled([
    genderCodes.load(),
    idTypeCodes.load(),
    nationCodes.load(),
    countryCodes.load(),
    politicalCodes.load(),
    householdCodes.load(),
    enrollTypeCodes.load(),
    studyModeCodes.load(),
    schoolLengthCodes.load(),
    disabilityCodes.load(),
    migrantCodes.load(),
    leftBehindCodes.load(),
    healthCodes.load(),
    statusCodes.load()
  ])
}

/** 空串统一转 null：后端把这些字段当可空码值，`''` 会被当成"填了一个不存在的码"。 */
function orNull(value?: string | null): string | null {
  const text = (value ?? '').trim()

  return text.length > 0 ? text : null
}

function openCreate(): void {
  formMode.value = 'create'
  editingId.value = null
  editingMaskedIdCard.value = ''
  duplicateResult.value = null
  Object.assign(form, emptyForm())
  formVisible.value = true

  void loadFormCodeOptions().then(() => nextTick(() => formRef.value?.clearValidate()))
}

async function openEdit(row: StudentListItem): Promise<void> {
  formMode.value = 'edit'
  editingId.value = row.id
  editingMaskedIdCard.value = row.maskedIdCard ?? ''
  duplicateResult.value = null
  Object.assign(form, emptyForm())
  formVisible.value = true

  await loadFormCodeOptions()

  try {
    const detail = await studentApi.get(row.id)

    Object.assign(form, detailToForm(detail))
    editingMaskedIdCard.value = detail.maskedIdCard ?? ''

    await nextTick()
    formRef.value?.clearValidate()
  } catch {
    // 详情拉不到就没法安全地改（缺字段会把值改坏），关掉对话框并说明
    formVisible.value = false
    ElMessage.error('读取学生详情失败，已取消修改。')
  }
}

/**
 * 详情 → 表单。
 *
 * `idCardNumber` 刻意置空：详情里只有脱敏值，明文是拿不到的（加密存储）。
 * 用户不重新输入就无法通过后端校验 —— 这是**有意**的，不是缺陷。
 */
function detailToForm(detail: StudentDetail): CreateUpdateStudent {
  return {
    name: detail.name ?? '',
    formerName: detail.formerName ?? null,
    genderCode: detail.genderCode ?? '',
    birthDate: (detail.birthDate ?? '').slice(0, 10),
    idCardTypeCode: detail.idCardTypeCode ?? '1',
    idCardNumber: '',
    schoolOrgId: detail.schoolOrgId,
    academicYear: detail.academicYear ?? currentAcademicYear(),
    gradeId: detail.gradeId ?? undefined,
    classId: detail.classId ?? undefined,
    classStudentNo: detail.classStudentNo ?? null,
    nationCode: detail.nationCode ?? null,
    countryCode: detail.countryCode ?? null,
    politicalCode: detail.politicalCode ?? null,
    nativePlace: detail.nativePlace ?? null,
    birthPlace: detail.birthPlace ?? null,
    householdNatureCode: detail.householdNatureCode ?? null,
    householdAddress: detail.householdAddress ?? null,
    homeAddress: detail.homeAddress ?? null,
    currentAddress: detail.currentAddress ?? null,
    phoneNumber: detail.phoneNumber ?? null,
    postalCode: detail.postalCode ?? null,
    email: detail.email ?? null,
    enrollDate: detail.enrollDate ? detail.enrollDate.slice(0, 10) : null,
    enrollTypeCode: detail.enrollTypeCode ?? null,
    studyModeCode: detail.studyModeCode ?? null,
    schoolLengthCode: detail.schoolLengthCode ?? null,
    studentStatusCode: detail.studentStatusCode ?? null,
    isMigrantChild: detail.isMigrantChild,
    migrantTypeCode: detail.migrantTypeCode ?? null,
    isLeftBehindChild: detail.isLeftBehindChild,
    leftBehindTypeCode: detail.leftBehindTypeCode ?? null,
    isPoor: detail.isPoor,
    isDisabled: detail.isDisabled,
    disabilityCode: detail.disabilityCode ?? null,
    healthCode: detail.healthCode ?? null,
    remark: detail.remark ?? null
  }
}

/**
 * 提交前查重（身份证摘要 + 姓名 + 出生日期，跨校跨区）。
 *
 * 后端在写入时**也会**查一次并拒绝，这里提前查一次是为了把
 * "这个人已经有学籍了"讲在填完表单之前，而不是让用户白填一遍。
 * 查重接口本身失败时静默放过：它是提示，不是闸门（闸门在后端）。
 */
async function runDuplicateCheck(): Promise<void> {
  duplicateResult.value = null

  const name = form.name.trim()
  const idCard = form.idCardNumber.trim()

  // 没填够长度就查不了；也避免每敲一个字符打一次接口
  if (!name || idCard.length < 15) {
    return
  }

  duplicateChecking.value = true

  try {
    const result = await studentApi.checkDuplicate({
      name,
      idCardNumber: idCard,
      birthDate: form.birthDate || null,
      excludeId: editingId.value
    })

    duplicateResult.value = result.hasDuplicate ? result : null
  } catch {
    // 查重失败不挡录入（写入时后端还会查），静默即可
    duplicateResult.value = null
  } finally {
    duplicateChecking.value = false
  }
}

async function submitForm(): Promise<void> {
  const valid = await formRef.value?.validate().catch(() => false)

  if (!valid) {
    return
  }

  saving.value = true

  try {
    const payload: CreateUpdateStudent = {
      ...form,
      name: form.name.trim(),
      idCardNumber: form.idCardNumber.trim(),
      genderCode: form.genderCode.trim(),
      idCardTypeCode: form.idCardTypeCode.trim(),
      academicYear: form.academicYear.trim(),
      formerName: orNull(form.formerName),
      classStudentNo: orNull(form.classStudentNo),
      nationCode: orNull(form.nationCode),
      countryCode: orNull(form.countryCode),
      politicalCode: orNull(form.politicalCode),
      nativePlace: orNull(form.nativePlace),
      birthPlace: orNull(form.birthPlace),
      householdNatureCode: orNull(form.householdNatureCode),
      householdAddress: orNull(form.householdAddress),
      homeAddress: orNull(form.homeAddress),
      currentAddress: orNull(form.currentAddress),
      phoneNumber: orNull(form.phoneNumber),
      postalCode: orNull(form.postalCode),
      email: orNull(form.email),
      enrollTypeCode: orNull(form.enrollTypeCode),
      studyModeCode: orNull(form.studyModeCode),
      schoolLengthCode: orNull(form.schoolLengthCode),
      // 开关关掉时把类型码一起清掉：否则会出现"不是随迁子女、却有随迁类型"的矛盾数据
      migrantTypeCode: form.isMigrantChild ? orNull(form.migrantTypeCode) : null,
      leftBehindTypeCode: form.isLeftBehindChild ? orNull(form.leftBehindTypeCode) : null,
      disabilityCode: form.isDisabled ? orNull(form.disabilityCode) : null,
      healthCode: orNull(form.healthCode),
      remark: orNull(form.remark),
      gradeId: form.gradeId || undefined,
      classId: form.classId || undefined,
      enrollDate: form.enrollDate || null,
      studentStatusCode: orNull(form.studentStatusCode)
    }

    if (formMode.value === 'create') {
      const created = await studentApi.create(payload)

      ElMessage.success(`学生已建档，学籍辅号 ${created.studentNo}`)
    } else if (editingId.value) {
      await studentApi.update(editingId.value, payload)
      ElMessage.success('学生信息已保存')
    }

    formVisible.value = false
    await load()
  } catch {
    // 错误提示由 http 拦截器统一弹出（含后端 `data.reason`，如"身份证号已存在学籍记录"），
    // 这里只负责**不关**对话框，让用户能改完再提交
  } finally {
    saving.value = false
  }
}

async function removeStudent(row: StudentListItem): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除学生「${row.name}」（学籍辅号 ${row.studentNo}）吗？` +
        '该学生的监护人 / 简历 / 奖励 / 处分 / 资助 / 体检与变更留痕会一并级联删除（软删除，可恢复）。',
      '删除学生',
      { type: 'warning', confirmButtonText: '确定删除', cancelButtonText: '取消' }
    )
  } catch {
    return // 用户取消
  }

  try {
    await studentApi.remove(row.id)
    ElMessage.success('学生已删除')
    await load()
  } catch {
    // 拦截器已提示
  }
}

// ---------------------------------------------------------------------------
// 打印（学籍卡 / 花名册）
// ---------------------------------------------------------------------------

const printingCard = ref(false)
const printingRoster = ref(false)

/**
 * 页面卸载后停止轮询。
 *
 * 少了这个，用户点了导出再立刻切走，定时器仍会每 1.5 秒打一次接口 ——
 * 直到 5 分钟超时为止，而那时他早就不在这个页面了。
 */
let disposed = false
onUnmounted(() => {
  disposed = true
})

/**
 * 提交一个导出/打印任务并等它完成，然后下载。
 *
 * 四个调用点共用这一条路径：提交 → 轮询 → 下载 → 提示。
 * 每个调用点各写一遍的话，"忘了处理截断提示"或"忘了停轮询"迟早会发生。
 */
async function runExportTask(
  input: Parameters<typeof exportTaskApi.run>[0],
  suggestedName: string
): Promise<void> {
  progressText.value = '排队中…'

  try {
    const task = await exportTaskApi.run(input, {
      shouldStop: () => disposed,
      onProgress: (t: ExportTask) => {
        // 让提示反映真实进度（排队中 → 已处理 3,000 / 12,000（25%）→ 正在生成…）。
        // 文案只有 `describeExportProgress` 一处实现：这里有四个入口，
        // 各写一遍必然有漏改的（债 23）。
        progressText.value = describeExportProgress(t)
      }
    })

    const blob = await exportTaskApi.download(task.id)

    // 用后端给的文件名（它带了人数/日期等有用信息），后端没给时才用兜底名
    saveBlob(blob, task.fileName || suggestedName)

    // 截断必须显式提示：名单类产出少人是最不能接受的失败方式，
    // 而后端已经如实算出"少了几条"，这里只弹"成功"就等于把那份交代吞掉了。
    const truncation = describeTruncation(task)

    if (truncation) {
      ElMessage.warning(truncation)
    } else {
      ElMessage.success(task.message ? `${task.kindName}已完成：${task.message}` : `${task.kindName}已完成`)
    }
  } finally {
    // 无论成功、失败还是超时都要清掉，否则那行提示会一直留在页面上
    progressText.value = ''
  }
}

const progressText = ref('')

async function printCard(row: StudentListItem): Promise<void> {
  printingCard.value = true

  try {
    await runExportTask(
      { kind: 'StudentCardPdf', studentId: row.id },
      `学籍卡_${row.name}_${row.studentNo}.pdf`
    )
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '打印学籍卡失败，请稍后重试')
  } finally {
    printingCard.value = false
  }
}

/**
 * 打印花名册：**沿用当前筛选条件**。
 *
 * 这一点很重要：如果花名册忽略筛选条件，"我筛出二班来打印"就会印出全校，
 * 而这种错误在校对时才会被发现 —— 纸已经印出来了。
 */
async function printRoster(): Promise<void> {
  printingRoster.value = true

  try {
    // maxRows 放到 10000（原来同步打印时是 2000）：任务式之后没有请求超时压力，
    // 真正的上限由后端 Xjgl:Export:MaxRows 兜底，且超限会在 message 里如实说明。
    await runExportTask(
      {
        kind: 'StudentRosterPdf',
        rosterQuery: {
          schoolOrgId: query.schoolOrgId,
          studentStatusCode: query.studentStatusCode,
          keyword: query.keyword || undefined,
          maxRows: 10000
        }
      },
      '花名册.pdf'
    )
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '打印花名册失败，请稍后重试')
  } finally {
    printingRoster.value = false
  }
}

// ---------------------------------------------------------------------------
// 导入导出
// ---------------------------------------------------------------------------

/**
 * 当前查询条件（导出要按同样的条件走，避免"看到的是筛选结果、导出的是全量"）。
 *
 * ⚠ **不要传 maxResultCount**：ABP 对单次请求的 MaxResultCount 有硬上限（默认 1000），
 * 传 5000 会被参数校验直接拒掉（HTTP 400）。
 * 第一版这里写的就是 5000 —— 表现是"点导出没反应"（前端弹错误提示、不下载），
 * 而集成测试直接调服务层、绕过了 DTO 校验，所以一直没暴露，直到端到端测试才抓到。
 *
 * 导出改成任务式之后这里**依然不能传**：条件会被原样快照进任务，
 * 而分页累积由后端的 IStudentCsvExportBuilder 负责（每页 1000，
 * 总量上限是可配置的 Xjgl:Export:MaxRows，超限会在任务提示里说明差多少）。
 */
function currentQuery() {
  return {
    schoolOrgId: query.schoolOrgId,
    keyword: query.keyword || undefined,
    studentStatusCode: query.studentStatusCode,
    sorting: 'StudentNo ASC'
  }
}

const exporting = ref(false)

async function exportStudents(): Promise<void> {
  exporting.value = true

  try {
    await runExportTask(
      { kind: 'StudentCsv', studentQuery: currentQuery() },
      `学生名单_${new Date().toISOString().slice(0, 10)}.csv`
    )
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '导出失败，请稍后重试')
  } finally {
    exporting.value = false
  }
}

const importVisible = ref(false)
const importing = ref(false)
const importColumns = ref<ImportColumn[]>([])
const importResult = ref<ImportStudentsResult | null>(null)
const importFileName = ref('')
/** 上一次导入文件的编码判定结果，回显在导入对话框里（便于"为什么被拒"当场看清）。 */
const encodingNote = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

async function openImport(): Promise<void> {
  importVisible.value = true
  importResult.value = null
  importFileName.value = ''
  encodingNote.value = ''

  if (importColumns.value.length === 0) {
    try {
      importColumns.value = await studentImportApi.getColumns()
    } catch {
      importColumns.value = []
    }
  }
}

async function downloadTemplate(): Promise<void> {
  const csv = await studentImportApi.getTemplate()
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')

  link.href = url
  link.download = '学生导入模板.csv'
  link.click()
  URL.revokeObjectURL(url)
}

function pickFile(): void {
  fileInput.value?.click()
}

async function doImport(): Promise<void> {
  const input = fileInput.value

  if (!input?.files?.length) {
    ElMessage.warning('请先选择要导入的 CSV 文件。')
    return
  }

  const file = input.files[0]
  importFileName.value = file.name
  encodingNote.value = ''
  importResult.value = null
  importing.value = true

  try {
    // ⚠ 先按**字节**探测编码（债 10），再决定要不要送去后端。
    //
    // 直接用 `file.text()` 是错的：它按 UTF-8 解码，GBK 文件会变成一串替换字符，
    // 于是后端只回一句「导入文件缺少必需列：姓名、性别…」——
    // 用户看到的提示与真正原因（编码）毫无关系，会去反复检查列名。
    // 也不能用 `file.text()` 之后再找替换字符，因为 U+FFFD 是有损的，
    // 拿不到"第几个字节有问题"这种能指导排查的信息。
    const probe = probeCsvEncoding(await file.arrayBuffer())

    encodingNote.value = probe.detected

    if (!probe.ok) {
      // 明确拒绝并给出可执行的做法，而不是把乱码送去后端换回一句莫名其妙的报错
      ElMessage.error(probe.reason ?? '文件编码不是 UTF-8，无法导入。')

      return
    }

    importResult.value = await studentImportApi.import(probe.text)

    if (importResult.value.failed === 0) {
      ElMessage.success(importResult.value.message)
    } else {
      ElMessage.warning(importResult.value.message)
    }

    // 导入成功后刷新列表（部分成功也要刷新：成功的那部分已经落库了）
    if (importResult.value.succeeded > 0) {
      await load()
    }
  } finally {
    importing.value = false
    input.value = ''
  }
}

onMounted(async () => {
  await Promise.all([loadSchools(), loadStatusOptions()])
  await load()
})
</script>

<template>
  <div class="student-list">
    <el-card shadow="never" class="student-list__filter">
      <el-form :inline="true" @submit.prevent="search">
        <el-form-item label="学校">
          <!-- 用虚拟滚动的学校下拉：全省几万所学校，普通 el-select 会渲染几万个 DOM 节点把页面卡死 -->
          <SchoolSelect
            v-model="query.schoolOrgId"
            :schools="schools"
            placeholder="全部（按权限范围）"
            style="width: 240px"
          />
        </el-form-item>

        <el-form-item label="学籍状态">
          <el-select v-model="query.studentStatusCode" placeholder="全部" clearable style="width: 140px">
            <el-option
              v-for="s in statusOptions"
              :key="s.value"
              :label="s.label"
              :value="s.value"
            />
          </el-select>
        </el-form-item>

        <!--
          年级 / 班级：这两个下拉挂在「学校 + 学年」下，因此学校未选时是禁用的
          （`GradeClassSelect` 内部统一处理这条依赖链，页面不需要自己管）。
          学年默认取当前学年，可以手改 —— 查往年名单时才需要动它。
        -->
        <el-form-item label="学年">
          <el-input
            v-model="academicYear"
            placeholder="如 2025-2026"
            clearable
            style="width: 130px"
          />
        </el-form-item>

        <el-form-item label="年级">
          <GradeClassSelect
            ref="gradeClassSelect"
            v-model:grade-id="query.gradeId"
            v-model:class-id="query.classId"
            :school-org-id="query.schoolOrgId"
            :academic-year="academicYear"
            show="both"
          />
        </el-form-item>

        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="姓名 / 学籍辅号"
            clearable
            style="width: 200px"
            @keyup.enter="search"
          >
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="loading" @click="search">查询</el-button>
          <el-button @click="reset">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <template #header>
        <div class="student-list__header">
          <span>学生信息（共 {{ total }} 人）</span>
          <div>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Import)"
              plain
              @click="openImport"
            >
              <AppIcon name="Upload" />
              <span>导入</span>
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Export)"
              plain
              :loading="exporting"
              @click="exportStudents"
            >
              <AppIcon name="Download" />
              <span>导出</span>
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Print)"
              plain
              :loading="printingRoster"
              @click="printRoster"
            >
              <AppIcon name="Printer" />
              <span>打印花名册</span>
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Query)"
              plain
              @click="changeLogVisible = true"
            >
              <AppIcon name="Document" />
              <span>变更留痕</span>
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Create)"
              type="primary"
              @click="openCreate"
            >
              <AppIcon name="Plus" />
              <span>新增学生</span>
            </el-button>

            <!-- 导出/打印改成任务式之后要等后台生成（可能几十秒），
                 只说"转圈"用户不知道卡在哪一步，所以把真实状态写出来。 -->
            <span v-if="progressText" class="student-list__progress">{{ progressText }}</span>
          </div>
        </div>
      </template>

      <el-table v-loading="loading" :data="rows" border stripe height="calc(100vh - 320px)">
        <el-table-column type="index" label="#" width="55" align="center" />

        <el-table-column prop="studentNo" label="学籍辅号" width="160" />

        <el-table-column label="姓名" min-width="140">
          <template #default="{ row }">
            <span class="student-list__name">{{ row.name }}</span>
            <el-tag v-if="row.hasRareCharacter" size="small" type="warning" effect="plain" class="student-list__rare">
              生僻字
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column prop="genderName" label="性别" width="70" align="center" />
        <el-table-column label="出生日期" width="110" align="center">
          <template #default="{ row }">{{ formatDate(row.birthDate) }}</template>
        </el-table-column>
        <el-table-column prop="maskedIdCard" label="身份证号" width="180" />
        <el-table-column prop="schoolName" label="学校" min-width="180" show-overflow-tooltip />
        <el-table-column prop="gradeName" label="年级" width="90" />
        <el-table-column prop="className" label="班级" width="90" />
        <el-table-column prop="academicYear" label="学年" width="110" align="center" />

        <el-table-column label="学籍状态" width="100" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="IN_SCHOOL_CODES.includes(row.studentStatusCode) ? 'success' : 'info'">
              {{ row.studentStatusName ?? row.studentStatusCode }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="照片" width="80" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.hasPhoto" size="small" type="success" effect="plain">已上传</el-tag>
            <el-tag v-else size="small" type="danger" effect="plain">缺失</el-tag>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="230" align="center" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openProfile(row)">详情</el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Update)"
              link
              type="primary"
              @click="openEdit(row)"
            >
              修改
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Print)"
              link
              type="primary"
              @click="printCard(row)"
            >
              学籍卡
            </el-button>
            <el-button
              v-if="auth.can(MetadataPermissions.Stu.Student.Delete)"
              link
              type="danger"
              @click="removeStudent(row)"
            >
              删除
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="page"
        v-model:page-size="query.maxResultCount"
        class="student-list__pager"
        :total="total"
        layout="total, sizes, prev, pager, next, jumper"
        :page-sizes="[20, 50, 100]"
        @current-change="load"
        @size-change="search"
      />
    </el-card>

    <!-- ---------------- 新增 / 修改学生主档 ---------------- -->
    <el-dialog
      v-model="formVisible"
      :title="formMode === 'create' ? '新增学生' : '修改学生'"
      width="880px"
      top="5vh"
      destroy-on-close
    >
      <!--
        查重提示：后端在写入时也会拒绝，但"填完一整张表单才被告知已有学籍"
        是最差的交互，所以在这里提前讲清楚。
      -->
      <el-alert
        v-if="duplicateResult"
        type="warning"
        :closable="false"
        show-icon
        class="student-list__tip"
        title="疑似重复学籍"
      >
        <div v-for="(reason, i) in duplicateResult.reasons" :key="i">· {{ reason }}</div>
        <div v-for="m in duplicateResult.matched" :key="m.id" class="student-list__muted">
          已存在：{{ m.name }}（学籍辅号 {{ m.studentNo }}，{{ m.schoolName ?? '未知学校' }}）
        </div>
      </el-alert>

      <el-form ref="formRef" :model="form" :rules="formRules" label-width="110px">
        <el-divider content-position="left">必填</el-divider>

        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="姓名" prop="name">
              <el-input v-model="form.name" placeholder="与户口簿一致" @blur="runDuplicateCheck" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="性别" prop="genderCode">
              <el-select v-model="form.genderCode" placeholder="请选择" style="width: 100%">
                <el-option
                  v-for="s in genderCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="出生日期" prop="birthDate">
              <el-date-picker
                v-model="form.birthDate"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="选择日期"
                style="width: 100%"
                @change="runDuplicateCheck"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="身份证件类型" prop="idCardTypeCode">
              <el-select v-model="form.idCardTypeCode" placeholder="请选择" style="width: 100%">
                <el-option
                  v-for="s in idTypeCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="身份证件号码" prop="idCardNumber">
          <el-input
            v-model="form.idCardNumber"
            placeholder="必须重新输入（身份证加密存储，界面不回显明文）"
            @blur="runDuplicateCheck"
          />
          <div v-if="formMode === 'edit' && editingMaskedIdCard" class="student-list__muted">
            当前库中：{{ editingMaskedIdCard }} —— 加密存储无法回显，保存时请确认这一栏填的是同一个人
          </div>
          <div v-if="duplicateChecking" class="student-list__muted">正在查重…</div>
        </el-form-item>

        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="学校" prop="schoolOrgId">
              <SchoolSelect
                v-model="form.schoolOrgId"
                :schools="schools"
                placeholder="请选择学校"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="学年" prop="academicYear">
              <el-input v-model="form.academicYear" placeholder="如 2025-2026" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="年级 / 班级">
              <GradeClassSelect
                v-model:grade-id="form.gradeId"
                v-model:class-id="form.classId"
                :school-org-id="form.schoolOrgId"
                :academic-year="form.academicYear"
                show="both"
                grade-width="140px"
                class-width="140px"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="学籍状态">
              <el-select
                v-model="form.studentStatusCode"
                placeholder="默认在读"
                clearable
                style="width: 100%"
              >
                <el-option
                  v-for="s in statusOptions"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">基本信息</el-divider>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="曾用名">
              <el-input v-model="form.formerName" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="班内学号">
              <el-input v-model="form.classStudentNo" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="民族">
              <el-select v-model="form.nationCode" clearable placeholder="请选择" style="width: 100%">
                <el-option
                  v-for="s in nationCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="国家 / 地区">
              <el-select v-model="form.countryCode" clearable placeholder="请选择" style="width: 100%">
                <el-option
                  v-for="s in countryCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="政治面貌">
              <el-select
                v-model="form.politicalCode"
                clearable
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in politicalCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="户口性质">
              <el-select
                v-model="form.householdNatureCode"
                clearable
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in householdCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="籍贯">
              <el-input v-model="form.nativePlace" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="出生地">
              <el-input v-model="form.birthPlace" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="入学日期">
              <el-date-picker
                v-model="form.enrollDate"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="选择日期"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="入学方式">
              <el-select
                v-model="form.enrollTypeCode"
                clearable
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in enrollTypeCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="就读方式">
              <el-select
                v-model="form.studyModeCode"
                clearable
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in studyModeCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="学制">
              <el-select
                v-model="form.schoolLengthCode"
                clearable
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in schoolLengthCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">联系方式与住址</el-divider>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="联系电话">
              <el-input v-model="form.phoneNumber" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="邮政编码">
              <el-input v-model="form.postalCode" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="电子邮箱">
              <el-input v-model="form.email" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="户口所在地">
          <el-input v-model="form.householdAddress" />
        </el-form-item>
        <el-form-item label="家庭住址">
          <el-input v-model="form.homeAddress" />
        </el-form-item>
        <el-form-item label="现居住址">
          <el-input v-model="form.currentAddress" />
        </el-form-item>

        <el-divider content-position="left">特殊情况</el-divider>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="随迁子女">
              <el-switch v-model="form.isMigrantChild" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="随迁类型">
              <el-select
                v-model="form.migrantTypeCode"
                clearable
                :disabled="!form.isMigrantChild"
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in migrantCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="留守儿童">
              <el-switch v-model="form.isLeftBehindChild" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="留守类型">
              <el-select
                v-model="form.leftBehindTypeCode"
                clearable
                :disabled="!form.isLeftBehindChild"
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in leftBehindCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="家庭经济困难">
              <el-switch v-model="form.isPoor" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残疾">
              <el-switch v-model="form.isDisabled" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="残疾类型">
              <el-select
                v-model="form.disabilityCode"
                clearable
                :disabled="!form.isDisabled"
                placeholder="请选择"
                style="width: 100%"
              >
                <el-option
                  v-for="s in disabilityCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="健康状况">
              <el-select v-model="form.healthCode" clearable placeholder="请选择" style="width: 100%">
                <el-option
                  v-for="s in healthCodes.options.value"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="备注">
          <el-input v-model="form.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="formVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submitForm">
          {{ formMode === 'create' ? '建档' : '保存' }}
        </el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 批量导入 ---------------- -->
    <el-dialog v-model="importVisible" title="批量导入学生" width="820px" top="6vh">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="student-list__tip"
        title="导入与手工录入走完全相同的校验（身份证校验位、跨校查重、生僻字姓名、学籍号生成、数据范围），逐行容错：个别行出错不影响其它行。"
      />

      <!--
        编码提示必须写在界面上、而不是只在失败时弹一次消息：
        "Excel 另存为 CSV 默认是 GBK" 是本功能唯一的高频失败原因，
        用户第一次就会被挡下，之后每次回来都应该看得见怎么另存。
      -->
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        class="student-list__tip"
        title="文件必须是 UTF-8 编码。Excel 的「另存为 CSV」默认用本机编码（简体中文为 GBK），中文会变成乱码并被拒绝 —— 请选择「CSV UTF-8（逗号分隔）」。"
      />

      <el-space wrap class="student-list__import-actions">
        <el-button @click="downloadTemplate">
          <AppIcon name="Download" />
          <span>下载模板</span>
        </el-button>

        <el-button type="primary" :loading="importing" @click="pickFile">
          <AppIcon name="Upload" />
          <span>选择 CSV 文件并导入</span>
        </el-button>

        <span v-if="importFileName" class="student-list__filename">{{ importFileName }}</span>

        <el-tag v-if="encodingNote" size="small" effect="plain">
          编码：{{ encodingNote }}
        </el-tag>

        <input ref="fileInput" type="file" accept=".csv,text/csv" hidden @change="doImport" />
      </el-space>

      <el-collapse v-if="importColumns.length > 0" class="student-list__columns">
        <el-collapse-item :title="`列说明（共 ${importColumns.length} 列，按表头名匹配，列顺序可调）`">
          <el-table :data="importColumns" size="small" border>
            <el-table-column prop="name" label="列名" width="120" />
            <el-table-column label="必需" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.required" size="small" type="danger" effect="plain">必需</el-tag>
                <span v-else class="student-list__muted">可选</span>
              </template>
            </el-table-column>
            <el-table-column prop="remark" label="说明" min-width="280" />
          </el-table>
        </el-collapse-item>
      </el-collapse>

      <template v-if="importResult">
        <el-alert
          :type="importResult.failed === 0 ? 'success' : 'warning'"
          :closable="false"
          show-icon
          class="student-list__tip"
          :title="importResult.message"
        />

        <el-table
          v-if="importResult.errors.length > 0"
          :data="importResult.errors"
          size="small"
          border
          max-height="300"
        >
          <el-table-column prop="rowIndex" label="行号" width="70" align="center" />
          <el-table-column prop="name" label="姓名" width="110" />
          <el-table-column prop="idCardNumber" label="身份证号" width="180" />
          <el-table-column prop="message" label="失败原因" min-width="300" />
        </el-table>
      </template>

      <template #footer>
        <el-button @click="importVisible = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 学生档案（监护人 / 简历 / 奖励 / 处分 / 资助 / 体检） -->
    <StudentProfileDrawer v-model="profileVisible" :student="profileStudent" @changed="load" />

    <!-- 全局变更留痕（按动作 / 时间 / 学生关键字查，受数据范围约束） -->
    <StudentChangeLogDialog v-model="changeLogVisible" />
  </div>
</template>

<style scoped>
.student-list__filter {
  margin-bottom: 16px;
}

.student-list__filter :deep(.el-form-item) {
  margin-bottom: 0;
}

.student-list__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.student-list__name {
  margin-right: 6px;
}

.student-list__rare {
  transform: scale(0.9);
}

/* 导出/打印任务的进行状态（排队中 / 已处理 N / M（x%） / 正在生成） */
.student-list__progress {
  display: inline-block;
  margin-left: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  vertical-align: middle;
}

.student-list__pager {
  margin-top: 16px;
  justify-content: flex-end;
}

.student-list__tip {
  margin-bottom: 12px;
}

.student-list__import-actions {
  margin-bottom: 12px;
}

.student-list__filename {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.student-list__columns {
  margin-bottom: 12px;
}

.student-list__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
