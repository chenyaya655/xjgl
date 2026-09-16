using System.Linq.Expressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using Volo.Abp;
using Volo.Abp.Caching;
using Volo.Abp.Domain.Entities;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Codes;

/// <summary>
/// 微代码（码表）管理实现。
///
/// 这是本项目第一个完整的业务纵切，贯穿了：
///   领域实体（零 ORM 特性）→ 领域层仓储接口 → SqlSugar 实现 → 应用服务 → 约定式 Controller。
/// 应用层只依赖 <see cref="IXjglRepository{TEntity}"/>，对 SqlSugar 无感知。
/// </summary>
[Authorize]
public class CodeAppService : XjglAppService, ICodeAppService
{
    private const string LookupCacheKeyPrefix = "CodeLookup:";
    private const string DefaultSorting = "Sort ASC, Code ASC";

    private readonly IXjglRepository<SysCodeCategory> _categories;
    private readonly IXjglRepository<SysCodeItem> _items;
    private readonly IDistributedCache<List<CodeItemLookupDto>, string> _lookupCache;

    public CodeAppService(
        IXjglRepository<SysCodeCategory> categories,
        IXjglRepository<SysCodeItem> items,
        IDistributedCache<List<CodeItemLookupDto>, string> lookupCache)
    {
        _categories = categories;
        _items = items;
        _lookupCache = lookupCache;
    }

    // ------------------------------------------------------------------
    // 码类别
    // ------------------------------------------------------------------

    public async Task<List<CodeCategoryDto>> GetCategoriesAsync(bool onlyEnabled = false)
    {
        Expression<Func<SysCodeCategory, bool>>? predicate =
            onlyEnabled ? c => c.IsEnabled : null;

        var categories = await _categories.GetSortedListAsync(predicate, DefaultSorting);
        var counts = await GetItemCountsAsync();

        return categories
            .Select(c =>
            {
                var dto = ToCategoryDto(c);
                dto.ItemCount = counts.TryGetValue(c.Id, out var n) ? n : 0;
                return dto;
            })
            .ToList();
    }

