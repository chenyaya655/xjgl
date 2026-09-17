import fs from 'node:fs'

import { expect, test } from '@playwright/test'

import { apiLogin, ensureSchool, login, seedStudent, type SeededStudent } from './support/auth'

/**
 * 学生档案的完整流程：查 → 详情 → 子表 → 变更留痕 → 打印学籍卡。
 *
 * 这一条链路串起了 P2 的全部能力，也是"每个模块的操作"里最核心的一段。
 * 其中打印那一步会**真的下载一个 PDF 并检查它的头部字节**——
 * 只看"点完没报错"是不够的：渲染失败时接口可能返回 200 但内容是一段错误 JSON。
 */
test.describe('学生档案流程', () => {
  let student: SeededStudent

  test.beforeAll(async ({ request }) => {
    const token = await apiLogin(request)
    const school = await ensureSchool(request, token)

    // 用唯一姓名，避免与历史数据混淆（测试库是长期累积的）
    student = await seedStudent(request, token, school.id, '端到端测试生')
  })

  test.beforeEach(async ({ page }) => {
    await login(page)
  })

  test('列表能按姓名查到学生，并显示掩码身份证', async ({ page }) => {
    await page.goto('/student/list')

    // ★ 学校下拉也必须是虚拟滚动的：这一页原本会把全省几万所学校全渲染成 DOM 节点，
    //   实测光进这一页就让主线程卡住一分钟（用例动不动"超时"，其实是被渲染拖死的）。
    await expect
      .poll(async () => page.evaluate(() => document.querySelectorAll('.el-select-dropdown__item').length))
      .toBeLessThan(200)

    await page.getByPlaceholder('姓名 / 学籍辅号').fill(student.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    const row = page.locator('.el-table__row', { hasText: student.name })

    await expect(row).toBeVisible()
    await expect(row).toContainText(student.studentNo)

    // 身份证必须是掩码形态（明文出现在列表里就是泄露）
    await expect(row).toContainText(/\d{6}\*{8}\d{4}/)
  })

  test('详情抽屉能新增监护人，并留下变更记录', async ({ page }) => {
    await page.goto('/student/list')
    await page.getByPlaceholder('姓名 / 学籍辅号').fill(student.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    const row = page.locator('.el-table__row', { hasText: student.name })

    await row.getByRole('button', { name: '详情' }).click()

    // 六个子表 + 只读的变更记录，共 7 个标签页
    const drawer = page.locator('.el-drawer')
    await expect(drawer.getByRole('tab', { name: '监护人' })).toBeVisible()
    await expect(drawer.getByRole('tab', { name: '变更记录' })).toBeVisible()

    // 新增一个监护人
    await drawer.getByRole('button', { name: '新增监护人' }).click()

    const dialog = page.locator('.el-dialog', { hasText: '新增监护人' })
    await expect(dialog).toBeVisible()

    const guardianName = `E2E家长${Date.now().toString().slice(-5)}`

    await dialog.locator('.el-form-item', { hasText: '姓名' }).locator('input').first().fill(guardianName)
    await dialog.locator('.el-form-item', { hasText: '联系电话' }).locator('input').first().fill('13900000001')
    await dialog.getByRole('button', { name: '保存' }).click()

    await expect(dialog).toBeHidden()
    await expect(drawer.locator('.el-table__row', { hasText: guardianName })).toBeVisible()

    // 建档 + 新增监护人至少产生两条留痕，且变更记录页是只读的（没有新增按钮）
    await drawer.getByRole('tab', { name: '变更记录' }).click()

    await expect(drawer.locator('.el-table__row').first()).toBeVisible()
    await expect(drawer.getByRole('button', { name: '新增变更记录' })).toHaveCount(0)
  })

  test('能打印学籍卡，且下载到的确实是 PDF', async ({ page }) => {
    await page.goto('/student/list')
    await page.getByPlaceholder('姓名 / 学籍辅号').fill(student.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    await page
      .locator('.el-table__row', { hasText: student.name })
      .getByRole('button', { name: '详情' })
      .click()

    const drawer = page.locator('.el-drawer')
    await expect(drawer).toBeVisible()

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      drawer.getByRole('button', { name: '打印学籍卡' }).click()
    ])

    expect(download.suggestedFilename()).toMatch(/学籍卡.*\.pdf$/)

    const filePath = await download.path()
    expect(filePath).toBeTruthy()

    const bytes = fs.readFileSync(filePath!)

    // 必须是真 PDF：渲染失败时接口可能返回 200 但内容是错误 JSON
    expect(bytes.subarray(0, 5).toString('ascii')).toBe('%PDF-')
    expect(bytes.length).toBeGreaterThan(2000)
  })

  test('能导出当前筛选结果（CSV，含表头与掩码身份证）', async ({ page }) => {
    await page.goto('/student/list')
    await page.getByPlaceholder('姓名 / 学籍辅号').fill(student.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('button', { name: '导出' }).click()
    ])

    expect(download.suggestedFilename()).toMatch(/\.csv$/)

    const content = fs.readFileSync((await download.path())!, 'utf8')

    expect(content).toContain('姓名')
    expect(content).toContain(student.name)
    expect(content).toMatch(/\d{6}\*{8}\d{4}/)
  })

  test('导入对话框可以打开并给出模板与列说明', async ({ page }) => {
    await page.goto('/student/list')

    await page.getByRole('button', { name: '导入' }).click()

    const dialog = page.locator('.el-dialog', { hasText: '批量导入学生' })

    await expect(dialog).toBeVisible()
    await expect(dialog.getByText(/模板|必填|列/).first()).toBeVisible()
  })
})
