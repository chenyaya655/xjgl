using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Codes;

/// <summary>
/// 码类别，例如「性别码」「民族码」「学生状态码」。
///
/// 表名：sys_code_category（由 SqlSugar 集成层按 snake_case 约定推导）
///
/// 注意：参数less 构造函数必须是 public —— SqlSugar 的 <c>Queryable&lt;T&gt;()</c>
/// 带 <c>new()</c> 约束，这是与 EF Core 不同的地方。
/// </summary>
public class SysCodeCategory : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>码类别编码，全局唯一，如 SEX / NATION / STUDENT_STATUS。</summary>
    public string Code { get; set; } = default!;

    /// <summary>码类别名称，如「性别码」。</summary>
    public string Name { get; set; } = default!;

    /// <summary>标准来源，如 GB/T 2261.1、JY/T 1001-2012。</summary>
    public string? StdSource { get; set; }

    /// <summary>是否为树形码（如行政区划、民族）。</summary>
    public bool IsTree { get; set; }

    /// <summary>是否系统内置（内置码类别不允许删除）。</summary>
    public bool IsSystem { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否参与缓存（前端取码走缓存）。</summary>
    public bool Cacheable { get; set; } = true;

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysCodeCategory()
    {
    }

    public SysCodeCategory(
        Guid id,
        string code,
        string name,
        string? stdSource = null,
        bool isTree = false,
        bool isSystem = false,
        int sort = 0) : base(id)
    {
        Code = code;
        Name = name;
        StdSource = stdSource;
        IsTree = isTree;
        IsSystem = isSystem;
        Sort = sort;
    }
}
