using Volo.Abp.Domain.Entities.Auditing;
using Xjgl.Entities;

namespace Xjgl.Organizations;

/// <summary>
/// 组织机构类型。
/// 与微代码 <c>ORG_TYPE</c> 的码值保持一致。
/// </summary>
public static class OrgTypes
{
    public const string Province = "Province";
    public const string City = "City";
    public const string County = "County";
    public const string School = "School";

    public static readonly string[] All = { Province, City, County, School };

    public static string GetDisplayName(string orgType) => orgType switch
    {
        Province => "省级教育行政部门",
        City => "市级教育行政部门",
        County => "县级教育行政部门",
        School => "学校",
        _ => orgType
    };

    /// <summary>
    /// 该类型**必须**挂在哪一级下面；省厅没有上级，返回 <c>null</c>。
    ///
    /// 【为什么这是一条硬规则，而不是"建议"】
    /// 四级树（省 → 市 → 县 → 校）不只是展示结构，它被三处**按层级推导**的东西依赖：
    ///   - 物化路径 `PathCode` 的段数与 `TreeLevel`；
    ///   - 数据范围（省厅=全省 / 市=本级及下级 / …）；
    ///   - 异动审核级别的截断（`ApproveLevels` 与组织层级对应）。
    /// 允许"跳级挂靠"（例如把学校直接挂在省厅下）不会立刻报错，
    /// 但会让上面三处**静默算错** —— 所以移动时必须校验。
    /// </summary>
    public static string? ExpectedParentOf(string orgType) => orgType switch
    {
        City => Province,
        County => City,
        School => County,
        // 省厅（以及未知类型）：没有期望的上级；未知类型在别处单独校验
        _ => null
    };

    /// <summary>
    /// 把用户写在 CSV 里的组织类型解析成码值。
    ///
    /// 【为什么要接受这么多写法】
    /// 导入模板是给**业务人员**填的，而"省厅 / 省 / Province / 省级教育行政部门"
    /// 在他们眼里是同一个东西。只认一种写法的话，最常见的失败是
    /// "导入 800 行、899 行报『未知的组织类型』" —— 而原因只是他写的是"省"不是"省厅"。
    /// 这类失败对使用者极不友好，且完全可以在解析层消化掉。
    ///
    /// 大小写与首尾空格不敏感（Excel 里很容易带上）。
    /// </summary>
    public static bool TryParse(string? text, out string orgType)
    {
        orgType = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        switch (value.ToLowerInvariant())
        {
            case "province":
            case "省级":
                orgType = Province;
                return true;

            case "city":
            case "市级":
                orgType = City;
                return true;

            case "county":
            case "县级":
                orgType = County;
                return true;

            case "school":
                orgType = School;
                return true;
        }

        // 中文别名（含 GetDisplayName 的完整叫法）
        if (value is "省厅" or "省" or "省教育厅" or "省级教育行政部门")
        {
            orgType = Province;
            return true;
        }

        if (value is "地市" or "市" or "市局" or "市教育局" or "市级教育行政部门" or "市教体局")
        {
            orgType = City;
            return true;
        }

        if (value is "县区" or "县" or "区" or "县局" or "县教育局" or "县级教育行政部门" or "县教体局")
        {
            orgType = County;
            return true;
        }

        if (value is "学校" or "校" or "中小学")
        {
            orgType = School;
            return true;
        }

        return false;
    }
}

/// <summary>
/// 稳定组织树节点。
///
/// ★ 为什么年级、班级**不在**这棵树里：
///   组织树是「权限」与「数据范围」的锚点，必须稳定。
///   年级班级每年升级分班，一旦进入组织树，每年都要重构组织树和历史权限，
///   权限树也会膨胀到几十万个节点。因此年级班级单独建模（SysGrade / SysClass，
///   挂在学校的学年维度下），见方案文档第 4 章。
///
/// 表名：sys_org
/// 层级：省教育厅 → 市教体局 → 县区教体局 → 学校（四级）
/// </summary>
public class SysOrg : FullAuditedAggregateRoot<Guid>, IXjglBusinessEntity
{
    /// <summary>父节点。省厅为 null。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>组织类型，取值见 <see cref="OrgTypes"/>。</summary>
    public string OrgType { get; set; } = default!;

    /// <summary>
    /// 组织编码。
    /// 教育局用行政区划码（GB/T 2260，6 位，如 410100）；
    /// 学校用学校标识码（10 位，其前 6 位即所属行政区划码）。
    /// </summary>
    public string Code { get; set; } = default!;

    /// <summary>组织名称。</summary>
    public string Name { get; set; } = default!;

    /// <summary>简称。</summary>
    public string? ShortName { get; set; }

    /// <summary>
    /// 物化路径，形如 <c>410000.410100.410102.4101020001</c>。
    /// 取子树只需 <c>WHERE path_code LIKE '410100%'</c>，不必递归查询，
    /// 这是数据范围过滤与统计聚合的关键。
    /// </summary>
    public string PathCode { get; set; } = default!;

    /// <summary>层级，省厅为 1。</summary>
    public int TreeLevel { get; set; }

    /// <summary>同级排序号。</summary>
    public int Sort { get; set; }

    /// <summary>是否启用（撤并校后置为 false，但历史数据保留）。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    public SysOrg()
    {
    }

    public SysOrg(
        Guid id,
        string orgType,
        string code,
        string name,
        Guid? parentId = null,
        string? shortName = null,
        int sort = 0) : base(id)
    {
        OrgType = orgType;
        Code = code;
        Name = name;
        ParentId = parentId;
        ShortName = shortName;
        Sort = sort;
    }

    /// <summary>根据父节点重算物化路径与层级。</summary>
    public void SetPathFrom(SysOrg? parent)
    {
        if (parent == null)
        {
            TreeLevel = 1;
            PathCode = Code;
            return;
        }

        TreeLevel = parent.TreeLevel + 1;
        PathCode = $"{parent.PathCode}.{Code}";
    }

    /// <summary>重命名（用于上移/下移子树时的路径重写）。</summary>
    public void RenamePath(string newPathCode, int newLevel)
    {
        PathCode = newPathCode;
        TreeLevel = newLevel;
    }
}
