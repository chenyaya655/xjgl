<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

import { codeApi } from '@/api'
import type { CodeCategory, CodeItem } from '@/api/types'
import AppIcon from '@/components/AppIcon.vue'
import { MetadataPermissions } from '@/constants/permissions'
import { useAuthStore } from '@/stores/auth'

/**
 * 微代码（码表）管理。
 *
 * 学籍系统里有大量国标码值（性别、民族、政治面貌、身份证件类型、学籍状态…），
 * 它们的共同点是：**会被业务页面频繁读取，但很少变化**。因此：
 *   - 查询接口对所有登录用户开放（下拉框都要用），写操作才要权限；
 *   - `cacheable` 的码表走后端缓存，改完码值需要失效缓存（本页提供手动入口）。
 *
 * 两处界面上的判断：
 *   1) **系统内置码表（isSystem）不给改** —— 服务端本来就会拒绝，
 *      界面上直接禁用比让用户点了再报错体验好；
 *   2) **树形码表按树渲染** —— `isTree` 为真时用 el-table 的树形数据展示，
 *      否则学籍系统的行政区划这类层级码表会全部平铺，根本看不出从属关系。
 */
const auth = useAuthStore()

const canCreate = computed(() => auth.can(MetadataPermissions.Sys.Code.Create))
const canUpdate = computed(() => auth.can(MetadataPermissions.Sys.Code.Update))
const canDelete = computed(() => auth.can(MetadataPermissions.Sys.Code.Delete))

const loading = ref(false)
const categories = ref<CodeCategory[]>([])
const keyword = ref('')
const selected = ref<CodeCategory | null>(null)

const itemsLoading = ref(false)
const items = ref<CodeItem[]>([])

async function loadCategories(): Promise<void> {
  loading.value = true

  try {
    categories.value = await codeApi.getCategories(keyword.value.trim() || undefined)

    // 保持选中项（重新加载后对象会变），否则右侧会忽然空掉
    if (selected.value) {
      selected.value = categories.value.find((c) => c.id === selected.value!.id) ?? null
    }
  } finally {
    loading.value = false
  }
}

async function selectCategory(category: CodeCategory): Promise<void> {
  selected.value = category
  itemsLoading.value = true

  try {
    // 树形码表要求后端组装成树，平铺表格看不出层级
    items.value = await codeApi.getItems(category.id, false, category.isTree)
  } finally {
    itemsLoading.value = false
  }
}

/** 系统内置码表不允许改（服务端也会拒绝，这里提前拦住）。 */
const readonly = computed(() => selected.value?.isSystem ?? false)

// ---------------------------------------------------------------------------
// 码类别
// ---------------------------------------------------------------------------

const catDialogVisible = ref(false)
const catSaving = ref(false)
const editingCatId = ref<string | null>(null)

const catForm = reactive({
  code: '',
  name: '',
  stdSource: '',
  isTree: false,
  isEnabled: true,
  cacheable: true,
  sort: 0,
  remark: ''
})

function openCategory(row?: CodeCategory): void {
  editingCatId.value = row?.id ?? null
  catForm.code = row?.code ?? ''
  catForm.name = row?.name ?? ''
  catForm.stdSource = row?.stdSource ?? ''
  catForm.isTree = row?.isTree ?? false
  catForm.isEnabled = row?.isEnabled ?? true
  catForm.cacheable = row?.cacheable ?? true
  catForm.sort = row?.sort ?? 0
  catForm.remark = row?.remark ?? ''
  catDialogVisible.value = true
}

async function saveCategory(): Promise<void> {
  if (!catForm.code.trim() || !catForm.name.trim()) {
    ElMessage.warning('编码与名称都不能为空。')

    return
  }

  catSaving.value = true

  try {
    const payload = {
      code: catForm.code.trim(),
      name: catForm.name.trim(),
      stdSource: catForm.stdSource?.trim() || null,
      isTree: catForm.isTree,
      isEnabled: catForm.isEnabled,
      cacheable: catForm.cacheable,
      sort: catForm.sort,
      remark: catForm.remark?.trim() || null
    }

    if (editingCatId.value) {
      await codeApi.updateCategory(editingCatId.value, payload)
      ElMessage.success('码类别已更新')
    } else {
      await codeApi.createCategory(payload)
      ElMessage.success('码类别已创建')
    }

    catDialogVisible.value = false
    await loadCategories()
  } finally {
    catSaving.value = false
  }
}

