using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Students;

/// <summary>变更动作（写进 <c>stu_change_log.action</c>）。</summary>
public static class StudentChangeActions
{
    /// <summary>新建学籍。</summary>
    public const string Create = "Create";

    /// <summary>修改学籍信息（字段级留痕）。</summary>
    public const string Update = "Update";

    /// <summary>删除学籍。</summary>
    public const string Delete = "Delete";

    /// <summary>异动审批引起的学籍变化（状态 / 学校 / 年级班级）。</summary>
    public const string Change = "Change";

    /// <summary>批量导入建档。</summary>
    public const string Import = "Import";

    public static readonly string[] All = { Create, Update, Delete, Change, Import };

    public static string GetName(string action)
    {
        return action switch
        {
            Create => "新建学籍",
            Update => "修改学籍",
            Delete => "删除学籍",
            Change => "学籍异动",
            Import => "批量导入",
            _ => action
        };
    }
}

/// <summary>
/// 学籍变更流水（留痕）。
///
/// 【为什么要有它，而且必须是字段级】
/// 学籍档案是要被审计的：家长问"我孩子的学籍状态什么时候被改成转出的、谁改的"，
/// 系统必须答得出来。只记"某人某时改过这条学生记录"没有意义 ——
/// 真正要看的是**改了哪个字段、从什么值变成什么值**。
///
/// 【为什么不是删掉就完事】
/// 学生被删除时同样写一条流水：档案的"消失"本身也是一次需要留痕的变更。
///
/// 【一次操作 = 一个 BatchId】
/// 改姓名同时改了出生日期，会产生两行；两行共享同一个 <see cref="BatchId"/>，
/// 界面上才能显示成"一次修改中的两处变动"，而不是两条互不相关的事件。
///
/// 表名：stu_change_log
/// </summary>
public class StuChangeLog : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属学生。</summary>
    public Guid StudentId { get; set; }

    /// <summary>学籍辅号（冗余：学生删掉之后仍然要能查到这是谁）。</summary>
    public string StudentNo { get; set; } = default!;

    /// <summary>学生姓名（冗余，理由同上）。</summary>
    public string StudentName { get; set; } = default!;

    /// <summary>
    /// 变更发生时的学校物化路径（冗余）。
    ///
    /// 留痕表要支持"县级以上按数据范围抽查"，而按学生 Id 反查会拼出巨大的 IN 条件；
    /// 存下路径就能像学生表一样用**前缀比较**过滤（与 `stu_photo` 同一做法）。
    /// 注意存的是**变更发生时**的路径：学生后来转学了，这条历史记录仍属于原来的学校。
    /// </summary>
    public string? SchoolPathCode { get; set; }

    /// <summary>同一批变更的标识（一次操作内的多行共享）。</summary>
    public Guid BatchId { get; set; }

    /// <summary>动作，取值见 <see cref="StudentChangeActions"/>。</summary>
    public string Action { get; set; } = default!;

    /// <summary>字段名（实体属性名，如 StudentStatusCode）。</summary>
    public string? FieldName { get; set; }

    /// <summary>字段中文名（如「学籍状态」），存下来是为了将来改字段名也不影响历史可读性。</summary>
    public string? FieldLabel { get; set; }

    /// <summary>变更前的值（已翻译成可读文本）。</summary>
    public string? OldValue { get; set; }

    /// <summary>变更后的值。</summary>
    public string? NewValue { get; set; }

    /// <summary>变更原因 / 说明。</summary>
    public string? Reason { get; set; }

    /// <summary>操作人。系统自动任务（如定时补偿）可能为空。</summary>
    public Guid? OperatorId { get; set; }

    /// <summary>操作人姓名。</summary>
    public string? OperatorName { get; set; }

    /// <summary>变更发生时间。</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>来源：手工录入 / 批量导入 / 学籍异动。</summary>
    public string? Source { get; set; }

    public StuChangeLog()
    {
    }

    public StuChangeLog(
        Guid id,
        Guid studentId,
        string studentNo,
        string studentName,
        Guid batchId,
        string action) : base(id)
    {
        StudentId = studentId;
        StudentNo = studentNo;
        StudentName = studentName;
        BatchId = batchId;
        Action = action;
    }
}
