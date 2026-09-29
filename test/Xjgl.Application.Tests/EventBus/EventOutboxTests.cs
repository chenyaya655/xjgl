using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Uow;
using Xunit;

namespace Xjgl.EventBus;

/// <summary>
/// 事务性发件箱（Outbox）的集成测试（债 4）。
///
/// 【它守的是什么 —— 一个会造成静默数据丢失的缺陷】
/// 不开 Outbox 时，ABP 是**在工作单元提交阶段**把分布式事件直接发给 broker 的。
/// broker 不可达时的实测表现（本仓库有完整证据）：
///   - `POST /api/identity/users` 返回 **200 且带完整用户对象**；
///   - 库里查不到那条用户（`AbpUsers` 计数没变）；
///   - 客户端同时收到 `transfer closed with outstanding read data remaining`。
/// 链路是：`PublishAsync` → 工作单元 `CompleteAsync` → **响应早已写出** → 提交失败回滚。
/// 结果是"建用户"静默丢失，而使用者以为成功了。
///
/// 开启 Outbox 之后，事件变成**与业务数据在同一个事务里的一行数据**，
/// 由 ABP 自带的 `OutboxSender` 后台重发。于是 broker 是否可达
/// **与业务写入是否成功彻底解耦**。
///
/// 【为什么要断言"能查到那一行"，而不是断言"没抛异常"】
/// "配好了 Outbox"在代码上完全看不出来 —— 少了 `UseDbContext`、
/// 少了 `ConfigureEventOutbox()`、少了迁移，都会静默退回"直接发 broker"的老行为，
/// 而**不报任何错**。只有真的去 `AbpEventOutbox` 表里查那一行，才能区分。
/// </summary>
public class EventOutboxTests : XjglIntegrationTestBase
{
    private readonly IDistributedEventBus _eventBus;
    private readonly IUnitOfWorkManager _uowManager;

    public EventOutboxTests()
    {
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _uowManager = GetRequiredService<IUnitOfWorkManager>();
    }

    /// <summary>一个只用于本测试的事件体（ETO）。</summary>
    [EventName("Xjgl.Tests.OutboxProbe")]
    public class OutboxProbeEto
    {
        public string Marker { get; set; } = string.Empty;
    }

    private async Task<List<string>> ReadOutboxPayloadsAsync(string marker)
    {
        using var scope = ServiceProvider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<Xjgl.EntityFrameworkCore.XjglDbContext>();

        // 直接查发件箱表：这是"事件真的被持久化了"的唯一硬证据。
        // 注意 `EventData` 是 **byte[]**（不是 string）—— 事件体按 UTF-8 字节存；
        // 先取回来再解码，别指望 EF 能翻译 `Encoding.UTF8.GetString(...)`。
        var rows = await db.OutgoingEvents
            .Where(e => e.EventName == "Xjgl.Tests.OutboxProbe")
            .Select(e => e.EventData)
            .ToListAsync();

        return rows
            .Select(bytes => System.Text.Encoding.UTF8.GetString(bytes))
            .ToList();
    }

    [Fact]
    public async Task 工作单元内发布的事件_会落进发件箱表()
    {
        var marker = "outbox-" + Guid.NewGuid().ToString("N")[..8];

        using (var uow = _uowManager.Begin(requiresNew: true))
        {
            await _eventBus.PublishAsync(new OutboxProbeEto { Marker = marker });

            await uow.CompleteAsync();
        }

        var payloads = await ReadOutboxPayloadsAsync(marker);

        payloads.ShouldContain(
            p => p.Contains(marker),
            "事件必须落进 AbpEventOutbox —— 否则 broker 一抖，写入就会静默丢失。不落库说明 " +
            "Outbox 没生效（少配 UseDbContext / 少 ConfigureEventOutbox / 少迁移，三者都不报错）");
    }

    [Fact]
    public async Task 工作单元回滚时_事件不会留在发件箱()
    {
        // 反向用例：只有"能落库"一条，会让"把事件直接写进另一条连接/立刻提交"的实现
        // 也通过 —— 那种实现下，业务回滚了事件却还在，收件方会收到一条**从未发生**的变更。
        // 事务性发件箱的意义正是"同生共死"。
        var marker = "outbox-rollback-" + Guid.NewGuid().ToString("N")[..8];

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var uow = _uowManager.Begin(requiresNew: true);

            await _eventBus.PublishAsync(new OutboxProbeEto { Marker = marker });

            // 不 CompleteAsync，直接抛 —— 工作单元应当回滚
            throw new InvalidOperationException("故意失败，用来验证事件的落库也是事务性的");
        });

        var payloads = await ReadOutboxPayloadsAsync(marker);

        payloads.ShouldNotContain(
            p => p.Contains(marker),
            "业务回滚了，事件也必须一起回滚 —— 否则收件方会收到一条从未发生的变更");
    }
}
