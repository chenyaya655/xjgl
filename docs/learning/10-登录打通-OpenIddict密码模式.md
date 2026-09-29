# 10 登录打通：OpenIddict 密码模式

> **对应阶段**：P0 ｜ **预计耗时**：2 小时 ｜ **产出**：`POST /connect/token` 能拿到带刷新令牌的 access_token

---

## 10.1 要解决的问题

学籍系统有三种客户端（PC、手机 H5、微信小程序），还有一类特殊需求：

> "教育系统内其它系统登录，比如**学分系统**登录，该系统不需要再次登录。"

也就是**既要自己的登录页，又要能给别人签发令牌**。这正好是 OpenIddict 的定位 ——
ABP 10 内置了它。

**先做哪种授权流程？**

| 流程 | 适合 | 本项目 |
|---|---|---|
| **密码模式**（Resource Owner Password） | 自有客户端 + 自有登录页 | ✅ 现在做 |
| 授权码模式（Authorization Code + PKCE） | 第三方应用跳转授权 | ⏳ P5 做（给学分系统用） |
| 客户端凭据模式 | 服务对服务 | 备用 |

**为什么自有客户端用密码模式**：本系统是**全省统一部署的自有系统**，
登录页就是自己的页面，没有"第三方应用"的概念。
授权码流程只会多两次跳转和一次回调处理，却带不来任何安全收益
（令牌最终还是落在同一个前端里）。

> 反过来说：**如果将来要做"学生/家长用微信登录"或"校外系统接入"，就必须用授权码模式。**
> 密码模式的前提是"你完全信任这个客户端"，微信小程序这种环境就不满足。

---

## 10.2 动手步骤

### 步骤 1：在 `PreConfigureServices` 里配置 OpenIddict

注意是 **`PreConfigureServices`**（不是 `ConfigureServices`）——
ABP 的模块系统在预配置阶段收集 OpenIddict 的构建器配置，放错阶段会不生效。

```csharp
public override void PreConfigureServices(ServiceConfigurationContext context)
{
    var hostingEnvironment = context.Services.GetHostingEnvironment();

    // 资源服务器侧：校验令牌、确认受众
    PreConfigure<OpenIddictBuilder>(builder =>
    {
        builder.AddValidation(options =>
        {
            options.AddAudiences(XjglConsts.OpenIddictAudience);   // "Xjgl"
            options.UseLocalServer();     // 授权服务器就在本进程里
            options.UseAspNetCore();
        });
    });

    // 授权服务器侧：端点与允许的授权类型
    PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
    {
        serverBuilder
            .SetTokenEndpointUris("/connect/token")
            .SetUserInfoEndpointUris("/connect/userinfo")
            .SetIntrospectionEndpointUris("/connect/introspect")
            .SetRevocationEndpointUris("/connect/revoke")
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetEndSessionEndpointUris("/connect/logout");

        // 自有前端（PC / H5 / 小程序）用密码模式 + 刷新令牌；
        // 授权码模式留给后续接入的教育系统做 SSO。
        serverBuilder
            .AllowPasswordFlow()
            .AllowRefreshTokenFlow()
            .AllowAuthorizationCodeFlow()
            .AllowClientCredentialsFlow();

        serverBuilder.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Phone,
            OpenIddictConstants.Scopes.Roles,
            OpenIddictConstants.Scopes.OfflineAccess,   // ★ 见坑 1
            XjglConsts.ApiScope);
    });
```

### 步骤 2：开发环境用临时密钥（不要碰系统证书存储）

```csharp
    if (hostingEnvironment.IsDevelopment())
    {
        // 关掉 OpenIddict 自带的"开发证书"功能
        PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
        {
            options.AddDevelopmentEncryptionAndSigningCertificate = false;
        });

        PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
        {
            serverBuilder.AddEphemeralEncryptionKey();
            serverBuilder.AddEphemeralSigningKey();

            // OpenIddict 默认要求令牌端点必须走 HTTPS，
            // 本地 http://localhost 调试会被拒绝（表现为 /connect/token 报错）。
            serverBuilder.UseAspNetCore().DisableTransportSecurityRequirement();
        });
    }
    else
    {
        // 生产：从应用目录读证书文件（*.pfx 已在 .gitignore 里）
        PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
        {
            serverBuilder.AddProductionEncryptionAndSigningCertificate(
                "openiddict.pfx", "<口令从配置注入>");
        });
    }
}
```

### 步骤 3：注册作用域与客户端（种子数据）

`src/Xjgl.Domain/OpenIddict/OpenIddictDataSeedContributor.cs`：

