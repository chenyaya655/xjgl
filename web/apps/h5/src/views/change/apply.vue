<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'

import {
  changeFlowApi,
  changeTypeApi,
  MetadataPermissions,
  orgApi,
  OrgTypes,
  studentApi
} from '@xjgl/api'
import type { ChangeApply, ChangeType, OrgDto, StudentListItem } from '@xjgl/api'
import { useAuthStore } from '@/stores/auth'

/**
 * 发起异动申请 —— ROADMAP 10 的最后一块（H5 端功能补全）。
 *
 * 【为什么入口在「我的」页，而不是「待办」页】
 *   「待办」的语义是**"轮到我审的任务"**（见 views/todo/index.vue 顶部注释：
 *   它回答的是"有没有轮到我审"）。"发起申请"是**另一个方向的业务**：
 *   我是申请人，接下来轮到别人审。把两者放进同一个列表，教师看到一条记录时
 *   第一件事得先判断"这条是让我审的、还是我提的"，而这个判断一旦出错，
 *   后果是把该自己审的单子当成自己提的（或反过来）—— 这是语义混用最典型的代价。
 *   而「我的」是**每个人都有的功能入口页**（身份、单位、退出都在那），
 *   把"我能发起的业务"放在这里，不依赖任何审核角色，语义也不会串。
 *
 * 【为什么目标学校是"关键字搜索"，不是整棵组织树】
 *   全量组织树在 H5 上不可行：`orgApi.getTree()` 的注释就写着"上万所学校时不要用"，
 *   移动网络下这一棵树本身就能把首屏拖死。而教师的真实处境是
 *   **知道学校叫什么、不知道它在哪个县**，所以正确的交互是搜名字，
 *   而不是让他从"省 → 市 → 县 → 校"一层层点下去。
 *   代价写在下面：`orgApi.getList` 返回的是 `List<OrgDto>`（**不分页**），
 *   关键字越短命中越多，因此这里
 *     - 至少 2 个字才发请求（一个字可能命中上千所学校）；
 *     - 最多渲染 20 条，且**明确提示"结果较多"**（静默截断会让老师以为
 *       "搜不到我们学校"，从而得出"系统里没有这所学校"的错误结论）。
 *
 * 【为什么学生也必须人工点选确认】
 *   与拍照上传那条"确认姓名"同一个理由（见 views/photo/index.vue）：
 *   错人很难事后发现 —— 照片装错人、异动申请提错人，往往要等到
 *   学生毕业/转学时才发现，而那时已经流转了好几级审核。
 *   所以这里**不自动选中第一条**：必须由老师点一下搜索结果，
 *   界面再把姓名、学籍辅号、学校班级原样摊开给他核对。
 *
 * 【与 PC 端的关系】
 *   提交的形状（`changeFlowApi.submit`）与 PC `views/change/apply/index.vue`
 *   完全一致：那是这个后端契约的权威用法。差异只在交互：PC 是弹窗 + 表格，
 *   H5 是一屏一事的卡片流。
 */
const auth = useAuthStore()
const router = useRouter()

const canCreate = computed(() => auth.can(MetadataPermissions.Chg.Apply.Create))

/**
 * 关键字的最少字数：**2**。
 * 一个字（例如"中"）在后端是 `Name.Contains` 匹配，能命中几百上千所学校，
 * 拉回来既慢又没用 —— 与其让老师等一个必然被截断的结果，不如让他多打一个字。
 */
const MIN_KEYWORD = 2

/** 一次最多渲染多少条搜索结果（两处搜索共用同一个口径）。 */
const MAX_ROWS = 20

// ---------------------------------------------------------------------------
// 表单
// ---------------------------------------------------------------------------

const types = ref<ChangeType[]>([])
const submitting = ref(false)

/** 异动类型是不是"没拉到"（而不是"确实一条都没有"）—— 两种情况的提示不该一样。 */
const typesLoadFailed = ref(false)

/** 提交成功后的结果；不为空时整页切换成"结果页"，避免老师重复点提交。 */
const result = ref<ChangeApply | null>(null)

