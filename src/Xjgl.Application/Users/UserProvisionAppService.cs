using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Identity;
using Volo.Abp.Security.Encryption;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Students;
using Xjgl.Text;
using Xjgl.Validation;

namespace Xjgl.Users;

/// <summary>
/// 用户预置与本人激活的实现。
/// </summary>
[Authorize]
public class UserProvisionAppService : XjglAppService, IUserProvisionAppService
{
    /// <summary>激活码字符集：去掉 0/O/1/I 等易混字符。</summary>
    private const string ActivationCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private const int ActivationCodeLength = 8;

    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IUserDataScopeProvider _dataScope;
    private readonly IdentityUserManager _userManager;
    private readonly IdentityRoleManager _roleManager;
    private readonly IStringEncryptionService _stringEncryptionService;
    private readonly IIdCardProtector _idCardProtector;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    public UserProvisionAppService(
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs,
        IUserDataScopeProvider dataScope,
        IdentityUserManager userManager,
        IdentityRoleManager roleManager,
        IStringEncryptionService stringEncryptionService,
        IIdCardProtector idCardProtector,
        IConfiguration configuration,
        IClock clock)
    {
        _predefines = predefines;
        _orgs = orgs;
        _dataScope = dataScope;
        _userManager = userManager;
        _roleManager = roleManager;
        _stringEncryptionService = stringEncryptionService;
        _idCardProtector = idCardProtector;
        _configuration = configuration;
        _clock = clock;
    }

