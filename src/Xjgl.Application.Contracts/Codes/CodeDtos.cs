using System.ComponentModel.DataAnnotations;
using Volo.Abp.Application.Dtos;

namespace Xjgl.Codes;

/// <summary>码类别 DTO。</summary>
public class CodeCategoryDto : EntityDto<Guid>
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? StdSource { get; set; }
    public bool IsTree { get; set; }
    public bool IsSystem { get; set; }
    public bool IsEnabled { get; set; }
    public bool Cacheable { get; set; }
    public int Sort { get; set; }
    public string? Remark { get; set; }

    /// <summary>该类别下的码项数量（列表页展示用）。</summary>
    public int ItemCount { get; set; }
}

/// <summary>新增 / 修改码类别。</summary>
public class CreateUpdateCodeCategoryDto
{
    [Required(ErrorMessage = "码类别编码不能为空")]
    [StringLength(64)]
    public string Code { get; set; } = default!;

    [Required(ErrorMessage = "码类别名称不能为空")]
    [StringLength(128)]
    public string Name { get; set; } = default!;

    [StringLength(64)]
    public string? StdSource { get; set; }

    public bool IsTree { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool Cacheable { get; set; } = true;

    public int Sort { get; set; }

    [StringLength(1024)]
    public string? Remark { get; set; }
}

/// <summary>码项 DTO（树形时通过 Children 递归）。</summary>
public class CodeItemDto : EntityDto<Guid>
{
    public Guid CategoryId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public Guid? ParentId { get; set; }
    public string? PathCode { get; set; }
    public int TreeLevel { get; set; }
    public string? StdCode { get; set; }
    public int Sort { get; set; }
    public bool IsEnabled { get; set; }
    public string? ExtJson { get; set; }
    public string? Remark { get; set; }

    public List<CodeItemDto> Children { get; set; } = new();
}

/// <summary>新增 / 修改码项。</summary>
public class CreateUpdateCodeItemDto
{
    [Required]
    public Guid CategoryId { get; set; }

    [Required(ErrorMessage = "码值不能为空")]
    [StringLength(64)]
    public string Code { get; set; } = default!;

    [Required(ErrorMessage = "码值名称不能为空")]
    [StringLength(256)]
    public string Name { get; set; } = default!;

    public Guid? ParentId { get; set; }

    [StringLength(64)]
    public string? StdCode { get; set; }

    public int Sort { get; set; }

    public bool IsEnabled { get; set; } = true;

    [StringLength(2048)]
    public string? ExtJson { get; set; }

    [StringLength(1024)]
    public string? Remark { get; set; }
}

/// <summary>
/// 前端下拉框使用的轻量码项结构。
/// 前端 <c>useCode('SEX')</c> / <c>&lt;CodeSelect code="SEX" /&gt;</c> 取的就是它。
/// </summary>
public class CodeItemLookupDto
{
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? ParentCode { get; set; }
    public int TreeLevel { get; set; }
    public bool IsEnabled { get; set; }
}
