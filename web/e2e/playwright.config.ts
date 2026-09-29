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
 * ⚠ 但它只在"**Playwright 自己拉起后端**"时才被传进去（见下面 webServer 的 env）：
 *   如果你已经手动起了一个后端并被复用，这个变量是**静默失效**的 ——
 *   打的是那个进程启动时配置的库（多半是 appsettings.json 里的开发库 xjgl）。
 *   `/health` 对任何库都返回 200，Playwright 分辨不出来，所以这类错配只能靠人确认。
 *
 * ===========================================================================
 * 【数据隔离：现状，以及"每个 spec 用独立 schema"为什么没做成】（技术债 29）
 * ===========================================================================
 *
 * 现状（诚实版）：
 *   - 整套用例共用**一个后端进程 + 一个数据库**。`workers: 1` 不是隔离，
 *     它只是把"互相踩"变成"排队踩同一套数据"；
 *   - spec 的策略是"把自己造的数据定位到学籍辅号 / 唯一姓名上"
 *     （见 support/auth.ts 的 seedStudent），**不是**"清理掉别的数据"，
 *     所以测试库会**长期累积**：组织树、学校、学生、子表行、角色，每跑一轮都多一些。
 *     本仓库记录过的实际数字：曾涨到 4000+ 测试组织 / 6,789 所学校 ——
 *     `03-photo` 里"学校下拉必须虚拟滚动"那条断言就是被这个规模逼出来的；
 *   - 因此有了 `tools/reset-test-db.mjs`（ROADMAP 第 13 条 / 技术债 8）：
 *     一条命令重建测试库。**它只解决"库被写脏"，没有做"每个 spec 独立 schema"**。
 *
 * 为什么没有做成"每个 spec 用独立 schema"（原计划），三条都是硬阻碍：
 *   1. **一个后端进程 = 一个连接串**。`ConnectionStrings__Default` 是进程级配置，
 *      而 Playwright 的 `webServer` 在**一次运行里只启动一次**；spec 无法换库/schema。
 *   2. **前端也只有一个**。spec 走的是同源相对路径（/api、/connect），由 Vite dev server
 *      代理到**固定**的后端（`VITE_API_BASE_URL`，默认 44300，见 apps/pc/vite.config.ts）。
 *      dev server 同样只起一次 —— 就算给每个 spec 起一个后端，浏览器里的请求也打不到它。
 *   3. **两套迁移都不认 schema**。框架表走 EF Core 迁移（`Migrations/*.cs` 里没有任何
 *      `schema:`），业务表走 SqlSugar CodeFirst（`InitTables`，且 `XjglConsts.DbSchema = null`），
 *      两者都落在连接默认的 `public`。要按 spec 分 schema，先得让这两条迁移路径都变成
 *      schema 可参数化（改 src/），再补"一个 spec 一套前后端进程"的编排。
 *
 * 现实的三条路（按代价从低到高）：
 *   A. **跑之前重建测试库**（现在推荐的路，也已经是 CI 的形态 —— CI 每个 job 都是新库）。
 *      ⚠ 关键前提：**让"被重建的库"与"E2E 实际用的库"是同一个**。
 *        `tools/reset-test-db.mjs` 默认重建 `127.0.0.1:55432 / xjgl_test`（集成测试库），
 *        而**不设 E2E_DB_CONNECTION 时后端连的是 appsettings.json 里的开发库 `xjgl`**
 *        （`appsettings.Development.json` 只改了日志级别，没有覆盖连接串）。
 *        于是"重置了 xjgl_test，却打了 xjgl"会安静地发生 —— 测试照跑，但跑在脏库上。
 *        所以按下面这条串起来做：
 *
 *          pnpm --filter @xjgl/e2e run db:reset            # dry-run，先看它会做什么
 *          pnpm --filter @xjgl/e2e run db:reset:force      # 真的重建（会 DROP DATABASE）
 *          # 用**同一个连接串**建表 + 种子（迁移器读 ConnectionStrings__Default）
 *          $env:ConnectionStrings__Default = 'Host=127.0.0.1;Port=55432;Database=xjgl_test;User ID=xjgl;Password=Xjgl@Dev2026'
 *          dotnet run --project src/Xjgl.DbMigrator
 *          # 让 E2E 打同一个库
 *          $env:E2E_DB_CONNECTION = $env:ConnectionStrings__Default
 *          pnpm test:e2e
 *
 *        想重建"你实际在用的那个库"也行：给脚本加 `--connection "Host=...;Database=..."`
 *        —— 但库名不含 `test` 时脚本会拒绝执行，必须显式加 `--allow-any-database`
 *        （那是为了防止手滑删掉开发库/演示库，别为了省事把它写进脚本）。
 *   B. **按 worker 分库**（`CREATE DATABASE xjgl_test_wN TEMPLATE <模板库>` + 每个 worker
 *      自己一套前后端进程）：需要把 `webServer` 换成自定义 fixture 并让 Vite 的代理目标
 *      跟着 worker 走。这是**没被验证过的改动**，不要顺手做；而且当前 `workers: 1`，
 *      在恢复并行之前它没有任何收益。
 *   C. 让 spec 自己清理它造的数据（API 删除）：只能覆盖"它知道自己造了什么"的部分。
 *      子表行/组织/角色可以删，但**软删也是行**（列表按学生重载才看不见它们），
 *      所以它替代不了 A，只适合给个别用例收尾（例如 91-role-management 的 deleteRoleByName）。
 *
 * 结论：本文件保持"一个库、串行、数据自我定位"的现状，并把 A 作为文档化的前置步骤；
 * 不引入 schema 级隔离 —— 那需要改 src/ 的迁移配置与进程编排，属于另一件工作。
 */
