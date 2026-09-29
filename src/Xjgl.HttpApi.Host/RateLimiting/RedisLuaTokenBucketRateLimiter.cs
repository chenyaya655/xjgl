using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Xjgl.Application.RateLimiting;
using Xjgl.RateLimiting;

namespace Xjgl.Host.RateLimiting;

/// <summary>
/// Redis 连接（可空）。
///
/// 【为什么单独抽一个可空的访问器，而不是直接注入 IConnectionMultiplexer】
/// Redis 在本系统里是**允许不可用**的组件（缓存有内存兜底、限流有降级实现）。
/// 若把它注册成必需依赖，Redis 一挂整个应用就起不来 ——
/// 那等于把一个"可选加速件"升级成了"单点故障"。
/// 所以这里**永不抛异常**：连不上就返回 null，由用它的地方各自降级。
///
/// 【为什么需要 Redis:Password 这个额外的键】
/// StackExchange.Redis 的连接串用**逗号**分隔参数，而口令本身可能含逗号
/// （本项目 dev 环境的 Redis 口令就是 <c>Qq123456,</c>）。
/// 写成 <c>password=Qq123456,,abortConnect=false</c> 时，解析器会把口令截成
/// <c>Qq123456</c> → 报 WRONGPASS；而错误信息只显示"认证失败"，
/// 完全看不出是"逗号被当成分隔符"。
/// 所以口令允许单独用 <c>Redis:Password</c> 提供，由这里覆盖解析结果。
/// </summary>
public interface IRedisConnectionAccessor
{
    /// <summary>可用的 Redis 连接；不可用时为 <c>null</c>。</summary>
    IConnectionMultiplexer? Connection { get; }
}

/// <inheritdoc />
public class RedisConnectionAccessor : IRedisConnectionAccessor, ISingletonDependency
{
    private readonly ILogger<RedisConnectionAccessor> _logger;

    public RedisConnectionAccessor(IConfiguration configuration, ILogger<RedisConnectionAccessor> logger)
    {
        _logger = logger;
        Connection = TryConnect(configuration, logger);
    }

    public IConnectionMultiplexer? Connection { get; }

    private static IConnectionMultiplexer? TryConnect(
        IConfiguration configuration,
        ILogger logger)
    {
        var raw = configuration["Redis:Configuration"];

        if (raw.IsNullOrWhiteSpace())
        {
            logger.LogWarning("[限流] 未配置 Redis:Configuration，Lua 原子限流不可用，将退回进程内+分布式缓存的实现。");

            return null;
        }

        try
        {
            var options = ConfigurationOptions.Parse(raw);

            // AbortOnConnectFail=false：启动瞬间 Redis 还没就绪时不抛异常，
            // 后台自动重连；否则容器编排里"应用先起、Redis 后起"就会直接崩
            options.AbortOnConnectFail = false;

            var password = configuration["Redis:Password"];

            if (!password.IsNullOrWhiteSpace())
            {
                options.Password = password;
            }

            var connection = ConnectionMultiplexer.Connect(options);

            logger.LogInformation(
                "[限流] Redis 连接已建立（{Endpoints}），使用 Lua 原子限流。",
                string.Join("、", options.EndPoints.Select(e => e.ToString())));

            return connection;
        }
        catch (Exception ex)
        {
            // 绝不抛出：Redis 不可用是允许的降级状态，不是启动失败
            logger.LogWarning(ex, "[限流] 连接 Redis 失败，Lua 原子限流不可用，将退回降级实现。");

            return null;
        }
    }
}

