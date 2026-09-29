# GitHub 开源发布清单

> 面向本项目维护者。执行完这一份清单，仓库就是可以公开的状态。
> 每一步都给了**验证方法** —— 发布前最怕的是"以为没提交敏感信息"。

---

## 一、发布前已经准备好的东西（无需再做）

| 项 | 状态 |
|---|---|
| `LICENSE` | Apache-2.0 全文已就位 |
| `README.md` | 面向使用者/贡献者：亮点、快速开始、测试、目录结构、**19 条已知限制** |
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
| `tools/check-permissions-sync.mjs` | 跨 C# 与 TS 核对**七处**约定：权限码 / 码类别 / 操作类型 / 导出任务种类 / 数据范围 / 异动状态 / **权限码执行点**（漏改不会报错，只会"按钮不显示、下拉变空"；最后一处防的是"声明了却没有实现的权限码"） |
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

# 3) 学习文档是否已随仓库发布（期望：50）
git ls-files docs/learning | Measure-Object -Line

# 4) 文档相对链接是否都能解析（中文文件名容易断）
node tools/check-doc-links.mjs

# 5) 所有被跟踪的文本文件都是**合法 UTF-8**（期望：0）
#    中文仓库最容易出的问题：某个文件被 GBK 编辑器保存过，
#    GitHub 上整段注释变成乱码，而本地用 GBK 终端看又是"正常"的。
$enc = New-Object System.Text.UTF8Encoding($false, $true)
$bad = @()
foreach ($f in (git -c core.quotePath=false ls-files)) {
  if (-not [System.IO.File]::Exists($f)) { continue }
  $b = [System.IO.File]::ReadAllBytes($f)
  if ($b.Length -eq 0 -or $b -contains 0) { continue }
  try { [void]$enc.GetString($b) } catch { $bad += $f }
}
"非 UTF-8 文件数: $($bad.Count)"; $bad

# 6) 没有"合法 UTF-8 但内容是乱码"的文件（期望：0）
#    第 5 步查不出这一类：文件是合法 UTF-8，但内容是被误解码过的残骸
#    （如 `娌冲崡` 而不是 `河南`）。判据是拿它按 GBK 反向编码，
#    再按 UTF-8 解码，看能不能还原出像样的中文。
```

第 2 步的期望结果：**只有 2 行** ——
（a）本文件里那条审计命令自身；
（b）`docs/learning/03-搭建解决方案骨架.md` 里一句**刻意**写的反例
（"不要照抄本机路径"，它必须含这个字面量才讲得清）。
除此之外**不该有任何文件命中**；冒出别的文件，停下来先处理。

> **为什么第 5、6 步值得单独列出来**：这两类问题都**不会让任何测试变红**，
> 也不会让构建失败 —— 它们只在 GitHub 网页上表现为"一片乱码"。
> 本项目真实发生过：`web/.gitignore` 被存成 GBK（第 5 步抓出），
> 根 `.gitignore` 的中文注释被误解码成乱码（第 6 步抓出），
> 而且乱码那一行还把后面的规则**注释掉了**，导致
> `web/apps/mp/dist/` 与 `photo-fallback/` 两条忽略规则**静默失效** ——
> 后者是照片降级目录，一旦真跑起来，运行期照片可能被提交进仓库。
> 一个纯显示问题底下藏着一个隐私问题，靠"看起来正常"是发现不了的。

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

## 三、发布前需要你决定/替换的（已经处理掉大部分）

本仓库现在的原则是：**发布前不留任何"必须填上用户名才能正常显示"的链接**，
所以下面这张表里已经有两项被**彻底去掉**（不是留占位），而不是等你去替换。

| # | 位置 | 现状 |
|---|---|---|
| 1 | `LICENSE` 末尾 `Copyright 2026 The Xjgl Authors` | **保持原样**（以项目名义发布）。若你要写具体单位，换掉这一行即可 —— 政务项目建议写单位，便于后续授权与审计追溯 |
| 2 | `README.md` 顶部的 CI 徽章 | **已移除**。Actions 徽章地址必须含 `<账号>/<仓库>`，留着就是一个裂图。README 里给了一行注释掉的写法，推上去之后填上账号再放开 |
| 3 | `.github/ISSUE_TEMPLATE/config.yml` 的 `contact_links` | **已移除**（那三个入口要求绝对 URL）。文件里保留了注释掉的写法；**安全漏洞上报通道不受影响** —— 仓库根有 `SECURITY.md`，GitHub 会自动在 Security 标签页展示「Report a vulnerability」 |
| 4 | `.github/workflows/ci.yml` 触发分支 | **已收敛为只监听 `main`**。本地分支会在推送那一步用 `git branch -M main` 改名（见第四节） |

> **为什么不留 `OWNER` 占位而是删掉**：占位符的失效方式是**静默**的 ——
> README 顶上一个裂图、Issue 页三个 404，谁都不会为此报错，
> 而读者第一眼看到的就是它。删掉之后，仓库在任何账号下都显示正常，
> "补徽章"变成一件**可选的、有明确命令**的收尾动作，而不是一个必须记得做的前置条件。

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
git ls-files | Measure-Object -Line     # 期望：约 458 个文件（含 docs/learning/ 的 50 个 md）

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
> "文档上看着什么都有、用起来处处是坑"。这份仓库刻意用 19 条已知限制
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
