<script setup lang="ts">
import { computed, ref, watch } from 'vue'

import { orgApi } from '@/api'
import type { ClassDto, GradeDto } from '@/api/types'

/**
 * 年级 / 班级下拉框（**虚拟滚动 + 按需加载**）。
 *
 * 【为什么必须是独立组件，而不是各页自己拉一次数据】
 * 年级与班级都挂在「学校 + 学年」下（见后端 `SysGrade` 的注释），
 * 所以这条链有三层依赖：**学校 → 学年 → 年级 → 班级**。
 * 让每个页面自己处理这条链，就必然出现三类缺陷，而且都不报错：
 *   1) 没选学校就去拉年级 —— 接口要 `schoolOrgId`，请求会 404 或拿到空；
 *   2) 换了学校不清空已选的年级/班级 —— 界面上会显示"某年级"，
 *      但那个 Id 属于上一所学校，查询条件因此在别处生效，结果为空；
 *   3) 学年留空时后端要求必填 —— 需要由组件统一回落到"当前学年"。
 * 把这条链收在一个组件里，页面只需要一个 `v-model`。
 *
 * 【为什么用 el-select-v2】
 * 与 `SchoolSelect` 同一个理由（见 12.2 的规约）：普通 `el-select` 会为每个选项
 * 渲染一个真实 DOM。一所大校一个学年可能有一两百个班，多个页面各开一次下拉就会卡；
 * 虚拟滚动让 DOM 数量与选项数无关。**上千条的集合一律用虚拟滚动下拉**是硬规约。
 */
const gradeId = defineModel<string | undefined>('gradeId')
const classId = defineModel<string | undefined>('classId')

const props = withDefaults(
  defineProps<{
    /** 已选学校；未选时两个下拉都禁用（年级班级不属于任何学校是无意义的）。 */
    schoolOrgId?: string
    /** 学年；留空时回落到 `defaultAcademicYear`。 */
    academicYear?: string
    /** 学年留空时的回落值（一般传当前学年，如 2025-2026）。 */
    defaultAcademicYear?: string
    /** 展示哪几个：年级 / 班级 / 两个都要。 */
    show?: 'both' | 'grade' | 'class'
    gradeWidth?: string
    classWidth?: string
  }>(),
  {
    schoolOrgId: undefined,
    academicYear: '',
    defaultAcademicYear: '',
    show: 'both',
    gradeWidth: '140px',
    classWidth: '140px'
  }
)

const grades = ref<GradeDto[]>([])
const classes = ref<ClassDto[]>([])
const loadingGrades = ref(false)
const loadingClasses = ref(false)
/** 载入失败的原因：显示在占位符里，而不是静默给一个空下拉。 */
const gradesError = ref('')
const classesError = ref('')

/** 实际生效的学年：外部没传就用回落值。 */
const effectiveYear = computed(() => props.academicYear?.trim() || props.defaultAcademicYear)

const gradeOptions = computed(() =>
  grades.value.map((g) => ({ label: g.gradeName, value: g.id }))
)

const classOptions = computed(() =>
  classes.value.map((c) => ({ label: c.className, value: c.id }))
)

const gradeDisabled = computed(() => !props.schoolOrgId || !effectiveYear.value)
const classDisabled = computed(() => gradeDisabled.value || !gradeId.value)

const gradePlaceholder = computed(() => {
  if (!props.schoolOrgId) {
    return '请先选学校'
  }

  if (!effectiveYear.value) {
    return '缺学年'
  }

  if (gradesError.value) {
    return '年级载入失败'
  }

  return grades.value.length === 0 && !loadingGrades.value ? '该校该学年无年级' : '全部年级'
})

const classPlaceholder = computed(() => {
  if (!gradeId.value) {
    return '请先选年级'
  }

  if (classesError.value) {
    return '班级载入失败'
  }

  return classes.value.length === 0 && !loadingClasses.value ? '该年级无班级' : '全部班级'
})

async function loadGrades(): Promise<void> {
  gradesError.value = ''
  grades.value = []
  classes.value = []
  classId.value = undefined

  const school = props.schoolOrgId
  const year = effectiveYear.value

  if (!school || !year) {
    // 清掉**上一个学校的**选择：留着它界面会显示一个属于别的学校的年级，
    // 而查询条件会静默作用在别处（见类注释第 2 条）
    gradeId.value = undefined

    return
  }

  loadingGrades.value = true

  try {
    grades.value = await orgApi.getGrades(school, year)
  } catch (e) {
    // 失败不抛：年级拉不到不应让整页打不开；把原因写进 placeholder
    gradesError.value = e instanceof Error ? e.message : '年级载入失败'
  } finally {
    loadingGrades.value = false
  }

  // 如果外部传进来的 gradeId 不在新列表里，说明它属于上一所学校 —— 清掉
  if (gradeId.value && !grades.value.some((g) => g.id === gradeId.value)) {
    gradeId.value = undefined
  }
}

async function loadClasses(): Promise<void> {
  classesError.value = ''
  classes.value = []
  classId.value = undefined

  const school = props.schoolOrgId
  const grade = gradeId.value

  if (!school || !grade) {
    return
  }

  loadingClasses.value = true

  try {
    classes.value = await orgApi.getClasses({
      schoolOrgId: school,
      academicYear: effectiveYear.value || undefined,
      gradeId: grade
    })
  } catch (e) {
    classesError.value = e instanceof Error ? e.message : '班级载入失败'
  } finally {
    loadingClasses.value = false
  }

  if (classId.value && !classes.value.some((c) => c.id === classId.value)) {
    classId.value = undefined
  }
}

// 学校或学年一变 → 重新拉年级（并连带清空班级）
watch(
  () => [props.schoolOrgId, effectiveYear.value] as const,
  () => void loadGrades(),
  { immediate: true }
)

// 年级一变 → 重新拉班级
watch(gradeId, () => void loadClasses())

/**
 * 供父组件在"重置"时调用：把两个下拉的数据也清掉。
 *
 * 只把 v-model 置空是不够的 —— 选项列表还留在组件里，
 * 用户会看到"学校已清空、年级下拉里却还有上一所学校的年级"。
 */
function reset(): void {
  gradeId.value = undefined
  classId.value = undefined
  grades.value = []
  classes.value = []
}

defineExpose({ reset, reloadGrades: loadGrades, reloadClasses: loadClasses })
</script>

<template>
  <template v-if="show !== 'class'">
    <el-select-v2
      v-model="gradeId"
      :options="gradeOptions"
      :placeholder="gradePlaceholder"
      :disabled="gradeDisabled"
      :loading="loadingGrades"
      :style="{ width: gradeWidth }"
      clearable
      filterable
    />
  </template>

  <template v-if="show !== 'grade'">
    <el-select-v2
      v-model="classId"
      :options="classOptions"
      :placeholder="classPlaceholder"
      :disabled="classDisabled"
      :loading="loadingClasses"
      :style="{ width: classWidth }"
      clearable
      filterable
    />
  </template>
</template>
