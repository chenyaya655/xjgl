# 02 把版本钉死：Directory.Build.props

> **对应阶段**：P0 ｜ **预计耗时**：15 分钟 ｜ **产出**：一个版本可控、升级只需改一处的解决方案

---

## 2.1 要解决的问题

一个 .NET 解决方案有 9 个项目，如果每个 `.csproj` 里都写一遍版本号：

```xml
<!-- 反面教材：9 个项目里各写一遍 -->
<PackageReference Include="Volo.Abp.Identity.Domain" Version="10.6.0" />
<PackageReference Include="Volo.Abp.Identity.Domain" Version="10.6.0" />
...
```

会发生什么：

1. 想升级 ABP，要改 60 多处，**一定会漏**；
2. 漏一处不会立刻报错，而是运行时炸：
   ```
   Could not load file or assembly 'Volo.Abp.Identity.Domain, Version=10.5.0.0'
   ```
3. 更阴的是 **EF Core 版本错配**，报错信息完全看不出是版本问题：
   ```
   Could not load file or assembly 'Microsoft.EntityFrameworkCore, Version=10.0.12.0'
   ```
   （我在这个项目上真实踩过：把 `Microsoft.Extensions.*` 写成最新版 10.0.12，
   而 ABP 10.6.0 依赖的是 10.0.9，运行时直接崩。）

**解决办法：`Directory.Build.props`。**

MSBuild 会**自动向上查找**名为 `Directory.Build.props` 的文件并导入，
不需要任何 `<Import>` 语句。放在仓库根目录，所有项目自动生效。

---

## 2.2 动手步骤

在仓库根目录新建 `Directory.Build.props`：

```xml
<Project>

  <!--
    解决方案级公共 MSBuild 属性。
    放在仓库根目录，MSBuild 会自动向上查找并应用到所有项目，
    因此各 .csproj 无需重复 Import。
  -->
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Version>1.0.0</Version>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <AbpProjectType>app</AbpProjectType>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>

    <!-- 统一版本号：升级 ABP 只需改这里 -->
    <AbpVersion>10.6.0</AbpVersion>

    <!--
      Microsoft.Extensions.* / EF Core 的版本必须与 ABP 依赖的版本保持一致
      （ABP 10.6.0 依赖 EF Core 10.0.9）。设高了会导致运行时
      "Could not load file or assembly 'Microsoft.EntityFrameworkCore, Version=x.y.z'"。
    -->
    <DotNetExtVersion>10.0.9</DotNetExtVersion>
    <SqlSugarVersion>5.1.4.221</SqlSugarVersion>
  </PropertyGroup>

</Project>
```

然后所有 `.csproj` 里就可以这样写：

```xml
<PackageReference Include="Volo.Abp.Identity.Domain" Version="$(AbpVersion)" />
<PackageReference Include="SqlSugarCore" Version="$(SqlSugarVersion)" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="$(DotNetExtVersion)">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

---

## 2.3 每个属性的作用（逐条解释）

| 属性 | 为什么这么设 |
|---|---|
| `LangVersion=latest` | 用上 C# 新语法（主构造函数、集合表达式等）。只有 SDK 版本限制，没有兼容风险 |
| `Nullable=enable` | **可空引用类型必须开**。本项目的"无过滤条件查询报 500"那个 bug，编译期就有 `CS8604` 警告精确指出位置（详见第 08 章），开了才能看到 |
| `ImplicitUsings=enable` | 省掉 `using System;` 之类的样板。注意 Web SDK 会额外隐式引入 `Microsoft.Extensions.Configuration` 等 |
| `TreatWarningsAsErrors=false` | **故意不设成 true**。理由是：把警告当错误会让"先跑起来再收拾"变得困难；但代价是必须**真的去看警告** —— 上面那个 500 的 bug 就是因为把 `CS8604` 当噪音忽略了 |
| `GenerateDocumentationFile=false` | 注释全部是中文且大量使用"为什么"叙述，生成 XML 文档没有意义，反而让编译变慢 |
| `AbpProjectType=app` | ABP 的构建目标会据此判断是应用还是模块包 |
| `DotNetExtVersion=10.0.9` | **最容易被忽略、也最容易出事的一条**：`Microsoft.Extensions.*` 与 EF Core 的版本必须与 ABP 依赖的版本一致。不要图新 |

---

## 2.4 验证方法

**① 属性确实生效了**

```powershell
dotnet msbuild src/Xjgl.Domain/Xjgl.Domain.csproj -getProperty:AbpVersion
```

期望输出 `10.6.0`。再试：

```powershell
dotnet msbuild src/Xjgl.Domain/Xjgl.Domain.csproj -getProperty:Nullable
# 期望：enable
```

**② 依赖树里没有版本冲突**

```powershell
dotnet list src/Xjgl.HttpApi.Host/Xjgl.HttpApi.Host.csproj package --include-transitive `
  | Select-String "EntityFrameworkCore|Microsoft.Extensions"
```

