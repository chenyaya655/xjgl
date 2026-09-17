# GitHub 开源发布清单

> 面向本项目维护者。执行完这一份清单，仓库就是可以公开的状态。
> 每一步都给了**验证方法** —— 发布前最怕的是"以为没提交敏感信息"。

---

## 一、发布前已经准备好的东西（无需再做）

| 项 | 状态 |
|---|---|
| `LICENSE` | Apache-2.0 全文已就位 |
| `README.md` | 面向使用者/贡献者重写：亮点、快速开始、测试、目录结构、已知限制 |
| `SECURITY.md` | 开发口令说明 + 生产安全清单 + 漏洞上报方式 |
| `.editorconfig` | 后端 4 空格、前端 2 空格、统一 UTF-8 与 LF |
| `.gitattributes` | 统一 LF、二进制文件标记、语言统计排除生成物 |
| `.gitignore` | 已排除 `.tools/`、`docs/learning/`、`Logs/`、`node_modules/`、`*.pfx`、密钥文件 |
| CI | `.github/workflows/ci.yml`：后端构建 + 单元测试 + 集成测试（真实 PostgreSQL 服务）+ Swagger 冒烟；前端类型检查 + 生产构建 |
| 敏感信息审计 | 见第二节（已审计通过） |

---

## 二、发布前必须亲自跑一遍的审计

在仓库根目录执行：

```powershell
# 1) 有没有不该提交的文件被跟踪？（期望：无输出）
git ls-files | Select-String -Pattern "\.pfx$|\.p12$|\.key$|Secrets|\.env$|\.tools/|docs/learning/|^Logs/|node_modules"

# 2) 已跟踪文件里有没有本机绝对路径？（期望：无输出）
git grep -n -I -F "D:\StudentManagement"

# 3) 有没有把生产密钥写进配置？（期望：只有本地开发口令，且都在已知的 4 个文件里）
git grep -n -I "Xjgl@Dev2026"

# 4) 学习文档真的没被跟踪？（期望：无输出）
git ls-files docs/
```

第 3 步的期望结果：只出现在
`deploy/docker-compose.dev.yml`、`deploy/README.md`、
`src/Xjgl.DbMigrator/appsettings.json`、`src/Xjgl.HttpApi.Host/appsettings.json`、
`test/Xjgl.TestBase/XjglTestConfiguration.cs`
—— 全部是**本地开发口令**，已在 `SECURITY.md` 里说明。

**如果第 3 步冒出了别的文件**（尤其是生产连接串），停下来先处理。

---

## 三、发布前需要你决定/替换的三处

| # | 位置 | 要做什么 |
|---|---|---|
| 1 | `LICENSE` 末尾的 `Copyright 2026 The Xjgl Authors` | 换成**真实的版权人**（个人姓名或单位名称）。政务项目建议写单位 |
| 2 | `README.md` 顶部标题与「English Summary」 | 如果仓库名不叫 `Xjgl`，把标题与克隆地址示例改掉 |
| 3 | `.github/workflows/ci.yml` 的触发分支 | 默认监听 `main` 与 `master`，按你的主分支名保留其一即可 |

---

## 四、创建仓库并推送

### 1. 在 GitHub 上新建仓库

- **可见性**：`Public`（开源目的）
- **不要**勾选 "Add a README file"、"Add .gitignore"、"Choose a license"
  —— 本地已经有了，勾了会产生一次冲突性的初始提交
- 仓库名建议：`xjgl` 或 `henan-student-management`

### 2. 本地配置并推送

```powershell
# 确认本机 git 身份（会写进每个提交）
git config user.name  "你的名字"
git config user.email "你的邮箱"

# 关联远端（把 <owner> 换成你的 GitHub 用户名）
git remote add origin https://github.com/<owner>/xjgl.git
git branch -M main

# 推之前先看一眼**将要推送的文件清单**，这是最后一道人工闸门
git ls-files | Measure-Object -Line      # 期望：约 190 个文件

git push -u origin main
```

### 3. 推送后立刻验证

```powershell
# 在浏览器打开仓库，确认：
#   - 看不到 docs/learning/、.tools/、Logs/、node_modules/
#   - LICENSE、README.md、SECURITY.md 都在根目录
#   - Actions 里 CI 跑起来了（第一次可能需要手动 Enable workflows）
```

---

## 五、推送后建议做的事

| 项 | 说明 |
|---|---|
| **开启分支保护** | Settings → Branches → 要求 CI 通过才能合并到 main |
| **添加仓库描述与话题** | 描述用一句话；话题建议 `abp-framework`、`sqlsugar`、`vue3`、`element-plus`、`education`、`postgresql`、`dotnet10` |
| **确认 Actions 权限** | Settings → Actions → General，允许运行工作流 |
| **配 Dependabot**（可选） | 自动提依赖升级 PR；注意 AutoMapper 那条已知告警 |
| **把学习文档单独交付** | 学习文档在 `docs/learning/`（约 45 章），按需求**不进入开源仓库**。建议单独打包或以私有仓库交付 |
| **打标签** | 第一个可用版本建议 `v0.1.0`，并在 Release 里写清"哪些是已实现、哪些是已知限制" |

---

## 六、本仓库的发布后维护约定

1. **提交信息**：`feat/fix/docs/test/chore(范围): 一句话说明`，
   正文写**为什么**改，而不是改了哪些文件（文件列表 git 自己会列）。
2. **改动业务逻辑必须带测试**：领域算法改单元测试，接口/持久化改集成测试。
3. **改 `MetadataPermissions.cs` 时同步改前端 `constants/permissions.ts`**，
   否则那个按钮永远不会显示（而且不报错）。
4. **动实体后立刻加 EF 迁移**（仅框架实体）或直接跑 DbMigrator（业务实体加了
   `IXjglBusinessEntity` 就会自动建表）。
5. **不要提交 `appsettings.Secrets.json`、`*.pfx`、`.tools/`** —— 已在 `.gitignore`，
   但形成习惯比依赖规则可靠。
