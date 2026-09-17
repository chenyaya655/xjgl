<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { moduleApi } from '@/api'
import type { ModuleAction, Subsystem, SysModule } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 模块管理：子系统 → 模块 → 操作 三级元数据维护。
 *
 * 这个页面是整个系统「把菜单变成数据」的落点 —— 它决定了：
 *   - 有哪些权限码（`{子系统}.{模块}.{操作}`）；
 *   - 左侧菜单长什么样、点进去是哪个前端页面（`path` + `component`）；
 *   - 权限管理页里能勾选哪些权限。
 *
 * 三处必须让用户看懂的地方：
 *   1) **权限码是拼出来的**，不是填的 —— 选中节点时直接把拼好的权限码显示出来，
 *      避免有人以为要自己输入；
 *   2) **改编码会让历史授权失效** —— 权限码里含编码，`AbpPermissionGrants`
 *      里存的是旧权限名，改名等于所有相关授权瞬间失效，必须给出警告；
 *   3) **新增模块后需要重启应用**，`[Authorize]` 才会认识新的权限码
 *      （权限定义在启动时构建）。这个限制写在界面上，比写在文档里有用。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Module.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Module.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Module.Delete))

type NodeKind = 'subsystem' | 'module' | 'action'

interface TreeNode {
  id: string
  label: string
  kind: NodeKind
  code: string
  isEnabled: boolean
  /** 拼好的权限码（子系统节点为空） */
  permissionName?: string
  subsystem?: Subsystem
  module?: SysModule
  action?: ModuleAction
  children: TreeNode[]
}

const loading = ref(false)
const tree = ref<Subsystem[]>([])
const keyword = ref('')
const treeKey = ref(0)

const selectedKind = ref<NodeKind | null>(null)
const selectedId = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true

  try {
    tree.value = await moduleApi.getTree(false)
    treeKey.value += 1
  } finally {
    loading.value = false
  }
}

/** 把后端的三层结构映射成 el-tree 需要的节点（带上 kind 便于右侧分支渲染）。 */
const treeData = computed<TreeNode[]>(() => {
  const kw = keyword.value.trim().toLowerCase()

  const match = (text?: string | null) => !kw || (text ?? '').toLowerCase().includes(kw)

  return tree.value
    .map((sub) => {
      const modules = sub.modules
        .map((mod) => {
          const actions = mod.actions
            .filter((a) => match(a.name) || match(a.code) || match(a.permissionName))
            .map<TreeNode>((a) => ({
              id: a.id,
              label: `${a.name}（${a.code}）`,
              kind: 'action',
              code: a.code,
              isEnabled: a.isEnabled,
              permissionName: a.permissionName,
              action: a,
              module: mod,
              children: []
            }))

          return {
            mod,
            actions
          }
        })
        .filter(({ mod, actions }) => match(mod.name) || match(mod.code) || actions.length > 0)
        .map<TreeNode>(({ mod, actions }) => ({
          id: mod.id,
          label: `${mod.name}（${mod.code}）`,
          kind: 'module',
          code: mod.code,
          isEnabled: mod.isEnabled,
          permissionName: mod.permissionName,
          module: mod,
          subsystem: sub,
          children: actions
        }))

      return {
        sub,
        modules
      }
    })
    .filter(({ sub, modules }) => match(sub.name) || match(sub.code) || modules.length > 0)
    .map<TreeNode>(({ sub, modules }) => ({
      id: sub.id,
      label: `${sub.name}（${sub.code}）`,
      kind: 'subsystem',
      code: sub.code,
      isEnabled: sub.isEnabled,
      subsystem: sub,
      children: modules
    }))
})

// ---------------------------------------------------------------------------
// 详情表单（三种节点共用一份 superset，按 kind 渲染对应字段）
// ---------------------------------------------------------------------------

