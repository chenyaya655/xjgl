using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Codes;

/// <summary>
/// 码项，例如性别码下的「1 男」「2 女」「0 未知」。
///
/// 表名：sys_code_item
/// 树形码通过 <see cref="ParentId"/> + <see cref="PathCode"/>（物化路径）表达，
/// 例如行政区划：410000 → 410100 → 410102。
/// </summary>
public class SysCodeItem : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>所属码类别。</summary>
    public Guid CategoryId { get; set; }

    /// <summary>码值。</summary>
    public string Code { get; set; } = default!;

    /// <summary>码值名称。</summary>
    public string Name { get; set; } = default!;

    /// <summary>父码项（树形码用）。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>物化路径，如 410000.410100.410102，便于前缀查询整棵子树。</summary>
    public string? PathCode { get; set; }

    /// <summary>层级，从 1 开始。</summary>
    public int TreeLevel { get; set; }

    /// <summary>对应的国标 / 部标码值（本系统内部码与标准码不一致时使用）。</summary>
    public string? StdCode { get; set; }

    /// <summary>排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>扩展属性（JSON）。列名以 _json 结尾，建表时推导为 PostgreSQL 的 jsonb。</summary>
    public string? ExtJson { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysCodeItem()
    {
    }

    public SysCodeItem(
        Guid id,
        Guid categoryId,
        string code,
        string name,
        int sort = 0,
        Guid? parentId = null,
        string? pathCode = null,
        int treeLevel = 1,
        string? stdCode = null) : base(id)
    {
        CategoryId = categoryId;
        Code = code;
        Name = name;
        Sort = sort;
        ParentId = parentId;
        PathCode = pathCode;
        TreeLevel = treeLevel;
        StdCode = stdCode;
    }
}
