using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>
/// 学生奖励记录。
///
/// 类型与级别分两个字段：类型是"三好学生/竞赛获奖"这类**性质**，
/// 级别是"校级/县级/省级"这类**层级** —— 统计"省级以上获奖人数"时要按级别筛，
/// 合成一个字段就没法查了。
///
/// 表名：stu_reward
/// </summary>
public class StuReward : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生（stu_student.Id）。</summary>
    public Guid StudentId { get; set; }

    /// <summary>显示顺序。</summary>
    public int Seq { get; set; }

    /// <summary>获奖日期。</summary>
    public DateTime RewardDate { get; set; }

    /// <summary>奖励名称（如"三好学生"）。</summary>
    public string RewardName { get; set; } = default!;

    /// <summary>奖励类型码（微代码 REWARD_TYPE）。</summary>
    public string? RewardTypeCode { get; set; }

    /// <summary>奖励级别码（微代码 REWARD_LEVEL）。</summary>
    public string? RewardLevelCode { get; set; }

    /// <summary>授奖单位。</summary>
    public string? AwardOrg { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public StuReward()
    {
    }

    public StuReward(Guid id, Guid studentId, int seq, DateTime rewardDate, string rewardName) : base(id)
    {
        StudentId = studentId;
        Seq = seq;
        RewardDate = rewardDate;
        RewardName = rewardName;
    }
}