const form = reactive({
  code: '',
  name: '',
  icon: '',
  sort: 0,
  isEnabled: true,
  remark: '',
  // 模块
  path: '',
  component: '',
  isHidden: false,
  isExternal: false,
  orgTypes: '',
  // 操作
  actionType: 'Query',
  isDefaultGranted: false
})

const currentPermission = ref('')

const actionTypeOptions = ['Query', 'Create', 'Update', 'Delete', 'Import', 'Export', 'Print', 'Assign', 'Approve', 'RevokeApprove', 'Upload', 'Compensate']

function selectNode(node: TreeNode): void {
  selectedKind.value = node.kind
  selectedId.value = node.id
  currentPermission.value = node.permissionName ?? ''

  const source = node.subsystem ?? node.module

  form.code = node.subsystem?.code ?? node.module?.code ?? node.action?.code ?? ''
  form.name = node.subsystem?.name ?? node.module?.name ?? node.action?.name ?? ''
  form.icon = node.subsystem?.icon ?? node.module?.icon ?? ''
  form.sort = node.subsystem?.sort ?? node.module?.sort ?? node.action?.sort ?? 0
  form.isEnabled = node.subsystem?.isEnabled ?? node.module?.isEnabled ?? node.action?.isEnabled ?? true
  form.remark = node.subsystem?.remark ?? node.module?.remark ?? ''

  form.path = node.module?.path ?? ''
  form.component = node.module?.component ?? ''
  form.isHidden = node.module?.isHidden ?? false
  form.isExternal = node.module?.isExternal ?? false
  form.orgTypes = node.module?.orgTypes ?? ''

  form.actionType = node.action?.actionType ?? 'Query'
  form.isDefaultGranted = node.action?.isDefaultGranted ?? false

  void source
}

async function save(): Promise<void> {
  if (!selectedKind.value || !selectedId.value) {
    return
  }

  if (!form.code.trim() || !form.name.trim()) {
    ElMessage.warning('编码与名称都不能为空。')

    return
  }

  if (selectedKind.value === 'subsystem') {
    await moduleApi.updateSubsystem(selectedId.value, {
      code: form.code.trim(),
      name: form.name.trim(),
      icon: form.icon?.trim() || null,
      sort: form.sort,
      isEnabled: form.isEnabled,
      remark: form.remark?.trim() || null
    })
  } else if (selectedKind.value === 'module') {
    await moduleApi.updateModule(selectedId.value, {
      subsystemId: findSubsystemId(selectedId.value),
      code: form.code.trim(),
      name: form.name.trim(),
      path: form.path?.trim() || null,
      component: form.component?.trim() || null,
      icon: form.icon?.trim() || null,
      sort: form.sort,
      isEnabled: form.isEnabled,
      isHidden: form.isHidden,
      isExternal: form.isExternal,
      orgTypes: form.orgTypes?.trim() || null,
      remark: form.remark?.trim() || null
    })
  } else {
    const moduleId = findModuleId(selectedId.value)

    await moduleApi.updateAction(selectedId.value, {
      moduleId,
      code: form.code.trim(),
      name: form.name.trim(),
      actionType: form.actionType,
      sort: form.sort,
      isEnabled: form.isEnabled,
      isDefaultGranted: form.isDefaultGranted
    })
  }

  ElMessage.success('已保存。若改动了权限码，请到「权限管理」重新授权。')
  await load()
}

function findSubsystemId(moduleId: string): string {
  for (const sub of tree.value) {
    if (sub.modules.some((m) => m.id === moduleId)) {
      return sub.id
    }
  }

  return ''
}

function findModuleId(actionId: string): string {
  for (const sub of tree.value) {
    for (const mod of sub.modules) {
      if (mod.actions.some((a) => a.id === actionId)) {
        return mod.id
      }
    }
  }

  return ''
}

