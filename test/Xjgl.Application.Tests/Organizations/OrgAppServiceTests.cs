using Shouldly;
using Volo.Abp;
using Xunit;

namespace Xjgl.Organizations;

/// <summary>
/// 组织管理的集成测试。
///
/// 重点验证**物化路径**这套机制：新增时的路径计算、移动节点时对整棵子树的级联重写、
/// 防环校验、以及基于前缀匹配的子树查询。
/// 这些逻辑用单元测试很难覆盖（前缀查询要真实 SQL 才生效），所以放在集成测试里。
/// </summary>
public class OrgAppServiceTests : XjglIntegrationTestBase
{
    private readonly IOrgAppService _orgAppService;

    public OrgAppServiceTests()
    {
        _orgAppService = GetRequiredService<IOrgAppService>();
    }

    /// <summary>每个用例用唯一编码，避免相互干扰（测试库是共享的）。</summary>
    private static string NewCode() => "9" + Guid.NewGuid().ToString("N")[..8];

    private Task<OrgDto> CreateAsync(string orgType, string name, Guid? parentId = null)
    {
        return _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = orgType,
            Code = NewCode(),
            Name = name,
            ParentId = parentId,
            IsEnabled = true
        });
    }

    [Fact]
    public async Task 新增省厅_层级为1_物化路径等于自身编码()
    {
        var province = await CreateAsync(OrgTypes.Province, "集成测试省厅");

        province.Id.ShouldNotBe(Guid.Empty);
        province.TreeLevel.ShouldBe(1);
        province.PathCode.ShouldBe(province.Code);
        province.ParentId.ShouldBeNull();
        province.OrgTypeName.ShouldBe("省级教育行政部门");
    }

    [Fact]
    public async Task 新增下级_路径为父路径加点加自身编码()
    {
        var province = await CreateAsync(OrgTypes.Province, "甲省");
        var city = await CreateAsync(OrgTypes.City, "甲市", province.Id);
        var county = await CreateAsync(OrgTypes.County, "甲县", city.Id);
        var school = await CreateAsync(OrgTypes.School, "甲县第一小学", county.Id);

        city.TreeLevel.ShouldBe(2);
        city.PathCode.ShouldBe($"{province.Code}.{city.Code}");

        county.TreeLevel.ShouldBe(3);
        county.PathCode.ShouldBe($"{province.Code}.{city.Code}.{county.Code}");

        school.TreeLevel.ShouldBe(4);
        school.PathCode.ShouldBe($"{province.Code}.{city.Code}.{county.Code}.{school.Code}");
    }

    [Fact]
    public async Task 移动城市到另一个省_整棵子树的路径被级联重写()
    {
        // 甲省 ─ 甲市 ─ 甲县 ─ 甲小学
        var provinceA = await CreateAsync(OrgTypes.Province, "甲省");
        var cityA = await CreateAsync(OrgTypes.City, "甲市", provinceA.Id);
        var countyA = await CreateAsync(OrgTypes.County, "甲县", cityA.Id);
        var schoolA = await CreateAsync(OrgTypes.School, "甲小学", countyA.Id);

        var provinceB = await CreateAsync(OrgTypes.Province, "乙省");

        // 把「甲市」整体划到「乙省」下
        await _orgAppService.MoveAsync(cityA.Id, new MoveOrgDto { NewParentId = provinceB.Id });

        var movedCity = await _orgAppService.GetAsync(cityA.Id);
        movedCity.ParentId.ShouldBe(provinceB.Id);
        movedCity.TreeLevel.ShouldBe(2);
        movedCity.PathCode.ShouldBe($"{provinceB.Code}.{cityA.Code}");

        // 关键断言：子孙的路径与层级都被重写，前缀查询才不会失效
        var movedCounty = await _orgAppService.GetAsync(countyA.Id);
        movedCounty.TreeLevel.ShouldBe(3);
        movedCounty.PathCode.ShouldBe($"{provinceB.Code}.{cityA.Code}.{countyA.Code}");

        var movedSchool = await _orgAppService.GetAsync(schoolA.Id);
        movedSchool.TreeLevel.ShouldBe(4);
        movedSchool.PathCode.ShouldBe($"{provinceB.Code}.{cityA.Code}.{countyA.Code}.{schoolA.Code}");

        // 原省下的子树查询结果不再包含它们
        var provinceASubtree = await _orgAppService.GetListAsync(new OrgQueryDto
        {
            ParentId = provinceA.Id,
            IncludeDescendants = true
        });

        provinceASubtree.ShouldNotContain(o => o.Id == cityA.Id);
        provinceASubtree.ShouldNotContain(o => o.Id == schoolA.Id);

        // 新省下的子树查询能查到它们
        var provinceBSubtree = await _orgAppService.GetListAsync(new OrgQueryDto
        {
            ParentId = provinceB.Id,
            IncludeDescendants = true
        });

        provinceBSubtree.ShouldContain(o => o.Id == cityA.Id);
        provinceBSubtree.ShouldContain(o => o.Id == countyA.Id);
        provinceBSubtree.ShouldContain(o => o.Id == schoolA.Id);
    }

    [Fact]
    public async Task 不能把节点移动到自己的下级()
    {
        var province = await CreateAsync(OrgTypes.Province, "丙省");
        var city = await CreateAsync(OrgTypes.City, "丙市", province.Id);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.MoveAsync(province.Id, new MoveOrgDto { NewParentId = city.Id }));
    }

    /// <summary>
    /// 跨层级移动必须被拒绝（四级树是省 → 市 → 县 → 校）。
    ///
    /// 这条守的是一个**静默算错**的缺口：`MoveAsync` 以前只挡"移到自己下面/自己子树"，
    /// 不管层级 —— 于是"把学校挂到省厅下"会成功，而物化路径、`TreeLevel`、
    /// 数据范围（按层级推导）与审核级别截断都会跟着错，且**没有任何报错**。
    /// </summary>
    [Fact]
    public async Task 跨层级移动被拒绝_把学校挂到省厅下()
    {
        var province = await CreateAsync(OrgTypes.Province, "己省");
        var city = await CreateAsync(OrgTypes.City, "己市", province.Id);
        var county = await CreateAsync(OrgTypes.County, "己县", city.Id);
        var school = await CreateAsync(OrgTypes.School, "己小学", county.Id);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.MoveAsync(school.Id, new MoveOrgDto { NewParentId = province.Id }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.OrgHierarchyInvalid);

        // 被拒绝之后原地不动（不能出现"报错了但父节点已经改了"）
        var unchanged = await _orgAppService.GetAsync(school.Id);
        unchanged.ParentId.ShouldBe(county.Id);
        unchanged.TreeLevel.ShouldBe(4);
    }

    /// <summary>
    /// 省厅只能是根节点，不能被挂到别的节点下。
    ///
    /// 注意这里用的是**两个不同的省**：换成"把省挂到自己的市下面"会先撞上
    /// 「不能移动到自己的下级」，那条错误信息更贴切，但也测不到这条规则。
    /// </summary>
    [Fact]
    public async Task 省厅不能挂到别的节点下()
    {
        var provinceA = await CreateAsync(OrgTypes.Province, "庚省甲");
        var provinceB = await CreateAsync(OrgTypes.Province, "庚省乙");

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.MoveAsync(provinceA.Id, new MoveOrgDto { NewParentId = provinceB.Id }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.OrgHierarchyInvalid);

        var unchanged = await _orgAppService.GetAsync(provinceA.Id);
        unchanged.ParentId.ShouldBeNull();
        unchanged.TreeLevel.ShouldBe(1);
    }

    /// <summary>
    /// 新增时也不能跳级挂靠：把学校直接建在省厅下必须被拒绝。
    ///
    /// 与"移动"共用同一条规则（`OrgAppService.EnsureHierarchy`）——
    /// 移动挡、新增不挡的话，绕一下就能造出同样非法的一棵树。
    /// </summary>
    [Fact]
    public async Task 新增学校时父节点不是县区_被拒绝()
    {
        var province = await CreateAsync(OrgTypes.Province, "辛省");
        var city = await CreateAsync(OrgTypes.City, "辛市", province.Id);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await CreateAsync(OrgTypes.School, "辛小学", province.Id));

        ex.Code.ShouldBe(XjglDomainErrorCodes.OrgHierarchyInvalid);

        // 挂到市局下同样不行（必须是县区）
        var ex2 = await Should.ThrowAsync<BusinessException>(async () =>
            await CreateAsync(OrgTypes.School, "辛小学二", city.Id));

        ex2.Code.ShouldBe(XjglDomainErrorCodes.OrgHierarchyInvalid);

        // 建在县区下才是合法的
        var county = await CreateAsync(OrgTypes.County, "辛县", city.Id);
        var school = await CreateAsync(OrgTypes.School, "辛小学三", county.Id);

        school.TreeLevel.ShouldBe(4);
        school.PathCode.ShouldBe($"{province.Code}.{city.Code}.{county.Code}.{school.Code}");
    }

    /// <summary>
    /// **刻意保留的宽松**：不指定父节点时，任意类型都可以作为根节点。
    ///
    /// 这不是漏洞：界面上"新增下级"会按当前节点自动推断类型（省→市→县→校），
    /// 用户没有机会凭空建一所"没有上级的学校"；而导入与测试会直接造单点数据。
    /// 把它当错误挡掉，只会逼出一堆纯为满足校验的脚手架数据。
    /// 这条用例的作用是**把这个决定固定下来** —— 免得以后有人"顺手"改成必须给父节点。
    /// </summary>
    [Fact]
    public async Task 不指定父节点时允许作为根节点()
    {
        var school = await CreateAsync(OrgTypes.School, "无上级学校");

        school.ParentId.ShouldBeNull();
        school.TreeLevel.ShouldBe(1);
        school.PathCode.ShouldBe(school.Code);
    }

    [Fact]
    public async Task 不能把节点挂到自己下面()
    {
        var province = await CreateAsync(OrgTypes.Province, "丁省");

        await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.MoveAsync(province.Id, new MoveOrgDto { NewParentId = province.Id }));
    }

    [Fact]
    public async Task 有下级组织时不允许删除()
    {
        var province = await CreateAsync(OrgTypes.Province, "戊省");
        await CreateAsync(OrgTypes.City, "戊市", province.Id);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.DeleteAsync(province.Id));
    }

    [Fact]
    public async Task 没有下级时可以删除()
    {
        var province = await CreateAsync(OrgTypes.Province, "己省");

        await _orgAppService.DeleteAsync(province.Id);

        await Should.ThrowAsync<Exception>(async () => await _orgAppService.GetAsync(province.Id));
    }

    [Fact]
    public async Task 组织编码重复时被拒绝()
    {
        var province = await CreateAsync(OrgTypes.Province, "庚省");

        await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.CreateAsync(new CreateUpdateOrgDto
            {
                OrgType = OrgTypes.Province,
                Code = province.Code,
                Name = "重复编码"
            }));
    }

    [Fact]
    public async Task 未知组织类型被拒绝()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _orgAppService.CreateAsync(new CreateUpdateOrgDto
            {
                OrgType = "NotARealType",
                Code = NewCode(),
                Name = "非法类型"
            }));
    }

    [Fact]
    public async Task 逐层加载_子节点正确标记是否还有下级()
    {
        var province = await CreateAsync(OrgTypes.Province, "辛省");
        var cityWithChild = await CreateAsync(OrgTypes.City, "辛市有下级", province.Id);
        var cityWithoutChild = await CreateAsync(OrgTypes.City, "辛市无下级", province.Id);
        await CreateAsync(OrgTypes.County, "辛县", cityWithChild.Id);

        var children = await _orgAppService.GetChildrenAsync(province.Id);

        children.Count.ShouldBe(2);
        children.First(c => c.Id == cityWithChild.Id).HasChildren.ShouldBeTrue();
        children.First(c => c.Id == cityWithoutChild.Id).HasChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task 按关键字查询能同时匹配名称与编码()
    {
        var province = await CreateAsync(OrgTypes.Province, "壬省关键字测试");

        var byName = await _orgAppService.GetListAsync(new OrgQueryDto { Keyword = "壬省关键字测试" });
        byName.ShouldContain(o => o.Id == province.Id);

        var byCode = await _orgAppService.GetListAsync(new OrgQueryDto { Keyword = province.Code });
        byCode.ShouldContain(o => o.Id == province.Id);
    }

    [Fact]
    public async Task 整棵树能正确组装父子关系()
    {
        var province = await CreateAsync(OrgTypes.Province, "癸省");
        var city = await CreateAsync(OrgTypes.City, "癸市", province.Id);
        await CreateAsync(OrgTypes.County, "癸县", city.Id);

        var tree = await _orgAppService.GetTreeAsync();
        var provinceNode = tree.FirstOrDefault(n => n.Id == province.Id);

        provinceNode.ShouldNotBeNull();
        provinceNode!.Children.ShouldContain(c => c.Id == city.Id);

        var cityNode = provinceNode.Children.First(c => c.Id == city.Id);
        cityNode.Children.ShouldNotBeEmpty();
    }
}
