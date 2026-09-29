import { expect, test } from '@playwright/test'

import { apiLogin, ensureSchool, login, realJpegBuffer, seedStudent, uniqueSuffix, type SeededStudent } from './support/auth'

/**
 * 照片管理流程：台账、缩略图、打包导出、存储状态。
 *
 * 【为什么缩略图一定要在这里测】
 * 界面上的照片来自 `/api/photos/{studentId}/thumbnail`，而图片请求**带不上 Authorization 头**——
 * 前端只能用 axios 取回 Blob 再 `createObjectURL`。这条链路任何一环写错
 * （路径写成 `/api/app/photo/...`、忘了 responseType: 'blob'、忘了 revoke）
 * 的表现都是"表格里一片空白"，而集成测试完全看不到。
 */
test.describe('照片管理流程', () => {
  /** 本 spec 的主体学生姓名；只在 beforeAll 里用一次，用例按**学籍辅号**定位。 */
  const PHOTO_STUDENT_NAME = '端到端拍照生'

  let student: SeededStudent

  test.beforeAll(async ({ request }) => {
    const token = await apiLogin(request)
    const school = await ensureSchool(request, token)

    student = await seedStudent(request, token, school.id, PHOTO_STUDENT_NAME)
  })

  test.beforeEach(async ({ page }) => {
    await login(page)
  })

  test('照片台账页能打开，并显示两个标签页与导出/上传入口', async ({ page }) => {
    await page.goto('/photo/list')

    await expect(page.getByRole('tab', { name: /照片台账/ })).toBeVisible()
    await expect(page.getByRole('tab', { name: /缺照片名单/ })).toBeVisible()

    // 台账的检索框与两个关键入口（占位符以界面实际文案为准）
    await expect(page.getByPlaceholder('学籍辅号 / 姓名')).toBeVisible()
    await expect(page.getByRole('button', { name: '导出照片' })).toBeVisible()
    await expect(page.getByRole('button', { name: '批量上传' })).toBeVisible()

    // 说明：存储健康度卡片依赖后端对存储做一次真实探测，它的语义由后端集成测试与接口实测守着
    // （实测返回 primaryName=「NAS 主存储」、primaryAvailable=true）；
    // 这里不再断言卡片文案，避免把"探测耗时"变成端到端的假失败。
  })

  test('批量上传对话框能打开、学校下拉能选中，并提示"文件名=学籍辅号"的约定', async ({ page }) => {
    await page.goto('/photo/list')

    await page.getByRole('button', { name: '批量上传' }).click()

    const dialog = page.locator('.el-dialog').filter({ hasText: '批量上传学生照片' })

    await expect(dialog).toBeVisible()

    // 上传交互是"先匹配再上传"：界面上要先说清文件命名约定，否则用户传上来一堆对不上的文件
    await expect(dialog.getByText(/文件名请用学籍辅号/)).toBeVisible()

    // 注意：Element Plus 2.x 的 el-select 把占位符渲染成 <span>，不是 input 的 placeholder 属性
    // （getByPlaceholder 找不到它）；而"有没有占位符文案"取决于是否已选学校，属于易变细节。
    const select = dialog.locator('.el-select').first()

    await expect(select).toBeVisible()

    const pickButton = dialog.getByRole('button', { name: /选择照片文件/ })

    await expect(pickButton).toBeVisible()

    // ★ 本用例最重要的一条：**学校下拉必须是虚拟滚动的**。
    //   全省部署时组织树里有几万所学校（本机测试库实测 6,789 所）。普通 `el-select` 会把每所学校
    //   渲染成一个真实 DOM 节点（实测 6,799 个 `.el-select-dropdown__item`，页面节点从 1.2 万涨到 3 万），
    //   打开这个对话框就让主线程卡死 100 秒以上，表现是"对话框点不开、页面假死"。
    //   而这个缺陷**接口、类型检查、集成测试全都是绿的**，只有真在浏览器里点一遍才看得见。
    //   判据刻意不写死学校数量：虚拟滚动只渲染可见的十几项，与学校总数无关。
    await expect
      .poll(async () => page.evaluate(() => document.querySelectorAll('.el-select-dropdown__item').length))
      .toBeLessThan(200)

    // 没选学校时"选择照片文件"是禁用的；选中之后才可用 —— 顺带证明虚拟下拉真的绑上了值
    // （虚拟列表 + filterable 写错时的表现就是"选不中、按钮永远是灰的"）
    await expect(pickButton).toBeDisabled()

    await select.click()
    await page.locator('.el-select-dropdown__item:visible').first().click()

    await expect(pickButton).toBeEnabled()

    // 页脚里的"关闭"（右上角的 X 也叫"关闭"，不加范围会命中两个元素）
    await dialog.locator('.el-dialog__footer').getByRole('button', { name: '关闭' }).click()
    await expect(dialog).toBeHidden()
  })

  test('上传后台账能显示缩略图（图片请求带不上令牌，这条链路最容易坏）', async ({ page, request }) => {
    // 照片用接口传（上传对话框的"选学校 + 选文件 + 匹配"编排脆弱），
    // 而本用例要守的是**界面渲染缩略图**这一段：
    //   /api/photos/{id}/thumbnail 需要 Bearer 令牌，而 <img src> 带不上，
    //   前端只能用 axios 取回 Blob 再 createObjectURL —— 路径写错、忘了 responseType:'blob'、
    //   忘了 revoke，表现都是"表格里一片空白"，只有真在浏览器里看才发现的。
    //
    // ⚠ 必须用一个**全新**的学生：复用同一个学生时，第二次上传相同内容会命中"秒传"，
    // 直接返回已有记录（它的缩略图早被后台补上了），于是"上传响应里没有缩略图路径"
    // 这条断言就不成立了 —— 那测的是秒传分支，不是本用例要守的渲染链路。
    const token = await apiLogin(request)
    const school = await ensureSchool(request, token)

    const subject = await seedStudent(request, token, school.id, `端到端缩略图生${uniqueSuffix()}`)

    const jpeg = await realJpegBuffer(page)

    const upload = await request.post('/api/photos/upload-one', {
      headers: { Authorization: `Bearer ${await apiLogin(request)}` },
      multipart: {
        studentId: subject.id,
        file: { name: `${subject.studentNo}.jpg`, mimeType: 'image/jpeg', buffer: jpeg }
      }
    })

    expect(upload.ok(), `上传失败：${upload.status()} ${await upload.text()}`).toBeTruthy()

    const uploaded = (await upload.json()) as {
      success: boolean
      skipped: boolean
      width: number
      thumbnailPath?: string | null
    }

    expect(uploaded.success).toBe(true)
    expect(uploaded.skipped, '新学生第一次上传不该命中秒传').toBe(false)
    expect(uploaded.width).toBeGreaterThan(0)

    // ★ 上传响应里**不该**有缩略图路径 —— 生成已经移出上传请求（债 18）。
    //
    // 这条断言原来写的是 `expect(uploaded.thumbnailPath).toBeTruthy()`，
    // 在"上传请求内同步生成"的那个版本上是对的，但那个行为已经改掉了；
    // 它之所以还能"偶尔通过"，是因为本地测试库当时恰好积压不多、
    // 后台任务正好把这张补上了 —— 也就是说它测的是**后台任务碰巧跑过**，
    // 而不是上传契约。
    expect(uploaded.thumbnailPath ?? null, '缩略图生成已移出上传请求').toBeNull()

    await page.goto('/photo/list')
    await page.getByPlaceholder('学籍辅号 / 姓名').fill(subject.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    const row = page.locator('.el-table__row', { hasText: subject.studentNo }).first()

    await expect(row).toBeVisible()

    // 缩略图由后台任务补齐后再刷新列表；
    // 后台任务扫描是"**新的优先**"，所以刚上传的这张第一轮就能拿到。
    // 默认首轮延迟 60 秒、间隔 300 秒 —— 断言超时按它的量级给。
    const img = row.locator('img.el-image__inner').first()

    await expect
      .poll(
        async () => {
          await page.getByRole('button', { name: '查询' }).click()

          return img.evaluate((el) => (el as HTMLImageElement).naturalWidth).catch(() => 0)
        },
        { timeout: 150_000, intervals: [3_000, 5_000, 10_000] }
      )
      .toBeGreaterThan(0)
  })
  test('打包导出对话框可以打开并说明导出范围', async ({ page }) => {
    await page.goto('/photo/list')

    await page.getByRole('button', { name: '导出照片' }).click()

    const dialog = page.locator('.el-dialog', { hasText: '导出照片' })

    await expect(dialog).toBeVisible()
    await expect(dialog.getByText(/照片清单|文件缺失/)).toBeVisible()

    await dialog.getByRole('button', { name: '取消' }).click()
    await expect(dialog).toBeHidden()
  })

  test('缺照片名单能查到刚建的学生（判据在主表 PhotoPath）', async ({ page, request }) => {
    // 【为什么要新建一个学生，而不是复用 beforeAll 里那个】
    // 判据是"该学生没有照片"。而测试库是长期累积的：
    // 上一轮跑这个 spec 时已经给那个学生传过照片，复用就永远查不到
    // —— 第一版就是这个写法，于是它**只能跑一次**。
    //
    // 姓名只能用规范汉字（后端校验拒收数字/字母），所以序号要转成中文数字，
    // 不能用常规的"加时间戳保唯一"。
    const token = await apiLogin(request)
    const school = await ensureSchool(request, token)

    const fresh = await seedStudent(
      request,
      token,
      school.id,
      `缺照片测试生${uniqueSuffix()}`
    )

    await page.goto('/photo/list')
    await page.getByRole('tab', { name: /缺照片名单/ }).click()
    await page.getByPlaceholder(/姓名|学籍辅号/).last().fill(fresh.studentNo)
    await page.getByRole('button', { name: '查询' }).click()

    await expect(page.locator('.el-table__row', { hasText: fresh.studentNo }).first()).toBeVisible()
  })
})
