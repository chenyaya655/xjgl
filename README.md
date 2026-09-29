# 河南省学籍管理系统（Xjgl）

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![ABP](https://img.shields.io/badge/ABP-10.6-1B9AAA.svg)](https://abp.io/)

> **为什么这里没有 CI 徽章**：GitHub Actions 的徽章地址必须写成
> `https://github.com/<账号>/<仓库>/actions/workflows/ci.yml/badge.svg`，
> 也就是说它**必须**知道仓库最终挂在谁的账号下。本仓库在发布前刻意不含任何
> 需要填用户名才能显示的链接（免得读者看到一个裂图）。
> 你把仓库推上去之后，把下面这一行取消注释、填上自己的账号即可：
>
> ```markdown
> [![CI](https://github.com/<账号>/xjgl/actions/workflows/ci.yml/badge.svg)](https://github.com/<账号>/xjgl/actions/workflows/ci.yml)
> ```

面向 **省 → 市 → 县区 → 学校** 四级教育行政体系的学籍管理系统，全省统一部署、统一入口登录。

后端 **ABP Framework 10.6 + .NET 10**（框架表 EF Core / 业务表 SqlSugar 混合 ORM），
前端 **Vue 3 + Element Plus**（PC / H5 / 小程序 共用一套 API），
基础设施 **PostgreSQL 16 + Redis 7 + RabbitMQ 3.13**。

> **设计文档**：[`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md) —— 每个设计决定的取舍与推演（约 2000 行）。
>
> **学习文档**：[`docs/learning/`](docs/learning/README.md) —— **从零手写本系统的全过程，49 章**（另有 1 篇导航 README，随仓库发布）。
> 包含环境搭建、ABP + SqlSugar 混合 ORM、可配置审批流算法、断路器与降级、
> 生僻字全链路、测试与交付。**它是为"想照着做一个类似系统的人"写的**，
> 而不是 API 手册；如果你只想跑起来，看下面的「快速开始」就够。

---

## 目录

- [这个项目值得一看的五个地方](#这个项目值得一看的五个地方)
- [功能范围](#功能范围)
- [技术选型](#技术选型)
- [快速开始](#快速开始)
- [测试](#测试)
- [目录结构](#目录结构)
- [关键设计](#关键设计)
- [已知限制](#已知限制)
- [参与贡献](#参与贡献)
- [许可证](#许可证)

---

## 这个项目值得一看的五个地方

| # | 问题 | 本项目的做法 | 代码位置 |
|---|---|---|---|
| 1 | **业务 ORM 想用 SqlSugar，但 ABP 的仓储/工作单元/软删除/审计都围绕 EF Core** | 框架表归 EF Core、业务表归 SqlSugar，边界明确；自建 ORM 无关的仓储抽象 + 显式事务范围 | `src/Xjgl.SqlSugarCore/`、`src/Xjgl.Domain/Data/` |
| 2 | **审批流程每个省市县都不一样，业务人员要能自己改** | 自研可配置审批流：节点只写"审哪一级"的相对描述（如"转入学校所在县区"），实例化时解析成真实组织并按组织去重合并 —— **"同县区转学县区只审一次"是算法涌现的结果，不是特例** | `src/Xjgl.Domain/Changes/ChangeFlowPlanner.cs` |
| 3 | **姓名里的生僻字（CJK 扩展 B 区及以上）全链路不能变方块** | 按 Unicode 码位（Rune）而非 `char` 处理长度与截断；数据库用 UTF-8 + ICU 排序规则；前端用 `unicode-range` 分片加载字体 | `src/Xjgl.Domain.Shared/Text/RareCharacterText.cs` |
| 4 | **照片存 NAS，NAS 会卡会掉线，但教师上传不能失败** | 手写熔断器（失败率+最小样本+半开试探）+ 超时 + 舱壁 + 降级写本地，后台任务回放。**降级后失败短路跳过，而不是每次去撞超时** | `src/Xjgl.Domain/Photos/`、`src/Xjgl.HttpApi.Host/Caching/` |
| 5 | **权限要随模块增减而变，不能每次改代码发版** | 三级元数据（子系统/模块/操作）驱动权限定义、菜单与前端动态路由；加一个模块只需在界面上配置 | `src/Xjgl.Domain/Metadata/`、`src/Xjgl.Application.Contracts/Permissions/` |

---

## 功能范围

| 模块 | 状态 |
|---|---|
| 平台底座：子系统/模块/操作、动态权限、组织树（省市县校）、年级班级、微代码、用户预置与激活 | 已完成 |
| 学生管理：主档 CRUD、学籍状态、16 位学籍辅号按校并发取号、身份证加密与摘要查重、生僻字链路、CSV 批量导入导出、**档案子表 6 张**（监护人/简历/奖励/处分/资助/体检）与**学生详情抽屉**、**学籍变更留痕**（字段级流水）、**学籍卡与花名册打印**（PDF，中文字体内嵌） | 已完成；查询方案待做 |
| 异动管理：异动类型、流程模板与发布、实例化与合并算法、申请、逐级审核/驳回/撤销、业务联动 | 已完成 |
| 照片管理：NAS 主存储 + 本地降级、熔断、SHA256 秒传、批量上传、台账、缺照片名单、补偿回放、存储健康度、**缩略图（ImageSharp）与取图接口（ETag + 304）**、**打包导出（zip + 清单）**、**令牌桶限流** | 除 MQ 削峰外已完成（削峰需先做 Outbox） |
| 前端 PC 端：登录、动态路由与菜单、工作台、学生列表、照片管理、异动三页、模块占位页 | 已完成 |
| H5 端：登录、待办审核、拍照上传、我的（独立 Vite 应用，hash 路由） | 已完成（业务功能远少于 PC 端） |
| 微信小程序 / 教育系统内 SSO（授权码模式） | 待做 |
| 统计报表：学生统计 / 异动统计（`/api/app/statistics/*` + 两个报表页，受数据范围约束） | 已完成 |
| 大屏、生产量级压测调优、K8s 编排 | 待做 |

---

## 技术选型

| 层次 | 选型 | 说明 |
|---|---|---|
| 后端框架 | **ABP Framework 10.6** / .NET 10 | 模块化、依赖注入、审计、权限、多语言开箱可用 |
| 业务 ORM | **SqlSugar** 5.1 | 业务表（`sys_*` / `stu_*`）的建表与访问 |
| 框架 ORM | **EF Core** 10 + Npgsql | ABP 自带模块（Identity / OpenIddict / 权限 / 审计）继续用 EF Core |
| 数据库 | **PostgreSQL 16** | UTF-8 原生支持全部 Unicode，生僻字无需额外处理 |
| 缓存 | **Redis 7** | 分布式缓存与锁；不可用时自动降级为进程内缓存并短路跳过 |
| 消息队列 | **RabbitMQ 3.13** | 编排里有，但**应用当前刻意未注册**（原因见「已知限制」第 3 条，一句话：Outbox 之前接 MQ 会导致静默数据丢失） |
| 照片存储 | **NAS**（SMB/NFS 挂载为目录） | 数据库只存相对路径与元数据 |
| 前端 | **Vue 3 + Vite 7 + TypeScript + Element Plus** | PC 管理端（动态路由）+ H5 端；共用一套 API 包 `@xjgl/api` |
| 认证 | **OpenIddict**（OIDC） | 密码模式供自有前端；授权码模式供教育系统内其它系统 SSO（**未打通**） |
| 测试 | **xunit v3** + Shouldly + Playwright | 单元测试不碰数据库；集成测试打真实 PostgreSQL；前端另有真浏览器 E2E |

---

## 快速开始

### 前置

- .NET SDK **10.0**、Node.js **20+**（pnpm 通过 `corepack` 提供）
- Docker（数据库/缓存/消息队列跑在容器里；Windows 下建议 WSL2 + Docker Desktop）

### 1. 起基础设施

```bash
cd deploy
docker compose -f docker-compose.dev.yml up -d
docker compose -f docker-compose.dev.yml ps     # 三个服务都应为 healthy
```

包含 PostgreSQL 16（`5432`）、Redis 7（`6379`）、RabbitMQ 3.13（`5672` / 管理台 `15672`）、
Adminer（`8080`）、Seq（`5341`）。数据在命名卷里，`down` 不会丢，**`down -v` 会清空**。

> **生产部署**（Linux + Docker）另有一套编排与逐步手册：见 [`deploy/README.md`](deploy/README.md)。
> 其中包含了 NAS 挂载、生僻字字体放置、HTTPS、备份与第三方许可证说明。
> 部署文件的一致性由 `node tools/verify-deploy.mjs` 与 CI 的 `docker compose config` 双重把关。

### 2. 初始化数据库

```bash
dotnet run --project src/Xjgl.DbMigrator
```

这一步会依次执行：EF Core 迁移（框架表）→ SqlSugar CodeFirst（业务表）→ 种子数据
（子系统/模块/操作、码表、管理员授权）。**可重复执行**，连跑两次输出完全一致。

### 3. 启动后端

```bash
dotnet run --project src/Xjgl.HttpApi.Host
```

- API：<http://localhost:44300>
- Swagger：<http://localhost:44300/swagger>

### 4. 启动前端

```bash
cd web
corepack pnpm@9.15.0 install
corepack pnpm@9.15.0 --filter @xjgl/pc dev     # http://127.0.0.1:5173
```

开发时前端通过 Vite 代理把 `/api` 与 `/connect` 转发到后端，因此**前端代码里一律用相对路径**，
生产环境前后端同域部署时不需要任何改动。

### 默认账号

初始管理员：**`admin` / `1q2w3E*`**（注意密码里是大写 `E`）。
用于生产的部署**必须**修改它，并替换数据库/Redis/RabbitMQ 口令 —— 见 [`SECURITY.md`](SECURITY.md)。

---

## 第三方许可证：两处需要采购时确认

`dotnet build` **开箱即过，不需要任何许可证 key**。但下面两个依赖的许可证条款
需要政务/企业采购知情 —— 它们都不是 Apache/MIT 这类无条件许可：

| 依赖 | 许可证 | 什么时候需要付费 | 怎么替换 |
|---|---|---|---|
| **SixLabors.ImageSharp** 3.1.x | Six Labors Split License | 组织年收入 **≥ 100 万美元**时需购买商业许可 | 只需改 `src/Xjgl.Imaging/`（应用层依赖 `IImageProcessor` 接口） |
| **QuestPDF**（学籍卡/花名册） | QuestPDF Community License | 同上（年收入 ≥ 100 万美元） | 只需改 `src/Xjgl.Reporting/`（应用层依赖两个渲染接口） |

- 小型组织、个人、非营利用途**可以免费使用**；
- 两个依赖都被**隔离在独立项目 + 接口**后面，替换成本是改一个项目 + 跑测试，
  不需要动业务代码 —— 这是刻意的架构选择；
- QuestPDF 需要在代码里声明许可证等级，本项目已在 `PrintFontRegistry` 里声明为
  `LicenseType.Community`。

### ⚠ 为什么 ImageSharp 必须钉在 3.1.x（不要升 4.x）

ImageSharp 从 **4.0.0 起在 MSBuild 里强制校验商业许可**（包内多了
`build/SixLabors.ImageSharp.targets` 与 `SixLabors.Licensing.dll`）。
没有 `SixLaborsLicenseKey` 或 `sixlabors.lic` 时，`dotnet build` 是
**`error`，不是可以忽略的 `warning`**：

```
error : No Six Labors license found. Set $(SixLaborsLicenseKey), set $(SixLaborsLicenseFile),
        or add a 'sixlabors.lic' file to the project/workspace.
error : Please obtain a license from https://sixlabors.com/pricing/
```

也就是说升到 4.x 后，**任何克隆本仓库的人都构建不过** —— 对一个"拿来就能用/就能读"
的仓库这是致命的。3.1.12 没有这套校验，`dotnet build` 开箱即过。

版本选择同时核对过已知漏洞：GitHub Advisory 中 3.x 线影响面最高的一条是
`>= 3.0.0, < 3.1.11`（GHSA-rxmq-m78w-7wmc，GIF 解码死循环），
**3.1.12 已在全部 7 条已知公告的修复版本之上**。

> 注意：钉 3.1.x 改变的是**能不能构建**，不是**要不要付费** ——
> 3.x 与 4.x 的许可证条款相同（都是 Six Labors Split License）。
> 要彻底去掉这个许可问题，就得换掉实现（业务层只依赖 `IImageProcessor`）。

---

## 测试

```bash
# 单元测试：181 个，不依赖数据库（含 PDF 渲染与图像缩放）
dotnet run --project test/Xjgl.Domain.Tests

# 集成测试：360 个，打真实 PostgreSQL
dotnet run --project test/Xjgl.Application.Tests

# 端到端：50 个用例（Playwright，chromium + h5 两个项目，自动拉起前后端）
cd web && corepack pnpm@9.15.0 install && corepack pnpm@9.15.0 exec playwright install chromium
corepack pnpm@9.15.0 test:e2e        # 加 :headed 可以看着它点，出报告用 test:e2e:report

# 前端类型检查与构建
corepack pnpm@9.15.0 --filter @xjgl/pc run typecheck
corepack pnpm@9.15.0 --filter @xjgl/pc run build

# 核对前端每次调用的路径与动词是否与后端一致（需要后端已启动，或传入 swagger.json）
node tools/audit-routes.mjs http://127.0.0.1:44300/swagger/v1/swagger.json
```

> **为什么用 `dotnet run --project` 而不是 `dotnet test`**：测试项目是
> xunit v3 + Microsoft.Testing.Platform，而 .NET 10 SDK 的 `dotnet test`
> 默认仍走 VSTest，直接执行会报
> `Testing with VSTest target is no longer supported by Microsoft.Testing.Platform`。
> 仓库根的 `global.json` 已经做了运行器 opt-in，但为了不依赖 SDK 的具体行为，
> **直接运行测试可执行程序是确定可用的方式**（CI 也这么做）。
> 两种方式都等价：单元测试项目与集成测试项目本身就是可执行程序（`OutputType=Exe`）。

集成测试的连接串优先取环境变量 `XJGL_TEST_CONNECTION`：

```bash
# ⚠️ 必须给：代码里的兜底值是 127.0.0.1:55432（作者本机沙箱的端口），
#    而 docker-compose.dev.yml 起的是 5432，所以照抄上面命令直接跑会连不上。
#    下面这条指向 README 第 1 步起的那个 PostgreSQL，可以直接用：
export XJGL_TEST_CONNECTION="Host=127.0.0.1;Port=5432;Database=xjgl_test;User ID=xjgl;Password=Xjgl@Dev2026"
dotnet run --project test/Xjgl.Application.Tests
```

> 为什么兜底值不改成 5432：开发沙箱与常规开发机用的端口不同，
> 兜底值只能照顾其中一边。**CI 与本文档都显式注入环境变量**，
> 因此"不给变量时的行为"不是任何一条受支持的路径 —— 请始终显式指定。

数据库要先建好并跑过迁移：

```bash
dotnet run --project src/Xjgl.DbMigrator        # 用同一个库（把 appsettings 指向 xjgl_test，或用 ConnectionStrings__Default 覆盖）
```

**端到端测试（`web/e2e`）覆盖什么**：

| 规格 | 覆盖 |
|---|---|
| `01-login` | 未登录重定向、密码错误不跳转、登录后菜单可见、**刷新页面后会话仍有效**、激活页公开 |
| `02-student` | 查询 + 掩码身份证、详情抽屉新增监护人并留痕、**打印并校验下载到的确是 PDF**、导出 CSV、导入对话框 |
| `03-photo` | 台账页签、**批量上传对话框 + 学校下拉可选中**、**上传后台账显示缩略图**、打包导出对话框 |
| `04-modules` | 业务模块逐个打开并渲染、左侧菜单与后端元数据一致 |
| `05-subtable-forms` | 学生档案 6 张子表的表单交互 |
| `06-report` | 统计报表页：按维度统计、码值被翻译成中文 |
| `07-sso` | 授权码模式 SSO 的现状（**未打通**，用例固化的是当前真实行为，不是期望行为） |
| `90-demo-walkthrough` | 演示走查：登录→学生→照片→异动→统计→系统管理，逐步截图到 `e2e/demo-shots/` |
| `91-role-management` | 角色增删改、给角色分配权限、**读取失败时必须明确报错而不是静默留空** |
| `h5-smoke`（独立 `h5` 项目） | H5 端能独立启动、三页签切换、窄屏无横向溢出、hash 路由刷新不 404 |

它守的是**集成测试与类型检查都看不见**的那一层。本项目第一次跑通它就抓出三个真缺陷：
刷新深链 404（路由守卫）、导出按钮报 400（`MaxResultCount` 超 ABP 上限 1000）、
以及**学校下拉把全省 6,789 所学校全渲染成 DOM 导致页面假死 100 秒**（已用虚拟滚动下拉修复）。

**三处值得注意的测试写法：**

1. **单元测试不需要数据库** —— 领域层不引用任何 ORM，所以"流程实例化与合并算法"
   （14 个用例覆盖跨市/同市/同县/本校/含省级）几毫秒就能跑完。
   验证方法：把数据库容器停掉再跑一次，仍然全绿。
2. **照片的故障演练不用真 NAS** —— 把"NAS 主存储"指向本地目录，需要模拟故障时
   把这个目录换成一个**同名文件**，于是"写入失败 → 熔断 → 降级 → 待补偿"真实发生；
   恢复只需把文件删掉。走的是与生产完全相同的依赖注入装配和熔断器，不是 mock。
3. **端到端测试的数据用接口造、界面只验界面** —— 学生建档走导入/接口，
   E2E 不假装去点一个还不存在的对话框；姓名只能用中文（后端会校验证件式姓名，
   用"姓名+时间戳"保证唯一会被直接拒掉），唯一性靠系统生成的学籍辅号。
   另有一条**与数据量无关**的守卫断言：下拉选项节点数必须少于 200 个（防"把全校渲染成 DOM"回归）。

---

## 目录结构

```
src/
  Xjgl.Domain.Shared/          常量、错误码、跨端校验规则（生僻字、身份证、学籍号）
  Xjgl.Domain/                 实体、领域服务、纯函数业务算法（不引用任何 ORM）
  Xjgl.Application.Contracts/  DTO 与应用服务接口、权限码常量
  Xjgl.Application/            应用服务实现（编排）
  Xjgl.EntityFrameworkCore/    ABP 框架表的 DbContext 与迁移
  Xjgl.SqlSugarCore/           业务 ORM 集成层（客户端工厂、仓储、事务、序列）
  Xjgl.HttpApi/                控制器（约定式 + 手写）
  Xjgl.HttpApi.Host/           组合根：配置、鉴权、缓存、后台任务、Swagger
  Xjgl.Reporting/              PDF 渲染（学籍卡 / 花名册，QuestPDF）
  Xjgl.Imaging/                图像处理（照片缩略图，ImageSharp）
  Xjgl.DbMigrator/             控制台迁移工具（建表 + 种子，幂等）
test/
  Xjgl.TestBase/               测试基础设施
  Xjgl.Domain.Tests/           单元测试（不碰数据库）
  Xjgl.Application.Tests/      集成测试（真实 PostgreSQL）
web/
  apps/pc/                     PC 管理端（Vue 3 + Element Plus）
deploy/                        开发（WSL2）与生产（Linux + Docker）编排、Dockerfile、nginx 配置、部署手册
tools/                         开发辅助脚本（前端路由核对、部署一致性核对、NuGet 代理）
docs/                          设计与交付文档（含 learning/ 学习文档、articles/ 技术文章）
```

---

## 关键设计

以下每一条在 [`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md) 里都有完整推演；
这里只列结论：

- **混合 ORM 的边界**：框架表归 EF Core、业务表归 SqlSugar，同一个 PostgreSQL 库。
  两者目前**不能共享同一事务**（ABP 的 `IUnitOfWork` 公开接口没有 `TransactionApi`，
  第三方 ORM 无法挂接提交链路），因此多步写操作用显式事务范围，
  跨 ORM 的写操作靠"顺序 + 幂等 + 流水日志"缓解。
- **权限与菜单是数据**：`sys_subsystem` / `sys_module` / `sys_module_action` 三级元数据，
  权限码为 `{子系统}.{模块}.{操作}`；登录后一次 `/api/app/my-profile` 拿到权限码与菜单树，
  前端据此注册动态路由。前端**不做任何角色推断**，只按权限码控制显隐。
- **组织两层建模**：`sys_org`（稳定的省/市/县/校树，物化路径）与学年维度的
  `sys_grade` / `sys_class` 分开 —— 年级班级每年重建，混进组织树会让树无限膨胀。
- **学籍号**：`学校标识码(10) + 流水号(6)` = 16 位，按学校用
  `pg_advisory_xact_lock` 并发取号（允许跳号）；全国学籍号只存不生成。
- **照片路径**：`photo/{学校组织Id}/{学生Id}.jpg` —— 一名学生一个固定路径（重传即覆盖，
  天然幂等），且**不放学籍号**：避免把学生身份信息暴露在 NAS 目录上，也绕开生僻字文件名编码。

---

## 已知限制

诚实地列在这里，避免使用者踩坑：

1. **跨 ORM 的写操作不是原子的**（见上）。
2. **新增模块后需要重启应用**，`[Authorize]` 才会认识新权限码 —— 权限定义在启动时构建。
   改进路径是接入 ABP 的动态权限定义存储。
3. **RabbitMQ 模块当前未注册**（从 Host 的 `DependsOn` 中移除）。
   原因是实测发现：MQ 不可达时，身份模块的用户/角色写操作会发布分布式事件，
   而 ABP 的工作单元在**响应写出之后**才提交 —— 发布失败导致事务回滚，
   于是**接口返回 200、数据库里却没有数据**（静默数据丢失，有堆栈与库表证据）。
   在 **Outbox 真正跑起来**（事件与业务数据同事务落库、后台重发）之前 ——
   当前 Outbox 的**表与分发器已就位**（`XjglDbContext` 实现 `IHasEventOutbox` / `IHasEventInbox`，
   迁移 `AddEventOutboxInbox` 已建表），但 `src/` 里**没有任何生产代码往它发事件**
   （一处 `PublishAsync` 调用也没有）：
   "有 MQ 配置但 MQ 不通"比"暂时不接 MQ"更危险。
   当前代码没有任何功能依赖 MQ，因此移除不损失能力，还顺带消除了登录时的 MQ 连接超时。
4. ~~**照片缩略图尚未生成**~~ **已实现**：上传时用 ImageSharp 生成缩略图，
   取图接口支持 ETag + 304。**历史数据例外**：缩略图是后加的，
   此前上传的照片 `ThumbnailPath` 为空 —— 取图接口会**回落返回原图**
   （页面不会白屏），但流量没省下来。需要时跑一次回填任务
   （按 `ThumbnailPath is null` 扫描补生成），见第 13 条。
5. **生僻字字体需要自行放置**：`web/apps/pc/public/fonts/` 下的 Plangothic 分片文件
   体积较大，**不进仓库**，缺失时回落到系统字体（生僻字显示为方块，功能不受影响）。
6. ~~**H5 与微信小程序尚未开始**~~ **H5 已实现**：`web/apps/h5` 是独立的 Vite 应用
   （5174，hash 路由），登录 + **待办审核 / 拍照上传 / 我的**三个页签，由 `h5` 项目下
   4 条端到端用例守着；但它**业务功能远少于 PC 端**（只有教师最常用的两条路径）。
   **微信小程序仍未开始**：只留了协议层口子（自定义 grant type `urn:wechat:miniprogram`）。
   **授权码模式 SSO 未接**（现状见第 9 条的 `07-sso` 用例）。
7. **数据范围**：默认按组织层级自动解析 —— 省厅=全省、市/县/校=本级及下级、
   未关联组织=什么都看不到，内置 admin 角色旁路。
   **「某市局只看本市不看他下属县」这类例外现在可以配了**：`sys_role_data_scope` 表
   （一个角色一行，**没有配置行 = 沿用层级推导**，所以升级后现有角色行为不变），
   可配「全部数据 / 本级及下级 / 仅本级 / 仅本人」四种；
   多个角色都配了范围时**取最宽的那个**（与权限的加法语义一致）。
   **剩余一个遗留**：「自定义组织集合」（多根路径）暂不支持 ——
   各列表查询的 SQL 谓词按单一根路径生成，落不了多个根，
   若接受配置再退化成默认范围就会**比配置更宽**，因此目前明确拒绝（见 ROADMAP 第 21 条）。
   （**前端配置入口已经补上了**：角色管理页的「数据范围」列 + 同页配置弹窗；
   四个选项的中文名与说明都从 `DATA_SCOPE` 码表取，因此后端改文案不必动前端。）
   实现位置：`src/Xjgl.Domain/Organizations/UserDataScopeProvider.cs`、
   `SysRoleDataScope.cs`，以及 `src/Xjgl.Domain.Shared/Organizations/DataScopes.cs`。
8. ~~**删除学生不会级联删除子表行**~~ **已实现级联软删**：删除学生时，
   `StudentAppService.CascadeSoftDeleteAsync`（`src/Xjgl.Application/Students/StudentAppService.cs`）
   把监护人 / 简历 / 奖励 / 处分 / 资助 / 体检这六张子表的行一并软删，不再留孤儿行。
9. ~~**前端尚无浏览器级端到端测试**~~ **已补上**：`web/e2e`（Playwright + 真实 Chromium，
   **50 个用例**（10 个规格文件，`chromium` 与独立 `h5` 两个项目），自动拉起前后端），覆盖登录与会话、学生档案
   （含**下载并校验 PDF 字节**）、照片管理（含**缩略图的 Blob 链路**）、
   **6 张学生子表的表单增删改**、统计报表、角色管理、演示走查与 H5 冒烟。**它第一次跑通就抓出三个集成测试看不见的缺陷**：
   刷新深链 404（路由守卫）、导出按钮 400（前端传了 `MaxResultCount: 5000`，而 ABP 上限是 1000）、
   以及**学校下拉把 6,789 所学校全渲染成 DOM 节点、主线程卡死 100 秒**（已改为虚拟滚动下拉）。
   **尚未覆盖**：非 Chromium 内核、真实微信 WebView。
10. **码值类下拉框必须从码表取**：本轮抓到两处前端手写英文名（学籍状态过滤 → 静默返回空列表；
    异动前后状态 → 审批后把非法值写进学生学籍状态）。现已统一收敛到
    `useCodeOptions(categoryCode)`（`web/apps/pc/src/composables/useCodeOptions.ts`），
    并把"前端不得手写码值"写进 [`CONTRIBUTING.md`](CONTRIBUTING.md) 第 5 节。
    **但它仍是 review 约定、不是编译期强制** —— 新代码手写字面量依然不会被工具拦住。
11. **学籍卡/花名册打印依赖服务器上有中文字体**：PDF 里会**内嵌**字体（`FontFile2`），
    但前提是渲染时能找到中文字体，否则会直接报错（刻意不给"满是方块的学籍卡"）。
    Windows 服务器默认用微软雅黑；Linux/容器部署需 `apt-get install fonts-noto-cjk`
    并把 `Xjgl:Print:DefaultFontFamily` 设为 `Noto Sans CJK SC`。
    **生僻字**还需要另放字体（`Xjgl:Print:RareCharacterFontDirectory`，如 Plangothic 分片），文件不进仓库。
12. **PDF 渲染用 QuestPDF 社区版**（年收入 100 万美元以下组织免费，已在代码里声明）。
    商用需自行购买许可证；替换为其它渲染库时只需改 `Xjgl.Reporting`，
    应用层依赖的是 `IStudentCardPdfRenderer` / `IStudentRosterPdfRenderer` 两个接口。
    同理，**图像处理用 ImageSharp 3.1.x**（Six Labors Split License，开源/小微组织免费；
    **不要升到 4.x**，4.x 会在构建期强制要求商业许可 key，见上文「第三方许可证」一节），
    替换只需改 `Xjgl.Imaging`（应用层依赖 `IImageProcessor`）。
13. ~~**老照片没有缩略图**~~ **已补上后台回填**：`PhotoThumbnailService` +
    `PhotoThumbnailWorker`（`src/Xjgl.Application/Photos/`、`src/Xjgl.HttpApi.Host/Photos/`）
    周期按 `ThumbnailPath is null` 扫描补生成，与上传路径共用同一段生成逻辑。
    回填完成前取图接口仍会**回落返回原图**（页面不会白屏，但流量没省下来）。
14. ~~**上传内容只校验扩展名**~~ **已改为内容真实性校验**：`PhotoUploadService` 调
    `IImageProcessor.ProbeAsync`（`src/Xjgl.Imaging` 用 ImageSharp `Image.IdentifyAsync`）
    探测字节内容，**探测失败即拒绝**并返回"文件内容不是可识别的图片"的明确错误。
    探测只解字节内容、**完全不碰存储**，因此 NAS 抖动不会被误判成内容非法。
15. ~~**照片导出与花名册打印都是"请求内同步生成"**~~ **已后台化**：照片 zip / 学生名单 CSV /
    学籍卡 PDF / 花名册 PDF 四种产物统一走 `sys_export_task` + `ExportTaskWorker`
    （`src/Xjgl.HttpApi.Host/Exporting/ExportTaskWorker.cs`、
    `src/Xjgl.Application/Exporting/ExportTaskRunner.cs`），请求只写一行任务记录就返回，
    前端轮询进度、从下载接口取产物；原来的同步接口已删除（实测 404）。
    **打包本身也已不再占内存**：`IPhotoExportService.ExportToAsync(input, Stream)`
    让后台任务经临时文件流式写入产物存储，回归测试
    `导出_流式重载产出同样的压缩包且不占内存`。
16. **部署后请检查日志里有没有「分布式缓存主实现不可用」**：
    本系统的分布式缓存是「Redis 主 + 内存兜底」的容错装饰器 —— 这是优点（Redis 挂了不影响使用），
    但代价是**配置错了不会报错，只会静默降级成单机缓存**。
    本机开发时就踩到过：`appsettings.json` 里的 Redis 密码与实际运行的 Redis 不一致，
    系统一直安静地用内存缓存跑着（登录、权限缓存都正常，看不出来）。
    多副本部署前务必确认真实连上了 Redis（限流、缓存共享都依赖它）。
17. ~~**限流是"每实例"精度**~~ **已改为 Redis Lua 原子限流**：
    `RedisLuaTokenBucketRateLimiter`（`src/Xjgl.HttpApi.Host/RateLimiting/`）把"读-算-写"
    放进一段 Lua 脚本、在 Redis 里原子执行，多副本部署不再被放大；时间基准取 Redis 的
    `TIME`（避免各实例时钟偏移）。限流维度也从一个"用户"扩到 **user / ip / global**
    三个（IP 维度正是防"某校整栋楼共用一个出口 IP"的那一维），策略值可在
    `Xjgl:RateLimit:*` 配置里按环境调。**Redis 不可用时会降级**回进程内 + 分布式缓存的
    老实现 —— 只有在这种降级状态下才回到"每实例精度"。
18. **大集合下拉必须用虚拟滚动**（与第 9 条的第三个缺陷同源）：凡是可能上万条的下拉
    （学校、班级、教师……）一律走 `el-select-v2`（本仓库已抽成 `components/SchoolSelect.vue`）。
    普通 `el-select` 会为每一条渲染一个真实 DOM 节点 —— 全省学校数量下，只打开一个对话框
    就能把浏览器主线程卡死上百秒，而**接口、类型检查、集成测试全都是绿的**。
    端到端测试里有一条与数据量无关的守卫断言（下拉选项节点数 < 200）防它回归。
19. **待办没有主动提醒，也没有超时催办**（站内信 / 短信 / 微信一个都没有）：
    异动申请提交后，下一节点的审核人**不会收到任何通知**，必须自己打开「待办」页才看得到。
    流程本身是通的（待办列表、权限、数据范围、`AllowReject` 开关都真的生效），
    缺的只是"把人叫过来"这一步。
    `TimeoutDays` 目前只算出一个 `DueTime` 展示在待办上
    （`ChangeFlowAppService` 里 `now.AddDays(t.TimeoutDays)`），
    **没有任何后台任务去扫它** —— 超时不会告警、也不会催办。
    **这一条在演示中看不出来**：演示的人知道自己该看哪里；
    真实的学校里它表现为"单子躺在系统里没人审"，而界面上一切正常、日志里也没有异常。
    设计稿原本写了「站内信 + 短信 + 微信模板消息，按角色配置」与
    「按 `TimeoutDays` 定时扫描 → 催办 → 超时告警」，见
    [`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md) §3.4 第 6 步
    （那一处也标了"未实现"）。补的落点是 `ChangeFlowAppService` 生成任务之后那一步，
    外加一个扫 `DueTime` 的后台任务（已列为 **ROADMAP 第 22 条**，可直接认领）。

---

## 参与贡献

欢迎提交 Issue 与 Pull Request。提交前请确认：

```bash
dotnet build Xjgl.sln                       # 0 错误
dotnet run --project test/Xjgl.Domain.Tests        # 单元测试全绿（不依赖数据库）
dotnet run --project test/Xjgl.Application.Tests   # 集成测试全绿（需要本地数据库）
cd web && corepack pnpm@9.15.0 -r typecheck        # 三端类型检查（PC / H5 / 共享包）
cd web && corepack pnpm@9.15.0 --filter @xjgl/pc run test:unit
node tools/check-permissions-sync.mjs       # 权限码 / 码类别 / 操作类型三处约定
cd web && corepack pnpm@9.15.0 test:e2e     # 端到端（自动拉起前后端）
```

> **为什么不用 `dotnet test`**：测试项目是 xunit v3 + Microsoft.Testing.Platform，
> 详见上面「测试」一节。**前端需要 Node ≥ 22.18**（`test:unit` 用 `node --test` 跑 `.ts`）。

代码风格约定：注释写**"为什么"**而不是"这行在做什么"；涉及取舍的地方要说明
"不这么做会怎样"。这是本仓库注释密度偏高且长句较多的原因，也是它最值得读的部分。

---

## 许可证

[Apache License 2.0](LICENSE) —— 允许商用与修改，并附带专利授权。

---

## English Summary

A student-status management system for the four-level education administration hierarchy
(**Province → City → County → School**) in Henan Province, designed for province-wide
single-instance deployment with a unified login entry.

**Stack**: ABP Framework 10.6 / .NET 10 with a **hybrid ORM** (EF Core + Npgsql for the
framework tables, SqlSugar for business tables), PostgreSQL 16, Redis 7, RabbitMQ 3.13,
and a Vue 3 + Element Plus frontend.

**Highlights**: a self-built configurable approval workflow whose nodes describe *which
administrative level* to resolve rather than hard-coded organizations (so "the same county
reviews only once" emerges from de-duplication instead of special-casing); full-path support
for rare CJK characters (Unicode Extended-B and beyond); and a NAS photo pipeline with a
hand-written circuit breaker, bulkhead, timeout, local degradation and background replay.

Design document (Chinese): [`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md).
A step-by-step tutorial series (**49 chapters** + a navigation README, Chinese) is in
[`docs/learning/`](docs/learning/README.md) — it walks through building this system
from an empty directory, including the hybrid-ORM boundary, the approval-flow
instantiation algorithm, and rare-CJK-character handling end to end.