const form = reactive({
  changeTypeId: undefined as string | undefined,
  studentId: undefined as string | undefined,
  targetSchoolOrgId: undefined as string | undefined,
  reason: '',
  remark: ''
})

/**
 * 可申请的异动类型。
 *
 * 两个条件都必要：
 *   1) **启用** —— 停用的类型后端直接拒（"异动类型已停用"），列出来只会让人白填；
 *      这一条交给后端过滤（`getTypes(true)`），移动端少拉一半数据；
 *   2) **已发布流程** —— 与 PC 端同一条理由：没配流程就提交，会生成一个
 *      没有任何审核节点的申请，卡死在"待审核"且没人能处理（后端只会回一句
 *      "流程实例化后没有任何可执行的节点"）。宁可在这里就说"没有可申请的类型"。
 */
const selectableTypes = computed(() => types.value.filter((t) => t.hasPublishedFlow))

const selectedType = computed(() => selectableTypes.value.find((t) => t.id === form.changeTypeId))

/**
 * 只有"允许跨校"的异动类型才需要选目标学校。
 *
 * 这不是界面上的取舍，而是后端契约：`SubmitAsync` 里
 * `targetSchoolId = input.TargetSchoolOrgId ?? student.SchoolOrgId`，
 * 一旦传了与学籍校不同的学校、而类型不允许跨校，就直接报"不允许跨校办理"。
 * 所以休学/退学这类不换学校的异动**不显示**这一块，也不会把它提交上去。
 */
const needTargetSchool = computed(() => selectedType.value?.allowCrossSchool ?? false)

// ---------------------------------------------------------------------------
// 学生搜索（学籍辅号 / 姓名）
// ---------------------------------------------------------------------------

const studentKeyword = ref('')
const searchingStudent = ref(false)
const studentRows = ref<StudentListItem[]>([])
/** 命中数超过渲染上限：必须明确提示，不能静默截断。 */
const studentTruncated = ref(false)
const studentSearched = ref(false)
const selectedStudent = ref<StudentListItem | null>(null)

async function searchStudents(): Promise<void> {
  const keyword = studentKeyword.value.trim()

  if (keyword.length < MIN_KEYWORD) {
    ElMessage.warning(`请至少输入 ${MIN_KEYWORD} 个字（学籍辅号或姓名）。`)

    return
  }

  searchingStudent.value = true

  try {
    const found = await studentApi.getList({
      keyword,
      skipCount: 0,
      maxResultCount: MAX_ROWS,
      // 按学籍辅号排序：与班主任手里的名册顺序一致，便于逐行核对
      sorting: 'StudentNo ASC'
    })

    studentRows.value = found.items
    // totalCount 是**命中总数**，items 只有前 20 条：两者不等就说明还有更多
    studentTruncated.value = found.totalCount > found.items.length
    studentSearched.value = true
  } finally {
    searchingStudent.value = false
  }
}

/**
 * 点选学生（**人工确认**这一步）。
 *
 * 刻意不在这里"顺便"提交任何东西，也不自动选第一条：这一下点击就是老师的核对动作，
 * 选完界面会把姓名与学籍辅号摊在最显眼处。
 * 另外，新一轮搜索**不清空**已选学生 —— 老师可能只是想再搜一下确认有没有重名，
 * 此时把他已经确认过的人悄悄换掉，比让他多点一次"重新选择"危险得多。
 */
function selectStudent(row: StudentListItem): void {
  form.studentId = row.id
  selectedStudent.value = row
}

function clearStudent(): void {
  form.studentId = undefined
  selectedStudent.value = null
}

// ---------------------------------------------------------------------------
// 目标学校搜索（关键字，不用组织树）
// ---------------------------------------------------------------------------

const schoolKeyword = ref('')
const searchingSchool = ref(false)
const schoolRows = ref<OrgDto[]>([])
const schoolTruncated = ref(false)
const schoolSearched = ref(false)
const selectedSchool = ref<OrgDto | null>(null)

