<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { orgApi } from '@/api'
import type { OrgDto, OrgTreeNode } from '@/api/types'
import { OrgTypeNames } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 组织管理：省厅 → 市局 → 县局 → 学校 四级树。
 *
 * 这个页面是所有其它模块的前提：没有组织树，就无法建用户、无法分配数据范围、
 * 学生的"学校"字段也无处可选。所以它必须支持**大组织树**的打开方式 ——
 * 全省有上万所学校，一次性拉整棵树（接口返回 1MB+）在县区网络上会明显卡顿，
 * 因此这里用 el-tree 的**懒加载**：展开哪个节点才去取哪个节点的下级。
 *
 * 另一处刻意的设计：**新增下级时自动推断组织类型**。
 * 省厅下面只能建地市、地市下面只能建县区、县区下面只能建学校 ——
 * 让用户手选类型既容易选错，也要多一次交互。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Org.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Org.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Org.Delete))

/** 换 key 强制重建树（见 refreshTree 的说明）。 */
const treeKey = ref(0)
const selected = ref<OrgDto | null>(null)
const loadingDetail = ref(false)

/** 各级组织的下级类型；学校没有下级。 */
const childTypeOf: Record<string, string | undefined> = {
  Province: 'City',
  City: 'County',
  County: 'School',
  School: undefined
}

const selectedChildType = computed(() =>
  selected.value ? childTypeOf[selected.value.orgType] : 'Province'
)

const canAddChild = computed(() => !!selectedChildType.value)

// ---------------------------------------------------------------------------
// 树（懒加载）
// ---------------------------------------------------------------------------

/**
 * el-tree 的懒加载回调。
 *
 * 根节点用「按类型过滤的列表接口」取省厅，其余层级用 children 接口。
 * 注意不要用 `/org/children/{parentId}` 传空来取根 —— 空路由段会 404。
 */
async function loadNode(node: { level: number; data: OrgDto | OrgTreeNode }, resolve: (data: unknown[]) => void) {
  try {
    if (node.level === 0) {
      const roots = await orgApi.getRoots()
      resolve(roots.map((o) => ({ ...o, leaf: false })))

      return
    }

    const children = await orgApi.getChildren(node.data.id)

    resolve(children.map((c) => ({ ...c, leaf: !c.hasChildren })))
  } catch {
    resolve([])
  }
}

async function selectNode(data: OrgDto | OrgTreeNode): Promise<void> {
  loadingDetail.value = true

  try {
    const detail = await orgApi.get(data.id)
    selected.value = detail
    form.parentId = detail.parentId ?? null
    form.orgType = detail.orgType
    form.code = detail.code
    form.name = detail.name
    form.shortName = detail.shortName ?? ''
    form.sort = detail.sort
    form.isEnabled = detail.isEnabled
    form.remark = detail.remark ?? ''
  } finally {
    loadingDetail.value = false
  }
}

/** 上级组织名称（用物化路径的后缀反查太绕，这里直接查一次父节点）。 */
const parentName = ref<string>('—')

async function resolveParentName(parentId?: string | null): Promise<void> {
  if (!parentId) {
    parentName.value = '（根节点）'

    return
  }

  try {
    parentName.value = (await orgApi.get(parentId)).name
  } catch {
    parentName.value = '—'
  }
}

function reload(): void {
  selected.value = null
  parentName.value = '—'
  refreshTree()
}

/**
 * 重新挂载树。
 *
 * 这里刻意不用 el-tree 的内部实例方法（store.setData / updateKeyChildren）：
 * 那些是内部实现，升级 Element Plus 时可能被改掉，而且类型上也拿不到。
 * 换 key 强制重建是官方支持的用法，代价只是重新加载一次根节点。
 */
function refreshTree(): void {
  treeKey.value += 1
}

// ---------------------------------------------------------------------------
// 表单
// ---------------------------------------------------------------------------

const form = reactive({
  parentId: null as string | null,
  orgType: 'Province',
  code: '',
  name: '',
  shortName: '',
  sort: 0,
  isEnabled: true,
  remark: ''
})

function toPayload() {
  return {
    parentId: form.parentId,
    orgType: form.orgType,
    code: form.code.trim(),
    name: form.name.trim(),
    shortName: form.shortName?.trim() || null,
    sort: form.sort,
    isEnabled: form.isEnabled,
    remark: form.remark?.trim() || null
  }
}

