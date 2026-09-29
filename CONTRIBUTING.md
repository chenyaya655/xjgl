# 参与贡献

感谢你愿意花时间。这个项目的**注释密度偏高**、提交信息偏长，都是刻意的 ——
先说清约定，可以省掉来回返工。

---

## 一、最快的参与方式

| 你想做的 | 怎么做 |
|---|---|
| 报告缺陷 | 用 [Bug 报告](../../issues/new?template=bug_report.yml) 模板，**附上可复现步骤** |
| 提功能建议 | 用 [功能建议](../../issues/new?template=feature_request.yml) 模板 |
| 部署遇到问题 | 用 [部署问题](../../issues/new?template=deployment.yml) 模板 —— 这类问题的环境信息最关键 |
| 认领待办 | 看 [`ROADMAP.md`](ROADMAP.md)，每条都标了难度与影响面 |
| 改进文档 | 直接 PR。文档错误和代码缺陷同等重要 |

---

## 二、本地验证（提交 PR 前请跑一遍）

```bash
# 后端：构建 + 单元测试（不依赖数据库）
dotnet build Xjgl.sln
dotnet run --project test/Xjgl.Domain.Tests

# 集成测试：需要真实 PostgreSQL
cd deploy && docker compose -f docker-compose.dev.yml up -d && cd ..
dotnet run --project src/Xjgl.DbMigrator          # 建表 + 种子（幂等，可重复跑）
dotnet run --project test/Xjgl.Application.Tests

# 前端：类型检查 + 单测 + 生产构建
cd web && corepack pnpm@9.15.0 install
corepack pnpm@9.15.0 -r typecheck
corepack pnpm@9.15.0 --filter @xjgl/pc run test:unit
corepack pnpm@9.15.0 --filter @xjgl/pc run build
corepack pnpm@9.15.0 --filter @xjgl/h5 run build

# 跨语言的三处"约定"核对（权限码 / 码类别 / 操作类型）
node tools/check-permissions-sync.mjs

# 端到端（自动拉起前后端）
corepack pnpm@9.15.0 test:e2e
```

> **前端需要 Node ≥ 22.18**（CI 用 24）：`test:unit` 用 `node --test` 直接跑 `.ts`，
> 靠的是 Node 内置的类型擦除；Node 20 会以 `Unknown file extension .ts` 失败。

> ⚠️ 两个反复绊倒人的点：
> 1. **测试项目是 xunit v3 + Microsoft.Testing.Platform**，
>    用 `dotnet run --project` 跑，不要用 `dotnet test`（会报 VSTest 不支持）；
> 2. **改了元数据种子（`ModuleMetadataDataSeedContributor`）后必须重跑 DbMigrator**，
>    否则新模块的权限在库里不存在，相关测试会红在看起来无关的地方。

CI 会跑上面全部步骤，**并且打真实 PostgreSQL 与真实 Chromium** ——
本地绿了 CI 红，通常是漏了字体（`fonts-noto-cjk`）或漏跑 DbMigrator。

---

## 三、代码约定（这几条是硬要求）

### 1. 注释写"为什么"，不写"这行在做什么"

```csharp
// ✗ 不要
// 如果路径为空则返回 null
if (path == null) return null;

// ✓ 要
// 空路径必须显式返回 null 而不是空字符串：
// 调用方用 `== null` 判断"没有数据范围"，空字符串会被当成"范围是空路径"，
// 于是未关联组织的用户反而能看到全省数据 —— 这是数据范围里唯一不能搞错的方向。
if (path == null) return null;
```

涉及取舍的地方要写**"不这么做会怎样"**。踩过的坑请把**现象**写进去
（"表现为列表空、接口 200、日志无异常"这类），因为下一个人是先看到现象才来搜代码的。

### 2. 改业务逻辑必须带测试

| 改了什么 | 要加的测试 |
|---|---|
| 领域算法（纯逻辑） | `test/Xjgl.Domain.Tests`，**不碰数据库** |
| 应用服务 / 持久化 / 权限 | `test/Xjgl.Application.Tests`，打真实 PostgreSQL |
| 前端交互 / 端到端流程 | `web/e2e/specs` |

**优先写"能证明缺陷会复现"的测试**，而不是"覆盖了这行代码"的测试。
本项目里最有价值的几条用例，都是先有缺陷、再补的回归。