/// <summary>
/// 用 Redis 的 Lua 脚本做**原子**令牌桶 —— 这是"多实例不再被放大 N 倍"的实现。
///
/// 【原先错在哪（债 25）】
/// 旧实现把"读桶 → 算令牌 → 写桶"三步放在进程内的 <c>SemaphoreSlim</c> 后面。
/// 那个锁只保护**本进程**；部署 N 个实例时，每个实例都按自己的桶放行，
/// 于是实际速率上限变成 N 倍。单实例看不出来，扩容之后才暴露，
/// 而那时候现象是"NAS 被压垮"，很难回溯到"限流是按实例算的"。
///
/// 【为什么必须用 Lua，而不是"加个分布式锁"】
/// 分布式锁只能串行化，仍需 3 次往返（取锁、读改、放锁），
/// 中间任何一次超时都要处理"锁还在别人手里"的状态；
/// 而 Lua 脚本在 Redis 里是**单线程原子执行**的，一次往返完成读-算-写，
/// 没有中间态、没有锁泄漏。
///
/// 【时间基准为什么取 Redis 的 TIME 而不是本机 IClock】
/// 多实例的机器时钟会有偏移（哪怕只有几十毫秒）。若各实例用自己的时钟写 <c>ts</c>，
/// 桶的补充量就会随"谁写的"而抖动，甚至出现负的 elapsed。
/// 用 Redis 的 TIME 让所有实例共用同一个时间基准，也从根上消除了这个问题。
///
/// 【失败策略：降级到旧实现，而不是放行】
/// Redis 出错时若直接放行，等于"Redis 一抖动限流就完全失效"；
/// 降级到 <see cref="DistributedTokenBucketRateLimiter"/> 至少还保留
/// 进程内闸门 + 分布式缓存（缓存本身也有 Redis→内存的兜底）两级保护。
/// 这与"限流绝不能变成挡上传的故障"这条底线并不冲突 —— 降级实现本身就是失败放行的。
/// </summary>
public class RedisLuaTokenBucketRateLimiter : ITokenBucketRateLimiter, ITransientDependency
{
    /// <summary>
    /// 令牌桶的原子实现。返回 {是否放行, 剩余令牌, 需等待毫秒}。
    ///
    /// 注意 <c>tokens</c> 在"放行"与"拒绝"两条路径上都写回：
    /// 拒绝时**也必须**写回（写的是补充后的令牌数，而不是扣减后的），
    /// 否则桶会停在旧值上、补充量被反复重算，实际速率会明显低于配置值。
    /// </summary>
    private const string Script = """
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill = tonumber(ARGV[2])
        local permits = tonumber(ARGV[3])
        local ttlMs = tonumber(ARGV[4])

        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)

        local data = redis.call('HMGET', key, 'tokens', 'ts')
        local tokens = tonumber(data[1])
        local ts = tonumber(data[2])

        if tokens == nil then
            tokens = capacity
            ts = now
        end

        local elapsed = (now - ts) / 1000.0
        if elapsed < 0 then elapsed = 0 end
        tokens = math.min(capacity, tokens + elapsed * refill)

        local allowed = 0
        local waitMs = 0

        if tokens < permits then
            waitMs = math.ceil((permits - tokens) / refill * 1000)
            if waitMs < 200 then waitMs = 200 end
        else
            tokens = tokens - permits
            allowed = 1
        end

        redis.call('HSET', key, 'tokens', tokens, 'ts', now)
        redis.call('PEXPIRE', key, ttlMs)

        return {allowed, math.floor(tokens), waitMs}
        """;

    private readonly IRedisConnectionAccessor _redis;
    private readonly DistributedTokenBucketRateLimiter _fallback;
    private readonly ILogger<RedisLuaTokenBucketRateLimiter> _logger;

    public RedisLuaTokenBucketRateLimiter(
        IRedisConnectionAccessor redis,
        DistributedTokenBucketRateLimiter fallback,
        ILogger<RedisLuaTokenBucketRateLimiter> logger)
    {
        _redis = redis;
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RateLimitResult> AcquireAsync(
        RateLimitPolicy policy,
        string key,
        int permits = 1,
        CancellationToken cancellationToken = default)
    {
        if (policy.Capacity <= 0 || policy.RefillPerSecond <= 0)
        {
            return RateLimitResult.Allow(policy.Capacity);
        }

        var connection = _redis.Connection;

        if (connection == null || !connection.IsConnected)
        {
            return await _fallback.AcquireAsync(policy, key, permits, cancellationToken);
        }

        try
        {
            var cacheKey = (RedisKey)$"ratelimit:{key}";

            // 空桶放满所需时间 + 60 秒余量：桶闲置后自然过期，不在 Redis 里留垃圾
            var ttlMs = (long)Math.Ceiling(policy.Capacity / policy.RefillPerSecond * 1000) + 60_000;

            var result = await connection.GetDatabase().ScriptEvaluateAsync(
                Script,
                new[] { cacheKey },
                new RedisValue[]
                {
                    policy.Capacity,
                    policy.RefillPerSecond,
                    permits,
                    ttlMs
                });

            var values = (RedisResult[])result!;

            var allowed = (long)values[0] == 1;
            var remaining = (long)values[1];
            var waitMs = (long)values[2];

            return allowed
                ? RateLimitResult.Allow(remaining)
                : RateLimitResult.Deny(TimeSpan.FromMilliseconds(waitMs));
        }
        catch (Exception ex)
        {
            // 降级而不是放行：放行会让"Redis 一抖限流就完全失效"
            _logger.LogWarning(ex, "[限流] Lua 原子限流执行失败，本次降级到进程内实现：{Key}", key);

            return await _fallback.AcquireAsync(policy, key, permits, cancellationToken);
        }
    }
}
