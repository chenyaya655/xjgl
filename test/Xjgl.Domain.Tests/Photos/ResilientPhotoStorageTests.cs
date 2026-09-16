using System.Text;
using Shouldly;
using Xunit;

namespace Xjgl.Photos;

/// <summary>
/// 照片存储韧性链路的单元测试。
///
/// 用一个「会坏的假存储」替代真实 NAS，因此可以精确验证四条保护行为：
/// 熔断、超时、并发舱壁、以及「故障不暴露给用户」的降级语义。
/// </summary>
public class ResilientPhotoStorageTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // 假存储
    // ------------------------------------------------------------------

    private sealed class FakeStorage : IPhotoStorage
    {
        public FakeStorage(string name) => Name = name;

        public string Name { get; }

        public bool ShouldFail { get; set; }

        public TimeSpan Delay { get; set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public List<string> Written { get; } = new();

        public async Task<PhotoWriteResult> WriteAsync(
            string relativePath,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (ShouldFail)
            {
                return PhotoWriteResult.Failed($"{Name} 模拟故障");
            }

            Written.Add(relativePath);
            return PhotoWriteResult.Ok(relativePath);
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
            {
                throw new IOException($"{Name} 不可用");
            }

            return Task.FromResult(Written.Contains(relativePath));
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            Written.Remove(relativePath);
            return Task.CompletedTask;
        }
    }

    private ResilientPhotoStorage CreateStorage(
        FakeStorage primary,
        FakeStorage fallback,
        int minimumSamples = 4,
        int bulkheadSize = 8,
        int timeoutSeconds = 5)
    {
        var breaker = new PhotoCircuitBreaker(
            new PhotoCircuitBreakerOptions
            {
                MinimumSamples = minimumSamples,
                FailureRateThreshold = 0.5,
                OpenDuration = TimeSpan.FromSeconds(30)
            },
            () => _now);

        return new ResilientPhotoStorage(
            primary, fallback, breaker, bulkheadSize, TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static Stream Content(string text = "fake-jpeg-bytes")
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    // ------------------------------------------------------------------
    // 正常路径
    // ------------------------------------------------------------------

    [Fact]
    public async Task 主存储正常时写入主存储且不降级()
    {
        var primary = new FakeStorage("NAS");
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback);

        var result = await storage.WriteAsync("photo/001.jpg", Content());

        result.Success.ShouldBeTrue();
        result.Degraded.ShouldBeFalse();
        result.StoredPath.ShouldBe("photo/001.jpg");

        primary.Written.ShouldContain("photo/001.jpg");
        fallback.WriteCount.ShouldBe(0);
        storage.GetCircuitSnapshot().State.ShouldBe(PhotoCircuitState.Closed);
    }

    [Fact]
    public async Task 连续成功不会误触发熔断()
    {
        var primary = new FakeStorage("NAS");
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback);

        for (var i = 0; i < 20; i++)
        {
            var result = await storage.WriteAsync($"photo/{i}.jpg", Content());
            result.Degraded.ShouldBeFalse();
        }

        storage.GetCircuitSnapshot().State.ShouldBe(PhotoCircuitState.Closed);
    }

    // ------------------------------------------------------------------
    // 降级
    // ------------------------------------------------------------------

    [Fact]
    public async Task 主存储故障时降级写本地_接口仍返回成功()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback);

        var result = await storage.WriteAsync("photo/002.jpg", Content());

        // ★ 故障不暴露给教师：仍然算写入成功，但标记为降级
        result.Success.ShouldBeTrue();
        result.Degraded.ShouldBeTrue();
        result.Error.ShouldNotBeNullOrWhiteSpace();

        fallback.Written.ShouldContain("photo/002.jpg");
    }

    [Fact]
    public async Task 主存储与降级存储都失败时返回失败()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地") { ShouldFail = true };
        var storage = CreateStorage(primary, fallback);

        var result = await storage.WriteAsync("photo/003.jpg", Content());

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 写入超时视为失败并降级()
    {
        var primary = new FakeStorage("NAS") { Delay = TimeSpan.FromSeconds(2) };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback, timeoutSeconds: 1);

        var result = await storage.WriteAsync("photo/004.jpg", Content());

        result.Success.ShouldBeTrue();
        result.Degraded.ShouldBeTrue();
        fallback.Written.ShouldContain("photo/004.jpg");
    }

    // ------------------------------------------------------------------
    // 熔断
    // ------------------------------------------------------------------

    [Fact]
    public async Task 失败达到阈值后熔断打开()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback, minimumSamples: 4);

        for (var i = 0; i < 4; i++)
        {
            await storage.WriteAsync($"photo/{i}.jpg", Content());
        }

        storage.GetCircuitSnapshot().State.ShouldBe(PhotoCircuitState.Open);
    }

    [Fact]
    public async Task 熔断期间不再尝试主存储_直接降级()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback, minimumSamples: 4);

        // 先打满熔断
        for (var i = 0; i < 4; i++)
        {
            await storage.WriteAsync($"warmup/{i}.jpg", Content());
        }

        var countBefore = primary.WriteCount;

        // 熔断期间的 10 次写入应全部直接走降级
        for (var i = 0; i < 10; i++)
        {
            var result = await storage.WriteAsync($"photo/{i}.jpg", Content());
            result.Degraded.ShouldBeTrue();
        }

        // ★ 关键：主存储一次都没被再调用，故障时不再浪费超时
        primary.WriteCount.ShouldBe(countBefore);
        fallback.Written.Count.ShouldBe(14);
    }

    [Fact]
    public async Task 熔断到期且主存储恢复后_自动恢复走主存储()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback, minimumSamples: 4);

        for (var i = 0; i < 4; i++)
        {
            await storage.WriteAsync($"warmup/{i}.jpg", Content());
        }

        storage.GetCircuitSnapshot().State.ShouldBe(PhotoCircuitState.Open);

        // NAS 恢复 + 熔断到期
        primary.ShouldFail = false;
        _now = _now.AddSeconds(31);

        var result = await storage.WriteAsync("photo/recovered.jpg", Content());

        result.Degraded.ShouldBeFalse();
        primary.Written.ShouldContain("photo/recovered.jpg");
        storage.GetCircuitSnapshot().State.ShouldBe(PhotoCircuitState.Closed);
    }

    // ------------------------------------------------------------------
    // 存在性与删除
    // ------------------------------------------------------------------

    [Fact]
    public async Task 主存储异常时存在性检查回落降级存储()
    {
        var primary = new FakeStorage("NAS") { ShouldFail = true };
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback);

        await storage.WriteAsync("photo/fallback.jpg", Content());

        // 主存储抛异常 → 回落检查本地
        (await storage.ExistsAsync("photo/fallback.jpg")).ShouldBeTrue();
        (await storage.ExistsAsync("photo/not-exists.jpg")).ShouldBeFalse();
    }

    [Fact]
    public async Task 删除会同时尝试主存储与降级存储()
    {
        var primary = new FakeStorage("NAS");
        var fallback = new FakeStorage("本地");
        var storage = CreateStorage(primary, fallback);

        await storage.WriteAsync("photo/to-delete.jpg", Content());

        await storage.DeleteAsync("photo/to-delete.jpg");

        primary.DeleteCount.ShouldBe(1);
        fallback.DeleteCount.ShouldBe(1);
    }
}