### 3. 改权限码要同步改前端

`src/Xjgl.Application.Contracts/Permissions/MetadataPermissions.cs`
与 `web/packages/api/src/permissions.ts` 是**手工同步**的。
漏了不会报错，只是那个按钮永远不显示 —— 属于最难排查的一类。
（`docs/learning/47` 有完整说明。）

现在有 `tools/check-permissions-sync.mjs` 在 CI 里**机械核对**它（连同码类别、
操作类型、导出种类、数据范围、异动状态，以及"权限码必须有执行点"共七处），
本地也可以直接跑；但**规约本身仍是"改一边就改另一边"**，
工具只是让漏改会红，而不是替你改。

> ⚠ **声明一个权限码之前，先问"它在哪被执行"**。最后那一处核对会失败：
> 一个只出现在 `MetadataPermissions.cs` 与元数据种子里、没有任何
> `[Authorize]` / `IsGrantedAsync` 指向它的权限码，会在权限管理页变成一个
> **"勾了却不影响任何行为"**的开关 —— 管理员据此以为已经能用了。
> 本项目真实栽过这个跟头（一次 8 条码，见 `MetadataPermissions.cs` 的注释）。
> 如果你要声明的是**菜单级**权限码（读接口只要求登录，不按该码卡），
> 请连同理由加进脚本的 `MENU_ONLY_PERMISSIONS` 白名单。

### 4. 元数据驱动：不要在代码里写死模块

菜单、路由、权限都来自 `sys_subsystem` / `sys_module` / `sys_module_action` 三张表。
新增一个功能模块时，**先在元数据里加模块与操作**，权限与菜单会自动出现。

### 5. 前端不得手写码值

下拉框的选项一律从码表（`/api/app/code`）取，不要在前端写 `'01' => '在读'`。
本项目为此吃过两次亏：学籍状态过滤静默返回空列表、审批后把非法值写进学生状态。
现成封装在 `web/apps/pc/src/composables/useCodeOptions.ts`。

### 6. 可能上千条的集合，不许直接塞进下拉

两条许可做法，二选一：

- **`el-select-v2` 虚拟滚动**（列表本来就该整体加载时，如学校、年级班级：
  见 `components/SchoolSelect.vue`、`components/GradeClassSelect.vue`）；
- **关键字服务端检索 + 硬上限**（如按姓名/辅号选学生、选用户）。

**并且：只要限了条数，界面就必须说出来**（"结果较多，请继续输入以缩小范围"）。
这一条比前两条更容易漏，本项目为此吃过两次亏：一是渲染全省 6789 所学校把主线程
卡死 100 秒；二是 3 处"限了 20/30/100 条却一句话不说"的地方 —— 老师输入一个"王"
只看到 20 条，会得出"系统里没有这个学生"的**错误结论**，而界面上没有任何异常。

配套细节：选中项要并回选项列表，否则 `el-select` 找不到选项会**直接显示 Guid**。

---

## 四、提交信息

格式：`type(scope): 一句话说明`，正文写**为什么**。

```
feat(photos): 照片缩略图（ImageSharp）+ 取图接口（ETag/304）
       ^类型    ^范围：模块名，小写

修掉 xx：<现象>。根因是 <原因>。<怎么发现的>。
```

- `type`：`feat` / `fix` / `docs` / `test` / `chore` / `refactor`；
- 一句话说清**做了什么**，细节放正文；
- **不要**写"修改了 A 文件、B 文件" —— `git log --stat` 已经有了。

---

## 五、Pull Request

CI 必须全绿。PR 描述里请回答三个问题：

1. **解决什么问题**（现象是什么，怎么发现的）；
2. **怎么验证的**（跑了哪条命令 / 哪条用例）；
3. **有什么已知限制或取舍**（如果有）。

⚠️ **不要提交**：

- `appsettings.Secrets.json`、`*.pfx`、`.env`、任何生产凭据；
- `.tools/`（本机工具链缓存，已在 `.gitignore`）；
- `bin/` `obj/` `Logs/` `node_modules/`（已在 `.gitignore`）。

发布前的敏感信息审计清单见 [`docs/GitHub发布清单.md`](docs/GitHub发布清单.md)。

---

## 六、行为准则

参与本项目即表示你同意遵守 [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md)。
技术讨论可以激烈，但**对人不可以**。