```csharp
private async Task CreateScopesAsync()
{
    if (await _scopeRepository.FindByNameAsync(XjglConsts.ApiScope) == null)
    {
        await _scopeManager.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = XjglConsts.ApiScope,                 // "Xjgl"
            DisplayName = "河南省学籍管理系统 API",
            Resources = { XjglConsts.OpenIddictAudience }
        });
    }

    // ★ offline_access 必须显式注册成一个 Scope，
    //   否则客户端请求它会被判 invalid_scope，
    //   而拿不到 refresh_token 就意味着教师每 1 小时被踢一次登录。
    if (await _scopeRepository.FindByNameAsync(OpenIddictConstants.Scopes.OfflineAccess) == null)
    {
        await _scopeManager.CreateAsync(new OpenIddictScopeDescriptor
        {
            Name = OpenIddictConstants.Scopes.OfflineAccess,
            DisplayName = "离线访问（用于刷新登录状态）"
        });
    }
}

private async Task CreateApplicationsAsync()
{
    var commonScopes = new List<string>
    {
        OpenIddictConstants.Permissions.Scopes.Email,
        OpenIddictConstants.Permissions.Scopes.Phone,
        OpenIddictConstants.Permissions.Scopes.Profile,
        OpenIddictConstants.Permissions.Scopes.Roles,
        OpenIddictConstants.Scopes.OfflineAccess,
        XjglConsts.ApiScope
    };

    await CreateApplicationAsync(
        name: "Xjgl_App",
        type: OpenIddictConstants.ClientTypes.Public,          // 前端无法保密，是公共客户端
        consentType: OpenIddictConstants.ConsentTypes.Implicit, // 自有客户端，不弹同意页
        displayName: "学籍管理系统前端（PC / H5 / 小程序）",
        secret: null,                                          // 公共客户端没有密钥
        grantTypes: new List<string>
        {
            OpenIddictConstants.GrantTypes.AuthorizationCode,
            OpenIddictConstants.GrantTypes.Password,
            OpenIddictConstants.GrantTypes.RefreshToken
        },
        scopes: commonScopes,
        clientUri: "http://localhost:5173",
        redirectUri: "http://localhost:5173",
        postLogoutRedirectUri: "http://localhost:5173");
}
```

### 步骤 4：启动配置文件的对应节点

`appsettings.json`：

```json
{
  "AuthServer": {
    "Authority": "http://localhost:44300",
    "RequireHttpsMetadata": false,
    "SwaggerClientId": "Xjgl_Swagger"
  },
  "OpenIddict": {
    "Applications": {
      "Xjgl_App":    { "ClientId": "Xjgl_App",    "RootUrl": "http://localhost:5173" },
      "Xjgl_Swagger":{ "ClientId": "Xjgl_Swagger","RootUrl": "http://localhost:44300" }
    }
  }
}
```

### 步骤 5：让 `[Authorize]` 走 Bearer

```csharp
private void ConfigureAuthentication(ServiceConfigurationContext context)
{
    // ABP 10 已移除旧的 ForwardIdentityAuthenticationForBearer 扩展方法，
    // 直接把默认认证方案指向 OpenIddict 的令牌校验
    context.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

    Configure<AbpClaimsPrincipalFactoryOptions>(options =>
    {
        options.IsDynamicClaimsEnabled = true;
    });
}
```

`Program.cs` 里的中间件顺序（**顺序错了会 401**）：

```csharp
app.UseAbpRequestLocalization();
app.UseCorrelationId();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthentication();                       // ★ 必须在 UseAuthorization 之前
app.UseAbpOpenIddictValidation();              // ★ 令牌校验
app.UseUnitOfWork();
app.UseAuthorization();
app.UseAuditing();
app.UseAbpSerilogEnrichers();
app.UseConfiguredEndpoints();
```

---

## 10.3 验证方法

**① 拿令牌**

```powershell
curl.exe -s -X POST "http://localhost:44300/connect/token" `
  -H "Content-Type: application/x-www-form-urlencoded" `
  --data-urlencode "grant_type=password" `
  --data-urlencode "client_id=Xjgl_App" `
  --data-urlencode "username=admin" `
  --data-urlencode "password=1q2w3E*" `
  --data-urlencode "scope=Xjgl offline_access"
```