async function remove(): Promise<void> {
  if (!selectedKind.value || !selectedId.value) {
    return
  }

  const labels = { subsystem: '子系统', module: '模块', action: '操作' }
  const warning =
    selectedKind.value === 'action'
      ? '删除后该权限码将不再存在，已授予它的角色会失去这一项能力。'
      : '若其下还有子节点或已被授权，服务端会拒绝删除。'

  try {
    await ElMessageBox.confirm(`确定删除该${labels[selectedKind.value]}吗？${warning}`, '删除确认', {
      type: 'warning'
    })
  } catch {
    return
  }

  if (selectedKind.value === 'subsystem') {
    await moduleApi.removeSubsystem(selectedId.value)
  } else if (selectedKind.value === 'module') {
    await moduleApi.removeModule(selectedId.value)
  } else {
    await moduleApi.removeAction(selectedId.value)
  }

  ElMessage.success('已删除')
  selectedKind.value = null
  selectedId.value = null
  await load()
}

async function refreshCache(): Promise<void> {
  await moduleApi.refreshCache()
  ElMessage.success('元数据缓存已刷新')
  await load()
}

// ---------------------------------------------------------------------------
// 新增
// ---------------------------------------------------------------------------

const dialogVisible = ref(false)
const creating = ref(false)
const createKind = ref<NodeKind>('subsystem')
const createParentName = ref('')
const createParentId = ref<string | null>(null)
const createForm = reactive({ code: '', name: '', icon: '', path: '', component: '', sort: 0 })

const dialogTitle = computed(() => {
  switch (createKind.value) {
    case 'subsystem':
      return '新增子系统'
    case 'module':
      return '新增模块'
    default:
      return '新增操作'
  }
})

function openCreate(kind: NodeKind): void {
  createKind.value = kind
  createForm.code = ''
  createForm.name = ''
  createForm.icon = ''
  createForm.path = ''
  createForm.component = ''
  createForm.sort = 0

  if (kind === 'subsystem') {
    createParentName.value = '（根）'
    createParentId.value = null
  } else if (kind === 'module') {
    createParentName.value = tree.value.find((s) => s.id === selectedId.value)?.name ?? ''
    createParentId.value = selectedId.value
  } else {
    createParentName.value =
      tree.value
        .flatMap((s) => s.modules)
        .find((m) => m.id === selectedId.value)?.name ?? ''
    createParentId.value = selectedId.value
  }

  dialogVisible.value = true
}

async function doCreate(): Promise<void> {
  if (!createForm.code.trim() || !createForm.name.trim()) {
    ElMessage.warning('编码与名称都不能为空。')

    return
  }

  creating.value = true

  try {
    if (createKind.value === 'subsystem') {
      await moduleApi.createSubsystem({
        code: createForm.code.trim(),
        name: createForm.name.trim(),
        icon: createForm.icon?.trim() || null,
        sort: createForm.sort,
        isEnabled: true,
        remark: null
      })
    } else if (createKind.value === 'module') {
      await moduleApi.createModule({
        subsystemId: createParentId.value!,
        code: createForm.code.trim(),
        name: createForm.name.trim(),
        path: createForm.path?.trim() || null,
        component: createForm.component?.trim() || null,
        icon: createForm.icon?.trim() || null,
        sort: createForm.sort,
        isEnabled: true,
        isHidden: false,
        isExternal: false,
        orgTypes: null,
        remark: null
      })
    } else {
      await moduleApi.createAction({
        moduleId: createParentId.value!,
        code: createForm.code.trim(),
        name: createForm.name.trim(),
        actionType: createForm.code.trim(),
        sort: createForm.sort,
        isEnabled: true,
        isDefaultGranted: false
      })
    }

    ElMessage.success(
      createKind.value === 'action'
        ? '操作已创建。权限码在应用重启后才会被 [Authorize] 认识，请重启后端。'
        : '已创建'
    )

    dialogVisible.value = false
    await load()
  } finally {
    creating.value = false
  }
}

onMounted(() => {
  void load()
})
</script>

