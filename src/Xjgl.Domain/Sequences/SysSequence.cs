using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Sequences;

/// <summary>
/// 通用取号器（号段表）。
///
/// 用途：学籍辅号、异动申请单号、导入批次号等需要「全局唯一且可读」的流水号。
///
/// 为什么不追求连续：并发取号必须避免锁竞争，允许跳号；
/// 唯一性由数据库行锁保证，不依赖应用层。
///
/// 表名：sys_sequence
/// </summary>
public class SysSequence : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>号段名称，全局唯一，如 STU_NO:{学校标识码}。</summary>
    public string Name { get; set; } = default!;

    /// <summary>当前值（已取到的最大号）。</summary>
    public long CurrentValue { get; set; }

    /// <summary>号段长度，仅用于展示与预警（如接近 999999 时提示扩容）。</summary>
    public int Length { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysSequence()
    {
    }

    public SysSequence(Guid id, string name, int length = 0) : base(id)
    {
        Name = name;
        Length = length;
        CurrentValue = 0;
    }
}

/// <summary>
/// 取号服务。
///
/// 实现必须保证「并发安全 + 允许跳号」：
///   同一号段的并发调用会被数据库行锁串行化，绝不会发出重复号。
/// </summary>
public interface ISequenceService
{
    /// <summary>取下一个号（从 1 开始）。</summary>
    long NextValue(string name, int length = 0);

    /// <summary>批量取号，返回起始号；调用方可用 起始号 .. 起始号+count-1。</summary>
    long NextRange(string name, int count, int length = 0);
}
