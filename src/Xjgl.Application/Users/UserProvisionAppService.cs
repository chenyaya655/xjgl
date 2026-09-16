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
    private readonly IdentityUserManager _userManager;
    private readonly IdentityRoleManager _roleManager;
    private readonly IStringEncryptionService _stringEncryptionService;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    public UserProvisionAppService(
        IXjglRepository<SysUserPredefine> predefines,
        IXjglRepository<SysOrg> orgs,
        IdentityUserManager userManager,
        IdentityRoleManager roleManager,
        IStringEncryptionService stringEncryptionService,
        IConfiguration configuration,
        IClock clock)
    {
        _predefines = predefines;
        _orgs = orgs;
        _userManager = userManager;
        _roleManager = roleManager;
        _stringEncryptionService = stringEncryptionService;
        _configuration = configuration;
        _clock = clock;
    }

    // ------------------------------------------------------------------
    // 管理端
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.UserProvision.Query)]
    public async Task<PagedResultDto<PredefineUserDto>> GetListAsync(PredefineUserQueryDto input)
    {
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
                    BuildPredicate(input, null, org.PathCode), input.Sorting ?? "CreationTime DESC");
                var total = list.Count;
                return new PagedResultDto<PredefineUserDto>(
                    total,
                    list.Skip(input.SkipCount).Take(input.MaxResultCount).Select(ToDto).ToList());
            }
        }

        var predicate = BuildPredicate(input, pathPrefix, null);

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
        string? exactPathCode)
    {
        var hasKeyword = !input.Keyword.IsNullOrWhiteSpace();
        var keyword = input.Keyword?.Trim();
        var hasStatus = !input.Status.IsNullOrWhiteSpace();
        var status = input.Status;
        var hasDescendant = !descendantPrefix.IsNullOrWhiteSpace();
        var hasExact = !exactPathCode.IsNullOrWhiteSpace();

        if (!hasKeyword && !hasStatus && !hasDescendant && !hasExact)
        {
            return null;
        }

        return p =>
            (!hasKeyword || p.RealName.Contains(keyword!) || p.PhoneNumber.Contains(keyword!)) &&
            (!hasStatus || p.Status == status) &&
            (!hasDescendant || (p.OrgPathCode != null && p.OrgPathCode.StartsWith(descendantPrefix!))) &&
            (!hasExact || (exactPathCode == null || p.OrgPathCode == exactPathCode));
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

    private static PredefineUserDto ToDto(SysUserPredefine p) => new()
    {
        Id = p.Id,
        RealName = p.RealName,
        PhoneNumber = MaskPhone(p.PhoneNumber),
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
}