async function removeCategory(row: CodeCategory): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定删除码类别「${row.name}」吗？其下 ${row.itemCount} 条码项会一并处理，` +
        '已被业务数据引用的码值删除后会导致历史数据无法翻译。',
      '删除确认',
      { type: 'warning' }
    )
  } catch {
    return
  }

  await codeApi.removeCategory(row.id)
  ElMessage.success('已删除')

  if (selected.value?.id === row.id) {
    selected.value = null
    items.value = []
  }

  await loadCategories()
}

// ---------------------------------------------------------------------------
// 码项
// ---------------------------------------------------------------------------

const itemDialogVisible = ref(false)
const itemSaving = ref(false)
const editingItemId = ref<string | null>(null)

const itemForm = reactive({
  code: '',
  name: '',
  stdCode: '',
  sort: 0,
  isEnabled: true,
  remark: ''
})

function openItem(row?: CodeItem): void {
  editingItemId.value = row?.id ?? null
  itemForm.code = row?.code ?? ''
  itemForm.name = row?.name ?? ''
  itemForm.stdCode = row?.stdCode ?? ''
  itemForm.sort = row?.sort ?? 0
  itemForm.isEnabled = row?.isEnabled ?? true
  itemForm.remark = row?.remark ?? ''
  itemDialogVisible.value = true
}

async function saveItem(): Promise<void> {
  if (!selected.value) {
    return
  }

  if (!itemForm.code.trim() || !itemForm.name.trim()) {
    ElMessage.warning('码值与名称都不能为空。')

    return
  }

  itemSaving.value = true

  try {
    const payload = {
      categoryId: selected.value.id,
      code: itemForm.code.trim(),
      name: itemForm.name.trim(),
      parentId: null,
      stdCode: itemForm.stdCode?.trim() || null,
      sort: itemForm.sort,
      isEnabled: itemForm.isEnabled,
      extJson: null,
      remark: itemForm.remark?.trim() || null
    }

    if (editingItemId.value) {
      await codeApi.updateItem(editingItemId.value, payload)
      ElMessage.success('码项已更新')
    } else {
      await codeApi.createItem(payload)
      ElMessage.success('码项已创建')
    }

    itemDialogVisible.value = false
    await Promise.all([selectCategory(selected.value), loadCategories()])
  } finally {
    itemSaving.value = false
  }
}

async function removeItem(row: CodeItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定删除码值「${row.name}（${row.code}）」吗？`, '删除确认', {
      type: 'warning'
    })
  } catch {
    return
  }

  await codeApi.removeItem(row.id)
  ElMessage.success('已删除')

  if (selected.value) {
    await Promise.all([selectCategory(selected.value), loadCategories()])
  }
}

async function refreshCache(): Promise<void> {
  await codeApi.refreshCache()
  ElMessage.success('码表缓存已刷新')
}

onMounted(async () => {
  await loadCategories()

  if (categories.value.length > 0) {
    await selectCategory(categories.value[0])
  }
})
</script>