async function searchSchools(): Promise<void> {
  const keyword = schoolKeyword.value.trim()

  if (keyword.length < MIN_KEYWORD) {
    ElMessage.warning(`请至少输入 ${MIN_KEYWORD} 个字（学校名称）。`)

    return
  }

  searchingSchool.value = true

  try {
    const found = await orgApi.getList({
      keyword,
      // 码值来自 @xjgl/api 的 OrgTypes，不手写 'School' 字面量：后端比对的就是它
      // （`BuildPredicate` 里是 `o.OrgType == orgType` 的字符串相等）
      orgType: OrgTypes.School,
      onlyEnabled: true
    })

    // 后端过滤之外再按类型滤一遍：这个接口是通用的组织查询，返回什么完全由参数决定，
    // 这里再确认一次"只留学校"，将来有人改了参数也不会把县区混进"转入学校"列表
    const schools = found.filter((o) => o.orgType === OrgTypes.School)

    schoolTruncated.value = schools.length > MAX_ROWS
    schoolRows.value = schools.slice(0, MAX_ROWS)
    schoolSearched.value = true
  } finally {
    searchingSchool.value = false
  }
}

function selectSchool(row: OrgDto): void {
  form.targetSchoolOrgId = row.id
  selectedSchool.value = row
}

function clearSchool(): void {
  form.targetSchoolOrgId = undefined
  selectedSchool.value = null
}

// ---------------------------------------------------------------------------
// 提交
// ---------------------------------------------------------------------------

function resetForm(): void {
  form.changeTypeId = undefined
  form.studentId = undefined
  form.targetSchoolOrgId = undefined
  form.reason = ''
  form.remark = ''

  studentKeyword.value = ''
  studentRows.value = []
  studentTruncated.value = false
  studentSearched.value = false
  selectedStudent.value = null

  schoolKeyword.value = ''
  schoolRows.value = []
  schoolTruncated.value = false
  schoolSearched.value = false
  selectedSchool.value = null
}

/**
 * 提交申请。
 *
 * 【失败时不清空任何已填内容】
 * 这里**没有 catch**：错误提示由 axios 拦截器统一弹出（http.ts 的
 * "错误提示只在这里弹"），再写一遍 catch 就会同一个错误弹两次。
 * 而"是否清空表单"取决于成功与否 —— 只有成功才 `resetForm()`。
 * 原因是使用场景：老师常常站在走廊里用手机填，给他一份被清空的表单
 * 等于让他重填一遍异动类型、学生、理由，成本远高于一次错误提示。
 */
async function doSubmit(): Promise<void> {
  if (!canCreate.value) {
    // 理论上到不了这里（入口与整页都由权限把关），但真到了也不该让后端回 403 了事
    ElMessage.warning('您没有发起异动申请的权限。')

    return
  }

  if (!form.changeTypeId) {
    ElMessage.warning('请选择异动类型。')

    return
  }

  if (!form.studentId) {
    ElMessage.warning('请先搜索并点选学生（确认是他本人）。')

    return
  }

  if (needTargetSchool.value && !form.targetSchoolOrgId) {
    ElMessage.warning('该异动类型需要选择目标学校。')

    return
  }

  if (!form.reason.trim()) {
    ElMessage.warning('请填写申请理由。')

    return
  }

  submitting.value = true

  try {
    const apply = await changeFlowApi.submit({
      changeTypeId: form.changeTypeId,
      studentId: form.studentId,
      // 不需要跨校时传 null，而不是"把界面上残留的选择一起提交"：
      // 后端会拿它当转入校去实例化流程，传错等于把流程走到了别的学校
      targetSchoolOrgId: needTargetSchool.value ? (form.targetSchoolOrgId ?? null) : null,
      reason: form.reason.trim(),
      remark: form.remark.trim() || null
    })

    result.value = apply
    resetForm()
  } finally {
    submitting.value = false
  }
}

function startAnother(): void {
  result.value = null
}