    [Authorize(MetadataPermissions.Sys.Code.Create)]
    public async Task<CodeCategoryDto> CreateCategoryAsync(CreateUpdateCodeCategoryDto input)
    {
        var code = input.Code.Trim();

        if (await _categories.AnyAsync(c => c.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        var entity = new SysCodeCategory(
            GuidGenerator.Create(),
            code,
            input.Name.Trim(),
            input.StdSource,
            input.IsTree,
            isSystem: false,
            input.Sort)
        {
            IsEnabled = input.IsEnabled,
            Cacheable = input.Cacheable,
            Remark = input.Remark
        };

        await _categories.InsertAsync(entity, autoSave: true);

        return ToCategoryDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Code.Update)]
    public async Task<CodeCategoryDto> UpdateCategoryAsync(Guid id, CreateUpdateCodeCategoryDto input)
    {
        var entity = await _categories.GetAsync(id);
        var code = input.Code.Trim();

        if (!string.Equals(entity.Code, code, StringComparison.Ordinal) &&
            await _categories.AnyAsync(c => c.Code == code && c.Id != id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        // 「是否树形」在已有码项后不允许修改，否则会破坏既有层级结构
        if (entity.IsTree != input.IsTree && await _items.AnyAsync(i => i.CategoryId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.SystemCodeNotAllowedToModify)
                .WithData("reason", "该码类别下已有码项，不能修改“是否树形”");
        }

        var oldCode = entity.Code;

        entity.Code = code;
        entity.Name = input.Name.Trim();
        entity.StdSource = input.StdSource;
        entity.IsTree = input.IsTree;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.Cacheable = input.Cacheable;
        entity.Remark = input.Remark;

        await _categories.UpdateAsync(entity, autoSave: true);

        await RemoveLookupCacheAsync(oldCode);
        if (!string.Equals(oldCode, code, StringComparison.Ordinal))
        {
            await RemoveLookupCacheAsync(code);
        }

        return ToCategoryDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Code.Delete)]
    public async Task DeleteCategoryAsync(Guid id)
    {
        var entity = await _categories.GetAsync(id);

        if (entity.IsSystem)
        {
            throw new BusinessException(XjglDomainErrorCodes.SystemCodeNotAllowedToModify)
                .WithData("reason", "系统内置码类别不允许删除");
        }

        if (await _items.AnyAsync(i => i.CategoryId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该码类别下还有码项，请先删除码项");
        }

        await _categories.DeleteAsync(entity, autoSave: true);
        await RemoveLookupCacheAsync(entity.Code);
    }

    // ------------------------------------------------------------------
    // 码项
    // ------------------------------------------------------------------

    public async Task<List<CodeItemDto>> GetItemsAsync(Guid categoryId, bool onlyEnabled = true, bool asTree = false)
    {
        Expression<Func<SysCodeItem, bool>> predicate = onlyEnabled
            ? i => i.CategoryId == categoryId && i.IsEnabled
            : i => i.CategoryId == categoryId;

        var items = await _items.GetSortedListAsync(predicate, DefaultSorting);
        var dtos = items.Select(ToItemDto).ToList();

        return asTree ? BuildTree(dtos) : dtos;
    }

    public async Task<List<CodeItemLookupDto>> GetItemsByCodeAsync(string categoryCode, bool onlyEnabled = true)
    {
        Check.NotNullOrWhiteSpace(categoryCode, nameof(categoryCode));

        if (!onlyEnabled)
        {
            // 后台管理界面看全量，不走缓存
            return await QueryLookupAsync(categoryCode, onlyEnabled: false);
        }

        var cacheKey = LookupCacheKeyPrefix + categoryCode + ":True";

        var cached = await _lookupCache.GetOrAddAsync(
            cacheKey,
            async () => await QueryLookupAsync(categoryCode, onlyEnabled: true),
            () => new DistributedCacheEntryOptions
            {
                // 码表极少变动，缓存 12 小时；变更时由 RemoveLookupCacheAsync 主动失效
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12)
            });

        return cached ?? new List<CodeItemLookupDto>();
    }

    [Authorize(MetadataPermissions.Sys.Code.Create)]
    public async Task<CodeItemDto> CreateItemAsync(CreateUpdateCodeItemDto input)
    {
        var category = await _categories.GetAsync(input.CategoryId);
        var code = input.Code.Trim();

        if (await _items.AnyAsync(i => i.CategoryId == input.CategoryId && i.Code == code))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        var entity = new SysCodeItem(
            GuidGenerator.Create(),
            input.CategoryId,
            code,
            input.Name.Trim(),
            input.Sort,
            input.ParentId,
            stdCode: input.StdCode)
        {
            IsEnabled = input.IsEnabled,
            ExtJson = input.ExtJson,
            Remark = input.Remark
        };

        await ApplyTreePathAsync(entity);
        await _items.InsertAsync(entity, autoSave: true);
        await RemoveLookupCacheAsync(category.Code);

        return ToItemDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Code.Update)]
    public async Task<CodeItemDto> UpdateItemAsync(Guid id, CreateUpdateCodeItemDto input)
    {
        var entity = await _items.GetAsync(id);
        var category = await _categories.GetAsync(entity.CategoryId);
        var code = input.Code.Trim();

        if (!string.Equals(entity.Code, code, StringComparison.Ordinal) &&
            await _items.AnyAsync(i => i.CategoryId == entity.CategoryId && i.Code == code && i.Id != id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData).WithData("code", code);
        }

        if (input.ParentId == id)
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "不能把码项挂到自己下面");
        }

        entity.Code = code;
        entity.Name = input.Name.Trim();
        entity.ParentId = input.ParentId;
        entity.StdCode = input.StdCode;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.ExtJson = input.ExtJson;
        entity.Remark = input.Remark;

        await ApplyTreePathAsync(entity);
        await _items.UpdateAsync(entity, autoSave: true);
        await RemoveLookupCacheAsync(category.Code);

