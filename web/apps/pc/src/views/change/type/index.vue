<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { changeTypeApi, codeApi, orgApi } from '@/api'
import type {
  ChangeFlowTemplate,
  ChangeType,
  CreateUpdateChangeType,
  CreateUpdateFlowNode,
  OrgDto,
  PreviewFlowResult
} from '@/api/types'
import {
  ApproveLevelNames,
  ApproveLevels,
  ChangeMergeStrategies,
  ChangeMergeStrategyNames,
  ChangeOrgResolvers,
  ChangeOrgResolverNames
} from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'
import { flattenSchools } from '@/utils/org'

/**
 * 异动类型管理 + 流程模板配置。
 *
 * 这个页面是「业务人员根据实际需要可以自行调整流程而不需要改动程序」的落点，
 * 所以它要解决的核心问题不是"能编辑"，而是**改完能立刻知道会发生什么**：
 *   - 流程节点用「组织解析器」这种相对描述（如"转入学校所在县区"），
 *     而不是写死具体组织；
 *   - 右侧随时可以**预览**：选一组"原学校 + 目标学校"，
 *     立刻看到实际会走几步、每步审到哪一级、哪几步被合并了。
 *     这就是"同县区转学只审一次"能被当场验证的地方。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Chg.Type.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Chg.Type.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Chg.Type.Delete))

const loading = ref(false)
const types = ref<ChangeType[]>([])
const schools = ref<OrgDto[]>([])

/**
 * 学籍状态下拉项**来自码表**（STUDENT_STATUS），不在前端硬编码。
 *
 * 这里原来是 `{ value: 'Studying', label: '在读' }` 这样的英文名，而这两个字段
 * （beforeStatusCode / afterStatusCode）会在**审批通过时直接写进学生的学籍状态**。
 * 也就是说：配错一次，审批一次，就给学生写上了一个码表里不存在的状态值。
 * 后端也加了同样的校验（ChangeTypeAppService.EnsureStudentStatusCodesAsync），
 * 双保险：前端不给错选项，后端不接受错值。
 */
const studentStatusOptions = ref<{ value: string; label: string }[]>([])

function statusLabel(code?: string | null): string {
  if (!code) {
    return '—'
  }

  return studentStatusOptions.value.find((o) => o.value === code)?.label ?? code
}

async function loadStudentStatusOptions(): Promise<void> {
  try {
    const items = await codeApi.getItemsByCode('STUDENT_STATUS')

    studentStatusOptions.value = items.map((i) => ({ value: i.code, label: i.name }))
  } catch {
    studentStatusOptions.value = []
  }
}

const levelOptions = Object.values(ApproveLevels).map((v) => ({
  value: v,
  label: ApproveLevelNames[v]
}))

// ---------------------------------------------------------------------------
// 类型列表
// ---------------------------------------------------------------------------