期望返回（节选）：

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIs...",
  "refresh_token": "CfDJ8...",
  "token_type": "Bearer",
  "expires_in": 3599,
  "scope": "Xjgl offline_access"
}
```

**三个必须检查的点**（只看"有没有 access_token"是不够的）：

| 检查 | 为什么 |
|---|---|
| `refresh_token` 存在 | 没有它，用户每小时被踢一次 |
| `scope` 里含 `Xjgl` | 不含的话调业务接口会 403（受众不匹配） |
| `expires_in` 是 3599 左右 | 如果是 300，说明配成了短令牌，前端要频繁刷新 |

**② 用令牌调业务接口**

```powershell
$token = (curl.exe -s -X POST ... | ConvertFrom-Json).access_token
curl.exe -s "http://localhost:44300/api/app/my-profile" -H "Authorization: Bearer $token"
```

期望返回当前用户的身份、角色、权限码与菜单树（第 12 章会详细讲这个接口）。

**③ 解码令牌看声明**（排查问题的利器）

把 `access_token` 粘到 <https://jwt.io>，或本地解：

```powershell
$payload = $token.Split('.')[1]
# base64url 补齐
$pad = $payload.Length % 4; if ($pad) { $payload += '=' * (4 - $pad) }
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Replace('-','+').Replace('_','/')) | ConvertFrom-Json
```

重点看 `aud`（必须是 `Xjgl`）与 `scope`。

---

## 10.4 踩坑记录

### 坑 1：登录成功但前端"每小时掉线一次"

**现象**：一切正常，就是过一阵子用户被要求重新登录。

**根因**：请求 token 时带了 `offline_access`，但服务端**没有把它注册成 Scope**，
于是 OpenIddict 判定 `invalid_scope`。为了不让登录直接失败，
有的实现会把 `offline_access` 悄悄丢掉 —— 结果就是"能登录、没有 refresh_token"。

**正确做法**：在 `CreateScopesAsync` 里显式 `CreateAsync` 一个名为 `offline_access` 的
`OpenIddictScopeDescriptor`（它**不需要 Resources**，只是一个开关），
并把它加进客户端的 scopes 列表。

### 坑 2：`AddDevelopmentEncryptionCertificate` 在受限环境抛异常

OpenIddict 的开发证书功能会打开 Windows 证书存储（`X509Store.Open()`）。
在容器或受限环境里会抛 `CryptographicException: 拒绝访问`，
**表现为每个请求都 500**，而且堆栈完全指不到"证书"两个字。

**处理**：开发环境显式关掉（`AddDevelopmentEncryptionAndSigningCertificate = false`）
并改用进程内临时密钥。代价是**重启后旧令牌全部失效** —— 开发环境可以接受，
生产必须用真实证书（`openiddict.pfx`）。

### 坑 3：`/connect/token` 直接返回错误，日志说"必须使用 HTTPS"

OpenIddict 默认要求令牌端点走 HTTPS。本地 `http://localhost` 调试时会被
`ValidateTransportSecurityRequirement` 拒绝。

**处理**：仅开发环境 `DisableTransportSecurityRequirement()`。
**生产环境务必保持启用**（前置 Nginx/网关做 TLS），否则令牌会以明文传输。

### 坑 4：默认管理员密码不是你以为的那个

ABP 的默认管理员密码是 **`1q2w3E*`**（大写 `E`），不是 `1q2w3e*`。
输错会得到 `invalid_grant`，而错误信息不会告诉你"密码里有个大写字母"。

### 坑 5：登录要 90 秒才返回（缓存与消息队列的锅）

这是本项目一个很值得记的坑，**现象与根因离得很远**：

- 现象：`POST /connect/token` 卡住不返回，90 秒超时；
- 日志：`StackExchange.Redis.RedisConnectionException: The message timed out in the backlog
  attempting to send because no connection became available (5000ms)`。

**根因**：一次登录要经过几十次缓存读写（OpenIddict 的应用/作用域缓存、
权限、设置、特性）。Redis 密码配错时，**每次操作都要等满 5 秒连接超时**，
几十次叠加起来就是"永远也返回不完"。

**关键认识**：降级逻辑本身是对的（都回落到了内存缓存），
但**"降级成功"不等于"用户可用"** —— 每次操作仍要付超时代价。

**修复**：容错缓存失败后开一个**跳过窗口**（默认 30 秒），窗口内直接走内存。

```csharp
private bool ShouldTryPrimary()
{
    var until = Interlocked.Read(ref _skipUntilTicks);
    return until == 0 || DateTimeOffset.UtcNow.Ticks >= until;
}
```

修复后：登录从"> 90 秒超时"降到 **8 秒**（含首次 5 秒超时），后续请求 6 秒。

> **剩下的 6 秒来自 RabbitMQ**：登录路径上发布的分布式事件会去连 MQ 并等连接超时。
> 彻底解决要做 Outbox（MQ 不可用时事件落库、后台重发），这个还没做。

### 坑 6：`PreConfigure` 写成 `Configure`

```csharp
// 无效：OpenIddict 的构建器在预配置阶段就被收集了
public override void ConfigureServices(ServiceConfigurationContext context)
{
    Configure<OpenIddictServerBuilder>(...);
}
```

**症状**：没有报错，只是配置完全不生效 —— 表现为"授权类型不被允许"或
"端点不存在"。

---

## 10.5 本章产出

| 项 | 值 |
|---|---|
| 端点 | `/connect/token`、`/connect/userinfo`、`/connect/introspect`、`/connect/revoke`、`/connect/authorize`、`/connect/logout` |
| 客户端 | `Xjgl_App`（公共客户端：密码 + 刷新 + 授权码） |
| 作用域 | `Xjgl`（API）、`offline_access`（刷新令牌）、`openid/profile/email/phone/roles` |
| 开发期 | 进程内临时密钥；关闭 HTTPS 强制（仅开发） |
| 验证 | 返回 `refresh_token`；`scope` 含 `Xjgl`；`expires_in ≈ 3599`；能用令牌调 `/api/app/my-profile` |

---

**上一章：** [09 混合 ORM 的事务](09-混合ORM的事务怎么共存.md) ｜ **下一章：** [11 元数据驱动的权限（一）：表怎么设计](../README.md)（待写）
