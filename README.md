# 河南省学籍管理系统（Xjgl）

面向 **省 → 市 → 县区 → 学校** 四级教育行政体系的学籍管理系统，全省统一部署、统一入口登录。

后端 **ABP Framework 10.6 + .NET 10**（框架表 EF Core / 业务表 SqlSugar 混合 ORM），
前端 **Vue 3 + Element Plus**（PC / H5 / 小程序 共用一套 API），
基础设施 **PostgreSQL 16 + Redis 7 + RabbitMQ 3.13**。

> 设计文档：[`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md)（含每个设计决定的取舍与推演）
>
> 学习文档：从零手写本系统的全过程（约 45 章，含环境搭建、SqlSugar 集成、混合 ORM 事务、
> 可配置审批流算法、熔断降级、测试与交付）**单独交付，不包含在本仓库中**。

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
| 学生管理：主档 CRUD、学籍状态、16 位学籍辅号按校并发取号、身份证加密与摘要查重、生僻字链路 | 主档已完成；子表（监护人/简历/奖励/资助/体质）、导入导出、学籍卡打印待做 |
| 异动管理：异动类型、流程模板与发布、实例化与合并算法、申请、逐级审核/驳回/撤销、业务联动 | 已完成 |
| 照片管理：NAS 主存储 + 本地降级、熔断、SHA256 秒传、批量上传、台账、缺照片名单、补偿回放、存储健康度 | 核心已完成；缩略图、限流、MQ 削峰、导出待做 |
| 前端 PC 端：登录、动态路由与菜单、工作台、学生列表、照片管理、异动三页、模块占位页 | 已完成 |
| H5 / 微信小程序 / 教育系统内 SSO（授权码模式） | 待做 |
| 报表与大屏、压测调优、生产部署编排 | 待做 |

---

## 技术选型

| 层次 | 选型 | 说明 |
|---|---|---|
| 后端框架 | **ABP Framework 10.6** / .NET 10 | 模块化、依赖注入、审计、权限、多语言开箱可用 |
| 业务 ORM | **SqlSugar** 5.1 | 业务表（`sys_*` / `stu_*`）的建表与访问 |
| 框架 ORM | **EF Core** 10 + Npgsql | ABP 自带模块（Identity / OpenIddict / 权限 / 审计）继续用 EF Core |
| 数据库 | **PostgreSQL 16** | UTF-8 原生支持全部 Unicode，生僻字无需额外处理 |
| 缓存 | **Redis 7** | 分布式缓存与锁；不可用时自动降级为进程内缓存并短路跳过 |
| 消息队列 | **RabbitMQ 3.13** | 分布式事件（照片削峰、通知）— 已接入，Outbox 待做 |
| 照片存储 | **NAS**（SMB/NFS 挂载为目录） | 数据库只存相对路径与元数据 |
| 前端 | **Vue 3 + Vite 7 + TypeScript + Element Plus** | PC 管理端；移动端与小程序共用 API |
| 认证 | **OpenIddict**（OIDC） | 密码模式供自有前端；授权码模式供教育系统内其它系统 SSO |
| 测试 | **xunit v3** + Shouldly | 单元测试不碰数据库；集成测试打真实 PostgreSQL |

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

## 测试

```bash
# 单元测试：156 个，不依赖数据库（停掉容器也全绿）
dotnet run --project test/Xjgl.Domain.Tests

# 集成测试：160 个，打真实 PostgreSQL
dotnet run --project test/Xjgl.Application.Tests

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

集成测试默认连 `Host=127.0.0.1;Port=5432;Database=xjgl_test`，可用环境变量覆盖：

```bash
XJGL_TEST_CONNECTION="Host=...;Database=...;User ID=...;Password=..." \
  dotnet run --project test/Xjgl.Application.Tests
```

**两处值得注意的测试写法：**

1. **单元测试不需要数据库** —— 领域层不引用任何 ORM，所以"流程实例化与合并算法"
   （14 个用例覆盖跨市/同市/同县/本校/含省级）几毫秒就能跑完。
   验证方法：把数据库容器停掉再跑一次，仍然全绿。
2. **照片的故障演练不用真 NAS** —— 把"NAS 主存储"指向本地目录，需要模拟故障时
   把这个目录换成一个**同名文件**，于是"写入失败 → 熔断 → 降级 → 待补偿"真实发生；
   恢复只需把文件删掉。走的是与生产完全相同的依赖注入装配和熔断器，不是 mock。

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
  Xjgl.DbMigrator/             控制台迁移工具（建表 + 种子，幂等）
test/
  Xjgl.TestBase/               测试基础设施
  Xjgl.Domain.Tests/           单元测试（不碰数据库）
  Xjgl.Application.Tests/      集成测试（真实 PostgreSQL）
web/
  apps/pc/                     PC 管理端（Vue 3 + Element Plus）
deploy/                        WSL2 + Docker 开发环境编排与初始化脚本
docs/                          设计与交付文档（学习文档单独交付，不在仓库内）
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
   在实现 **Outbox**（事件与业务数据同事务落库、后台重发）之前，
   "有 MQ 配置但 MQ 不通"比"暂时不接 MQ"更危险。
   当前代码没有任何功能依赖 MQ，因此移除不损失能力，还顺带消除了登录时的 MQ 连接超时。
4. **照片缩略图尚未生成**（`stu_photo.width/height` 恒为 0），列表页加载原图。
5. **生僻字字体需要自行放置**：`web/apps/pc/public/fonts/` 下的 Plangothic 分片文件
   体积较大，**不进仓库**，缺失时回落到系统字体（生僻字显示为方块，功能不受影响）。
6. **H5 与微信小程序尚未开始**；授权码模式 SSO 未接。
7. **数据范围目前按组织层级自动解析**：省厅=全省、市/县/校=本级及下级、未关联组织=什么都看不到，
   内置 admin 角色旁路。**按角色单独配置数据范围**（`sys_role_data_scope` 表）尚未实现，
   因此"某市局只看本市不看他下属县"这类例外暂时无法配置。
   实现位置：`src/Xjgl.Domain/Organizations/UserDataScope.cs` 与 `UserDataScopeProvider.cs`。

---

## 参与贡献

欢迎提交 Issue 与 Pull Request。提交前请确认：

```bash
dotnet build Xjgl.sln                       # 0 错误
dotnet test test/Xjgl.Domain.Tests          # 全绿
dotnet test test/Xjgl.Application.Tests     # 全绿（需要本地数据库）
cd web && corepack pnpm@9.15.0 --filter @xjgl/pc run typecheck
```

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
A step-by-step tutorial series (about 45 chapters) is delivered separately and is **not**
part of this repository.