/**
 * 返回「我的」。
 *
 * 用 replace 而不是 back()：老师可能从微信里直接打开这个地址，
 * 此时 back() 会退出应用（hash 路由下表现为白屏），replace 有确定落点
 * （与 photo/missing.vue 的 goBack 同一个处理）。
 *
 * 【为什么成功后的落点是「我的」而不是「待办」】
 *   自己刚提交的申请**不会**出现在「待办」里 —— 那是"轮到我审"的队列
 *   （见本文件顶部注释）。把老师引导到待办页，他看到的是别人的单子、
 *   或者一个空列表，只会怀疑"我刚才提交成功了没有"。回到发起它的地方
 *   （「我的」→ 业务入口），语义才是连贯的。
 */
function goBack(): void {
  void router.replace({ name: 'mine' })
}

/**
 * 拉取可申请的异动类型。
 *
 * 失败时**不在这里弹提示**（拦截器已经说过一次了），只记一个标志位：
 * 否则界面会把"加载失败"和"确实没配任何类型"混成同一句话，
 * 让老师白跑一趟电脑端去建类型。
 */
async function loadTypes(): Promise<void> {
  typesLoadFailed.value = false

  try {
    types.value = await changeTypeApi.getTypes(true)
  } catch {
    typesLoadFailed.value = true
  }
}

onMounted(() => {
  if (!canCreate.value) {
    return
  }

  void loadTypes()
})
</script>

