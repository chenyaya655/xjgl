import { expect, type APIRequestContext, type Page } from '@playwright/test'

/** 开发/测试环境的默认管理员（见 README 的默认账号说明）。 */
export const ADMIN = { userName: 'admin', password: '1q2w3E*' }

/** 用界面登录，并等待跳到工作台。 */
export async function login(
  page: Page,
  userName: string = ADMIN.userName,
  password: string = ADMIN.password
): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder('请输入用户名').fill(userName)
  await page.getByPlaceholder('请输入密码').fill(password)

  // 注意按钮文案是「登 录」（两个字之间有排版空格），
  // 因此用正则匹配而不是精确字符串 —— 排版调整不该让测试挂掉
  await page.getByRole('button', { name: /登\s*录/ }).click()
  await page.waitForURL('**/dashboard')
}

/** 直接调令牌端点拿 access_token（用于给用例造数据，不经过界面）。 */
export async function apiLogin(request: APIRequestContext): Promise<string> {
  const response = await request.post('/connect/token', {
    form: {
      grant_type: 'password',
      username: ADMIN.userName,
      password: ADMIN.password,
      client_id: 'Xjgl_App',
      scope: 'Xjgl offline_access'
    }
  })

  expect(response.ok(), `登录接口失败：${response.status()}`).toBeTruthy()

  const body = (await response.json()) as { access_token?: string }

  expect(body.access_token, '登录响应里没有 access_token').toBeTruthy()

  return body.access_token!
}

/**
 * 生成一个**校验位正确**的身份证号。
 *
 * 为什么要在用例里算校验位：后端会严格校验（校验位、出生日期段），
 * 用随机数拼的号会被拒 —— 这一点在本项目的导入功能验证时就踩过。
 */
export function newIdCard(): string {
  const area = '410102'
  const year = 2016
  const month = String(Math.floor(Math.random() * 12) + 1).padStart(2, '0')
  const day = String(Math.floor(Math.random() * 28) + 1).padStart(2, '0')
  const seq = String(Math.floor(Math.random() * 900) + 100)

  const first17 = `${area}${year}${month}${day}${seq}`
  const weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2]
  const codes = '10X98765432'

  let sum = 0

  for (let i = 0; i < 17; i++) {
    sum += Number(first17[i]) * weights[i]!
  }

  return first17 + codes[sum % 11]
}

export interface SeededStudent {
  id: string
  studentNo: string
  name: string
}

/**
 * 通过接口建一名学生并返回其信息。
 *
 * 为什么用接口造数据而不是点界面：学生列表页的「新增学生」按钮目前还没有接对话框
 * （建档走"导入"或接口）。E2E 用例的重点是**界面能走通的部分**：
 * 列表、详情抽屉、子表、变更记录、打印。数据准备走接口是刻意的。
 *
 * ⚠ 姓名只能用中文：后端会校验姓名（不能含数字/字母/符号）。
 * 第一版用例我用了 `E2E学生123456` 这种"带时间戳"的名字，直接被拒：
 *   「姓名包含非法字符"2"」—— 提示很清楚，是我造数据的方式错了。
 * 唯一性因此不靠姓名，而靠**系统生成的学籍辅号**（用例按辅号检索）。
 */
export async function seedStudent(
  request: APIRequestContext,
  token: string,
  schoolOrgId: string,
  name: string
): Promise<SeededStudent> {
  const response = await request.post('/api/app/student', {
    headers: { Authorization: `Bearer ${token}` },
    data: {
      name,
      genderCode: '1',
      birthDate: '2016-05-06T00:00:00',
      idCardTypeCode: '1',
      idCardNumber: newIdCard(),
      schoolOrgId,
      academicYear: '2025-2026',
      phoneNumber: '13800000000'
    }
  })

  expect(response.ok(), `建学生失败：${response.status()} ${await response.text()}`).toBeTruthy()

  const body = (await response.json()) as { id: string; studentNo: string }

  return { id: body.id, studentNo: body.studentNo, name }
}

