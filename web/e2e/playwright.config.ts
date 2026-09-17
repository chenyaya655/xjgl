import { defineConfig, devices } from '@playwright/test'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * 端到端流程测试配置。
 *
 * 【为什么从"浏览器真的点一遍"开始，而不是只靠集成测试】
 * 集成测试打的是应用服务层：它证明"接口按预期工作"，但证明不了
 *   - 前端到底调没调对接口（路径、动词、字段名）——本项目在这里踩过两次（405、字段名不一致导致整列空白）；
 *   - 界面上的按钮是不是真的能走通（权限、路由、动态菜单）；
 *   - 一个完整流程（登录 → 建档 → 传照片 → 打印）端到端能不能走完。
 *
 * 【两个服务的启动方式】
 *   - 前端 dev server：由 Playwright 自动拉起（Vite，5173），它会把 /api、/connect
 *     代理到后端，因此浏览器里看到的是与生产一致的同源形态；
 *   - 后端 Host：默认也由 Playwright 拉起；如果你已经手动起了一个（本地开发常见），
 *     `reuseExistingServer: true` 会直接复用，不会重复启动。
 *
 * 数据库连接串可用 E2E_DB_CONNECTION 覆盖（本仓库沙箱里的 PostgreSQL 跑在 55432）。
 */
const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '../..')
const webRoot = path.resolve(here, '..')

const WEB_URL = process.env.E2E_WEB_URL ?? 'http://127.0.0.1:5173'
const API_URL = process.env.E2E_API_URL ?? 'http://127.0.0.1:44300'

export default defineConfig({
  testDir: './specs',

  // 说明：这里**刻意不用 globalSetup 做预热** —— Playwright 的 globalSetup
  // 在 webServer 启动**之前**执行，此时 dev server 还没起来，`page.goto` 会直接超时
  // （实测踩过）。冷启动的代价改由"放宽的断言超时"吸收：第一个访问某个视图的用例
  // 可能要等 Vite 编译十几秒，因此 expect 超时放到 30 秒、单用例超时 90 秒。
  //
  // 用例之间共用一套数据库与一个后台进程：并行会互相影响（比如都在改同一所学校的学生）
  fullyParallel: false,
  workers: 1,

  // 一个完整流程可能包含渲染 PDF、生成 zip 这类慢操作
  timeout: 90_000,
  expect: { timeout: 30_000 },

  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI
    ? [['list'], ['html', { open: 'never' }]]
    : [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: WEB_URL,
    locale: 'zh-CN',
    timezoneId: 'Asia/Shanghai',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off'
  },

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ],

  webServer: [
    {
      // 构建配置可覆盖：本地是 Debug，CI 里构建的是 Release
      command: `dotnet run --project src/Xjgl.HttpApi.Host -c ${
        process.env.E2E_DOTNET_CONFIG ?? 'Debug'
      } --no-build`,
      cwd: repoRoot,
      url: `${API_URL}/health`,
      timeout: 240_000,
      reuseExistingServer: true,
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: API_URL,
        ...(process.env.E2E_DB_CONNECTION
          ? { ConnectionStrings__Default: process.env.E2E_DB_CONNECTION }
          : {})
      }
    },
    {
      command: 'corepack pnpm@9.15.0 --filter @xjgl/pc dev',
      cwd: webRoot,
      url: WEB_URL,
      timeout: 180_000,
      reuseExistingServer: true,
      env: { ...process.env }
    }
  ]
})
