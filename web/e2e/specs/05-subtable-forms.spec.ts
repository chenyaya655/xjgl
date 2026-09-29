import { expect, test } from '@playwright/test'

import { ensureSchool, apiLogin, chineseNumeral, login, seedStudent, type SeededStudent } from './support/auth'

/**
 * 学生档案 6 张子表的**界面**增删改（债 11 的后半段）。
 *
 * 【为什么服务层已经有 18 个用例了还要在浏览器里再点一遍】
 * 服务层用例（`StudentProfileTests`）证明的是"接口按预期工作"，
 * 它**证明不了**下面这些 —— 而这三类缺陷本项目都真实踩过：
 *   1) 六个标签页共用一套"字段由描述驱动"的表单模板，某个页的 `fields` 配错
 *      （字段名拼错、必填项漏配）时接口照样正常，只是那个页面上**这个字段根本不存在**；
 *   2) 表单字段与 DTO 属性名不一致 → 提交时静默丢字段，保存成功但内容为空；
 *   3) 码值下拉为空（码表没载入）时，选择框看起来正常，但选不出任何值。
 * 这三类都不会让服务层用例变红。
 *
 * 【每个标签页只断言"能建、能看到、能删"】
 * 每张子表的业务规则（第一监护人唯一、体检一学年一条、简历日期有序…）
 * 已经由服务层用例覆盖，这里**刻意不重复**：浏览器用例慢且脆，
 * 它该守的是"这条路走得通"，不是把所有规则再验一遍。
 */