<template>
  <div class="code">
    <el-row :gutter="16">
      <!-- ---------------- 左：码类别 ---------------- -->
      <el-col :span="9">
        <el-card shadow="never" class="code__cat-card">
          <template #header>
            <div class="code__header">
              <span>码类别（{{ categories.length }}）</span>
              <div>
                <el-button size="small" @click="refreshCache">刷新缓存</el-button>
                <el-button v-if="canCreate" size="small" type="primary" @click="openCategory()">
                  <AppIcon name="Plus" />
                  <span>新增</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-input
            v-model="keyword"
            placeholder="搜索编码 / 名称"
            clearable
            class="code__search"
            @keyup.enter="loadCategories"
          >
            <template #prefix>
              <AppIcon name="Search" />
            </template>
          </el-input>

          <el-alert
            type="info"
            :closable="false"
            show-icon
            class="code__tip"
            title="标记「内置」的码表由国标/教育行业标准定义，服务端不允许修改。"
          />

          <el-table
            v-loading="loading"
            :data="categories"
            size="small"
            highlight-current-row
            height="calc(100vh - 330px)"
            @row-click="selectCategory"
          >
            <el-table-column prop="name" label="名称" min-width="130" show-overflow-tooltip />
            <el-table-column prop="code" label="编码" width="110" />
            <el-table-column prop="itemCount" label="码值" width="60" align="center" />
            <el-table-column label="" width="70" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.isSystem" size="small" type="info" effect="plain">内置</el-tag>
                <el-tag v-else-if="row.isTree" size="small" type="warning" effect="plain">树</el-tag>
              </template>
            </el-table-column>
            <el-table-column v-if="canUpdate" label="操作" width="92" align="center">
              <template #default="{ row }">
                <el-button link type="primary" :disabled="row.isSystem" @click.stop="openCategory(row)">
                  编辑
                </el-button>
                <el-button
                  v-if="canDelete"
                  link
                  type="danger"
                  :disabled="row.isSystem"
                  @click.stop="removeCategory(row)"
                >
                  删
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>

      <!-- ---------------- 右：码项 ---------------- -->
      <el-col :span="15">
        <el-card shadow="never">
          <template #header>
            <div class="code__header">
              <span>
                码值
                <span v-if="selected" class="code__muted">
                  — {{ selected.name }}（{{ selected.code }}）
                  <template v-if="selected.stdSource">· 依据 {{ selected.stdSource }}</template>
                </span>
              </span>
              <div v-if="selected">
                <el-button
                  v-if="canCreate"
                  size="small"
                  type="primary"
                  :disabled="readonly"
                  @click="openItem()"
                >
                  <AppIcon name="Plus" />
                  <span>新增码值</span>
                </el-button>
              </div>
            </div>
          </template>

          <el-empty v-if="!selected" description="请在左侧选择一个码类别" :image-size="80" />

          <template v-else>
            <el-alert
              v-if="readonly"
              type="warning"
              :closable="false"
              show-icon
              class="code__tip"
              title="这是系统内置码表，只读。如需在业务上屏蔽某些码值，请停用对应码项而不是删除。"
            />

            <el-table
              v-loading="itemsLoading"
              :data="items"
              row-key="id"
              :tree-props="{ children: 'children' }"
              :default-expand-all="selected.isTree"
              border
              height="calc(100vh - 330px)"
            >
              <el-table-column prop="code" label="码值" width="130" />
              <el-table-column prop="name" label="名称" min-width="180" show-overflow-tooltip />
              <el-table-column prop="stdCode" label="标准码" width="120">
                <template #default="{ row }">{{ row.stdCode || '—' }}</template>
              </el-table-column>
              <el-table-column prop="sort" label="排序" width="70" align="center" />
              <el-table-column label="状态" width="80" align="center">
                <template #default="{ row }">
                  <el-tag size="small" :type="row.isEnabled ? 'success' : 'info'">
                    {{ row.isEnabled ? '启用' : '停用' }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="remark" label="备注" min-width="140" show-overflow-tooltip />
              <el-table-column v-if="canUpdate" label="操作" width="110" align="center" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" :disabled="readonly" @click="openItem(row)">编辑</el-button>
                  <el-button v-if="canDelete" link type="danger" :disabled="readonly" @click="removeItem(row)">
                    删除
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <!-- ---------------- 码类别编辑 ---------------- -->
    <el-dialog
      v-model="catDialogVisible"
      :title="editingCatId ? '编辑码类别' : '新增码类别'"
      width="560px"
    >
      <el-form label-width="110px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="编码" required>
              <el-input v-model="catForm.code" :disabled="!!editingCatId" placeholder="如 NATION" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="名称" required>
              <el-input v-model="catForm.name" placeholder="如 民族码" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="标准依据">
          <el-input v-model="catForm.stdSource" placeholder="如 GB/T 3304" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="catForm.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="属性">
              <el-checkbox v-model="catForm.isTree">树形码表</el-checkbox>
              <el-checkbox v-model="catForm.cacheable">可缓存</el-checkbox>
              <el-checkbox v-model="catForm.isEnabled">启用</el-checkbox>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="备注">
          <el-input v-model="catForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="catDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="catSaving" @click="saveCategory">保存</el-button>
      </template>
    </el-dialog>

    <!-- ---------------- 码项编辑 ---------------- -->
    <el-dialog v-model="itemDialogVisible" :title="editingItemId ? '编辑码值' : '新增码值'" width="520px">
      <el-form label-width="100px">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="码值" required>
              <el-input v-model="itemForm.code" placeholder="如 01" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="名称" required>
              <el-input v-model="itemForm.name" placeholder="如 汉族" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="标准码">
              <el-input v-model="itemForm.stdCode" placeholder="国标原始码值" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="itemForm.sort" :min="0" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="启用">
          <el-switch v-model="itemForm.isEnabled" />
          <span class="code__muted code__inline">停用后业务下拉框不再出现，但历史数据仍能翻译</span>
        </el-form-item>

        <el-form-item label="备注">
          <el-input v-model="itemForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="itemDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="itemSaving" @click="saveItem">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.code__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.code__cat-card :deep(.el-card__body) {
  overflow: hidden;
}

.code__search {
  margin-bottom: 12px;
}

.code__tip {
  margin-bottom: 12px;
}

.code__muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.code__inline {
  margin-left: 12px;
}
</style>