重点看有没有同一个包出现两个版本（尤其是 `Microsoft.Extensions.*`）。
如果出现 `10.0.12` 与 `10.0.9` 并存，说明某处硬编码了版本号。

**③ 真实跑一次的验证（最重要）**

```powershell
dotnet build Xjgl.sln
dotnet run --project src/Xjgl.HttpApi.Host
```

如果版本错配，就会在启动时爆出
`Could not load file or assembly 'Microsoft.EntityFrameworkCore, Version=...'`。
**这一步比看依赖树可靠**，因为程序集加载失败只发生在运行期。

---

## 2.5 踩坑记录

### 坑 1：把 `Microsoft.Extensions.*` 升到最新

我真实的错误：看到 `NETSDK1057` 提示有新版，顺手把
`Microsoft.Extensions.Hosting` 升到 10.0.12，
结果 ABP 内部依赖的是 10.0.9，运行时程序集加载失败。

**结论**：`DotNetExtVersion` 的值 = **ABP 对应版本依赖的值**。
怎么查？

```powershell
# 查 ABP 包依赖了哪个版本
dotnet list src/Xjgl.EntityFrameworkCore/Xjgl.EntityFrameworkCore.csproj package --include-transitive `
  | Select-String "Microsoft.EntityFrameworkCore "
```

以 ABP 的输出为准，不要以 NuGet 上"最新版"为准。

### 坑 2：`Directory.Build.props` 放错位置

它必须是**仓库根目录**（或项目目录的任意祖先目录）。
如果你放在 `src/` 下，那么 `test/` 下的测试项目就享受不到，
表现为"测试项目的 `Nullable` 没生效"，很难联想到是文件位置问题。

### 坑 3：与 `Directory.Build.targets` 的区别

- `Directory.Build.props`：在项目文件**之前**导入 → 用来定义属性（`PropertyGroup`）；
- `Directory.Build.targets`：在项目文件**之后**导入 → 用来定义目标（`Target`）或覆盖项目里的属性。

本项目只需要 props。如果你发现设置了属性却被项目文件里的同名属性覆盖了，
那是导入顺序问题，应该改用 targets。

### 坑 4：`ImplicitUsings` 与显式 `using` 冲突

开启隐式 using 后，某些类型会"莫名其妙"可用（如 Web SDK 下的
`Microsoft.Extensions.Configuration`）。这本身是好事，但会让**复制粘贴到类库项目**时代码编译不过
（类库 SDK 没有那些隐式 using）。遇到"在我的项目里能编译、拷过去就不行"，先查隐式 using。

---

## 2.6 本章产出

| 项 | 值 |
|---|---|
| 文件 | `Directory.Build.props`（仓库根） |
| 统一版本 | ABP `10.6.0`、.NET Extensions / EF Core `10.0.9`、SqlSugar `5.1.4.221` |
| 已开启 | `Nullable`、`ImplicitUsings`、`LangVersion=latest` |
| 升级方式 | 只改这一个文件 |

---

**上一章：** [01 WSL2 + Docker 三件套](01-环境准备-WSL2与Docker三件套.md) ｜ **下一章：** [03 手动搭出 ABP 解决方案骨架](03-搭建解决方案骨架.md)
