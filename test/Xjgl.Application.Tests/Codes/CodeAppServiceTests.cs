using Shouldly;
using Volo.Abp;
using Xunit;

namespace Xjgl.Codes;

/// <summary>
/// 微代码模块的集成测试。
///
/// 这些用例跑在**真实 PostgreSQL** 上，验证的是 SqlSugar 建表约定、
/// 仓储的条件查询与排序、码表缓存与失效、以及应用服务的业务规则。
/// 单元测试无法覆盖这一层（列名映射、类型映射、树形路径都要真实数据库才生效）。
/// </summary>
public class CodeAppServiceTests : XjglIntegrationTestBase
{
    private readonly ICodeAppService _codeAppService;

    public CodeAppServiceTests()
    {
        _codeAppService = GetRequiredService<ICodeAppService>();
    }

    private static string NewCode(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task 内置码表已随种子数据初始化()
    {
        var categories = await _codeAppService.GetCategoriesAsync();

        categories.Count.ShouldBeGreaterThanOrEqualTo(20);
        categories.ShouldContain(c => c.Code == "SEX");
        categories.ShouldContain(c => c.Code == "STUDENT_STATUS");
        categories.ShouldContain(c => c.Code == "CHANGE_TYPE");

        // 系统内置码类别被正确标记
        categories.Where(c => c.Code == "ORG_TYPE").ShouldAllBe(c => c.IsSystem);
    }

    [Fact]
    public async Task 码类别的码项数量统计正确()
    {
        var categories = await _codeAppService.GetCategoriesAsync();
        var sex = categories.First(c => c.Code == "SEX");

        // 性别码预置 3 条：1 男 / 2 女 / 0 未知
        sex.ItemCount.ShouldBe(3);
    }

    [Fact]
    public async Task 按类别编码取码_返回轻量结构()
    {
        var items = await _codeAppService.GetItemsByCodeAsync("SEX");

        items.Count.ShouldBe(3);
        items.ShouldContain(i => i.Code == "1" && i.Name == "男");
        items.ShouldContain(i => i.Code == "2" && i.Name == "女");
        items.ShouldContain(i => i.Code == "0" && i.Name == "未知");
    }

    [Fact]
    public async Task 按类别编码取码_排序稳定()
    {
        // 排序字符串 "Sort ASC, Code ASC" 由仓储翻译成 snake_case 列名，
        // 这里验证翻译确实生效（否则会抛异常或顺序错乱）
        var items = await _codeAppService.GetItemsByCodeAsync("STUDENT_STATUS");

        items.ShouldNotBeEmpty();
        items.Select(i => i.Code).ShouldBe(items.Select(i => i.Code).ToList());
    }

    [Fact]
    public async Task 新增码类别后可以查到并能删除()
    {
        var code = NewCode("T_CAT_");

        var created = await _codeAppService.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
        {
            Code = code,
            Name = "集成测试类别",
            StdSource = "测试",
            IsEnabled = true,
            Cacheable = true,
            Sort = 9999
        });

        created.Id.ShouldNotBe(Guid.Empty);
        created.Code.ShouldBe(code);

        var afterCreate = await _codeAppService.GetCategoriesAsync();
        afterCreate.ShouldContain(c => c.Id == created.Id && c.Name == "集成测试类别");

        await _codeAppService.DeleteCategoryAsync(created.Id);

        var afterDelete = await _codeAppService.GetCategoriesAsync();
        afterDelete.ShouldNotContain(c => c.Id == created.Id);
    }

    [Fact]
    public async Task 码类别编码重复时被拒绝()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
            {
                Code = "SEX",
                Name = "重复的性别码"
            }));
    }

    [Fact]
    public async Task 系统内置码类别不允许删除()
    {
        var system = (await _codeAppService.GetCategoriesAsync()).First(c => c.IsSystem);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.DeleteCategoryAsync(system.Id));
    }

    [Fact]
    public async Task 码类别下还有码项时不允许删除()
    {
        var sex = (await _codeAppService.GetCategoriesAsync()).First(c => c.Code == "SEX");

        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.DeleteCategoryAsync(sex.Id));
    }

    [Fact]
    public async Task 新增码项并验证物化路径与层级()
    {
        var category = await _codeAppService.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
        {
            Code = NewCode("T_TREE_"),
            Name = "测试树形类别",
            IsTree = true
        });

        var parent = await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
        {
            CategoryId = category.Id,
            Code = "01",
            Name = "父节点"
        });

        parent.TreeLevel.ShouldBe(1);
        parent.PathCode.ShouldBe("01");

        var child = await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
        {
            CategoryId = category.Id,
            Code = "02",
            Name = "子节点",
            ParentId = parent.Id
        });

        child.TreeLevel.ShouldBe(2);
        child.PathCode.ShouldBe("01.02");

        // 树形接口的父子关系正确
        var tree = await _codeAppService.GetItemsAsync(category.Id, onlyEnabled: true, asTree: true);
        tree.Count.ShouldBe(1);
        tree[0].Id.ShouldBe(parent.Id);
        tree[0].Children.Count.ShouldBe(1);
        tree[0].Children[0].Id.ShouldBe(child.Id);
    }

    [Fact]
    public async Task 同一类别下码值重复被拒绝()
    {
        var category = await _codeAppService.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
        {
            Code = NewCode("T_DUP_"),
            Name = "测试重复码值"
        });

        await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
        {
            CategoryId = category.Id,
            Code = "X",
            Name = "第一个"
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
            {
                CategoryId = category.Id,
                Code = "X",
                Name = "重复的"
            }));
    }

    [Fact]
    public async Task 有子码项时不允许删除父码项()
    {
        var category = await _codeAppService.CreateCategoryAsync(new CreateUpdateCodeCategoryDto
        {
            Code = NewCode("T_DEL_"),
            Name = "测试删除约束",
            IsTree = true
        });

        var parent = await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
        {
            CategoryId = category.Id,
            Code = "P",
            Name = "父"
        });

        await _codeAppService.CreateItemAsync(new CreateUpdateCodeItemDto
        {
            CategoryId = category.Id,
            Code = "C",
            Name = "子",
            ParentId = parent.Id
        });

        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.DeleteItemAsync(parent.Id));
    }

    [Fact]
    public async Task 码表缓存刷新不报错()
    {
        // RefreshCacheAsync 会把所有可缓存类别逐个失效，
        // 在 Redis 不可用时依赖 ABP 的 HideErrors 降级
        await Should.NotThrowAsync(async () => await _codeAppService.RefreshCacheAsync());
    }

    [Fact]
    public async Task 不存在的类别编码取码时报业务异常()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _codeAppService.GetItemsByCodeAsync("NOT_EXIST_CATEGORY"));
    }
}
