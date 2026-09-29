using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Students;

/// <summary>学籍变更流水（留痕）的一条记录。</summary>
public class StudentChangeLogDto : EntityDto<Guid>
{
    public Guid StudentId { get; set; }
    public string StudentNo { get; set; } = default!;
    public string StudentName { get; set; } = default!;

    /// <summary>同一批变更的标识（一次操作内的多行相同）。</summary>
    public Guid BatchId { get; set; }

    public string Action { get; set; } = default!;

    /// <summary>动作的中文名（新建学籍 / 修改学籍 / 学籍异动 …）。</summary>
    public string ActionName { get; set; } = default!;

    public string? FieldName { get; set; }
    public string? FieldLabel { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }

    public Guid? OperatorId { get; set; }
    public string? OperatorName { get; set; }

    public DateTime OccurredAt { get; set; }
    public string? Source { get; set; }
}

/// <summary>变更流水的查询条件。</summary>
public class StudentChangeLogQueryDto : PagedAndSortedResultRequestDto
{
    /// <summary>
    /// 按学生查。
    ///
    /// 【它曾经是个"死参数"】DTO 上一直有这个字段，但全局查询的实现**根本没读它** ——
    /// 传了 StudentId 会返回**全部**流水（HTTP 200、看起来一切正常），
    /// 调用方以为筛的是一个人，实际拿到的是所有人。
    /// 这正是本项目最警惕的那类失败：不报错、只是结果不对。
    /// 现在真的按它过滤。
    /// </summary>
    public Guid? StudentId { get; set; }

    /// <summary>
    /// 按姓名 / 学籍辅号模糊匹配（校长与县区管理员最常用的查法：
    /// "这个学生到底被改过什么"）。
    ///
    /// 在本页之前只能在前端"对当前页结果做过滤"，而当前页过滤会漏掉其它页的命中项 ——
    /// 于是补上这个服务端条件。与 `StudentAppService` 的关键字口径一致（Contains）。
    /// </summary>
    [StringLength(64)]
    public string? Keyword { get; set; }

    /// <summary>按动作过滤（Create / Update / Delete / Change）。</summary>
    [StringLength(16)]
    public string? Action { get; set; }

    /// <summary>起始时间（含）。</summary>
    public DateTime? From { get; set; }

    /// <summary>结束时间（含）。</summary>
    public DateTime? To { get; set; }
}
