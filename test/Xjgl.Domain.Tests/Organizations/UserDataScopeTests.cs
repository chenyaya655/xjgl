using Shouldly;
using Xunit;

namespace Xjgl.Organizations;

/// <summary>
/// 数据范围规则的单元测试。
///
/// 数据范围是**安全边界**，而且它的实现全是字符串前缀比较 ——
/// 这类代码最容易在"边界值"上出错（自己算不算？上级算不算？同级的兄弟组织算不算？）。
/// 因此这里把每种范围的允许/拒绝逐条钉死，而不是靠集成测试间接覆盖。
/// </summary>
public class UserDataScopeTests
{
    private const string ProvincePath = "410000";
    private const string CityPath = "410000.410100";
    private const string CountyPath = "410000.410100.410102";
    private const string SchoolAPath = "410000.410100.410102.4101020001";
    private const string SchoolBPath = "410000.410100.410102.4101020002";
    private const string OtherCountyPath = "410000.410100.410103";
    private const string OtherCountySchoolPath = "410000.410100.410103.4101030001";

    private static UserDataScope CountyScope()
        => UserDataScope.ForOrgAndChildren(Guid.NewGuid(), CountyPath, Guid.NewGuid());

    private static UserDataScope SchoolScope()
        => UserDataScope.ForOrgAndChildren(Guid.NewGuid(), SchoolAPath, Guid.NewGuid());

    // ------------------------------------------------------------------
    // 全部数据
    // ------------------------------------------------------------------

    [Fact]
    public void 全部数据_允许任意路径()
    {
        var scope = UserDataScope.ForAll(Guid.NewGuid());

        scope.IsUnrestricted.ShouldBeTrue();
        scope.IsEmpty.ShouldBeFalse();
        scope.AllowsEntityPath(SchoolAPath).ShouldBeTrue();
        scope.AllowsEntityPath(OtherCountySchoolPath).ShouldBeTrue();
        scope.AllowsEntityPath(ProvincePath).ShouldBeTrue();
    }

    [Fact]
    public void 超级管理员_即使没有组织也允许全部()
    {
        var scope = UserDataScope.ForSuperAdmin(Guid.NewGuid());

        scope.IsUnrestricted.ShouldBeTrue();
        scope.IsEmpty.ShouldBeFalse();
        scope.AllowsEntityPath(OtherCountySchoolPath).ShouldBeTrue();
    }

    // ------------------------------------------------------------------
    // 本级及下级
    // ------------------------------------------------------------------

    [Fact]
    public void 本级及下级_允许自己与后代_拒绝上级与兄弟组织()
    {
        var scope = CountyScope();

        scope.AllowsEntityPath(CountyPath).ShouldBeTrue("自己本级");
        scope.AllowsEntityPath(SchoolAPath).ShouldBeTrue("下级学校");
        scope.AllowsEntityPath(CountyPath + ".4101020009").ShouldBeTrue("下级（未建但路径在后代范围内）");

        scope.AllowsEntityPath(ProvincePath).ShouldBeFalse("上级不该看到");
        scope.AllowsEntityPath(CityPath).ShouldBeFalse("上级不该看到");
        scope.AllowsEntityPath(OtherCountyPath).ShouldBeFalse("兄弟县区");
        scope.AllowsEntityPath(OtherCountySchoolPath).ShouldBeFalse("兄弟县区的学校");
    }

    [Fact]
    public void 本级及下级_空路径一律拒绝()
    {
        var scope = CountyScope();

        scope.AllowsEntityPath(null).ShouldBeFalse();
        scope.AllowsEntityPath("").ShouldBeFalse();
        scope.AllowsEntityPath("   ").ShouldBeFalse();
    }

