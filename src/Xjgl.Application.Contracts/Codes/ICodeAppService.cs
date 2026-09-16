using Volo.Abp.Application.Services;

namespace Xjgl.Codes;

/// <summary>
/// 微代码（码表）管理。
///
/// 查询接口对所有登录用户开放（业务页面都要用码表做下拉框），
/// 增删改需要 <c>Xjgl.System.Code</c> 权限。
/// </summary>
public interface ICodeAppService : IApplicationService
{
    // ---- 码类别 ----

    Task<List<CodeCategoryDto>> GetCategoriesAsync(bool onlyEnabled = false);

    Task<CodeCategoryDto> CreateCategoryAsync(CreateUpdateCodeCategoryDto input);

    Task<CodeCategoryDto> UpdateCategoryAsync(Guid id, CreateUpdateCodeCategoryDto input);

    Task DeleteCategoryAsync(Guid id);

    // ---- 码项 ----

    /// <summary>按类别 Id 取码项；<paramref name="asTree"/> 为 true 时组装成树。</summary>
    Task<List<CodeItemDto>> GetItemsAsync(Guid categoryId, bool onlyEnabled = true, bool asTree = false);

    /// <summary>按类别编码取码项（前端取码走这个接口，便于加缓存）。</summary>
    Task<List<CodeItemLookupDto>> GetItemsByCodeAsync(string categoryCode, bool onlyEnabled = true);

    Task<CodeItemDto> CreateItemAsync(CreateUpdateCodeItemDto input);

    Task<CodeItemDto> UpdateItemAsync(Guid id, CreateUpdateCodeItemDto input);

    Task DeleteItemAsync(Guid id);

    /// <summary>清空码表缓存（码值变更后调用）。</summary>
    Task RefreshCacheAsync();
}