<template>
  <div class="h5-page">
    <div class="chg__head">
      <h2 class="h5-title">发起异动申请</h2>
      <el-button size="small" @click="goBack">返回</el-button>
    </div>

    <el-alert
      v-if="!canCreate"
      type="warning"
      :closable="false"
      show-icon
      title="您没有发起异动申请的权限，本页不可用。"
    />

    <!-- ---------------- 提交结果 ---------------- -->
    <el-card v-else-if="result" shadow="never" class="chg__card">
      <div class="chg__done-title">申请已提交</div>

      <el-descriptions :column="1" size="small" border>
        <el-descriptions-item label="申请单号">{{ result.applyNo }}</el-descriptions-item>
        <el-descriptions-item label="学生">
          {{ result.studentName }}
          <span v-if="result.studentNo">（{{ result.studentNo }}）</span>
        </el-descriptions-item>
        <el-descriptions-item label="异动类型">{{ result.changeTypeName }}</el-descriptions-item>
        <el-descriptions-item label="学校变动">
          {{ result.sourceSchoolName || '—' }}
          <span v-if="result.targetSchoolName"> → {{ result.targetSchoolName }}</span>
        </el-descriptions-item>
        <el-descriptions-item label="审核步骤">共 {{ result.tasks.length }} 步</el-descriptions-item>
      </el-descriptions>

      <div class="h5-muted chg__done-tip">
        系统已按该异动类型的已发布流程自动生成审核待办（同一单位只会审一次）。
        后续进度请在电脑端的「异动申请」里查看。
      </div>

      <div class="chg__done-actions">
        <el-button size="large" @click="startAnother">再发起一笔</el-button>
        <el-button size="large" type="primary" @click="goBack">返回我的</el-button>
      </div>
    </el-card>

    <!-- ---------------- 表单 ---------------- -->
    <template v-else>
      <el-card shadow="never" class="chg__card">
        <div class="chg__label">异动类型 <span class="chg__req">*</span></div>

        <el-select
          v-model="form.changeTypeId"
          size="large"
          class="chg__full"
          placeholder="请选择异动类型"
        >
          <el-option v-for="t in selectableTypes" :key="t.id" :label="t.name" :value="t.id" />
        </el-select>

        <div v-if="typesLoadFailed" class="chg__hint chg__hint--danger">
          异动类型加载失败，请检查网络后重试。
          <el-button text type="primary" size="small" @click="loadTypes">重试</el-button>
        </div>
        <div v-else-if="selectableTypes.length === 0" class="chg__hint chg__hint--danger">
          没有可申请的异动类型：需要先在电脑端「异动类型管理」里创建类型，并发布流程。
        </div>
        <div v-else-if="selectedType" class="chg__hint h5-muted">
          最高 {{ selectedType.maxApproveLevelName }} 审核
          <template v-if="needTargetSchool"> · 该类型可跨校，需选择目标学校</template>
        </div>
      </el-card>

      <!-- 学生：搜索 → 点选确认 -->
      <el-card shadow="never" class="chg__card">
        <div class="chg__label">学生 <span class="chg__req">*</span></div>

        <div class="chg__row">
          <el-input
            v-model="studentKeyword"
            size="large"
            placeholder="学籍辅号或姓名（至少2字）"
            clearable
            @keyup.enter="searchStudents"
          />
          <el-button size="large" type="primary" :loading="searchingStudent" @click="searchStudents">
            查找
          </el-button>
        </div>

        <!--
          已选学生：选完就把结果列表收起来（下面的 v-else），让"核对这一下"成为
          屏幕上唯一的事 —— 两个理由：
            1) 这是**人工确认**动作，确认对象被别的东西包围着就容易一眼扫过去；
            2) 结果列表在点击点**上方**插入内容会把整行往下推，手指还停在原处，
               连续两次点击很容易点到刚滑上来的另一行 —— 而"点错人"正是这里最怕的错。
          「重新选择」只是清掉选中项、把刚才那份结果重新摊开（不重新请求），
          所以点错了不费事。
        -->
        <div v-if="selectedStudent" class="chg__picked">
          <div class="chg__picked-head">已选学生（请核对姓名）</div>
          <div class="chg__picked-name">
            {{ selectedStudent.name }}
            <span class="h5-muted">{{ selectedStudent.studentNo }}</span>
          </div>
          <div class="h5-muted chg__picked-sub">
            {{ selectedStudent.schoolName || '—' }}
            {{ selectedStudent.gradeName || '' }}
            {{ selectedStudent.className || '' }}
          </div>
          <el-button text type="primary" class="chg__reselect" @click="clearStudent">
            重新选择
          </el-button>
        </div>

        <template v-else>
          <div v-if="studentSearched && studentRows.length === 0" class="chg__hint">
            没有匹配的学生，请换一个学籍辅号或姓名再试。
          </div>

          <div v-if="studentRows.length > 0" class="chg__results">
            <div class="h5-muted chg__results-tip">
              共 {{ studentRows.length }} 条，点一下即选中（不会自动帮你选）
            </div>

            <div
              v-for="s in studentRows"
              :key="s.id"
              class="chg__option"
              @click="selectStudent(s)"
            >
              <div class="chg__option-name">
                {{ s.name }}
                <span class="h5-muted">{{ s.studentNo }}</span>
              </div>
              <div class="h5-muted chg__option-sub">
                {{ s.schoolName || '—' }} {{ s.gradeName || '' }} {{ s.className || '' }}
              </div>
            </div>

            <div v-if="studentTruncated" class="chg__hint">
              结果较多，请继续输入以缩小范围。
            </div>
          </div>
        </template>
      </el-card>

      <!-- 目标学校：只有允许跨校的类型才需要 -->
      <el-card v-if="needTargetSchool" shadow="never" class="chg__card">
        <div class="chg__label">目标学校 <span class="chg__req">*</span></div>

        <div class="chg__row">
          <el-input
            v-model="schoolKeyword"
            size="large"
            placeholder="学校名称（至少2字）"
            clearable
            @keyup.enter="searchSchools"
          />
          <el-button size="large" type="primary" :loading="searchingSchool" @click="searchSchools">
            搜索
          </el-button>
        </div>

        <!-- 目标学校同样"选中后收起结果"：选中的那一所必须一眼可见 -->
        <div v-if="selectedSchool" class="chg__picked">
          <div class="chg__picked-head">目标学校</div>
          <div class="chg__picked-name">{{ selectedSchool.name }}</div>
          <div class="h5-muted chg__picked-sub">学校编码：{{ selectedSchool.code }}</div>
          <el-button text type="primary" class="chg__reselect" @click="clearSchool">
            清空重选
          </el-button>
        </div>

        <template v-else>
          <div v-if="schoolSearched && schoolRows.length === 0" class="chg__hint">
            没有匹配的学校，请换一个关键字（只搜学校名称，不含县区）。
          </div>

          <div v-if="schoolRows.length > 0" class="chg__results">
            <div
              v-for="o in schoolRows"
              :key="o.id"
              class="chg__option"
              @click="selectSchool(o)"
            >
              <div class="chg__option-name">{{ o.name }}</div>
              <div class="h5-muted chg__option-sub">{{ o.orgTypeName }} · {{ o.code }}</div>
            </div>

            <div v-if="schoolTruncated" class="chg__hint">结果较多，请继续输入以缩小范围。</div>
          </div>
        </template>
      </el-card>

      <!-- 理由与备注 -->
      <el-card shadow="never" class="chg__card">
        <div class="chg__label">申请理由 <span class="chg__req">*</span></div>
        <el-input
          v-model="form.reason"
          type="textarea"
          :rows="3"
          maxlength="1024"
          show-word-limit
          placeholder="如：家庭搬迁至XX区"
        />

        <div class="chg__label chg__label--gap">备注</div>
        <el-input
          v-model="form.remark"
          type="textarea"
          :rows="2"
          maxlength="1024"
          show-word-limit
          placeholder="可选"
        />

        <div class="h5-muted chg__notice">
          提交后系统会按该异动类型的已发布流程自动生成审核待办；同一单位只会审一次。
        </div>
      </el-card>

      <div class="chg__submit">
        <el-button
          size="large"
          type="primary"
          class="chg__full"
          :loading="submitting"
          @click="doSubmit"
        >
          提交申请
        </el-button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.chg__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.chg__card {
  margin-bottom: 12px;
  border-radius: 10px;
}