    // ------------------------------------------------------------------
    // 管理端
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.UserProvision.Query)]
    public async Task<PagedResultDto<PredefineUserDto>> GetListAsync(PredefineUserQueryDto input)
    {
        // ── 数据范围必须先于一切条件生效 ──
        //
        // 【修的是什么】此前只有"传了 OrgId"才会去解析范围；不传 OrgId 时谓词为 null，
        //   即**不加任何 where**。于是一个县区管理员只要不带 OrgId 列表，
        //   就能看到**全系统**的预置记录（含手机号与身份证摘要）—— 这是实打实的越权读取。
        //
        // 【为什么范围外返回空页而不是报错】这是分页查询接口，调用方传了范围外的 OrgId
        //   时返回空页，与"该范围内确实没数据"表现一致；报错会把前端的筛选交互
        //   变成一堆红色提示，而用户只是换了个学校下拉。
        var scope = await _dataScope.GetAsync();

        if (scope.IsEmpty)
        {
            return new PagedResultDto<PredefineUserDto>(0, new List<PredefineUserDto>());
        }

        // 不受限（省厅 / admin）时传 null，等于不加范围条件
        var scopePathCode = scope.IsUnrestricted ? null : scope.OrgPathCode;

        // ★ 「仅本级」必须把"后代"这一半**关掉**：换成一个不可能出现的前缀，
        //   让 StartsWith 永不命中，只剩"路径完全相等"生效。
        //
        //   这里原先直接用了 `scopePathCode + "."`，等于**无论什么范围都按"本级及下级"处理** ——
        //   于是给角色配了「仅本级」之后，预置名单照旧能看到下属县的人。
        //   这个方向的错误最危险：它是**放宽**可见性（管理员以为收紧了），而且不报错。
        //   （集成测试 `市局配置为仅本级_看得到本局预置名单_看不到下属县的` 抓到的就是它。）
        var scopeChildPrefix = scopePathCode == null
            ? null
            : (scope.Scope == DataScopes.OrgOnly ? DataScopes.ImpossiblePathPrefix : scopePathCode + ".");

        // 先按组织范围解析出路径前缀；为空表示不限组织
        string? pathPrefix = null;
        if (input.OrgId.HasValue)
        {
            var org = await _orgs.FindAsync(input.OrgId.Value);
            if (org == null)
            {
                return new PagedResultDto<PredefineUserDto>(0, new List<PredefineUserDto>());
            }

            pathPrefix = input.IncludeDescendantOrgs ? org.PathCode + "." : null;

            if (!input.IncludeDescendantOrgs)
            {
                var list = await _predefines.GetSortedListAsync(
                    BuildPredicate(input, null, org.PathCode, scopePathCode, scopeChildPrefix),
                    input.Sorting ?? "CreationTime DESC");
                var total = list.Count;
                return new PagedResultDto<PredefineUserDto>(
                    total,
                    list.Skip(input.SkipCount).Take(input.MaxResultCount).Select(ToDto).ToList());
            }

            // 「含下级」= 整棵子树，**包含该组织自己**：把 exactPathCode 一起传下去，
            // 谓词里与"后代前缀"按"或"处理。只传前缀会把本级建的记录排除掉 ——
            // 而本级恰恰是市局/省厅自己建的账号，现象是"刚建完就查不到"。
            var subtreePredicate = BuildPredicate(input, pathPrefix, org.PathCode, scopePathCode, scopeChildPrefix);

            var subtreeItems = await _predefines.GetPagedListAsync(
                subtreePredicate,
                input.Sorting ?? "CreationTime DESC",
                input.SkipCount,
                input.MaxResultCount);

            var subtreeCount = await _predefines.GetCountAsync(subtreePredicate);

            return new PagedResultDto<PredefineUserDto>(
                subtreeCount, subtreeItems.Select(ToDto).ToList());
        }

        var predicate = BuildPredicate(input, pathPrefix, null, scopePathCode, scopeChildPrefix);

        var items = await _predefines.GetPagedListAsync(
            predicate,
            input.Sorting ?? "CreationTime DESC",
            input.SkipCount,
            input.MaxResultCount);

        var count = await _predefines.GetCountAsync(predicate);

        return new PagedResultDto<PredefineUserDto>(count, items.Select(ToDto).ToList());
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Query)]
    public async Task<PredefineUserDto> GetAsync(Guid id)
    {
        return ToDto(await _predefines.GetAsync(id));
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Create)]
    public async Task<PredefineUserDto> CreateAsync(CreatePredefineUserDto input)
    {
        var entity = await BuildPredefineAsync(input, batchNo: null, rowIndex: 1);
        await _predefines.InsertAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Import)]
    public async Task<ImportPredefineResultDto> ImportAsync(ImportPredefineUsersDto input)
    {
        var batchNo = input.BatchNo.IsNullOrWhiteSpace()
            ? $"B{_clock.Now:yyyyMMddHHmmss}"
            : input.BatchNo!.Trim();

        var result = new ImportPredefineResultDto
        {
            BatchNo = batchNo,
            Total = input.Users.Count
        };

        for (var i = 0; i < input.Users.Count; i++)
        {
            var row = input.Users[i];
            try
            {
                var entity = await BuildPredefineAsync(row, batchNo, i + 1);
                await _predefines.InsertAsync(entity, autoSave: true);
                result.Succeeded++;
            }
            catch (Exception ex)
            {
                // 逐行容错：单行失败不影响其它行，最后统一返回失败明细
                result.Errors.Add(new ImportPredefineErrorDto
                {
                    RowIndex = i + 1,
                    RealName = row.RealName,
                    PhoneNumber = row.PhoneNumber,
                    Message = ex is BusinessException be ? be.Message : ex.Message
                });
            }
        }

        return result;
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Update)]
    public async Task<PredefineUserDto> RenewActivationAsync(Guid id, int validDays = 30)
    {
        var entity = await _predefines.GetAsync(id);

        if (entity.Status == UserPredefineStatus.Activated)
        {
            throw new BusinessException(XjglDomainErrorCodes.UserAlreadyActivated);
        }

        entity.RenewActivation(GenerateActivationCode(), _clock.Now.AddDays(Math.Max(1, validDays)));

        await _predefines.UpdateAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Update)]
    public async Task<PredefineUserDto> RevokeAsync(Guid id)
    {
        var entity = await _predefines.GetAsync(id);

        if (entity.Status == UserPredefineStatus.Activated)
        {
            throw new BusinessException(XjglDomainErrorCodes.UserAlreadyActivated)
                .WithData("reason", "已激活的账号请通过「用户管理」停用，而不是撤销预置记录");
        }

        entity.Status = UserPredefineStatus.Revoked;
        await _predefines.UpdateAsync(entity, autoSave: true);

        return ToDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.UserProvision.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        var entity = await _predefines.GetAsync(id);

        if (entity.Status == UserPredefineStatus.Activated)
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "已激活的预置记录不允许删除，以保留审计痕迹");
        }

        await _predefines.DeleteAsync(entity, autoSave: true);
    }

    // ------------------------------------------------------------------
    // 本人激活（无需登录）
    // ------------------------------------------------------------------

    /// <summary>
    /// 本人激活账号。
    ///
    /// 安全约束：必须同时匹配 手机号 + 姓名 + 激活码；预置记录含身份证号时还要匹配身份证号。
    /// 任何一项不符都拒绝，且不泄露「哪一项不对」以外的信息。
    /// </summary>
    [AllowAnonymous]
    public async Task<ActivateUserResultDto> ActivateAsync(ActivateUserDto input)
    {
        var phone = input.PhoneNumber.Trim();
        var realName = input.RealName.Trim();
        var code = input.ActivationCode.Trim().ToUpperInvariant();

        var predefine = await _predefines.FindAsync(p => p.PhoneNumber == phone && p.ActivationCode == code);

        if (predefine == null)
        {
            // 不区分「手机号不存在」与「激活码错误」，避免被用来枚举名单
            throw new BusinessException(XjglDomainErrorCodes.PredefinedUserNotFound);
        }

        switch (predefine.Status)
        {
            case UserPredefineStatus.Activated:
                throw new BusinessException(XjglDomainErrorCodes.UserAlreadyActivated);

            case UserPredefineStatus.Revoked:
                throw new BusinessException(XjglDomainErrorCodes.PredefinedUserNotFound)
                    .WithData("reason", "该预置记录已被撤销，请联系管理员");
        }

        if (!string.Equals(predefine.RealName, realName, StringComparison.Ordinal))
        {
            throw new BusinessException(XjglDomainErrorCodes.IdentityMismatch)
                .WithData("reason", "姓名与预置名单不一致");
        }

        if (predefine.ActivationExpiresAt <= _clock.Now)
        {
            predefine.Status = UserPredefineStatus.Expired;
            await _predefines.UpdateAsync(predefine, autoSave: true);

            throw new BusinessException(XjglDomainErrorCodes.ActivationCodeExpired);
        }

        await VerifyIdCardAsync(predefine, input.IdCardNumber);

        // ---- 创建 ABP 用户 ----
        var userId = GuidGenerator.Create();
        var user = new IdentityUser(userId, phone, $"{phone}@xjgl.local")
        {
            Name = realName
        };

        var createResult = await _userManager.CreateAsync(user, input.Password);
        if (!createResult.Succeeded)
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData)
                .WithData("reason", string.Join("；", createResult.Errors.Select(e => e.Description)));
        }

        await _userManager.SetPhoneNumberAsync(user, phone);

        // ---- 授予预置角色（角色不存在则跳过，不阻断激活）----
        var grantedRoles = new List<string>();
        foreach (var roleName in predefine.GetRoleNames())
        {
            if (await _roleManager.FindByNameAsync(roleName) != null)
            {
                await _userManager.AddToRoleAsync(user, roleName);
                grantedRoles.Add(roleName);
            }
            else
            {
                Logger.LogWarning(
                    "预置记录 {PredefineId} 指定的角色 {RoleName} 不存在，已跳过。",
                    predefine.Id, roleName);
            }
        }

        // ---- 标记已激活 ----
        predefine.MarkActivated(userId, _clock.Now);
        await _predefines.UpdateAsync(predefine, autoSave: true);

        return new ActivateUserResultDto
        {
            UserId = userId,
            UserName = user.UserName,
            RealName = realName,
            OrgId = predefine.OrgId,
            Roles = grantedRoles
        };
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private async Task VerifyIdCardAsync(SysUserPredefine predefine, string? idCardNumber)
    {
        if (predefine.IdCardHash.IsNullOrWhiteSpace())
        {
            // 预置时未登记身份证号，不做校验
            return;
        }

        if (idCardNumber.IsNullOrWhiteSpace())
        {
            throw new BusinessException(XjglDomainErrorCodes.IdentityMismatch)
                .WithData("reason", "该预置记录登记了身份证号，激活时必须提供");
        }

        if (!IdCardValidator.TryNormalize(idCardNumber, out var normalized, out var error))
        {
            throw new BusinessException(XjglDomainErrorCodes.IdentityMismatch).WithData("reason", error);
        }

        var hash = ComputeIdCardHash(normalized);
        if (!string.Equals(hash, predefine.IdCardHash, StringComparison.Ordinal))
        {
            throw new BusinessException(XjglDomainErrorCodes.IdentityMismatch)
                .WithData("reason", "身份证号与预置名单不一致");
        }

        await Task.CompletedTask;
    }

    private async Task<SysUserPredefine> BuildPredefineAsync(
        CreatePredefineUserDto input,
        string? batchNo,
        int rowIndex)
    {
        var phone = input.PhoneNumber.Trim();
        var realName = input.RealName.Trim();

        if (!RareCharacterText.IsValidPersonName(realName, out var nameError))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("row", rowIndex)
                .WithData("reason", nameError);
        }

        if (!phone.All(char.IsAsciiDigit) || phone.Length != 11)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("row", rowIndex)
                .WithData("reason", "手机号必须是 11 位数字");
        }

        // 手机号在预置名单里唯一
        if (await _predefines.AnyAsync(p => p.PhoneNumber == phone
                                            && p.Status != UserPredefineStatus.Revoked))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData)
                .WithData("reason", $"手机号 {phone} 已在预置名单中");
        }

        var org = await _orgs.FindAsync(input.OrgId)
                  ?? throw new BusinessException(XjglDomainErrorCodes.OrgNotFound);

        // ── 逐级建用户：只能给自己范围内的组织建用户 ──
        //
        // 【修的是什么】在此之前这里**没有任何范围校验**：任何拿到 Sys.UserProvision.Create
        //   权限的人都能给任意组织建预置用户 —— 一个县区管理员可以直接给别的市、甚至
        //   别的省建账号。权限回答"能不能建用户"，范围才回答"能给谁建"。
        //
        // 【为什么用 AllowsEntityPath 而不是 AllowsRequestedOrgPath】
        //   后者**故意放行"请求的是我的上级"**（见 UserDataScope 注释：县区管理员要按市
        //   筛选统计时会传市 Id）。那是给**查询过滤**用的，前提是实体层再叠一层范围约束。
        //   照搬到建用户上就会开一个洞：市级用户传省级组织 Id 会被判为"有交集"而放行，
        //   结果就是**市级能给省级建用户** —— 与"逐级"正好相反。
        //   AllowsEntityPath 的语义才是这里要的：不受限放行、本级及下级限自身与后代、
        //   仅本级只限自身、无范围一律拒绝。
        var scope = await _dataScope.GetAsync();

        if (!scope.AllowsEntityPath(org.PathCode))
        {
            throw new BusinessException(XjglDomainErrorCodes.UserNotInDataScope)
                .WithData("row", rowIndex)
                .WithData("reason", $"组织「{org.Name}」不在你的数据范围内，只能给本级及下级组织建用户");
        }

        var entity = new SysUserPredefine(
            GuidGenerator.Create(),
            realName,
            phone,
            org.Id,
            GenerateActivationCode(),
            _clock.Now.AddDays(Math.Max(1, input.ActivationValidDays)));

        entity.OrgPathCode = org.PathCode;
        entity.RoleNames = input.RoleNames;
        entity.Position = input.Position;
        entity.Remark = input.Remark;
        entity.BatchNo = batchNo;

        if (!input.IdCardNumber.IsNullOrWhiteSpace())
        {
            if (!IdCardValidator.TryNormalize(input.IdCardNumber, out var normalized, out var error))
            {
                throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("row", rowIndex)
                    .WithData("reason", error);
            }

            entity.IdCardHash = ComputeIdCardHash(normalized);
            entity.IdCardEncrypted = _stringEncryptionService.Encrypt(normalized);
        }

        return entity;
    }

    private System.Linq.Expressions.Expression<Func<SysUserPredefine, bool>>? BuildPredicate(
        PredefineUserQueryDto input,
        string? descendantPrefix,
        string? exactPathCode,
        string? scopePathCode,
        string? scopeChildPrefix)
    {
        var hasKeyword = !input.Keyword.IsNullOrWhiteSpace();
        var keyword = input.Keyword?.Trim();
        var hasStatus = !input.Status.IsNullOrWhiteSpace();
        var status = input.Status;
        var hasDescendant = !descendantPrefix.IsNullOrWhiteSpace();
        var hasExact = !exactPathCode.IsNullOrWhiteSpace();
        var hasOrgFilter = hasDescendant || hasExact;
        var hasScope = !scopePathCode.IsNullOrWhiteSpace();
        var hasScopeChildPrefix = !scopeChildPrefix.IsNullOrWhiteSpace();

        // hasScope 必须参与这个判断：否则"不带任何筛选条件"的请求会拿到 null 谓词，
        // 也就是**不加任何 where** —— 那正是范围泄露的入口。
        if (!hasKeyword && !hasStatus && !hasOrgFilter && !hasScope)
        {
            return null;
        }

        return p =>
            (!hasKeyword || p.RealName.Contains(keyword!) || p.PhoneNumber.Contains(keyword!)) &&
            (!hasStatus || p.Status == status) &&
            // 组织条件：本级（路径相等）与后代（前缀匹配）是**或**的关系。
            // 「含下级」的语义是"整棵子树"，包含根自己 —— 两个 AND 会把本级漏掉，
            // 而本级正是市局/省厅自己建的账号，现象是"刚建完就查不到"。
            (!hasOrgFilter
             || (hasExact && p.OrgPathCode == exactPathCode)
             || (hasDescendant && p.OrgPathCode != null && p.OrgPathCode.StartsWith(descendantPrefix!))) &&
            // 数据范围：路径完全相等，或落在"我的路径."之下。
            // ⚠ 「仅本级」时 scopeChildPrefix 是哨兵前缀，StartsWith 永不命中 ——
            //   这一条不能省，省了 OrgOnly 就会退化成"本级及下级"（比配置更宽）。
            (!hasScope || (p.OrgPathCode != null &&
                           (p.OrgPathCode == scopePathCode
                            || (hasScopeChildPrefix && p.OrgPathCode.StartsWith(scopeChildPrefix!)))));
    }

    private string ComputeIdCardHash(string normalizedIdCard)
    {
        var key = _configuration["Xjgl:Security:IdCardHmacKey"];

        if (key.IsNullOrWhiteSpace())
        {
            // 开发默认值；生产必须通过配置/环境变量注入（Xjgl__Security__IdCardHmacKey）
            key = "xjgl-dev-idcard-hmac-key-please-change-in-production";
        }

        return IdCardValidator.ComputeIdCardHash(normalizedIdCard, Encoding.UTF8.GetBytes(key!));
    }

    private static string GenerateActivationCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(ActivationCodeLength);
        var chars = new char[ActivationCodeLength];

        for (var i = 0; i < ActivationCodeLength; i++)
        {
            chars[i] = ActivationCodeAlphabet[bytes[i] % ActivationCodeAlphabet.Length];
        }

        return new string(chars);
    }

    /// <summary>
    /// 实体 → 对外 DTO。
    ///
    /// 从静态改成实例，是因为掩码要解密（<see cref="_idCardProtector"/>）——
    /// 明文只在这一步短暂存在，绝不进 DTO。
    /// </summary>
    private PredefineUserDto ToDto(SysUserPredefine p) => new()
    {
        Id = p.Id,
        RealName = p.RealName,
        PhoneNumber = MaskPhone(p.PhoneNumber),
        MaskedIdCard = MaskIdCardLast4(p.IdCardEncrypted),
        OrgId = p.OrgId,
        OrgPathCode = p.OrgPathCode,
        RoleNames = p.RoleNames,
        Position = p.Position,
        Status = p.Status,
        StatusName = UserPredefineStatus.GetDisplayName(p.Status),
        ActivationCode = p.ActivationCode,
        ActivationExpiresAt = p.ActivationExpiresAt,
        ActivatedUserId = p.ActivatedUserId,
        ActivatedAt = p.ActivatedAt,
        BatchNo = p.BatchNo,
        Remark = p.Remark,
        CreationTime = p.CreationTime
    };

    /// <summary>手机号脱敏：138****8000。</summary>
    private static string MaskPhone(string phone)
    {
        return phone.Length == 11 ? $"{phone[..3]}****{phone[7..]}" : phone;
    }

    /// <summary>
    /// 身份证**只保留后 4 位**的掩码（`**************1234`）。
    ///
    /// 【为什么比学生那条（前 6 后 4）更严】这里不共用 `IIdCardProtector.MaskPlain`，
    /// 是因为两者的读者不同：学生名单是学籍管理员的工作底稿，前 6 位（行政区划码）
    /// 对他判断"这个号对不对"有帮助；而预置名单导出是一份会被转发、会落盘的**通讯录**，
    /// 前 6 位同样是身份信息的一部分，能不出去就不出去。
    ///
    /// 解密失败（密钥轮换后的老数据）返回 null，让那一格显示 `—`，
    /// 绝不能让一条坏数据把整页列表变成 500。
    /// </summary>
    private string? MaskIdCardLast4(string? idCardEncrypted)
    {
        var plain = _idCardProtector.Decrypt(idCardEncrypted);

        if (plain.IsNullOrWhiteSpace())
        {
            return null;
        }

        return plain.Length > 4
            ? new string('*', plain.Length - 4) + plain[^4..]
            : new string('*', plain.Length);
    }
}
