using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using System.Net;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using StackExchange.Redis;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Caching;
using Volo.Abp.Caching.StackExchangeRedis;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.RabbitMq;
using Volo.Abp.Identity;
using Volo.Abp.Identity.AspNetCore;
using Volo.Abp.Modularity;
using Volo.Abp.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.Swashbuckle;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;
using Xjgl.Application.RateLimiting;
using Xjgl.Caching;
using Xjgl.Data;
using Xjgl.EntityFrameworkCore;
using Xjgl.Exporting;
using Xjgl.Host.RateLimiting;
using Xjgl.Imaging;
using Xjgl.Photos;
using Xjgl.RateLimiting;
using Xjgl.Reporting;
using Xjgl.SqlSugarCore;
using Xjgl.Sso;

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
    typeof(XjglSqlSugarCoreModule),
    // PDF 渲染（学籍卡 / 花名册）：独立项目，只有 Host 需要把它装配进来
    typeof(XjglReportingModule),
    // 图像处理（照片缩略图）：同上
    typeof(XjglImagingModule)
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
        ConfigureRedis(configuration);
        ConfigureVirtualFileSystem(hostingEnvironment);
        ConfigureCors(context, configuration);
        ConfigureSwaggerServices(context, configuration);
        ConfigurePhotoCompensationWorker(context);
        ConfigurePhotoThumbnailWorker(context);
        ConfigureExportTaskWorker(context);
        ConfigurePermissionDelegationWorker(context);
        ConfigureRateLimiting(context);

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
    /// 事件总线：**已接入 Outbox，但 RabbitMQ 仍未注册**（债 4 的前置条件已完成）。
    ///
    /// 【背景：为什么之前不能接 MQ —— 一个会造成静默数据丢失的缺陷，有完整证据】
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
    ///
    /// 【本轮做了什么：把那个前置条件补上】
    /// 在 `XjglDbContext` 上启用 **Outbox + Inbox**（`IHasEventOutbox` / `IHasEventInbox`
    /// + 迁移 `AddEventOutboxInbox`）。开启之后：
    ///   - 分布式事件与业务数据在**同一个 EF Core 事务**里写入 `AbpEventOutbox`；
    ///   - 由 ABP 自带的 `OutboxSender` 后台任务重发到 broker；
    ///   - **broker 不可达不再影响业务写入** —— 事件只是"稍后重发"。
    /// 这正是当初"必须先做 Outbox 才能接 MQ"的那件事。
    ///
    /// 【为什么这次仍然不注册 <c>AbpEventBusRabbitMqModule</c>】
    /// 注册它需要现场有可达的 broker，否则启动与登录会为连接超时白等 5~6 秒
    /// （这是当初移除它的另一个原因，与本机环境一致：dev 的 RabbitMQ 未被依赖）。
    /// 现在的状态是"前置条件已具备、随时可接线"：加回 DependsOn 即可，
    /// 配置项（`RabbitMQ:Connections/EventBus`）与容器都已经在 compose 里。
    /// 这样取舍是刻意的：**没有业务逻辑依赖 MQ**，而接线需要现场有 broker 才能验证 ——
    /// 不做"配了但没验过"的接线。
    /// </summary>
    private void ConfigureEventBus()
    {
        // Outbox / Inbox 的注册在 `XjglEntityFrameworkCoreModule`（数据访问层的能力，
        // 集成测试也必须走到它）。这里只留说明与将来的 broker 接线点。
        //
        // 要接 RabbitMQ 时只需两步（前置条件已具备）：
        //   1) 回到模块的 DependsOn 里加回 `typeof(AbpEventBusRabbitMqModule)`；
        //   2) 确认 `RabbitMQ:Connections` / `RabbitMQ:EventBus` 配置指向可达的 broker。
        // compose 里已经有 rabbitmq 服务与对应环境变量，`.env.example` 也齐。
        //
        // 不接线的原因：**没有业务逻辑依赖 MQ**，而接线必须现场有 broker 才能验证。
        // 不做"配了但没验过"的接线 —— 那正是本仓库一直在避免的交付方式。
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
    /// 限流：用 Redis Lua 的**原子**实现替换按约定注册的进程内实现。
    ///
    /// 【为什么必须 RemoveAll 而不是直接 AddTransient】
    /// ABP 会按约定给 <c>ITokenBucketRateLimiter</c> 注册一份（就是那个进程内闸门的实现）。
    /// 两条注册同时存在时"谁生效"取决于模块加载与注册的先后 —— 那是一个隐式约定，
    /// 而它站错边的表现是"多实例部署时限流被放大 N 倍"：**单实例永远看不出来**，
    /// 扩容之后才暴露成"NAS 被压垮"，极难回溯。所以显式清空再注册。
    ///
    /// 被清掉的实现仍被注册为具体类型：Lua 实现拿它当**降级路径**
    /// （Redis 不可用或脚本执行失败时退回它，而不是干脆放行）。
    /// </summary>
    private void ConfigureRateLimiting(ServiceConfigurationContext context)
    {
        context.Services.RemoveAll<ITokenBucketRateLimiter>();

        context.Services.AddTransient<DistributedTokenBucketRateLimiter>();
        context.Services.AddTransient<ITokenBucketRateLimiter, RedisLuaTokenBucketRateLimiter>();

        // 限流在应用服务里是抛 BusinessException 的，而 ABP 默认把它映射成 403。
        // 403 会让前端提示"没有权限"—— 而实际情况是"慢一点再来"，用户会去找管理员要权限。
        // 映成 429 才能让前端按"稍后重试"处理。
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(XjglDomainErrorCodes.RateLimitExceeded, HttpStatusCode.TooManyRequests);
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
    /// 注册「缩略图补齐」后台任务。
    ///
    /// 缩略图生成移出上传请求（债 18）之后，这里是**唯一生成缩略图的地方** ——
    /// 少了它，新上传的照片与老照片都会永远没有缩略图（列表页一直加载原图）。
    /// 可通过 <c>Xjgl:Photo:ThumbnailEnabled=false</c> 关闭（例如只在某个实例上跑）。
    /// </summary>
    private static void ConfigurePhotoThumbnailWorker(ServiceConfigurationContext context)
    {
        context.Services.AddHostedService<PhotoThumbnailWorker>();
    }

    /// <summary>
    /// 注册「导出/打印任务」后台执行器。
    ///
    /// 本批把导出、打印、照片打包从"同步返回文件"改成"任务 + 下载链接"之后，
    /// 这里是**唯一真正生成文件的地方** —— 少了它，所有导出任务会永远停在 Pending。
    /// 可通过 <c>Xjgl:ExportTask:Enabled=false</c> 关闭（例如只想让某一个实例跑，
    /// 避免多实例抢同一批任务）。
    /// </summary>
    private static void ConfigureExportTaskWorker(ServiceConfigurationContext context)
    {
        context.Services.AddHostedService<ExportTaskWorker>();
    }

    /// <summary>
    /// 注册「权限委派到期回收」后台任务（ROADMAP 第 19 条）。
    ///
    /// **少了它，一条到期的委派会永远生效**：委派靠 ABP 的用户级授权生效，
    /// 而 `[Authorize]` 只认授权存储、不认本项目的台账表 ——
    /// 只在查询时过滤台账等于没回收（台账上还显示"已到期"，更具误导性）。
    /// </summary>
    private static void ConfigurePermissionDelegationWorker(ServiceConfigurationContext context)
    {
        context.Services.AddHostedService<Xjgl.Permissions.PermissionDelegationExpiryWorker>();
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
    /// 把 <c>Redis:Password</c> 应用到 ABP 的缓存连接上。
    ///
    /// 【为什么口令不能只写在 Configuration 字符串里】
    /// StackExchange.Redis 的连接串以**逗号**分隔参数，而本项目 dev 环境的 Redis 口令
    /// 本身就含逗号（<c>Qq123456,</c>）。写成 <c>password=Qq123456,,abortConnect=false</c>
    /// 会被解析成 <c>Qq123456</c> → WRONGPASS，而报错只说"认证失败"，
    /// 完全看不出根因是"逗号被当成了分隔符"。
    /// <c>RedisCacheOptions.ConfigurationOptions</c> 优先级高于 <c>Configuration</c> 字符串，
    /// 所以在这里用对象形式把口令设进去。
    ///
    /// ⚠ 不配这一段时缓存会**静默降级为内存**（日志只说"分布式缓存主实现不可用"），
    ///   而限流走的是另一条连接 —— 于是出现"限流用了 Redis、缓存却没用"的半吊子状态，
    ///   从日志上很难看出来。两者必须用同一份配置。
    /// </summary>
    private void ConfigureRedis(IConfiguration configuration)
    {
        var password = configuration["Redis:Password"];

        if (password.IsNullOrWhiteSpace())
        {
            return;
        }

        var raw = configuration["Redis:Configuration"];

        Configure<RedisCacheOptions>(options =>
        {
            var parsed = raw.IsNullOrWhiteSpace()
                ? new ConfigurationOptions()
                : ConfigurationOptions.Parse(raw!);

            parsed.Password = password;

            // 启动瞬间 Redis 未就绪时不抛异常，由后台自动重连 ——
            // 容器编排里"应用先起、Redis 后起"是常态
            parsed.AbortOnConnectFail = false;

            options.ConfigurationOptions = parsed;
        });
    }

    /// <summary>
    /// 把 IDistributedCache 换成容错装饰器：Redis 不可用时自动降级为进程内内存缓存。
    ///
    /// 为什么必须替换而不能只靠 AbpDistributedCacheOptions.HideErrors：
    /// 该开关只对 ABP 包装的 IDistributedCache&lt;T&gt; 生效，
    /// 而 SettingManagement / PermissionManagement / FeatureManagement 的内部初始化器
    /// 直接使用原生 IDistributedCache，Redis 抛异常会直接变成 500。
    /// </summary>
    private static void ConfigureResilientDistributedCache(ServiceConfigurationContext context)    {
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

        // ── 登录链路：把"挑战"指向 Identity 应用 Cookie ──
        //
        // 【这一步修的是什么】授权端点发现"浏览器未登录"时要 302 到登录页，
        // 而 ASP.NET Core 的 Challenge() 用的是 **DefaultChallengeScheme**。
        // 本 Host 原本只设了 DefaultAuthenticateScheme（OpenIddict 令牌校验），
        // 于是 Challenge 落到一个**没有重定向能力**的方案上，浏览器拿到的是**裸 401**
        // 而不是登录页 —— 这就是"SSO 浏览器交互式登录未接线"的直接原因。
        // 改完之后实测：`GET /connect/authorize`（未登录）**302 → /sso/login**，
        // 而不是 401。背景见 docs/学籍管理系统-总体方案.md §17.3 / §17.8。
        //
        // 【为什么 DefaultAuthenticateScheme 不动】
        // 它必须保持 OpenIddict 令牌校验：`/api` 上的 [Authorize] 失败要仍是 **401**
        // （Bearer 客户端要的是 401，不是 HTML 登录页）。实测确认没被这次改动影响。
        context.Services.Configure<AuthenticationOptions>(options =>
        {
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        });

        // ⚠ 这里**刻意不设** `DefaultSignInScheme`，**也刻意不改** `LoginPath`。
        //
        // 我一度把两者都指到 Identity 应用 Cookie 与前端 /login，跑起来才发现那是一段
        // **走不通的路**：前端的登录页走的是 OpenIddict **密码模式**，它只签出一个
        // Bearer 令牌，**不建立 Identity 应用 Cookie**；而授权端点认的恰恰是 Cookie。
        // 实测表现（07-sso.spec.ts 的浏览器链路）：
        //   登录页登录成功 → 浏览器带着参数回到 /connect/authorize
        //   → 授权端点仍然"未登录" → 再次 302 到 /sso/login → 无限往返、最终停在登录页。
        // 也就是说那两个配置只会把 401 换成一个**看起来在跳转、实际永远进不去**的状态，
        // 比裸 401 更难查，所以撤回。
        //
        // 【真正打通还差什么】（下一步，有明确做法，本轮不做）
        //   SSO 需要的是一次"在浏览器里建立 Identity 会话"的登录，与密码模式并存：
        //     a) 加 `POST /api/sso/session`：接收用户名/口令 → `SignInManager.PasswordSignInAsync`
        //        （它会写出 Identity 应用 Cookie）→ 把 `sid` 放进会话声明
        //        （ABP 的 AuthorizeController 会把 sid 复制进令牌，见 SsoConstants.SessionIdClaimName）；
        //     b) 前端登录页在**带 returnUrl** 时改调该端点（普通登录继续走密码模式，互不影响）；
        //     c) 再把 `ConfigureApplicationCookie(o => o.LoginPath = SsoConstants.LoginPath)`
        //        加上，让未经 SPA 的裸浏览器访问也能被送到登录页。
        //   注意 (a) 的 Cookie 必须落在**前端同源**的域上，否则浏览器根本不会把它带回来；
        //   本机是 5173（Vite 代理）→ 44300，需要确认 `localhost` 与 `127.0.0.1` 不被当成两个站点。

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

        // ⚠ 健康检查必须挂在**管道最前面**，而不是与其它端点一起挂在最后。
        //
        // 实测踩过：数据库不可达时，ABP 的请求本地化中间件会去读"租户设置"（要连数据库），
        // 于是它在管道中途就抛 500 —— 探针根本走不到我们写的端点，
        // 拿到的是一页异常堆栈，而不是"503 数据库不可用"。
        // 一个只会返回 200/500 的探针是没有意义的（容器与负载均衡都据此判断是否放流量）。
        MapHealthEndpoint(app);

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

        // ⚠ `MultiTenancyConsts.IsEnabled` 是**编译期常量 false**（本系统一省一套，
        //   多租户是明确的非目标，见 ROADMAP「非目标」一节），因此编译器会判定
        //   这个 if 的主体是"无法访问的代码"并给出 CS0162。
        //
        //   这里**保留这个调用**是有意的：ABP 的官方装配骨架里就有它，
        //   留着能让"要不要开多租户"这件事保持一行开关的形状（改常量即可），
        //   而不是让下一个人去猜"是不是漏了 UseMultiTenancy"。
        //   用局部 pragma 把那一条警告消掉 —— 它的信息量是零，但会混进
        //   构建输出里，让真正需要看的警告更难被看见。
#pragma warning disable CS0162 // 无法访问的代码：多租户在编译期常量上被有意关闭
        if (MultiTenancyConsts.IsEnabled)
        {
            app.UseMultiTenancy();
        }
#pragma warning restore CS0162

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

    /// <summary>
    /// 健康检查端点（匿名，**终止式中间件**）。
    ///
    /// 【为什么必须自己加一个】
    /// 生产编排里 `depends_on: condition: service_healthy` 与容器 healthcheck 都要有个能探的地址。
    /// 而本系统的接口**全都要求令牌**：拿一个需要鉴权的接口当探针，
    /// 只能靠"返回 401 也算活着"这种脆弱约定。因此给一个明确语义的端点：
    ///   - 200：进程活着 **且数据库连得上**；
    ///   - 503：数据库连不上（此时实例不该被放进负载均衡）。
    ///
    /// 【为什么用裸中间件而不是 MapGet】
    /// 端点路由在 ABP 的一整套中间件之后；而数据库不可用时，前序的请求本地化中间件
    /// 自己就会抛 500，探针永远拿不到我们的 503（实测踩过，见 OnApplicationInitialization 注释）。
    /// 裸中间件能在管道最前面把 /health 拦下来，与数据库状态无关地给出确定答案。
    ///
    /// 注意这里**刻意不检查 Redis**：Redis 不可用是本系统设计上允许的降级
    /// （缓存与限流都会退到内存），把它算作"不健康"会让整个站点被摘掉 ——
    /// 那是把"部分降级"升级成了"全站不可用"。
    /// </summary>
    private static void MapHealthEndpoint(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                || !context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var databaseOk = false;

            try
            {
                var db = context.RequestServices.GetRequiredService<XjglDbContext>();

                databaseOk = await db.Database.CanConnectAsync(context.RequestAborted);
            }
            catch (Exception)
            {
                // 连不上就是"不健康"，这里不需要把异常抛出去 ——
                // 探针的语义是"能不能服务"，不是"为什么不能"
                databaseOk = false;
            }

            context.Response.StatusCode = databaseOk
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;

            await context.Response.WriteAsJsonAsync(
                new
                {
                    status = databaseOk ? "ok" : "degraded",
                    database = databaseOk ? "up" : "down",
                    time = DateTime.UtcNow
                },
                context.RequestAborted);
        });
    }
}