    [Fact]
    public void 前缀比较不能把兄弟当成后代()
    {
        // 关键边界：4101020 是 410102 的前缀，但它不是 410102 的后代。
        // 实现里用 "路径 + '.'" 做前缀，才能避免这种误判。
        var scope = UserDataScope.ForOrgAndChildren(Guid.NewGuid(), "410102", Guid.NewGuid());

        scope.AllowsEntityPath("4101020").ShouldBeFalse("同前缀但不是后代，必须拒绝");
        scope.AllowsEntityPath("410102.1").ShouldBeTrue();
    }

    [Fact]
    public void 校级账号_只能看本校()
    {
        var scope = SchoolScope();

        scope.AllowsEntityPath(SchoolAPath).ShouldBeTrue("本校");
        scope.AllowsEntityPath(SchoolBPath).ShouldBeFalse("同县区的另一所学校也看不到");
        scope.AllowsEntityPath(CountyPath).ShouldBeFalse("上级");
    }

    // ------------------------------------------------------------------
    // 仅本级 / 仅本人 / 空范围
    // ------------------------------------------------------------------

    [Fact]
    public void 仅本级_只允许完全相等()
    {
        var scope = UserDataScope.ForOrgOnly(Guid.NewGuid(), CountyPath, Guid.NewGuid());

        scope.AllowsEntityPath(CountyPath).ShouldBeTrue();
        scope.AllowsEntityPath(SchoolAPath).ShouldBeFalse("下级不在'仅本级'范围内");
    }

    [Fact]
    public void 空范围_什么都不允许()
    {
        var scope = UserDataScope.Empty(Guid.NewGuid());

        scope.IsEmpty.ShouldBeTrue();
        scope.IsUnrestricted.ShouldBeFalse();
        scope.AllowsEntityPath(ProvincePath).ShouldBeFalse();
        scope.AllowsRequestedOrgPath(ProvincePath).ShouldBeFalse();
    }

    // ------------------------------------------------------------------
    // 请求的组织是否有交集
    // ------------------------------------------------------------------

    [Fact]
    public void 请求的下级组织_在我范围内则允许()
    {
        var scope = CountyScope();

        scope.AllowsRequestedOrgPath(SchoolAPath).ShouldBeTrue("请求的是我的下级");
        scope.AllowsRequestedOrgPath(CountyPath).ShouldBeTrue("请求的就是我自己");
    }

    [Fact]
    public void 请求的上级组织_也应允许_因为交集是我的范围()
    {
        // 场景：县区管理员想看"本市的统计"，但接口按组织过滤时传了市 Id。
        // 他只能看到自己县的数据（范围仍然生效），因此不必拒绝这个请求 ——
        // 拒绝反而会让"按上级组织筛选"这种正常用法失效。
        var scope = CountyScope();

        scope.AllowsRequestedOrgPath(CityPath).ShouldBeTrue();
        // 但数据本身仍然只返回本县的（由 AllowsEntityPath 决定）
        scope.AllowsEntityPath(OtherCountySchoolPath).ShouldBeFalse();
    }

    [Fact]
    public void 请求的无关组织_拒绝()
    {
        var scope = CountyScope();

        scope.AllowsRequestedOrgPath(OtherCountyPath).ShouldBeFalse("兄弟县区");
        scope.AllowsRequestedOrgPath(OtherCountySchoolPath).ShouldBeFalse("兄弟县区的学校");
    }

    [Fact]
    public void 仅本级_请求下级应被拒绝()
    {
        var scope = UserDataScope.ForOrgOnly(Guid.NewGuid(), CountyPath, Guid.NewGuid());

        scope.AllowsRequestedOrgPath(CountyPath).ShouldBeTrue();
        scope.AllowsRequestedOrgPath(SchoolAPath).ShouldBeFalse();
    }

    [Fact]
    public void 未登录或未关联组织_拒绝一切()
    {
        var noOrg = UserDataScope.Empty(null);
        noOrg.AllowsEntityPath(SchoolAPath).ShouldBeFalse();
        noOrg.AllowsRequestedOrgPath(SchoolAPath).ShouldBeFalse();
    }
}
