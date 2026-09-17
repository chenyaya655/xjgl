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

        // ---- 预留：教育系统内其它系统（如学分系统）作为第三方接入 ----
        // 需要时在 appsettings 的 OpenIddict:Applications 下加一节即可，无需改代码。
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
        string? postLogoutRedirectUri = null)
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

        if (changed)
        {
            await _applicationManager.UpdateAsync(client.ToModel());
        }
    }
}
