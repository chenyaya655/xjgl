# GitHub 开源发布清单

> 面向本项目维护者。执行完这一份清单，仓库就是可以公开的状态。
> 每一步都给了**验证方法** —— 发布前最怕的是"以为没提交敏感信息"。

---

## 一、发布前已经准备好的东西（无需再做）

| 项 | 状态 |
|---|---|
| `LICENSE` | Apache-2.0 全文已就位 |
| `README.md` | 面向使用者/贡献者：亮点、快速开始、测试、目录结构、**18 条已知限制** |
| `SECURITY.md` | 开发口令说明 + 生产安全清单 + 漏洞上报方式 |
| `CONTRIBUTING.md` | 本地验证命令、代码约定（注释写"为什么"、权限码同步、码值不手写） |
| `CODE_OF_CONDUCT.md` | Contributor Covenant v2.1 + **领域特别提醒**（不许贴真实个人数据） |
| `CHANGELOG.md` | `0.1.0` 首个版本的能力与**已知限制** |
| `ROADMAP.md` | 待办按「难度 × 影响面」拆成可认领条目 + 一节明确的「非目标」 |
| `.github/ISSUE_TEMPLATE/` | Bug / 功能建议 / 部署问题 三个表单（含脱敏提醒） |
| `.github/pull_request_template.md` | PR 自查清单 |
| `.github/workflows/ci.yml` | 4 个 job：后端（构建+单测+集成+Swagger 冒烟）/ 前端（Node 24，三端类型检查 + 单测 + 约定核对 + PC/H5 两次构建）/ 部署编排 / 端到端 |
| `docs/` | 设计文档 + **49 章学习文档**（另 1 篇文档导航 README，共 50 个 md）+ 3 篇对外技术文章 |
| `tools/check-doc-links.mjs` | 校验所有相对链接可解析（中文文件名最容易断链） |
| `tools/check-permissions-sync.mjs` | 跨 C# 与 TS 核对权限码 / 码类别 / 操作类型三处约定（漏改不会报错，只会"按钮不显示、下拉变空"） |
| `tools/reset-test-db.mjs` | 本机重建测试库（默认 dry-run；库名不匹配 `/test/i` 直接拒绝） |
| `.editorconfig` / `.gitattributes` | 4/2 空格、统一 UTF-8 与 LF、二进制标记 |
| `.gitignore` | 排除 `.tools/`、`Logs/`、`node_modules/`、`*.pfx`、密钥文件、**内部会话纪要** |

---

## 二、发布前必须亲自跑一遍的审计

在仓库根目录执行（**这几条都必须为"无输出"**）：

```powershell
# 1) 有没有不该提交的文件被跟踪？
git ls-files | Select-String -Pattern "\.pfx$|\.p12$|\.key$|Secrets|\.env$|^\.tools/|^Logs/|node_modules|(^|/)(bin|obj)/"

# 2) 已跟踪文件里有没有本机绝对路径？（期望只命中 2 处，见下方说明）
git grep -n -I -F "D:\StudentManagement"

# 3) 学习文档是否已随仓库发布（期望：能看到 docs/learning/ 下大量文件）
git ls-files docs/learning | Measure-Object -Line

# 4) 文档相对链接是否都能解析（中文文件名容易断）
node tools/check-doc-links.mjs
```

第 2 步的期望结果：**只有 2 行** ——
（a）本文件里那条审计命令自身；
（b）`docs/learning/03-搭建解决方案骨架.md` 里一句**刻意**写的反例
（"不要照抄本机路径"，它必须含这个字面量才讲得清）。
除此之外**不该有任何文件命中**；冒出别的文件，停下来先处理。

### 关于"开发口令"

`Xjgl@Dev2026`、`1q2w3E*` 等**是本地开发口令，允许出现在仓库里**
（`deploy/docker-compose.dev.yml`、`deploy/README.md`、两个 `appsettings.json`、
`test/Xjgl.TestBase/XjglTestConfiguration.cs`）。它们已在 `SECURITY.md` 里说明。

**但生产口令绝不能出现。** 校验：

```powershell
# 期望：无输出
git grep -n -I "POSTGRES_PASSWORD: [^$]" -- deploy/docker-compose.prod.yml
```

---

## 三、发布前需要你决定/替换的四处

