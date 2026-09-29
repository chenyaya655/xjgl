using System.Text.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OpenIddict.Abstractions;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.OpenIddict.Applications;
using Volo.Abp.OpenIddict.Scopes;
using Volo.Abp.Uow;
using Xjgl.Sso;

namespace Xjgl.OpenIddict;

/// <summary>
/// 初始化 OpenIddict 的 Scope 与客户端应用，让前端（PC / H5 / 小程序）
/// 以及后续接入的教育系统（如学分系统）能够换取访问令牌。
///
/// 客户端清单配置在 appsettings 的 OpenIddict:Applications 节点，
/// 因此新增一个接入方只需要改配置 + 重跑 DbMigrator，不需要改代码。
/// </summary>
public class OpenIddictDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IConfiguration _configuration;
    private readonly IOpenIddictApplicationRepository _openIddictApplicationRepository;
    private readonly IAbpApplicationManager _applicationManager;
    private readonly IOpenIddictScopeRepository _openIddictScopeRepository;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IStringLocalizer<OpenIddictResponse> L;

    public OpenIddictDataSeedContributor(
        IConfiguration configuration,
        IOpenIddictApplicationRepository openIddictApplicationRepository,
        IAbpApplicationManager applicationManager,
        IOpenIddictScopeRepository openIddictScopeRepository,
        IOpenIddictScopeManager scopeManager,
        IStringLocalizer<OpenIddictResponse> l)
    {
        _configuration = configuration;
        _openIddictApplicationRepository = openIddictApplicationRepository;
        _applicationManager = applicationManager;
        _openIddictScopeRepository = openIddictScopeRepository;
        _scopeManager = scopeManager;
        L = l;
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        await CreateScopesAsync();
        await CreateApplicationsAsync();
    }

    private async Task CreateScopesAsync()
    {
        if (await _openIddictScopeRepository.FindByNameAsync(XjglConsts.ApiScope) == null)
        {
            await _scopeManager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = XjglConsts.ApiScope,
                DisplayName = "河南省学籍管理系统 API",
                Resources = { XjglConsts.OpenIddictAudience }
            });
        }

        // offline_access 必须显式注册成一个 Scope，否则客户端请求它会被判为
        // invalid_scope —— 而拿不到 refresh_token 就意味着教师每 1 小时被踢一次登录。
        // 它本身不携带任何资源（Resources 为空），只是「允许发刷新令牌」的开关。
        if (await _openIddictScopeRepository.FindByNameAsync(OpenIddictConstants.Scopes.OfflineAccess) == null)
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

        var section = _configuration.GetSection("OpenIddict:Applications");

        // ---- PC 管理端 / 移动端 H5 / 微信小程序：同一个公共客户端 ----
        var appClientId = section["Xjgl_App:ClientId"];
        if (!appClientId.IsNullOrWhiteSpace())
        {
            var appRootUrl = section["Xjgl_App:RootUrl"]?.TrimEnd('/');
            await CreateApplicationAsync(
                name: appClientId!,
                type: OpenIddictConstants.ClientTypes.Public,
                consentType: OpenIddictConstants.ConsentTypes.Implicit,
                displayName: "学籍管理系统前端（PC / H5 / 小程序）",
                secret: null,
                grantTypes: new List<string>
                {
                    OpenIddictConstants.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.GrantTypes.Password,
                    OpenIddictConstants.GrantTypes.RefreshToken
                },
                scopes: commonScopes,
                clientUri: appRootUrl,
                redirectUri: appRootUrl,
                postLogoutRedirectUri: appRootUrl
            );
        }

        // ---- Swagger UI 调试用 ----
        var swaggerClientId = section["Xjgl_Swagger:ClientId"];
        if (!swaggerClientId.IsNullOrWhiteSpace())
        {
            var swaggerRootUrl = section["Xjgl_Swagger:RootUrl"]?.TrimEnd('/');
            await CreateApplicationAsync(
                name: swaggerClientId!,
                type: OpenIddictConstants.ClientTypes.Public,
                consentType: OpenIddictConstants.ConsentTypes.Implicit,
                displayName: "Swagger 调试客户端",
                secret: null,
                grantTypes: new List<string> { OpenIddictConstants.GrantTypes.AuthorizationCode },
                scopes: commonScopes,
                clientUri: swaggerRootUrl,
                redirectUri: $"{swaggerRootUrl}/swagger/oauth2-redirect.html"
            );
        }

        // ---- 教育系统内其它业务系统（学分系统等）作为授权码流程的 RP ----
        await CreateEducationResourcePartnersAsync(commonScopes);
    }

    /// <summary>
    /// 把教育系统内其它业务系统（学分系统、其它教育业务系统）种成授权码流程的 RP。
    ///
    /// 【为什么单列一节配置，而不是并进上面的 Applications】
    /// 上面那两个是「本系统自己的前端 / Swagger 调试客户端」，授权方式（密码模式 + 刷新令牌）
    /// 与接入语义都是固定的；而 RP 是**外部系统**：它们的 ClientType、密钥、回跳地址、
    /// 以及最关键的 FrontChannelLogoutUri 都各不相同。混在一节里会让"新增一个接入方"
    /// 变成"改一段带业务判断的代码"，而这正是本节要避免的。
    ///
    /// 【为什么这里能承诺"只改配置"】
    /// 遍历配置子节点而非硬编码键名，所以新增 RP 不需要动代码。
    /// </summary>
    private async Task CreateEducationResourcePartnersAsync(List<string> commonScopes)
    {
        var section = _configuration.GetSection(SsoPartnerConsts.ConfigSection);

        foreach (var partner in section.GetChildren())
        {
            // 跳过 _说明 之类的注释节点。本仓库的配置约定是用下划线开头的键写说明
            // （见 Xjgl:Photo 一节），那些键没有 ClientId，不显式跳过也无害，
            // 但会让"配置里到底有几个 RP"变得难以判断。
            if (partner.Key.StartsWith('_'))
            {
                continue;
            }

            var clientId = partner["ClientId"];
            if (clientId.IsNullOrWhiteSpace())
            {
                continue;
            }

            var rootUrl = partner["RootUrl"]?.TrimEnd('/');

            var clientType = partner["ClientType"].IsNullOrWhiteSpace()
                ? OpenIddictConstants.ClientTypes.Public
                : partner["ClientType"]!;

            await CreateApplicationAsync(
                name: clientId!,
                type: clientType,
                consentType: OpenIddictConstants.ConsentTypes.Implicit,
                displayName: partner["DisplayName"] ?? clientId,
                secret: partner["ClientSecret"],
                grantTypes: ResolvePartnerGrantTypes(partner),
                scopes: commonScopes,
                clientUri: rootUrl,
                redirectUri: ResolvePartnerUri(partner["RedirectUri"], rootUrl, SsoPartnerConsts.DefaultRedirectPath),
                postLogoutRedirectUri: ResolvePartnerUri(
                    partner["PostLogoutRedirectUri"], rootUrl, SsoPartnerConsts.DefaultPostLogoutRedirectPath),
                frontChannelLogoutUri: ResolvePartnerUri(
                    partner[SsoPartnerConsts.FrontChannelLogoutUriKey], rootUrl, SsoPartnerConsts.DefaultFrontChannelLogoutPath));
        }
    }

    /// <summary>
    /// 取某个接入方允许的授权类型；配置里没写时给默认的「授权码 + 刷新令牌」。
    ///
    /// 【为什么这个要可配置 —— 为将来的小程序留口子】
    /// 微信小程序拿不到浏览器会话，走的是自定义授权类型（<c>urn:wechat:miniprogram</c>），
    /// 不能再用授权码模式。下面的 <c>CreateApplicationAsync</c> 对未知授权类型的
    /// <c>default</c> 分支本来就会补上 <c>Permissions.Prefixes.GrantType + 值</c>，
    /// 所以将来接小程序**只要改配置**（把预留项的键名去掉前导下划线、写好 GrantTypes），
    /// 不需要动种子框架 —— 这正是"只改配置"这句承诺能否兑现的关键一处。
    /// </summary>
    private static List<string> ResolvePartnerGrantTypes(IConfigurationSection partner)
    {
        var configured = partner.GetSection("GrantTypes").Get<string[]>();

        if (configured is { Length: > 0 })
        {
            return configured.ToList();
        }

        return new List<string>
        {
            OpenIddictConstants.GrantTypes.AuthorizationCode,
            OpenIddictConstants.GrantTypes.RefreshToken
        };
    }

    /// <summary>
    /// 取接入方地址：优先用显式配置，否则按约定路径从 RootUrl 推导。
    ///
    /// 推导只为了"占位也能跑通"，真实联调必须显式配置 —— 猜出来的回跳地址
    /// 在对方那里一定是 404，而现象只是"登出后停在白页"，很难定位到根因。
    /// </summary>
    private static string? ResolvePartnerUri(string? configured, string? rootUrl, string defaultPath)
    {
        if (!configured.IsNullOrWhiteSpace())
        {
            return configured;
        }

        return rootUrl.IsNullOrWhiteSpace() ? null : rootUrl + defaultPath;
    }

    private async Task CreateApplicationAsync(
        [NotNull] string name,
        [NotNull] string type,
        [NotNull] string consentType,
        string? displayName,
        string? secret,
        List<string> grantTypes,
        List<string> scopes,
        string? clientUri = null,
        string? redirectUri = null,
        string? postLogoutRedirectUri = null,
        string? frontChannelLogoutUri = null)
    {
        if (!string.IsNullOrEmpty(secret) &&
            string.Equals(type, OpenIddictConstants.ClientTypes.Public, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(L["NoClientSecretCanBeSetForPublicApplications"]);
        }

        if (string.IsNullOrEmpty(secret) &&
            string.Equals(type, OpenIddictConstants.ClientTypes.Confidential, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(L["TheClientSecretIsRequiredForConfidentialApplications"]);
        }

        var client = await _openIddictApplicationRepository.FindByClientIdAsync(name);

        var application = new AbpApplicationDescriptor
        {
            ClientId = name,
            ClientType = type,
            ClientSecret = secret,
            ConsentType = consentType,
            DisplayName = displayName,
            ClientUri = clientUri
        };

        Check.NotNullOrEmpty(grantTypes, nameof(grantTypes));
        Check.NotNullOrEmpty(scopes, nameof(scopes));

        foreach (var grantType in grantTypes)
        {
            switch (grantType)
            {
                case OpenIddictConstants.GrantTypes.AuthorizationCode:
                    application.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode);
                    application.Permissions.Add(OpenIddictConstants.Permissions.ResponseTypes.Code);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Authorization);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Revocation);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Introspection);
                    break;

                case OpenIddictConstants.GrantTypes.Password:
                    application.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.Password);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Revocation);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Introspection);
                    break;

                case OpenIddictConstants.GrantTypes.RefreshToken:
                    application.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.RefreshToken);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Revocation);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Introspection);
                    break;

                case OpenIddictConstants.GrantTypes.ClientCredentials:
                    application.Permissions.Add(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Introspection);
                    break;

                default:
                    // 自定义授权类型（如微信小程序 urn:wechat:miniprogram）
                    application.Permissions.Add(OpenIddictConstants.Permissions.Prefixes.GrantType + grantType);
                    application.Permissions.Add(OpenIddictConstants.Permissions.Endpoints.Token);
                    break;
            }
        }

        var builtInScopes = new[]
        {
            OpenIddictConstants.Permissions.Scopes.Address,
            OpenIddictConstants.Permissions.Scopes.Email,
            OpenIddictConstants.Permissions.Scopes.Phone,
            OpenIddictConstants.Permissions.Scopes.Profile,
            OpenIddictConstants.Permissions.Scopes.Roles
        };

        foreach (var scope in scopes)
        {
            application.Permissions.Add(
                builtInScopes.Contains(scope)
                    ? scope
                    : OpenIddictConstants.Permissions.Prefixes.Scope + scope);
        }

        if (!redirectUri.IsNullOrWhiteSpace())
        {
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
            {
                throw new BusinessException(L["InvalidRedirectUri", redirectUri!]);
            }

            if (application.RedirectUris.All(x => x != uri))
            {
                application.RedirectUris.Add(uri);
            }
        }

        if (!postLogoutRedirectUri.IsNullOrWhiteSpace())
        {
            if (!Uri.TryCreate(postLogoutRedirectUri, UriKind.Absolute, out var uri) ||
                !uri.IsWellFormedOriginalString())
            {
                throw new BusinessException(L["InvalidPostLogoutRedirectUri", postLogoutRedirectUri!]);
            }

            if (application.PostLogoutRedirectUris.All(x => x != uri))
            {
                application.PostLogoutRedirectUris.Add(uri);
            }
        }

        // 前通道登出地址。它是「一处登出、所有 RP 全部下线」的投递地址，
        // 因此这里**必须校验**而不是原样存下：一个拼错的地址不会让任何流程报错，
        // 只会让登出通知打到不存在的站点，表现为"登出后 RP 侧会话仍然活着"。
        Uri? frontChannelLogout = null;
        if (!frontChannelLogoutUri.IsNullOrWhiteSpace())
        {
            if (!Uri.TryCreate(frontChannelLogoutUri, UriKind.Absolute, out var uri) ||
                !uri.IsWellFormedOriginalString())
            {
                throw new BusinessException(
                    message: $"FrontChannelLogoutUri 必须是合法的绝对地址，当前值为：{frontChannelLogoutUri}");
            }

            frontChannelLogout = uri;
            application.FrontChannelLogoutUri = uri;
        }

        if (client == null)
        {
            await _applicationManager.CreateAsync(application);
            return;
        }

        // 已存在则对比并更新，保证改配置后重跑 DbMigrator 能生效（幂等）
        var newRedirectUris = JsonSerializer.Serialize(
            application.RedirectUris.Select(u => u.ToString().TrimEnd('/')));
        var newPostLogoutUris = JsonSerializer.Serialize(
            application.PostLogoutRedirectUris.Select(u => u.ToString().TrimEnd('/')));
        var newPermissions = JsonSerializer.Serialize(application.Permissions.Select(p => p.ToString()));

        // 前通道登出地址是**单值**（一个客户端只有一个投递地址），所以与上面几个
        // JSON 数组列不同：直接比字符串，不做序列化再比较。
        var newFrontChannelLogoutUri = frontChannelLogout?.ToString();

        var changed = false;
        if (client.RedirectUris != newRedirectUris)
        {
            client.RedirectUris = newRedirectUris;
            changed = true;
        }

        if (client.PostLogoutRedirectUris != newPostLogoutUris)
        {
            client.PostLogoutRedirectUris = newPostLogoutUris;
            changed = true;
        }

        if (client.Permissions != newPermissions)
        {
            client.Permissions = newPermissions;
            changed = true;
        }

        if (client.FrontChannelLogoutUri != newFrontChannelLogoutUri)
        {
            client.FrontChannelLogoutUri = newFrontChannelLogoutUri;
            changed = true;
        }

        if (changed)
        {
            await _applicationManager.UpdateAsync(client.ToModel());
        }
    }
}