.chg__label {
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 8px;
}

.chg__label--gap {
  margin-top: 14px;
}

.chg__req {
  color: var(--el-color-danger);
}

.chg__full {
  width: 100%;
}

.chg__row {
  display: flex;
  gap: 8px;
}

/* 输入框必须能收缩：320px 宽下"输入框 + 按钮"不给 min-width:0 就会横向溢出 */
.chg__row .el-input {
  flex: 1;
  min-width: 0;
}

.chg__hint {
  margin-top: 8px;
  font-size: 12px;
  color: var(--el-color-warning);
  line-height: 1.6;
}

.chg__hint--danger {
  color: var(--el-color-danger);
}

/* 已选项：整块都用于"让人核对"，因此给它一个明确的底色与边框 */
.chg__picked {
  margin-top: 10px;
  padding: 10px 12px;
  border: 1px solid var(--el-color-primary);
  border-radius: 8px;
  background: var(--el-color-primary-light-9);
}

.chg__picked-head {
  font-size: 12px;
  color: var(--el-color-primary);
  margin-bottom: 4px;
}

.chg__picked-name {
  font-size: 16px;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.chg__picked-sub {
  margin-top: 2px;
  overflow-wrap: anywhere;
}

.chg__reselect {
  padding: 0;
  margin-top: 4px;
  height: 28px;
}

.chg__results {
  margin-top: 10px;
}

.chg__results-tip {
  margin-bottom: 6px;
}

/* 可选行：整行都是触摸目标（≥44px 高），不依赖 hover */
.chg__option {
  padding: 10px 12px;
  margin-bottom: 8px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: #fff;
  min-height: 44px;
  cursor: pointer;
}

.chg__option-name {
  font-size: 15px;
  overflow-wrap: anywhere;
}

.chg__option-sub {
  margin-top: 2px;
  overflow-wrap: anywhere;
}

.chg__notice {
  margin-top: 12px;
  line-height: 1.7;
}

.chg__submit {
  padding-bottom: 4px;
}

.chg__done-title {
  font-size: 16px;
  font-weight: 600;
  margin-bottom: 10px;
}

.chg__done-tip {
  margin-top: 10px;
  line-height: 1.7;
}

.chg__done-actions {
  display: flex;
  gap: 8px;
  margin-top: 14px;
}

.chg__done-actions .el-button {
  flex: 1;
  margin-left: 0;
}
</style>