async function save(): Promise<void> {
  if (!selected.value) {
    return
  }

  if (!form.code.trim() || !form.name.trim()) {
    ElMessage.warning('组织编码与名称都不能为空。')

    return
  }

  await orgApi.update(selected.value.id, toPayload())
  ElMessage.success('已保存')

  await selectNode(selected.value)
}

async function remove(): Promise<void> {
  if (!selected.value) {
    return
  }

  try {
    await ElMessageBox.confirm(
      `确定删除「${selected.value.name}」吗？` +
        '若该组织下还有下级组织或学生，服务端会拒绝删除 —— 这是有意为之，避免产生孤儿数据。',
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await orgApi.remove(selected.value.id)
  ElMessage.success('已删除')
  reload()
}

// ---------------------------------------------------------------------------
// 新增下级
// ---------------------------------------------------------------------------

const createVisible = ref(false)
const creating = ref(false)
const createForm = reactive({
  parentName: '',
  parentId: null as string | null,
  orgType: 'Province',
  code: '',
  name: '',
  shortName: '',
  sort: 0,
  remark: ''
})

function openCreate(): void {
  createForm.parentId = selected.value?.id ?? null
  createForm.parentName = selected.value
    ? `${selected.value.name}（${OrgTypeNames[selected.value.orgType] ?? ''}）`
    : '（根节点）'
  createForm.orgType = selectedChildType.value ?? 'Province'
  createForm.code = ''
  createForm.name = ''
  createForm.shortName = ''
  createForm.sort = 0
  createForm.remark = ''
  createVisible.value = true
}

async function doCreate(): Promise<void> {
  if (!createForm.code.trim() || !createForm.name.trim()) {
    ElMessage.warning('组织编码与名称都不能为空。')

    return
  }

  creating.value = true

  try {
    const created = await orgApi.create({
      parentId: createForm.parentId,
      orgType: createForm.orgType,
      code: createForm.code.trim(),
      name: createForm.name.trim(),
      shortName: createForm.shortName?.trim() || null,
      sort: createForm.sort,
      isEnabled: true,
      remark: createForm.remark?.trim() || null
    })

    ElMessage.success(`已创建「${created.name}」`)
    createVisible.value = false

    // 重建树后再把新节点显示在右侧（不再多查一次接口）
    refreshTree()
    selected.value = created
    form.parentId = created.parentId ?? null
    form.orgType = created.orgType
    form.code = created.code
    form.name = created.name
    form.shortName = created.shortName ?? ''
    form.sort = created.sort
    form.isEnabled = created.isEnabled
    form.remark = created.remark ?? ''
    await resolveParentName(created.parentId)
  } finally {
    creating.value = false
  }
}

onMounted(() => {
  refreshTree()
})
</script>

<template>
  <div class="org">
    <el-row :gutter="16">
      <!-- ---------------- 左：组织树 ---------------- -->
      <el-col :span="9">
        <el-card shadow="never" class="org__tree-card">
          <template #header>
            <div class="org__header">
              <span>组织树</span>
              <div>
                <el-button size="small" @click="reload">
                  <AppIcon name="Refresh" />
                  <span>刷新</span>
                </el-button>
                <el-button
                  v-if="canCreate"
                  size="small"
                  type="primary"
                  :disabled="!canAddChild"
                  @click="openCreate"
                >
                  <AppIcon name="Plus" />
                  <span>{{ !selected ? '新增省厅' : canAddChild ? '新增下级' : '学校无下级' }}</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="org__tip"
            title="逐层加载：展开哪个节点才去取它的下级。全省上万所学校时，一次性拉整棵树会明显卡顿。"
          />

          <el-tree
            :key="treeKey"
            lazy
            node-key="id"
            :load="loadNode"
            :props="{ label: 'name', isLeaf: 'leaf' }"
            highlight-current
            :expand-on-click-node="false"
            @node-click="selectNode"
          >
            <template #default="{ data }">
              <span class="org__node">
                <el-tag size="small" effect="plain" :type="data.orgType === 'School' ? 'success' : 'info'">
                  {{ OrgTypeNames[data.orgType] ?? data.orgType }}
                </el-tag>
                <span>{{ data.name }}</span>
                <el-tag v-if="!data.isEnabled" size="small" type="danger" effect="plain">停用</el-tag>
              </span>
            </template>
          </el-tree>
        </el-card>
      </el-col>

      <!-- ---------------- 右：详情表单 ---------------- -->
      <el-col :span="15">
        <el-card v-loading="loadingDetail" shadow="never">
          <template #header>
            <div class="org__header">
              <span>{{ selected ? `编辑：${selected.name}` : '组织详情' }}</span>
              <div v-if="selected">
                <el-button v-if="canDelete" size="small" type="danger" plain @click="remove">删除</el-button>
                <el-button v-if="canUpdate" size="small" type="primary" @click="save">保存</el-button>
              </div>
            </div>
          </template>

          <el-empty
            v-if="!selected"
            description="请在左侧选择一个组织节点"
            :image-size="80"
          />

          <template v-else>
            <el-descriptions :column="3" size="small" border class="org__meta">
              <el-descriptions-item label="上级">{{ parentName }}</el-descriptions-item>
              <el-descriptions-item label="层级">
                {{ OrgTypeNames[selected.orgType] ?? selected.orgType }}
              </el-descriptions-item>
              <el-descriptions-item label="物化路径">
                <span class="org__path">{{ selected.pathCode || '—' }}</span>
              </el-descriptions-item>
            </el-descriptions>

            <el-form label-width="110px" class="org__form">
              <el-row :gutter="16">
                <el-col :span="12">
                  <el-form-item label="组织编码" required>
                    <el-input v-model="form.code" placeholder="学校填 10 位学校标识码" />
                    <div class="org__hint">
                      学校的编码同时是学籍辅号的前 10 位，务必与教育事业统计口径一致。
                    </div>
                  </el-form-item>
                </el-col>
                <el-col :span="12">
                  <el-form-item label="组织名称" required>
                    <el-input v-model="form.name" />
                  </el-form-item>
                </el-col>
              </el-row>

              <el-row :gutter="16">
                <el-col :span="12">
                  <el-form-item label="简称">
                    <el-input v-model="form.shortName" />
                  </el-form-item>
                </el-col>
                <el-col :span="6">
                  <el-form-item label="排序">
                    <el-input-number v-model="form.sort" :min="0" style="width: 100%" />
                  </el-form-item>
                </el-col>
                <el-col :span="6">
                  <el-form-item label="启用">
                    <el-switch v-model="form.isEnabled" />
                  </el-form-item>
                </el-col>
              </el-row>

              <el-form-item label="备注">
                <el-input v-model="form.remark" type="textarea" :rows="3" />
              </el-form-item>
            </el-form>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <!-- ---------------- 新增对话框 ---------------- -->
    <el-dialog v-model="createVisible" title="新增组织" width="600px">
      <el-form label-width="110px">
        <el-form-item label="上级组织">
          <el-input :model-value="createForm.parentName" disabled />
        </el-form-item>

        <el-form-item label="组织类型">
          <el-radio-group v-model="createForm.orgType" :disabled="!!createForm.parentId">
            <el-radio-button value="Province">省厅</el-radio-button>
            <el-radio-button value="City">地市</el-radio-button>
            <el-radio-button value="County">县区</el-radio-button>
            <el-radio-button value="School">学校</el-radio-button>
          </el-radio-group>
          <div v-if="createForm.parentId" class="org__hint">
            类型由上级自动推断（{{ OrgTypeNames[createForm.orgType] }}），一般不需要改。
          </div>
        </el-form-item>

        <el-form-item label="组织编码" required>
          <el-input v-model="createForm.code" />
        </el-form-item>

        <el-form-item label="组织名称" required>
          <el-input v-model="createForm.name" />
        </el-form-item>

        <el-form-item label="简称">
          <el-input v-model="createForm.shortName" />
        </el-form-item>

        <el-form-item label="排序">
          <el-input-number v-model="createForm.sort" :min="0" />
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="createForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="doCreate">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.org__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.org__tree-card :deep(.el-card__body) {
  max-height: calc(100vh - 220px);
  overflow: auto;
}

.org__tip {
  margin-bottom: 12px;
}

.org__node {
  display: flex;
  align-items: center;
  gap: 6px;
}

.org__meta {
  margin-bottom: 16px;
}

.org__path {
  font-family: Consolas, Monaco, monospace;
  font-size: 12px;
}

.org__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}
</style>
