using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Caching;
using Volo.Abp.Caching.StackExchangeRedis;
using Volo.Abp.EventBus.RabbitMq;
using Volo.Abp.Identity.AspNetCore;
using Volo.Abp.Modularity;
using Volo.Abp.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.Swashbuckle;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;
using Xjgl.Caching;
using Xjgl.Data;
using Xjgl.EntityFrameworkCore;
using Xjgl.Photos;
using Xjgl.SqlSugarCore;

namespace Xjgl;

/// <summary>
/// 应用宿主：一个进程同时提供 API、OpenIddict 令牌端点与 Swagger，
/// 对应「全省统一部署、同一个入口登录」的要求。
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpCachingStackExchangeRedisModule),
    // ⚠ 这里**故意不注册** AbpEventBusRabbitMqModule —— 原因见 ConfigureEventBus 的注释：
    //   MQ 不可达时它会让框架表的写入静默回滚（接口返回 200、数据库里却没有）。
    //   重新接入的前置条件是先实现 Outbox，见「已知技术债」。
    typeof(AbpOpenIddictAspNetCoreModule),
    // 密码模式登录需要 SignInManager / UserManager 的 ASP.NET Core 集成
    typeof(AbpIdentityAspNetCoreModule),
    typeof(XjglHttpApiModule),
    typeof(XjglApplicationModule),
    typeof(XjglEntityFrameworkCoreModule),
    typeof(XjglSqlSugarCoreModule)
)]
public class XjglHttpApiHostModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                options.AddAudiences(XjglConsts.OpenIddictAudience);
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
        {
            serverBuilder
                .SetTokenEndpointUris("/connect/token")
                .SetUserInfoEndpointUris("/connect/userinfo")
                .SetIntrospectionEndpointUris("/connect/introspect")
                .SetRevocationEndpointUris("/connect/revoke")
                .SetAuthorizationEndpointUris("/connect/authorize")
                .SetEndSessionEndpointUris("/connect/logout");

            // 自有前端（PC / H5 / 小程序）使用密码模式 + 刷新令牌；
            // 授权码模式留给后续接入的教育系统（如学分系统）做 SSO。
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
                OpenIddictConstants.Scopes.OfflineAccess,
                XjglConsts.ApiScope);
        });

        if (hostingEnvironment.IsDevelopment())
        {
            // 开发环境使用进程内的临时密钥，**不读写 Windows 证书存储**。
            //
            // 原因：OpenIddict 的 AddDevelopmentEncryptionCertificate/SigningCertificate 会调用
            // X509Store.Open() 访问系统证书存储，在受限环境与容器里会抛
            //   CryptographicException: 拒绝访问
            // 表现为每个请求都 500。临时密钥的代价是重启后旧令牌失效，开发环境可以接受。
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddEphemeralEncryptionKey();
                serverBuilder.AddEphemeralSigningKey();

                // OpenIddict 默认要求令牌端点必须走 HTTPS，
                // 本地用 http://localhost 调试时会被 ValidateTransportSecurityRequirement 拒绝，
                // 表现为 /connect/token 直接返回错误响应。开发环境显式放行。
                // 生产环境务必保持启用（走 Nginx/网关的 TLS）。
                serverBuilder.UseAspNetCore().DisableTransportSecurityRequirement();
            });
        }
        else
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            // 生产环境从应用目录读取证书文件（不要提交到仓库，见 .gitignore 的 *.pfx）。
            // 证书口令通过配置或环境变量注入：OpenIddict:Certificate:Password
            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddProductionEncryptionAndSigningCertificate(
                    "openiddict.pfx",
                    "00000000-0000-0000-0000-000000000000");
            });
        }
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var hostingEnvironment = context.Services.GetHostingEnvironment();

        ConfigureAuthentication(context);
        ConfigureConventionalControllers();
        ConfigureDataProtection(context, hostingEnvironment);
        ConfigureResilientDistributedCache(context);
        ConfigureVirtualFileSystem(hostingEnvironment);
        ConfigureCors(context, configuration);
        ConfigureSwaggerServices(context, configuration);
        ConfigurePhotoCompensationWorker(context);

        Configure<AbpDistributedCacheOptions>(options =>
        {
            options.KeyPrefix = "Xjgl:";

            // 对应方案文档「熔断降级矩阵」：Redis 不可用时，缓存操作降级为“未命中”，
            // 由下层数据库兜底，而不是把异常抛给用户。
            options.HideErrors = true;
        });

        ConfigureEventBus();
    }

    /// <summary>
    /// 事件总线：**当前刻意不接 RabbitMQ**。
    ///
    /// 【为什么 —— 一个会造成静默数据丢失的缺陷，有完整证据】
    ///
    /// 实测（RabbitMQ 不可达时）：
    ///   - `POST /api/identity/users` 返回 **200 且带完整用户对象**；
    ///   - 库里查不到：`AbpUsers` 共 312 行，没有刚建的那个；
    ///   - 客户端同时收到 `curl: (18) transfer closed with outstanding read data remaining`。
    ///
    /// 宿主日志里的堆栈给出了准确链路：
    /// <code>
    /// RabbitMqDistributedEventBus.PublishAsync(...)
    ///   → DistributedEventBusBase.PublishAsync(...)
    ///   → UnitOfWorkEventPublisher.PublishDistributedEventsAsync(...)
    ///   → UnitOfWork.CompleteAsync(...)
    ///   → AbpUnitOfWorkMiddleware.InvokeAsync(HttpContext)
    /// </code>
    ///
    /// 也就是说：ABP 的工作单元是在**响应写出之后**才提交的，
    /// 而身份模块的用户/角色写操作会发布分布式事件；MQ 发布失败 →
    /// 工作单元提交失败 → 事务回滚 —— 但 HTTP 响应早已发出去，客户端只看到"成功"。
    /// **结果是"建用户"这类框架表写入静默丢失，而用户以为成功了。**
    /// （业务表不受影响：SqlSugar 的写入是即时提交，不挂在 ABP 的工作单元上。）
    ///
    /// 【当前取舍】
    /// 宁可暂时不接 MQ，也不能让写操作静默丢数据。当前代码里没有任何业务逻辑
    /// 依赖 MQ（照片上传是同步写 + 舱壁/熔断保护），所以移除它不损失功能，
    /// 反而顺带修掉了"登录要为 MQ 连接超时白等 5~6 秒"的问题。
    ///
    /// 【重新接入的前置条件】
    /// 必须先实现 **Outbox**（方案文档 11.4 / 熔断降级矩阵）：
    /// 事件与业务数据在同一个事务里落库，由后台任务重发；ABP 10 已内置
    /// <c>OutboxSender</c> / <c>InboxProcessor</c>（见 Volo.Abp.EventBus.Distributed）。
    /// 届时把 <c>AbpEventBusRabbitMqModule</c> 加回 DependsOn 并配置 Outbox 存储即可。
    /// </summary>
    private static void ConfigureEventBus()
    {
        // 暂时没有需要配置的内容。保留这个方法是为了给上面那段说明一个落点，
        // 也方便将来接 Outbox 时在这里补齐配置。
    }

    private void ConfigureConventionalControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            // 应用服务按约定自动生成 Controller：XxxAppService -> /api/app/xxx
            options.ConventionalControllers.Create(typeof(XjglApplicationModule).Assembly);
        });
    }

    /// <summary>
    /// 注册「降级照片回放」后台任务。
    ///
    /// 熔断降级保证上传不失败，但照片终究要回到 NAS；
    /// 这个任务负责在 NAS 恢复后把本地暂存的照片补传回去。
    /// 可通过 <c>Xjgl:Photo:CompensationEnabled=false</c> 关闭（例如只在某个实例上跑）。
    /// </summary>
    private static void ConfigurePhotoCompensationWorker(ServiceConfigurationContext context)
    {
        context.Services.AddHostedService<PhotoCompensationWorker>();
    }

    /// <summary>
    /// 数据保护密钥的存放位置。
    ///
    /// 默认行为是写到 %LOCALAPPDATA%\ASP.NET\DataProtection-Keys，
    /// 这在容器化部署（方案第 10 章：Linux + Docker/K8s）和受限环境下都会出问题：
    /// 容器重建后密钥丢失导致已签发的 Cookie / Token 全部失效，
    /// 受限环境则直接抛 CryptographicException「拒绝访问」。
    ///
    /// 因此改为落在应用目录下的 App_Data/DataProtection-Keys（该目录已在 .gitignore 中）。
    /// 生产环境如需多副本共享，可改为 PersistKeysToStackExchangeRedis 或挂载卷。
    /// </summary>
    private static void ConfigureDataProtection(
        ServiceConfigurationContext context,
        IWebHostEnvironment hostingEnvironment)
    {
        var keysDirectory = new DirectoryInfo(
            Path.Combine(hostingEnvironment.ContentRootPath, "App_Data", "DataProtection-Keys"));

        if (!keysDirectory.Exists)
        {
            keysDirectory.Create();
        }

        context.Services
            .AddDataProtection()
            .SetApplicationName("Xjgl")
            .PersistKeysToFileSystem(keysDirectory);
    }

    /// <summary>
    /// 把 IDistributedCache 换成容错装饰器：Redis 不可用时自动降级为进程内内存缓存。
    ///
    /// 为什么必须替换而不能只靠 AbpDistributedCacheOptions.HideErrors：
    /// 该开关只对 ABP 包装的 IDistributedCache&lt;T&gt; 生效，
    /// 而 SettingManagement / PermissionManagement / FeatureManagement 的内部初始化器
    /// 直接使用原生 IDistributedCache，Redis 抛异常会直接变成 500。
    /// </summary>
    private static void ConfigureResilientDistributedCache(ServiceConfigurationContext context)
    {
        // 先注册兜底实现（内存缓存），再注册主实现（Redis），最后由装饰器包住两者。
        // 注意注册顺序：后注册的才是解析 IDistributedCache 时拿到的那个。
        context.Services.AddDistributedMemoryCache();

        context.Services.AddSingleton<IDistributedCache>(serviceProvider =>
        {
            var redisOptions = serviceProvider.GetRequiredService<IOptions<RedisCacheOptions>>();
            var memoryOptions = serviceProvider.GetRequiredService<IOptions<MemoryDistributedCacheOptions>>();

            IDistributedCache primary = new RedisCache(redisOptions);
            IDistributedCache fallback = new MemoryDistributedCache(memoryOptions);

            return new ResilientDistributedCache(
                primary,
                fallback,
                serviceProvider.GetRequiredService<ILogger<ResilientDistributedCache>>(),
                // Redis 一旦失败就短路这么久，避免每次缓存操作都去撞 5 秒连接超时
                TimeSpan.FromSeconds(serviceProvider
                    .GetRequiredService<IConfiguration>()
                    .GetValue("Xjgl:Cache:SkipPrimarySeconds", 30)));
        });
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        // 把默认认证方案指向 OpenIddict 的令牌校验，配合 app.UseAbpOpenIddictValidation()
        // 让 [Authorize] 直接按 Bearer Token 鉴权。
        // （ABP 10 已移除旧的 ForwardIdentityAuthenticationForBearer 扩展方法）
        context.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });
    }

    private void ConfigureVirtualFileSystem(IWebHostEnvironment hostingEnvironment)
    {
        if (!hostingEnvironment.IsDevelopment())
        {
            return;
        }

        // 开发期直接从物理目录读取本地化 JSON，改完立即生效，无需重新编译
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            var root = hostingEnvironment.ContentRootPath;
            var physicalPath = Path.GetFullPath(Path.Combine(root, "..", "Xjgl.Domain.Shared"));

            // 物理目录不存在时**必须跳过**，否则 ABP 解析 IVirtualFileProvider 会抛
            // DirectoryNotFoundException，直接把整个应用带崩 —— 而这个映射本来就只是
            // 开发期的便利功能。什么情况下会不存在：直接用 bin 目录里的 exe 启动、
            // 或者把发布产物拷贝到别处运行（此时 ContentRoot 不是源码目录）。
            if (!Directory.Exists(physicalPath))
            {
                return;
            }

            options.FileSets.ReplaceEmbeddedByPhysical<XjglDomainSharedModule>(physicalPath);
        });
    }

    private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder
                    .WithOrigins(configuration["App:CorsOrigins"]?
                        .Split(",", StringSplitOptions.RemoveEmptyEntries)
                        .Select(o => o.RemovePostFix("/"))
                        .ToArray() ?? Array.Empty<string>())
                    .WithAbpExposedHeaders()
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private static void ConfigureSwaggerServices(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddAbpSwaggerGenWithOAuth(
            configuration["AuthServer:Authority"]!,
            new Dictionary<string, string>
            {
                { XjglConsts.ApiScope, "河南省学籍管理系统 API" }
            },
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "河南省学籍管理系统 API",
                    Version = "v1",
                    Description = "ABP 10.6 + SqlSugar（业务）/ EF Core（框架）+ PostgreSQL"
                });
                options.DocInclusionPredicate((_, _) => true);
                options.CustomSchemaIds(type => type.FullName);
            });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        // 说明：本宿主不引用 MVC UI 主题包（前端为独立的 Vue 应用），
        // 因此不使用 ABP 的 UseErrorPage()，非开发环境由异常处理中间件统一返回 ProblemDetails。
        if (!env.IsDevelopment())
        {
            app.UseExceptionHandler();
        }

        app.UseCorrelationId();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (MultiTenancyConsts.IsEnabled)
        {
            app.UseMultiTenancy();
        }

        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "河南省学籍管理系统 API");

            var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
            options.OAuthClientId(configuration["AuthServer:SwaggerClientId"]);
            options.OAuthScopes(XjglConsts.ApiScope);
        });

        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        app.UseConfiguredEndpoints();
    }
}