const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '../..')
const webRoot = path.resolve(here, '..')

const WEB_URL = process.env.E2E_WEB_URL ?? 'http://127.0.0.1:5173'
const API_URL = process.env.E2E_API_URL ?? 'http://127.0.0.1:44300'

/**
 * H5（手机端）的地址。
 *
 * 与 PC 是两个**独立的 dev server**（5174，见 apps/h5/vite.config.ts 的说明），
 * 因此这里必须再多起一个 webServer，并且只让 h5 项目用它。
 */
const H5_URL = process.env.E2E_H5_URL ?? 'http://127.0.0.1:5174'

export default defineConfig({
  testDir: './specs',

  // 说明：这里**刻意不用 globalSetup 做预热** —— Playwright 的 globalSetup
  // 在 webServer 启动**之前**执行，此时 dev server 还没起来，`page.goto` 会直接超时
  // （实测踩过）。冷启动的代价改由"放宽的断言超时"吸收：第一个访问某个视图的用例
  // 可能要等 Vite 编译十几秒，因此 expect 超时放到 30 秒、单用例超时 90 秒。
  //
  // 用例之间共用一套数据库与一个后台进程：并行会互相影响（比如都在改同一所学校的学生）。
  //
  // ⚠ 这不是"数据隔离"，只是"不并发"：每个 spec 造的数据都留在同一个库里，
  //   而且种子/唯一名机制刻意做成"先找后建、可重复跑"（见 support/auth.ts），
  //   所以**跑得越多、库越脏**。要回到"干净库"的形态，按文件头注释里的 A 方案做
  //   （注意里面那条"别重置了 xjgl_test、却打了 xjgl"的提醒）。
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
    locale: 'zh-CN',
    timezoneId: 'Asia/Shanghai',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off'
  },

  projects: [
    {
      /**
       * PC 端（默认）。
       *
       * ⚠ 用 `testIgnore` 把 H5 专用的 spec 排除掉 —— H5 的页面在 5174、
       * 且是 hash 路由，拿 PC 的 baseURL 去跑必然全红。
       * 反过来 `h5` 项目也排除了 PC 的 spec（见下）。
       * 这不是洁癖：两个 app 的登录页字段、页签结构都不同，
       * 让它们互相"顺便跑一下"只会制造看不懂的失败。
       */
      name: 'chromium',
      testIgnore: /h5-.*\.spec\.ts$/,
      use: { ...devices['Desktop Chrome'], baseURL: WEB_URL }
    },
    {
      /**
       * H5（手机端）：**债 28**。
       *
       * 【为什么要单独一个项目，而不是"在 PC 项目里多写几条"】
       * H5 是另一个 Vite 应用（端口 5174、hash 路由、移动端布局），
       * 它的回归面与 PC 完全不同：
       *   - 触屏交互（点按、无 hover）—— 桌面浏览器的 hover 态在手机上不存在；
       *   - 窄屏布局（320~414px）—— 桌面宽度下"看起来正常"不代表手机上不溢出；
       *   - 老 WebView（构建目标 es2015）—— 只有低版本引擎才暴露的语法问题。
       *
       * 用 `devices['Pixel 5']`（Chromium 内核 + 移动视口 + 触屏 + 移动 UA），
       * 是本仓库现有条件下最接近真实教师手机的形态。
       */
      name: 'h5',
      testMatch: /h5-.*\.spec\.ts$/,
      use: { ...devices['Pixel 5'], baseURL: H5_URL }
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
    },
    {
      // H5 的 dev server（债 28）。它是另一个应用，必须单独起 ——
      // 少了这一条，`h5` 项目的用例会以"连接被拒绝"的形式失败，
      // 而那个报错完全指不到"配置里少了第三个 webServer"。
      command: 'corepack pnpm@9.15.0 --filter @xjgl/h5 dev',
      cwd: webRoot,
      url: H5_URL,
      timeout: 180_000,
      reuseExistingServer: true,
      env: { ...process.env }
    }
  ]
})