async function load(): Promise<void> {
  loading.value = true

  try {
    types.value = await changeTypeApi.getTypes(false)
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------------------
// 类型编辑
// ---------------------------------------------------------------------------

const editVisible = ref(false)
const editingId = ref<string | null>(null)
const saving = ref(false)

function emptyType(): CreateUpdateChangeType {
  return {
    code: '',
    name: '',
    categoryCode: null,
    beforeStatusCode: null,
    afterStatusCode: null,
    maxApproveLevel: ApproveLevels.City,
    needPeerSchoolConfirm: false,
    affectGrade: false,
    affectClass: false,
    allowCrossSchool: true,
    applyWindowStart: null,
    applyWindowEnd: null,
    allowCrossAcademicYear: false,
    maxApplyTimesPerYear: 1,
    gradeScope: null,
    requireAttachment: false,
    sort: 10,
    isEnabled: true,
    remark: null
  }
}

const form = ref<CreateUpdateChangeType>(emptyType())

function openCreate(): void {
  editingId.value = null
  form.value = emptyType()
  editVisible.value = true
}

function openEdit(row: ChangeType): void {
  editingId.value = row.id
  form.value = {
    code: row.code,
    name: row.name,
    categoryCode: row.categoryCode,
    beforeStatusCode: row.beforeStatusCode,
    afterStatusCode: row.afterStatusCode,
    maxApproveLevel: row.maxApproveLevel,
    needPeerSchoolConfirm: row.needPeerSchoolConfirm,
    affectGrade: row.affectGrade,
    affectClass: row.affectClass,
    allowCrossSchool: row.allowCrossSchool,
    applyWindowStart: row.applyWindowStart,
    applyWindowEnd: row.applyWindowEnd,
    allowCrossAcademicYear: row.allowCrossAcademicYear,
    maxApplyTimesPerYear: row.maxApplyTimesPerYear,
    gradeScope: row.gradeScope,
    requireAttachment: row.requireAttachment,
    sort: row.sort,
    isEnabled: row.isEnabled,
    remark: row.remark
  }
  editVisible.value = true
}

async function save(): Promise<void> {
  saving.value = true

  try {
    if (editingId.value) {
      await changeTypeApi.update(editingId.value, form.value)
      ElMessage.success('异动类型已更新')
    } else {
      await changeTypeApi.create(form.value)
      ElMessage.success('异动类型已创建')
    }

    editVisible.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function remove(row: ChangeType): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除异动类型「${row.name}」吗？已提交的申请不受影响。`,
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await changeTypeApi.remove(row.id)
  ElMessage.success('已删除')
  await load()
}

// ---------------------------------------------------------------------------
// 流程模板
// ---------------------------------------------------------------------------

const flowVisible = ref(false)
const flowLoading = ref(false)
const flowSaving = ref(false)
const flowType = ref<ChangeType | null>(null)
const template = ref<ChangeFlowTemplate | null>(null)
const nodes = ref<CreateUpdateFlowNode[]>([])

async function openFlow(row: ChangeType): Promise<void> {
  flowType.value = row
  flowVisible.value = true
  flowLoading.value = true
  previewResult.value = null

  try {
    template.value = await changeTypeApi.getTemplate(row.id)

    nodes.value = template.value
      ? template.value.nodes.map((n) => ({
          seq: n.seq,
          nodeName: n.nodeName,
          orgResolver: n.orgResolver,
          roleCode: n.roleCode,
          mergeStrategy: n.mergeStrategy,
          allowReject: n.allowReject,
          allowDelegate: n.allowDelegate,
          timeoutDays: n.timeoutDays
        }))
      : defaultNodes()
  } finally {
    flowLoading.value = false
  }
}

/**
 * 默认给一套「转学」流程模板。
 *
 * 为什么不给空模板：业务人员面对空白表格不知道该填什么。
 * 给一套符合河南实际的默认值，改起来比从零建快得多。
 */
function defaultNodes(): CreateUpdateFlowNode[] {
  return [
    node(10, '转入学校审核', ChangeOrgResolvers.TargetSchool, 'SchoolAdmin'),
    node(20, '转入县区审核', ChangeOrgResolvers.TargetCounty, 'CountyAdmin'),
    node(30, '转入地市审核', ChangeOrgResolvers.TargetCity, 'CityAdmin'),
    node(40, '转出学校审核', ChangeOrgResolvers.SourceSchool, 'SchoolAdmin'),
    node(50, '转出县区审核', ChangeOrgResolvers.SourceCounty, 'CountyAdmin'),
    node(60, '转出地市审核', ChangeOrgResolvers.SourceCity, 'CityAdmin')
  ]
}

function node(
  seq: number,
  nodeName: string,
  orgResolver: string,
  roleCode: string
): CreateUpdateFlowNode {
  return {
    seq,
    nodeName,
    orgResolver,
    roleCode,
    mergeStrategy: ChangeMergeStrategies.MergeToLast,
    allowReject: true,
    allowDelegate: false,
    timeoutDays: 5
  }
}

function addNode(): void {
  const last = nodes.value[nodes.value.length - 1]
  nodes.value.push(
    node(
      (last?.seq ?? 0) + 10,
      `第 ${nodes.value.length + 1} 步审核`,
      ChangeOrgResolvers.TargetSchool,
      'SchoolAdmin'
    )
  )
}

function removeNode(index: number): void {
  nodes.value.splice(index, 1)
  renumber()
}

function moveNode(index: number, delta: number): void {
  const target = index + delta

  if (target < 0 || target >= nodes.value.length) {
    return
  }

  const list = nodes.value
  ;[list[index], list[target]] = [list[target], list[index]]
  renumber()
}

/** 序号始终按 10/20/30… 重排，避免手工改乱后出现重复序号。 */
function renumber(): void {
  nodes.value.forEach((n, i) => (n.seq = (i + 1) * 10))
}

async function saveFlow(publish: boolean): Promise<void> {
  if (!flowType.value) {
    return
  }

  if (nodes.value.length === 0) {
    ElMessage.warning('流程至少要有一个节点。')

    return
  }

  flowSaving.value = true

  try {
    renumber()

    const saved = await changeTypeApi.saveTemplate({
      changeTypeId: flowType.value.id,
      name: `${flowType.value.name}流程`,
      remark: template.value?.remark ?? null,
      nodes: nodes.value
    })

    template.value = saved

    if (publish) {
      template.value = await changeTypeApi.publishTemplate(saved.id)
      ElMessage.success(`流程已发布（版本 ${template.value.version}），新提交的申请将使用它`)
      await load()
    } else {
      ElMessage.success('草稿已保存（未发布，提交申请时仍用已发布版本）')
    }
  } finally {
    flowSaving.value = false
  }
}

// ---------------------------------------------------------------------------
// 流程预览
// ---------------------------------------------------------------------------

const previewForm = reactive({
  sourceSchoolOrgId: undefined as string | undefined,
  targetSchoolOrgId: undefined as string | undefined
})

const previewResult = ref<PreviewFlowResult | null>(null)
const previewing = ref(false)

async function runPreview(): Promise<void> {
  if (!flowType.value || !previewForm.targetSchoolOrgId) {
    ElMessage.warning('请先选择转入学校。')

    return
  }

  previewing.value = true

  try {
    previewResult.value = await changeTypeApi.previewFlow({
      changeTypeId: flowType.value.id,
      targetSchoolOrgId: previewForm.targetSchoolOrgId,
      sourceSchoolOrgId: previewForm.sourceSchoolOrgId
    })
  } finally {
    previewing.value = false
  }
}

/**
 * 合并说明：让"哪几步被合并成一步"一眼可见。
 *
 * ⚠ 判空条件是 `length > 0` 而不是 `> 1`：
 * `mergedFromSeqs` 装的是**被合并进来的那些步骤的序号**，
 * 所以哪怕只有一个元素（例如 [20]），也说明这一步实际承担了两步的审核。
 * 写成 `> 1` 会让最常见的"同县区转学"场景一条提示都不显示 ——
 * 而"为什么 6 步变成了 4 步"恰恰是业务人员最需要看到的信息。
 */
function mergeText(mergedFromSeqs: number[]): string {
  if (!mergedFromSeqs || mergedFromSeqs.length === 0) {
    return ''
  }

  return `由第 ${mergedFromSeqs.join('、')} 步合并而来`
}

onMounted(async () => {
  try {
    schools.value = flattenSchools(await orgApi.getTree())
  } catch {
    schools.value = []
  }

  await loadStudentStatusOptions()
  await load()
})
</script>

<template>
  <div class="chg-type">
    <el-card shadow="never">
      <template #header>
        <div class="chg-type__header">
          <span>异动类型（共 {{ types.length }} 类）</span>
          <div>
            <el-button @click="load">
              <AppIcon name="Refresh" />
              <span>刷新</span>
            </el-button>
            <el-button v-if="canCreate" type="primary" @click="openCreate">
              <AppIcon name="Plus" />
              <span>新增类型</span>
            </el-button>
          </div>
        </div>
      </template>

      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="chg-type__tip"
        title="异动类型决定「能做什么」；点「配置流程」决定「谁来审、审几级」。流程可随时调整并预览，不需要改程序。"
      />

      <el-table v-loading="loading" :data="types" border stripe>
        <el-table-column prop="code" label="编码" width="120" />
        <el-table-column prop="name" label="名称" min-width="150" />
        <el-table-column label="学籍状态" width="150" align="center">
          <template #default="{ row }">
            {{ statusLabel(row.beforeStatusCode) }} → {{ statusLabel(row.afterStatusCode) }}
          </template>
        </el-table-column>
        <el-table-column prop="maxApproveLevelName" label="最高审核级别" width="120" align="center" />
        <el-table-column label="申请时间窗" width="150" align="center">
          <template #default="{ row }">
            <span v-if="row.applyWindowStart || row.applyWindowEnd">
              {{ row.applyWindowStart || '不限' }} ~ {{ row.applyWindowEnd || '不限' }}
            </span>
            <span v-else>不限</span>
          </template>
        </el-table-column>
        <el-table-column label="跨校" width="70" align="center">
          <template #default="{ row }">{{ row.allowCrossSchool ? '允许' : '不允许' }}</template>
        </el-table-column>
        <el-table-column label="流程" width="120" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.hasPublishedFlow" type="success" size="small">已发布</el-tag>
            <el-tag v-else type="danger" size="small">未配置</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="启用" width="80" align="center">
          <template #default="{ row }">
            <el-tag :type="row.isEnabled ? 'success' : 'info'" size="small">
              {{ row.isEnabled ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="230" align="center" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canUpdate" link type="primary" @click="openFlow(row)">配置流程</el-button>
            <el-button v-if="canUpdate" link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button v-if="canDelete" link type="danger" @click="remove(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- ---------- 类型编辑 ---------- -->
    <el-dialog
      v-model="editVisible"
      :title="editingId ? '编辑异动类型' : '新增异动类型'"
      width="760px"
      top="6vh"
    >
      <el-form label-width="130px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="编码" required>
              <el-input v-model="form.code" placeholder="如 TRANSFER" :disabled="!!editingId" />
              <div v-if="editingId" class="chg-type__hint">
                编码一经使用不建议修改：权限与历史数据都按它关联。
              </div>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="名称" required>
              <el-input v-model="form.name" placeholder="如 转学" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="异动前状态">
              <el-select v-model="form.beforeStatusCode" clearable style="width: 100%">
                <el-option
                  v-for="s in studentStatusOptions"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="异动后状态">
              <el-select v-model="form.afterStatusCode" clearable style="width: 100%">
                <el-option
                  v-for="s in studentStatusOptions"
                  :key="s.value"
                  :label="s.label"
                  :value="s.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="最高审核级别">
          <el-radio-group v-model="form.maxApproveLevel">
            <el-radio-button v-for="l in levelOptions" :key="l.value" :value="l.value">
              {{ l.label }}
            </el-radio-button>
          </el-radio-group>
          <div class="chg-type__hint">
            流程模板里超过这一级的节点，在实例化时会被自动截断。
            例如"休学只需县区和市局审核"就把这里设成"地市级"。
          </div>
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="申请时间窗">
              <el-input v-model="form.applyWindowStart" placeholder="起，如 08-15" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="至">
              <el-input v-model="form.applyWindowEnd" placeholder="止，如 09-15" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="8">
            <el-form-item label="每学年最多次数">
              <el-input-number v-model="form.maxApplyTimesPerYear" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="排序">
              <el-input-number v-model="form.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="年级范围">
              <el-input v-model="form.gradeScope" placeholder="留空表示不限" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="控制项">
          <el-checkbox v-model="form.allowCrossSchool">允许跨校</el-checkbox>
          <el-checkbox v-model="form.affectGrade">影响年级</el-checkbox>
          <el-checkbox v-model="form.affectClass">影响班级</el-checkbox>
          <el-checkbox v-model="form.needPeerSchoolConfirm">需对方学校确认</el-checkbox>
          <el-checkbox v-model="form.allowCrossAcademicYear">允许跨学年</el-checkbox>
          <el-checkbox v-model="form.requireAttachment">必须上传附件</el-checkbox>
          <el-checkbox v-model="form.isEnabled">启用</el-checkbox>
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="form.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="editVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <!-- ---------- 流程配置 ---------- -->
    <el-dialog
      v-model="flowVisible"
      :title="`配置流程 —— ${flowType?.name ?? ''}`"
      width="1000px"
      top="5vh"
    >
      <div v-loading="flowLoading">
        <el-alert
          v-if="template"
          :type="template.status === 'Published' ? 'success' : 'warning'"
          :closable="false"
          show-icon
          class="chg-type__tip"
          :title="`当前版本 v${template.version}（${template.statusName}）${
            template.publishedTime ? '，发布于 ' + template.publishedTime.replace('T', ' ').slice(0, 19) : ''
          }`"
        />

        <el-divider content-position="left">审核节点（按顺序执行）</el-divider>

        <el-table :data="nodes" border size="small">
          <el-table-column label="序" width="60" align="center">
            <template #default="{ row }">{{ row.seq }}</template>
          </el-table-column>
          <el-table-column label="节点名称" width="170">
            <template #default="{ row }">
              <el-input v-model="row.nodeName" size="small" />
            </template>
          </el-table-column>
          <el-table-column label="审核单位（解析方式）" width="220">
            <template #default="{ row }">
              <el-select v-model="row.orgResolver" size="small" style="width: 100%">
                <el-option
                  v-for="(label, value) in ChangeOrgResolverNames"
                  :key="value"
                  :label="label"
                  :value="value"
                />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column label="角色编码" width="150">
            <template #default="{ row }">
              <el-input v-model="row.roleCode" size="small" />
            </template>
          </el-table-column>
          <el-table-column label="同一组织重复时" min-width="200">
            <template #default="{ row }">
              <el-select v-model="row.mergeStrategy" size="small" style="width: 100%">
                <el-option
                  v-for="(label, value) in ChangeMergeStrategyNames"
                  :key="value"
                  :label="label"
                  :value="value"
                />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column label="时限(天)" width="90">
            <template #default="{ row }">
              <el-input-number v-model="row.timeoutDays" size="small" :min="0" controls-position="right" style="width: 100%" />
            </template>
          </el-table-column>
          <el-table-column label="操作" width="130" align="center">
            <template #default="{ $index }">
              <el-button link @click="moveNode($index, -1)">上移</el-button>
              <el-button link @click="moveNode($index, 1)">下移</el-button>
              <el-button link type="danger" @click="removeNode($index)">删</el-button>
            </template>
          </el-table-column>
        </el-table>

        <div class="chg-type__flow-actions">
          <el-button @click="addNode">
            <AppIcon name="Plus" />
            <span>添加节点</span>
          </el-button>
          <el-button @click="nodes = defaultNodes()">恢复默认转学流程</el-button>
        </div>

        <el-divider content-position="left">流程预览（不改动数据，只看会发生什么）</el-divider>

        <el-space wrap>
          <span>转出学校：</span>
          <el-select
            v-model="previewForm.sourceSchoolOrgId"
            placeholder="（本校转出可留空）"
            clearable
            filterable
            style="width: 260px"
          >
            <el-option v-for="s in schools" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>

          <span>转入学校：</span>
          <el-select
            v-model="previewForm.targetSchoolOrgId"
            placeholder="请选择"
            filterable
            style="width: 260px"
          >
            <el-option v-for="s in schools" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>

          <el-button type="primary" plain :loading="previewing" @click="runPreview">预览</el-button>
        </el-space>

        <template v-if="previewResult">
          <el-alert
            v-if="previewResult.warnings?.length"
            type="warning"
            :closable="false"
            show-icon
            class="chg-type__tip"
            :title="previewResult.warnings.join('；')"
          />

          <el-descriptions :column="4" size="small" border class="chg-type__preview-summary">
            <el-descriptions-item label="模板节点数">
              {{ previewResult.configuredNodeCount }}
            </el-descriptions-item>
            <el-descriptions-item label="实际步数">
              <b>{{ previewResult.actualStepCount }}</b>
            </el-descriptions-item>
            <el-descriptions-item label="模板版本">v{{ previewResult.templateVersion }}</el-descriptions-item>
            <el-descriptions-item label="最高审核级别">
              {{ previewResult.maxApproveLevelName }}
            </el-descriptions-item>
          </el-descriptions>

          <el-steps direction="vertical" :active="previewResult.nodes.length" class="chg-type__steps">
            <el-step
              v-for="n in previewResult.nodes"
              :key="n.seq"
              :title="`第 ${n.seq} 步 · ${n.nodeName}`"
            >
              <template #description>
                <div>
                  <el-tag size="small" effect="plain">{{ n.orgName }}</el-tag>
                  <el-tag size="small" type="info" effect="plain">{{ n.orgResolverName }}</el-tag>
                  <el-tag size="small" type="info" effect="plain">{{ n.approveLevelName }}</el-tag>
                  <span v-if="mergeText(n.mergedFromSeqs)" class="chg-type__merge">
                    ⤶ {{ mergeText(n.mergedFromSeqs) }}
                  </span>
                  <div v-if="n.mergeNote" class="chg-type__hint">{{ n.mergeNote }}</div>
                </div>
              </template>
            </el-step>
          </el-steps>
        </template>
      </div>

      <template #footer>
        <el-button @click="flowVisible = false">关闭</el-button>
        <el-button :loading="flowSaving" @click="saveFlow(false)">保存草稿</el-button>
        <el-button type="primary" :loading="flowSaving" @click="saveFlow(true)">保存并发布</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.chg-type__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.chg-type__tip {
  margin-bottom: 12px;
}

.chg-type__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

.chg-type__flow-actions {
  margin-top: 12px;
  display: flex;
  gap: 8px;
}

.chg-type__preview-summary {
  margin: 12px 0;
}

.chg-type__steps {
  height: auto;
  max-height: 320px;
  overflow: auto;
  padding-left: 8px;
}

.chg-type__merge {
  margin-left: 8px;
  font-size: 12px;
  color: var(--el-color-warning);
}
</style>