test.describe('学生档案 6 张子表的界面增删改', () => {
  let student: SeededStudent

  test.beforeAll(async ({ request }) => {
    const token = await apiLogin(request)
    const school = await ensureSchool(request, token)

    student = await seedStudent(request, token, school.id, '子表界面测试生')
  })

  test.beforeEach(async ({ page }) => {
    await login(page)
  })

  /** 打开该学生的详情抽屉。 */
  async function openDrawer(page: import('@playwright/test').Page) {
    await page.goto('/student/list')
    await page.getByPlaceholder('姓名 / 学籍辅号').fill(student.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    // 靠**学籍辅号**定位并取第一行：姓名不是唯一键，
    // 命中多行时 Playwright 的 strict mode 会直接报错（见 support/auth.ts 的说明）
    const row = page.locator('.el-table__row', { hasText: student.studentNo }).first()

    await row.getByRole('button', { name: '详情' }).click()

    const drawer = page.locator('.el-drawer')

    await expect(drawer.getByRole('tab', { name: '监护人' })).toBeVisible()

    return drawer
  }

  /**
   * 六张子表的用例参数。
   *
   * `probe` 是"这次填进去的那个值"，列表里能搜到它，就说明
   * "字段确实存在，且值确实提交到了后端"。
   *
   * ⚠ `probe` **必须每轮都不同**（所以拼了一个中文数字后缀）。
   * 子表的增删改用例是**破坏性**的，而测试库是长期累积的：
   *   - probe 固定时，上一轮留下的同名行会让"删完之后搜不到"永远不成立
   *     （删掉的是 `.first()` 那一行，历史行还在）；
   *   - 而且那种失败信息（"expected 0 but was 1"）看起来像"删除功能坏了"，
   *     真实原因只是测试数据没隔离。
   * 序号用中文数字而不是 `Date.now()`：后端对姓名类字段做规范汉字校验。
   */
  const runSuffix = chineseNumeral(Number(String(Date.now()).slice(-5)))

  const cases = [
    {
      tab: '监护人',
      probe: `界面家长${runSuffix}`,
      fields: [
        { label: '姓名', value: `界面家长${runSuffix}` },
        { label: '联系电话', value: '13900000002' }
      ]
    },
    {
      tab: '学习简历',
      probe: `界面幼儿园${runSuffix}`,
      fields: [
        { label: '学校（园）名称', value: `界面幼儿园${runSuffix}` },
        { label: '开始日期', value: '2019-09-01' }
      ]
    },
    {
      tab: '奖励',
      probe: `界面三好学生${runSuffix}`,
      fields: [
        { label: '获奖日期', value: '2025-06-01' },
        { label: '奖励名称', value: `界面三好学生${runSuffix}` }
      ]
    },
    {
      tab: '处分',
      probe: `界面警告${runSuffix}`,
      fields: [
        { label: '处分日期', value: '2024-11-01' },
        { label: '处分名称', value: `界面警告${runSuffix}` }
      ]
    },
    {
      tab: '资助',
      probe: `界面寄宿补助${runSuffix}`,
      fields: [
        { label: '资助项目', value: `界面寄宿补助${runSuffix}` }
      ]
    },
    {
      tab: '体质健康',
      // 体检没有"名称"类字段，用身高的字符串形式当探针（列表里能搜到就说明落库了）。
      // 身高是数字，每轮给一个不同的值即可 —— 它不受汉字校验限制。
      probe: String(130 + (Number(String(Date.now()).slice(-2)) % 20)),
      fields: [
        { label: '身高（厘米）', value: String(130 + (Number(String(Date.now()).slice(-2)) % 20)) }
      ]
    }
  ] as const

  for (const item of cases) {
    test(`【${item.tab}】能新增、能看到、能删除`, async ({ page }) => {
      const drawer = await openDrawer(page)

      await drawer.getByRole('tab', { name: item.tab }).click()

      // 新增
      await drawer.getByRole('button', { name: `新增${item.tab}` }).click()

      const dialog = page.locator('.el-dialog', { hasText: `新增${item.tab}` })

      await expect(dialog).toBeVisible()

      for (const field of item.fields) {
        const formItem = dialog.locator('.el-form-item', { hasText: field.label }).first()
        const input = formItem.locator('input').first()

        await input.fill(field.value)
        // 日期选择器需要回车/失焦才写回 v-model，否则提交时该字段是空的
        await input.press('Enter')
      }

      await dialog.getByRole('button', { name: '保存' }).click()

      await expect(dialog).toBeHidden()

      // 值确实到了后端（列表里搜得到）—— 这一步才真正证明"字段名没写错"
      //
      // ⚠ 取 `.first()`：测试库是长期累积的，历史运行可能留下同值的行；
      // 我们的目的是"这次新增的确实落库了"，不是"全库只有一条"。
      const created = drawer.locator('.el-table__row', { hasText: item.probe }).first()

      await expect(created).toBeVisible()

      // 删除（弹确认框）
      //
      // ⚠ 确认按钮的文案是「删除」而不是默认的「确定」：
      // `StudentProfileDrawer` 创建确认框时显式设了 `confirmButtonText: '删除'`。
      // 按「确定」找会一直等到 90 秒超时，而错误信息只说"locator.click 超时"，
      // 完全指不到"按钮文案不对"这个原因。
      await created.getByRole('button', { name: '删除' }).click()

      const confirm = page.locator('.el-message-box')

      await expect(confirm).toBeVisible()
      await confirm.getByRole('button', { name: '删除' }).click()

      // 删除是软删 + 列表按学生重新加载，所以被删掉的那一行必然消失。
      //
      // ⚠ 这里**不能**写成 `await expect(created).toHaveCount(0)`：
      // `created` 带了 `.first()`，而"带 .first() 的定位器"最多只有 1 个元素，
      // 一旦它还匹配到东西，`toHaveCount(0)` 就永远失败 ——
      // 即使 `.first()` 指向的那一行**已经被删掉、换成了另一行**。
      // 用 toBeHidden 表达"这一行不在了"才准确。
      await expect(created).toBeHidden()
    })
  }

  test('变更记录由新增动作自动写入，且没有新增按钮', async ({ page }) => {
    const drawer = await openDrawer(page)

    // 先造一条（用监护人页，最快）
    await drawer.getByRole('button', { name: '新增监护人' }).click()

    const dialog = page.locator('.el-dialog', { hasText: '新增监护人' })

    await dialog.locator('.el-form-item', { hasText: '姓名' }).first().locator('input').first()
      .fill('留痕家长')
    await dialog.getByRole('button', { name: '保存' }).click()
    await expect(dialog).toBeHidden()

    await drawer.getByRole('tab', { name: '变更记录' }).click()

    await expect(drawer.locator('.el-table__row').first()).toBeVisible()
    await expect(drawer.getByRole('button', { name: '新增变更记录' })).toHaveCount(0)
  })
})