| # | 位置 | 要做什么 |
|---|---|---|
| 1 | `LICENSE` 末尾 `Copyright 2026 The Xjgl Authors` | 换成**真实版权人**。政务项目建议写单位 |
| 2 | `README.md` 顶部徽章里的 `OWNER` | 换成你的 GitHub 用户名（**共 3 处**：CI 徽章那一行的两个链接 + 下方提示文字各 1 处） |
| 3 | `.github/ISSUE_TEMPLATE/config.yml` 里的 `OWNER` | 同上（3 个链接） |
| 4 | `.github/workflows/ci.yml` 触发分支 | 默认监听 `main` 与 `master`，按你的主分支名保留其一 |

一键替换（**确认无误再执行**）：

```powershell
# 把 OWNER 换成你的用户名
(Get-ChildItem README.md, .github\ISSUE_TEMPLATE\config.yml -File) |
  ForEach-Object {
    $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($_.FullName, $t.Replace('OWNER', '<你的用户名>'), (New-Object System.Text.UTF8Encoding($false)))
  }
```

---

## 四、创建仓库并推送

### 1. 在 GitHub 上新建仓库

- **可见性**：`Public`
- **不要**勾选 "Add a README file" / ".gitignore" / "Choose a license"
  —— 本地已经有了，勾了会产生一次冲突性的初始提交
- 仓库名建议：`xjgl` 或 `henan-student-management`

### 2. 本地配置并推送

```powershell
git config user.name  "你的名字"
git config user.email "你的邮箱"

git remote add origin https://github.com/<你的用户名>/xjgl.git
git branch -M main

# 推之前先看一眼**将要推送的文件清单**，这是最后一道人工闸门
git ls-files | Measure-Object -Line     # 期望：约 393 个文件（含 docs/learning/ 的 50 个 md）

git push -u origin main
```

### 3. 推送后立刻验证

- 仓库根能看到 `LICENSE` / `README.md` / `SECURITY.md` / `CONTRIBUTING.md` / `ROADMAP.md` / `CHANGELOG.md`；
- **看不到** `.tools/`、`Logs/`、`node_modules/`、任何会话纪要；
- `docs/learning/` 下有 **49 章 + 1 篇导航 README**（共 50 个 md）；
- Actions 里 CI 跑起来了（首次可能需手动 Enable workflows）。

---

## 五、推送后建议做的事（影响"能不能被看到"）

| 项 | 说明 |
|---|---|
| **填仓库描述** | 一句话，建议强调**技术栈 + 领域**，例如"ABP + SqlSugar 混合 ORM 的四级学籍管理系统" |
| **加 Topics** | `abp-framework`、`sqlsugar`、`vue3`、`element-plus`、`education`、`government`、`postgresql`、`dotnet10`、`openiddict` |
| **确认 Actions 权限** | Settings → Actions → General，允许运行工作流 |
| **开启分支保护** | 要求 CI 通过才能合并到 main |
| **打标签 `v0.1.0`** | Release 说明直接引用 `CHANGELOG.md`。**务必写清"哪些没做"** —— 建立信任 |
| **放一个在线 Demo** | ROADMAP 里列为高优先级：能点开就看的项目，反馈量通常差一个量级 |
| **配 Dependabot**（可选） | 注意 AutoMapper 那条已知告警 |
| **写一篇发布文章** | 直接复用 `docs/articles/` 里的三篇（发到掘金/知乎/博客园），文末链回仓库 |

> **为什么强调"写清没做什么"**：政务/开源项目的读者最反感的是
> "文档上看着什么都有、用起来处处是坑"。这份仓库刻意用 18 条已知限制
> 换可信度 —— 发布时**不要**因为"不好看"而删掉它们。

---

## 六、发布后维护约定

1. **提交信息**：`type(scope): 一句话`，正文写**为什么**（不要列文件清单）；
2. **改业务逻辑必须带测试**：领域算法改单元测试，接口/持久化改集成测试；
3. **改 `MetadataPermissions.cs` 时同步改 `web/packages/api/src/permissions.ts`**，
   否则那个按钮永远不显示（且不报错）；
4. **动实体后立刻**：框架实体加 EF 迁移，业务实体跑 DbMigrator；
5. **新增元数据模块后必须重跑 DbMigrator**（演示库与测试库是两个库，各跑一次）；
6. **不要提交** `appsettings.Secrets.json`、`*.pfx`、`.tools/`；
7. **不要往仓库里放真实个人数据**（姓名/身份证号/手机号/学生照片），
   演示数据一律用合成占位（见 `DemoDataSeedContributor`）。
