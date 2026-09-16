# 河南省学籍管理系统

面向 **省 → 市 → 县(区) → 学校** 四级教育行政体系的学籍管理系统，全省统一部署、
统一入口登录。当前处于 P0 阶段（骨架已跑通，业务模块待开发）。

> 本项目的技术设计文档见 [`docs/学籍管理系统-总体方案.md`](docs/学籍管理系统-总体方案.md)。

---

## 技术选型

| 层次 | 选型 | 说明 |
|---|---|---|
| 后端框架 | **ABP Framework 10.6** / .NET 10 | 模块化、依赖注入、审计、权限、多语言开箱可用 |
| 业务 ORM | **SqlSugar** 5.1 | 业务表（`sys_*` / `stu_*`）由 SqlSugar 负责建表与访问 |
| 框架 ORM | **EF Core** 10 + Npgsql | ABP 自带模块（Identity / OpenIddict / 权限 / 审计…）继续用 EF Core |
| 数据库 | **PostgreSQL 16** | UTF-8 原生支持全部 Unicode，生僻字无额外处理 |
| 缓存 | **Redis 7** | 分布式缓存、分布式锁、幂等键、限流、任务进度 |
| 消息队列 | **RabbitMQ 3.13** | 照片处理削峰、批量导入导出、通知、缓存失效广播 |
| 照片存储 | **NAS**（SMB/NFS 挂载为目录） | 数据库只存相对路径与元数据 |
| 前端 | **Vue 3 + Vite + TypeScript + Element Plus** | PC 管理端；移动端 H5 与微信小程序共用 API |
| 认证 | **OpenIddict**（OIDC） | 密码模式/刷新令牌供自有前端；授权码模式供教育系统内其它系统 SSO |

### 为什么是「两套 ORM 共存」

ABP 官方只支持 EF Core / MongoDB，且 Identity、OpenIddict、Permission、AuditLogging
等框架模块与 EF Core 深度耦合（各自带 `*.EntityFrameworkCore` 包）。用 SqlSugar 重写
这些仓储需要实现数十个接口，成本极高且难以跟随 ABP 升级。

因此采用**混合模式**：

- **框架表** → EF Core 迁移（`Xjgl.EntityFrameworkCore`）
- **业务表** → SqlSugar CodeFirst（`Xjgl.SqlSugarCore`）
- 两者共用**同一个 PostgreSQL 数据库**与**同一套 ABP 工作单元**
- 建表编排在 `XjglDbMigrationService` 里，通过 `IXjglDbSchemaMigrator.Order` 保证顺序

> 关键取舍：SqlSugar 的 `ISugarQueryable<T>` **不实现 `IQueryable<T>`**（已实测确认），
> 因此业务仓储无法派生 ABP 的 `RepositoryBase`，改为 SqlSugar 原生仓储
> `IXjglRepository<TEntity>`，审计字段/软删除/事务仍是 ABP 语义。

---

## 目录结构

```
StudentManagement/
├─ Xjgl.sln
├─ Directory.Build.props          # 统一的版本号与编译属性
├─ src/
│  ├─ Xjgl.Domain.Shared/         # 常量、错误码、多语言资源
│  ├─ Xjgl.Domain/                # 实体、领域服务、仓储接口、OpenIddict 种子
│  ├─ Xjgl.Application.Contracts/ # DTO、应用服务接口、权限定义
│  ├─ Xjgl.Application/           # 应用服务实现
│  ├─ Xjgl.SqlSugarCore/          # ★ 自研 SqlSugar 集成层（客户端/仓储/事务）
│  ├─ Xjgl.EntityFrameworkCore/   # 框架表 DbContext + 迁移
│  ├─ Xjgl.HttpApi/               # Controller
│  ├─ Xjgl.HttpApi.Host/          # 宿主：API + OpenIddict + Swagger
│  └─ Xjgl.DbMigrator/            # 迁移与种子数据控制台
├─ deploy/                        # WSL Docker 开发环境
└─ docs/                          # 技术设计文档
```

---

## 快速开始

### 1. 启动开发环境（WSL2 + Docker）

```bash
cd /mnt/d/StudentManagement/deploy
docker compose -f docker-compose.dev.yml up -d
docker compose -f docker-compose.dev.yml ps      # 等到都 healthy
```

详见 [`deploy/README.md`](deploy/README.md)。

### 2. 建表与初始化数据

```powershell
dotnet run --project src\Xjgl.DbMigrator
```

### 3. 启动后端

```powershell
dotnet run --project src\Xjgl.HttpApi.Host
```

- Swagger：<http://localhost:44300/swagger>
- 探活接口：<http://localhost:44300/api/system/ping>（无需登录，返回 `pong`）

### 4. 前端（P0 阶段尚未创建）

计划位于 `web/`，pnpm workspace + Vue 3 + Element Plus。

---

## 给政务系统开发者的参考要点

本项目在几个政务系统常见难点上做了取舍，供同类项目参考：

1. **全省统一部署下的权限与数据隔离**
   单租户 + 组织树（`PathCode` 物化路径）+ 数据范围，而不是多租户。
   理由是教育局需要跨校聚合统计。

2. **用户不能自助注册**
   管理员按组织批量预置名单 → 本人凭激活码/身份证核验激活，
   注册接口只接受有效激活令牌，外人无法注册。

3. **可配置的异动审批流**
   节点用「组织解析器」描述（转入校/转入县/转入市/转出校/转出县/转出市），
   流程实例化时解析出真实组织并**按组织去重合并**。
   于是「同县区转学县区只审一次」是算法自然结果，业务人员可在界面上调整，无需改代码。

4. **生僻字（CJK 扩展 B 区及以上）**
   存储层 PostgreSQL 无压力；真正的坑在前端字体（需 `unicode-range` 分片加载）、
   C# 的长度校验（必须按 Unicode 码点而非 UTF-16 长度）、
   以及导出 Excel 后客户端字体不受控（改用后端渲染 PDF 嵌入字体）。

5. **照片上传的韧性**
   NAS 写入包裹 超时 → 重试 → 熔断 → 并发舱壁；熔断打开时降级写本地盘并入补偿队列，
   后台任务回放，接口仍然返回「已接收（排队中）」，不把故障暴露给用户。

---

## 开发约定

- 提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/)
- 权限码规范：`{子系统}.{模块}.{操作}`，例如 `Stu.Student.Import`
- 数据库表命名：框架表 `Abp*`，业务表 `sys_*` / `stu_*`（snake_case）
- 新增业务实体需实现 `IXjglBusinessEntity`，DbMigrator 会自动建表

---

## 许可证

待定（计划 Apache-2.0）。