/** 取一个学校组织（用于造数据）；库里一所学校都没有时**现建一棵最小组织树**。 */
export async function ensureSchool(
  request: APIRequestContext,
  token: string
): Promise<{ id: string; name: string; code: string }> {
  const orgs = await listOrgs(request, token)
  const school = orgs.find((o) => o.orgType === 'School')

  if (school) {
    return school
  }

  // 【为什么要"没有就建"】
  // 迁移器（DbMigrator）只建表与码表/权限等基础数据，**不会**建组织树 ——
  // 全新装出来的库（CI 上就是这个形态）里一个组织都没有，连一个学生都建不了。
  // 第一版这里直接断言"测试库里必须有学校"，于是本地（长期累积的库）能过、CI 上必挂。
  // 现在改成"自给自足"：按**固定编码**先找后建，所以重复跑也是幂等的。
  //
  // 编码取真实行政区划的形状：省 6 位、学校标识码 10 位（前 6 位是所属行政区划码），
  // 因为学籍辅号 = 学校标识码(10) + 流水号(6)。
  const province = await ensureOrg(request, token, orgs, 'Province', '410000', '端到端测试省', null)
  const city = await ensureOrg(request, token, orgs, 'City', '410100', '端到端测试市', province.id)
  const county = await ensureOrg(request, token, orgs, 'County', '410102', '端到端测试区', city.id)

  return ensureOrg(request, token, orgs, 'School', '4101020001', '端到端测试小学', county.id)
}

interface OrgRow {
  id: string
  orgType: string
  code: string
  name: string
}

async function listOrgs(request: APIRequestContext, token: string): Promise<OrgRow[]> {
  const response = await request.get('/api/app/org?onlyEnabled=false', {
    headers: { Authorization: `Bearer ${token}` }
  })

  expect(response.ok(), `取组织列表失败：${response.status()}`).toBeTruthy()

  return (await response.json()) as OrgRow[]
}

/** 按编码先找后建：同一个测试库上反复跑不会因为"编码已存在"而失败。 */
async function ensureOrg(
  request: APIRequestContext,
  token: string,
  known: OrgRow[],
  orgType: string,
  code: string,
  name: string,
  parentId: string | null
): Promise<{ id: string; name: string; code: string }> {
  const hit = known.find((o) => o.code === code)

  if (hit) {
    return hit
  }

  const response = await request.post('/api/app/org', {
    headers: { Authorization: `Bearer ${token}` },
    data: { parentId, orgType, code, name, sort: 0, isEnabled: true }
  })

  expect(response.ok(), `建组织「${name}」（${code}）失败：${response.status()} ${await response.text()}`).toBeTruthy()

  return (await response.json()) as { id: string; name: string; code: string }
}

/**
 * 在浏览器里用 canvas 现画一张真实 JPEG。
 *
 * 用真实图片而不是几个随机字节：照片上传会**探测尺寸并生成缩略图**，
 * 假图片（只改后缀的字节）会让缩略图链路走不到，等于没测。
 */
export async function realJpegBuffer(page: Page, width = 640, height = 480): Promise<Buffer> {
  const base64 = await page.evaluate(
    ([w, h]) => {
      const canvas = document.createElement('canvas')

      canvas.width = w!
      canvas.height = h!

      const ctx = canvas.getContext('2d')!

      ctx.fillStyle = '#8ecae6'
      ctx.fillRect(0, 0, w!, h!)
      ctx.fillStyle = '#023047'
      ctx.beginPath()
      ctx.arc(w! / 2, h! / 2, Math.min(w!, h!) / 4, 0, Math.PI * 2)
      ctx.fill()

      return canvas.toDataURL('image/jpeg', 0.9).split(',')[1]!
    },
    [width, height]
  )

  return Buffer.from(base64, 'base64')
}