<template>
  <div class="meta">
    <el-row :gutter="16">
      <el-col :span="9">
        <el-card shadow="never" class="meta__tree-card">
          <template #header>
            <div class="meta__header">
              <span>元数据树</span>
              <div>
                <el-button size="small" @click="refreshCache">刷新缓存</el-button>
                <el-button v-if="canCreate" size="small" type="primary" @click="openCreate('subsystem')">
                  <AppIcon name="Plus" />
                  <span>新增子系统</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-input v-model="keyword" placeholder="搜索名称 / 编码 / 权限码" clearable class="meta__search">
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="meta__tip"
            title="这里就是左侧菜单与权限码的来源。停用模块后菜单消失；改编码会让历史授权失效。"
          />

          <el-tree
            v-loading="loading"
            :key="treeKey"
            :data="treeData"
            node-key="id"
            :props="{ label: 'label', children: 'children' }"
            default-expand-all
            highlight-current
            :expand-on-click-node="false"
            @node-click="selectNode"
          >
            <template #default="{ data }">
              <span class="meta__node">
                <el-tag size="small" effect="plain" :type="data.kind === 'action' ? 'success' : 'info'">
                  {{ data.kind === 'subsystem' ? '子系统' : data.kind === 'module' ? '模块' : '操作' }}
                </el-tag>
                <span>{{ data.label }}</span>
                <el-tag v-if="!data.isEnabled" size="small" type="danger" effect="plain">停用</el-tag>
              </span>
            </template>
          </el-tree>
        </el-card>
      </el-col>

      <el-col :span="15">
        <el-card shadow="never">
          <template #header>
            <div class="meta__header">
              <span>
                {{
                  selectedKind === 'subsystem'
                    ? '子系统详情'
                    : selectedKind === 'module'
                      ? '模块详情'
                      : selectedKind === 'action'
                        ? '操作详情'
                        : '详情'
                }}
              </span>
              <div v-if="selectedKind">
                <el-button v-if="canCreate && selectedKind === 'subsystem'" size="small" @click="openCreate('module')">
                  新增模块
                </el-button>
                <el-button v-if="canCreate && selectedKind === 'module'" size="small" @click="openCreate('action')">
                  新增操作
                </el-button>
                <el-button v-if="canDelete" size="small" type="danger" plain @click="remove">删除</el-button>
                <el-button v-if="canUpdate" size="small" type="primary" @click="save">保存</el-button>
              </div>
            </div>
          </template>

          <el-empty v-if="!selectedKind" description="请在左侧选择子系统 / 模块 / 操作" :image-size="80" />

          <template v-else>
            <el-alert
              v-if="currentPermission"
              type="success"
              :closable="false"
              show-icon
              class="meta__tip"
              :title="`该节点对应的权限码：${currentPermission}（由「子系统.模块.操作」自动拼装）`"
            />

            <el-alert
              v-if="selectedKind === 'module'"
              type="warning"
              :closable="false"
              show-icon
              class="meta__tip"
              title="新增或改动模块后，权限定义要等应用重启才会被 [Authorize] 认识；菜单与权限管理页面则会立刻生效。"
            />

            <el-form label-width="120px" class="meta__form">
              <el-row :gutter="16">
                <el-col :span="12">
                  <el-form-item label="编码" required>
                    <el-input v-model="form.code" :disabled="selectedKind === 'action'" />
                    <div v-if="selectedKind === 'action'" class="meta__hint">
                      操作编码不允许修改：权限码里含它，改了历史授权就失效。
                    </div>
                  </el-form-item>
                </el-col>
                <el-col :span="12">
                  <el-form-item label="名称" required>
                    <el-input v-model="form.name" />
                  </el-form-item>
                </el-col>
              </el-row>

              <template v-if="selectedKind === 'action'">
                <el-row :gutter="16">
                  <el-col :span="12">
                    <el-form-item label="操作类型">
                      <el-select v-model="form.actionType" style="width: 100%">
                        <el-option v-for="t in actionTypeOptions" :key="t" :label="t" :value="t" />
                      </el-select>
                    </el-form-item>
                  </el-col>
                  <el-col :span="12">
                    <el-form-item label="默认授予">
                      <el-switch v-model="form.isDefaultGranted" />
                      <span class="meta__hint meta__inline">新建角色时默认勾选</span>
                    </el-form-item>
                  </el-col>
                </el-row>
              </template>

              <template v-else>
                <el-row :gutter="16">
                  <el-col :span="8">
                    <el-form-item label="图标">
                      <el-input v-model="form.icon" placeholder="Element Plus 图标名" />
                    </el-form-item>
                  </el-col>
                  <el-col :span="8">
                    <el-form-item label="排序">
                      <el-input-number v-model="form.sort" :min="0" style="width: 100%" />
                    </el-form-item>
                  </el-col>
                  <el-col :span="8">
                    <el-form-item label="启用">
                      <el-switch v-model="form.isEnabled" />
                    </el-form-item>
                  </el-col>
                </el-row>
              </template>

              <template v-if="selectedKind === 'module'">
                <el-row :gutter="16">
                  <el-col :span="12">
                    <el-form-item label="路由路径">
                      <el-input v-model="form.path" placeholder="如 /student/list" />
                    </el-form-item>
                  </el-col>
                  <el-col :span="12">
                    <el-form-item label="前端组件">
                      <el-input v-model="form.component" placeholder="如 student/list/index" />
                      <div class="meta__hint">
                        约定：<code>student/list/index</code> → <code>src/views/student/list/index.vue</code>；
                        找不到时前端会回落到占位页而不是报错。
                      </div>
                    </el-form-item>
                  </el-col>
                </el-row>

                <el-row :gutter="16">
                  <el-col :span="12">
                    <el-form-item label="组织层级">
                      <el-input v-model="form.orgTypes" placeholder="如 Province,City；留空不限" />
                    </el-form-item>
                  </el-col>
                  <el-col :span="12">
                    <el-form-item label="其它">
                      <el-checkbox v-model="form.isHidden">隐藏菜单（仍可按路径访问）</el-checkbox>
                      <el-checkbox v-model="form.isExternal">外链</el-checkbox>
                    </el-form-item>
                  </el-col>
                </el-row>
              </template>

              <el-form-item v-if="selectedKind !== 'action'" label="备注">
                <el-input v-model="form.remark" type="textarea" :rows="2" />
              </el-form-item>
            </el-form>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <el-dialog v-model="dialogVisible" :title="dialogTitle" width="560px">
      <el-form label-width="110px">
        <el-form-item label="上级">
          <el-input :model-value="createParentName" disabled />
        </el-form-item>
        <el-form-item label="编码" required>
          <el-input v-model="createForm.code" placeholder="如 Student / Apply / Query" />
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="createForm.name" placeholder="界面上显示的名字" />
        </el-form-item>

        <template v-if="createKind === 'module'">
          <el-form-item label="路由路径">
            <el-input v-model="createForm.path" placeholder="如 /student/list" />
          </el-form-item>
          <el-form-item label="前端组件">
            <el-input v-model="createForm.component" placeholder="如 student/list/index" />
          </el-form-item>
        </template>

        <el-row v-if="createKind !== 'action'" :gutter="16">
          <el-col :span="12">
            <el-form-item label="图标">
              <el-input v-model="createForm.icon" placeholder="如 User / School" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="createForm.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <el-alert
        v-if="createKind === 'action'"
        type="warning"
        :closable="false"
        show-icon
        title="新增操作后需要重启后端，新的权限码才会被 [Authorize] 认识。"
      />

      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="doCreate">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.meta__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.meta__tree-card :deep(.el-card__body) {
  max-height: calc(100vh - 260px);
  overflow: auto;
}

.meta__search {
  margin-bottom: 12px;
}

.meta__tip {
  margin-bottom: 12px;
}

.meta__node {
  display: flex;
  align-items: center;
  gap: 6px;
}

.meta__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

.meta__inline {
  margin-left: 12px;
}

.meta__form {
  margin-top: 8px;
}
</style>
