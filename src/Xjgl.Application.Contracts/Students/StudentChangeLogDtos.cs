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
    /// <summary>按学生查（学生详情页用）。为空则按其它条件查。</summary>
    public Guid? StudentId { get; set; }

    /// <summary>按动作过滤（Create / Update / Delete / Change）。</summary>
    [StringLength(16)]
    public string? Action { get; set; }

    /// <summary>起始时间（含）。</summary>
    public DateTime? From { get; set; }

    /// <summary>结束时间（含）。</summary>
    public DateTime? To { get; set; }
}