        return ToItemDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Code.Delete)]
    public async Task DeleteItemAsync(Guid id)
    {
        var entity = await _items.GetAsync(id);
        var category = await _categories.GetAsync(entity.CategoryId);

        if (await _items.AnyAsync(i => i.ParentId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该码项下还有子码项，请先删除子项");
        }

        await _items.DeleteAsync(entity, autoSave: true);
        await RemoveLookupCacheAsync(category.Code);
    }

    public async Task RefreshCacheAsync()
    {
        var cacheable = await _categories.GetListAsync(c => c.Cacheable);

        foreach (var category in cacheable)
        {
            await RemoveLookupCacheAsync(category.Code);
        }
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private async Task<List<CodeItemLookupDto>> QueryLookupAsync(string categoryCode, bool onlyEnabled)
    {
        var category = await _categories.FindAsync(c => c.Code == categoryCode)
                       ?? throw new BusinessException(XjglDomainErrorCodes.CodeCategoryNotFound)
                           .WithData("categoryCode", categoryCode);

        Expression<Func<SysCodeItem, bool>> predicate = onlyEnabled
            ? i => i.CategoryId == category.Id && i.IsEnabled
            : i => i.CategoryId == category.Id;

        var items = await _items.GetSortedListAsync(predicate, DefaultSorting);

        // 父码值用于前端级联（树形码）
        var idToCode = items.ToDictionary(i => i.Id, i => i.Code);

        return items.Select(i => new CodeItemLookupDto
        {
            Code = i.Code,
            Name = i.Name,
            ParentCode = i.ParentId.HasValue && idToCode.TryGetValue(i.ParentId.Value, out var parentCode)
                ? parentCode
                : null,
            TreeLevel = i.TreeLevel,
            IsEnabled = i.IsEnabled
        }).ToList();
    }

    private async Task<Dictionary<Guid, int>> GetItemCountsAsync()
    {
        // 码项总量有限（几千条），一次性取出后内存分组，避免在应用层使用 ORM 专属的聚合函数
        var items = await _items.GetListAsync();

        return items
            .GroupBy(i => i.CategoryId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>树形码的物化路径：父路径 + 自己的码值。</summary>
    private async Task ApplyTreePathAsync(SysCodeItem entity)
    {
        if (entity.ParentId == null)
        {
            entity.TreeLevel = 1;
            entity.PathCode = entity.Code;
            return;
        }

        var parent = await _items.FindAsync(entity.ParentId.Value)
                     ?? throw new EntityNotFoundException(typeof(SysCodeItem), entity.ParentId.Value);

        entity.TreeLevel = parent.TreeLevel + 1;
        entity.PathCode = $"{parent.PathCode}.{entity.Code}";
    }

    private async Task RemoveLookupCacheAsync(string categoryCode)
    {
        await _lookupCache.RemoveAsync(LookupCacheKeyPrefix + categoryCode + ":True");
        await _lookupCache.RemoveAsync(LookupCacheKeyPrefix + categoryCode + ":False");
    }

    private static List<CodeItemDto> BuildTree(List<CodeItemDto> all)
    {
        var byId = all.ToDictionary(i => i.Id);
        var roots = new List<CodeItemDto>();

        foreach (var item in all)
        {
            if (item.ParentId.HasValue && byId.TryGetValue(item.ParentId.Value, out var parent))
            {
                parent.Children.Add(item);
            }
            else
            {
                roots.Add(item);
            }
        }

        return roots;
    }

    private static CodeCategoryDto ToCategoryDto(SysCodeCategory c) => new()
    {
        Id = c.Id,
        Code = c.Code,
        Name = c.Name,
        StdSource = c.StdSource,
        IsTree = c.IsTree,
        IsSystem = c.IsSystem,
        IsEnabled = c.IsEnabled,
        Cacheable = c.Cacheable,
        Sort = c.Sort,
        Remark = c.Remark
    };

    private static CodeItemDto ToItemDto(SysCodeItem i) => new()
    {
        Id = i.Id,
        CategoryId = i.CategoryId,
        Code = i.Code,
        Name = i.Name,
        ParentId = i.ParentId,
        PathCode = i.PathCode,
        TreeLevel = i.TreeLevel,
        StdCode = i.StdCode,
        Sort = i.Sort,
        IsEnabled = i.IsEnabled,
        ExtJson = i.ExtJson,
        Remark = i.Remark
    };
}
