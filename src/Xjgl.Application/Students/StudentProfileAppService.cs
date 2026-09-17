using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Permissions;
using Xjgl.Validation;

namespace Xjgl.Students;

/// <summary>
/// 学生档案子表实现（监护人 / 学习简历 / 奖励 / 处分 / 资助 / 体检）。
///
/// 六张子表的实现结构完全一致，每个方法都走同一条链路：
///   1. 用 <see cref="IStudentAccessGuard"/> 校验学生主档是否存在、是否在数据范围内
///      （**子表接口是独立路由，不校验就等于越权读家长信息**）；
///   2. 读写子表行（行上冗余 <c>StudentId</c>，所有查询都按它过滤）；
///   3. 出参把码值翻译成中文名，身份证号只给掩码。
///
/// 顺序号 <c>Seq</c> 由服务端分配（新增时取当前最大值 +1），
/// 不让前端传：两个标签页同时编辑时，前端算出来的序号必然打架。
/// </summary>
[Authorize]
public class StudentProfileAppService : XjglAppService, IStudentProfileAppService
{
    private const string GuardianRelationCategory = "GUARDIAN_RELATION";
    private const string RewardTypeCategory = "REWARD_TYPE";
    private const string RewardLevelCategory = "REWARD_LEVEL";
    private const string PunishTypeCategory = "PUNISH_TYPE";
    private const string SubsidyTypeCategory = "SUBSIDY_TYPE";
    private const string HealthCategory = "HEALTH";

    private const string QueryPermission = MetadataPermissions.Stu.Student.Query;
    private const string UpdatePermission = MetadataPermissions.Stu.Student.Update;

    private readonly IStudentAccessGuard _guard;
    private readonly IIdCardProtector _idCards;
    private readonly ICodeAppService _codes;

    private readonly IXjglRepository<StuGuardian> _guardians;
    private readonly IXjglRepository<StuResume> _resumes;
    private readonly IXjglRepository<StuReward> _rewards;
    private readonly IXjglRepository<StuPunishment> _punishments;
    private readonly IXjglRepository<StuSubsidy> _subsidies;
    private readonly IXjglRepository<StuPhysical> _physicals;

    public StudentProfileAppService(
        IStudentAccessGuard guard,
        IIdCardProtector idCards,
        ICodeAppService codes,
        IXjglRepository<StuGuardian> guardians,
        IXjglRepository<StuResume> resumes,
        IXjglRepository<StuReward> rewards,
        IXjglRepository<StuPunishment> punishments,
        IXjglRepository<StuSubsidy> subsidies,
        IXjglRepository<StuPhysical> physicals)
    {
        _guard = guard;
        _idCards = idCards;
        _codes = codes;
        _guardians = guardians;
        _resumes = resumes;
        _rewards = rewards;
        _punishments = punishments;
        _subsidies = subsidies;
        _physicals = physicals;
    }

    // ==================================================================
    // 监护人
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<GuardianDto>> GetGuardiansAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _guardians.GetSortedListAsync(g => g.StudentId == studentId, "Seq ASC");
        var relations = await GetCodeNameMapAsync(GuardianRelationCategory);

        return rows.Select(g =>
        {
            var dto = ToGuardianDto(g);
            dto.RelationName = Lookup(relations, g.RelationCode);

            return dto;
        }).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<GuardianDto> CreateGuardianAsync(Guid studentId, CreateUpdateGuardianDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var siblings = await _guardians.GetListAsync(g => g.StudentId == studentId);

        var entity = new StuGuardian(
            GuidGenerator.Create(),
            studentId,
            siblings.Count == 0 ? 1 : siblings.Max(s => s.Seq) + 1,
            input.Name.Trim());

        ApplyGuardian(entity, input);

        await _guardians.InsertAsync(entity, autoSave: true);

        // 「第一监护人」全校唯一：设了新的就把旧的取消，
        // 否则界面上会出现两个"第一监护人"，谁也说不清该联系谁
        if (entity.IsPrimary)
        {
            await ClearOtherPrimaryGuardiansAsync(siblings, entity.Id);
        }

        var created = ToGuardianDto(entity);
        created.RelationName = Lookup(await GetCodeNameMapAsync(GuardianRelationCategory), entity.RelationCode);

        return created;
    }

    [Authorize(UpdatePermission)]
    public async Task<GuardianDto> UpdateGuardianAsync(Guid id, CreateUpdateGuardianDto input)
    {
        var entity = await _guardians.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplyGuardian(entity, input);

        await _guardians.UpdateAsync(entity, autoSave: true);

        if (entity.IsPrimary)
        {
            var siblings = await _guardians.GetListAsync(g => g.StudentId == entity.StudentId && g.Id != entity.Id);

            await ClearOtherPrimaryGuardiansAsync(siblings, entity.Id);
        }

        var updated = ToGuardianDto(entity);
        updated.RelationName = Lookup(await GetCodeNameMapAsync(GuardianRelationCategory), entity.RelationCode);

        return updated;
    }

    [Authorize(UpdatePermission)]
    public async Task DeleteGuardianAsync(Guid id)
    {
        var entity = await _guardians.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _guardians.DeleteAsync(entity, autoSave: true);
    }

    private void ApplyGuardian(StuGuardian entity, CreateUpdateGuardianDto input)
    {
        entity.Name = input.Name.Trim();
        entity.RelationCode = NullIfEmpty(input.RelationCode);
        entity.PhoneNumber = NullIfEmpty(input.PhoneNumber);
        entity.WorkUnit = NullIfEmpty(input.WorkUnit);
        entity.Address = NullIfEmpty(input.Address);
        entity.IsPrimary = input.IsPrimary;
        entity.Remark = NullIfEmpty(input.Remark);

        // 身份证号：留空 = 不修改（界面只显示掩码，用户没改就不该回传）
        if (!input.IdCardNumber.IsNullOrWhiteSpace())
        {
            if (!IdCardValidator.TryNormalize(input.IdCardNumber, out var idCard, out var error))
            {
                throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                    .WithData("reason", $"监护人身份证号不合法：{error}");
            }

            entity.IdCardEncrypted = _idCards.Encrypt(idCard);
        }
    }

    private async Task ClearOtherPrimaryGuardiansAsync(List<StuGuardian> siblings, Guid keepId)
    {
        var changed = siblings.Where(s => s.IsPrimary && s.Id != keepId).ToList();

        if (changed.Count == 0)
        {
            return;
        }

        foreach (var item in changed)
        {
            item.IsPrimary = false;
        }

        await _guardians.UpdateManyAsync(changed, autoSave: true);
    }

    private GuardianDto ToGuardianDto(StuGuardian entity)
    {
        return new GuardianDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Seq = entity.Seq,
            Name = entity.Name,
            RelationCode = entity.RelationCode,
            MaskedIdCard = _idCards.MaskEncrypted(entity.IdCardEncrypted),
            HasIdCard = !entity.IdCardEncrypted.IsNullOrWhiteSpace(),
            PhoneNumber = entity.PhoneNumber,
            WorkUnit = entity.WorkUnit,
            Address = entity.Address,
            IsPrimary = entity.IsPrimary,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 学习简历
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<ResumeDto>> GetResumesAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _resumes.GetSortedListAsync(r => r.StudentId == studentId, "Seq ASC");

        return rows.Select(ToResumeDto).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<ResumeDto> CreateResumeAsync(Guid studentId, CreateUpdateResumeDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var siblings = await _resumes.GetListAsync(r => r.StudentId == studentId);

        var entity = new StuResume(
            GuidGenerator.Create(),
            studentId,
            siblings.Count == 0 ? 1 : siblings.Max(s => s.Seq) + 1,
            input.SchoolName.Trim());

        ApplyResume(entity, input);

        await _resumes.InsertAsync(entity, autoSave: true);

        return ToResumeDto(entity);
    }

    [Authorize(UpdatePermission)]
    public async Task<ResumeDto> UpdateResumeAsync(Guid id, CreateUpdateResumeDto input)
    {
        var entity = await _resumes.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplyResume(entity, input);

        await _resumes.UpdateAsync(entity, autoSave: true);

        return ToResumeDto(entity);
    }

    [Authorize(UpdatePermission)]
    public async Task DeleteResumeAsync(Guid id)
    {
        var entity = await _resumes.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _resumes.DeleteAsync(entity, autoSave: true);
    }

    private static void ApplyResume(StuResume entity, CreateUpdateResumeDto input)
    {
        // 起止日期反了就明确报错：简历按时间排序展示，反的区间会让时间轴看起来像乱序
        if (input.StartDate.HasValue && input.EndDate.HasValue && input.EndDate < input.StartDate)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "学习简历的结束日期不能早于开始日期");
        }

        entity.StartDate = input.StartDate;
        entity.EndDate = input.EndDate;
        entity.SchoolName = input.SchoolName.Trim();
        entity.GradeName = NullIfEmpty(input.GradeName);
        entity.ClassName = NullIfEmpty(input.ClassName);
        entity.Post = NullIfEmpty(input.Post);
        entity.Referee = NullIfEmpty(input.Referee);
        entity.Remark = NullIfEmpty(input.Remark);
    }

    private static ResumeDto ToResumeDto(StuResume entity)
    {
        return new ResumeDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Seq = entity.Seq,
            StartDate = entity.StartDate,
            EndDate = entity.EndDate,
            SchoolName = entity.SchoolName,
            GradeName = entity.GradeName,
            ClassName = entity.ClassName,
            Post = entity.Post,
            Referee = entity.Referee,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 奖励
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<RewardDto>> GetRewardsAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _rewards.GetSortedListAsync(r => r.StudentId == studentId, "Seq ASC");
        var types = await GetCodeNameMapAsync(RewardTypeCategory);
        var levels = await GetCodeNameMapAsync(RewardLevelCategory);

        return rows.Select(r =>
        {
            var dto = ToRewardDto(r);
            dto.RewardTypeName = Lookup(types, r.RewardTypeCode);
            dto.RewardLevelName = Lookup(levels, r.RewardLevelCode);

            return dto;
        }).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<RewardDto> CreateRewardAsync(Guid studentId, CreateUpdateRewardDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var siblings = await _rewards.GetListAsync(r => r.StudentId == studentId);

        var entity = new StuReward(
            GuidGenerator.Create(),
            studentId,
            siblings.Count == 0 ? 1 : siblings.Max(s => s.Seq) + 1,
            input.RewardDate.Date,
            input.RewardName.Trim());

        ApplyReward(entity, input);

        await _rewards.InsertAsync(entity, autoSave: true);

        var dto = ToRewardDto(entity);
        dto.RewardTypeName = Lookup(await GetCodeNameMapAsync(RewardTypeCategory), entity.RewardTypeCode);
        dto.RewardLevelName = Lookup(await GetCodeNameMapAsync(RewardLevelCategory), entity.RewardLevelCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task<RewardDto> UpdateRewardAsync(Guid id, CreateUpdateRewardDto input)
    {
        var entity = await _rewards.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplyReward(entity, input);

        await _rewards.UpdateAsync(entity, autoSave: true);

        var dto = ToRewardDto(entity);
        dto.RewardTypeName = Lookup(await GetCodeNameMapAsync(RewardTypeCategory), entity.RewardTypeCode);
        dto.RewardLevelName = Lookup(await GetCodeNameMapAsync(RewardLevelCategory), entity.RewardLevelCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task DeleteRewardAsync(Guid id)
    {
        var entity = await _rewards.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _rewards.DeleteAsync(entity, autoSave: true);
    }

    private static void ApplyReward(StuReward entity, CreateUpdateRewardDto input)
    {
        entity.RewardDate = input.RewardDate.Date;
        entity.RewardName = input.RewardName.Trim();
        entity.RewardTypeCode = NullIfEmpty(input.RewardTypeCode);
        entity.RewardLevelCode = NullIfEmpty(input.RewardLevelCode);
        entity.AwardOrg = NullIfEmpty(input.AwardOrg);
        entity.Remark = NullIfEmpty(input.Remark);
    }

    private static RewardDto ToRewardDto(StuReward entity)
    {
        return new RewardDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Seq = entity.Seq,
            RewardDate = entity.RewardDate,
            RewardName = entity.RewardName,
            RewardTypeCode = entity.RewardTypeCode,
            RewardLevelCode = entity.RewardLevelCode,
            AwardOrg = entity.AwardOrg,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 处分
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<PunishmentDto>> GetPunishmentsAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _punishments.GetSortedListAsync(p => p.StudentId == studentId, "Seq ASC");
        var types = await GetCodeNameMapAsync(PunishTypeCategory);

        return rows.Select(p =>
        {
            var dto = ToPunishmentDto(p);
            dto.PunishTypeName = Lookup(types, p.PunishTypeCode);

            return dto;
        }).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<PunishmentDto> CreatePunishmentAsync(Guid studentId, CreateUpdatePunishmentDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var siblings = await _punishments.GetListAsync(p => p.StudentId == studentId);

        var entity = new StuPunishment(
            GuidGenerator.Create(),
            studentId,
            siblings.Count == 0 ? 1 : siblings.Max(s => s.Seq) + 1,
            input.PunishDate.Date,
            input.PunishName.Trim());

        ApplyPunishment(entity, input);

        await _punishments.InsertAsync(entity, autoSave: true);

        var dto = ToPunishmentDto(entity);
        dto.PunishTypeName = Lookup(await GetCodeNameMapAsync(PunishTypeCategory), entity.PunishTypeCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task<PunishmentDto> UpdatePunishmentAsync(Guid id, CreateUpdatePunishmentDto input)
    {
        var entity = await _punishments.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplyPunishment(entity, input);

        await _punishments.UpdateAsync(entity, autoSave: true);

        var dto = ToPunishmentDto(entity);
        dto.PunishTypeName = Lookup(await GetCodeNameMapAsync(PunishTypeCategory), entity.PunishTypeCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task DeletePunishmentAsync(Guid id)
    {
        var entity = await _punishments.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _punishments.DeleteAsync(entity, autoSave: true);
    }

    private static void ApplyPunishment(StuPunishment entity, CreateUpdatePunishmentDto input)
    {
        entity.PunishDate = input.PunishDate.Date;
        entity.PunishName = input.PunishName.Trim();
        entity.PunishTypeCode = NullIfEmpty(input.PunishTypeCode);
        entity.PunishOrg = NullIfEmpty(input.PunishOrg);
        entity.IsRevoked = input.IsRevoked;
        entity.RevokeReason = NullIfEmpty(input.RevokeReason);
        entity.Remark = NullIfEmpty(input.Remark);

        // 撤销日期跟着"是否撤销"走：撤销了却没日期，档案上会显示成一条悬空的记录
        entity.RevokeDate = input.IsRevoked
            ? input.RevokeDate ?? DateTime.UtcNow.Date
            : null;
    }

    private static PunishmentDto ToPunishmentDto(StuPunishment entity)
    {
        return new PunishmentDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Seq = entity.Seq,
            PunishDate = entity.PunishDate,
            PunishName = entity.PunishName,
            PunishTypeCode = entity.PunishTypeCode,
            PunishOrg = entity.PunishOrg,
            IsRevoked = entity.IsRevoked,
            RevokeDate = entity.RevokeDate,
            RevokeReason = entity.RevokeReason,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 资助
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<SubsidyDto>> GetSubsidiesAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _subsidies.GetSortedListAsync(s => s.StudentId == studentId, "Seq ASC");
        var types = await GetCodeNameMapAsync(SubsidyTypeCategory);

        return rows.Select(s =>
        {
            var dto = ToSubsidyDto(s);
            dto.SubsidyTypeName = Lookup(types, s.SubsidyTypeCode);

            return dto;
        }).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<SubsidyDto> CreateSubsidyAsync(Guid studentId, CreateUpdateSubsidyDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var siblings = await _subsidies.GetListAsync(s => s.StudentId == studentId);

        var entity = new StuSubsidy(
            GuidGenerator.Create(),
            studentId,
            siblings.Count == 0 ? 1 : siblings.Max(s => s.Seq) + 1,
            input.AcademicYear.Trim(),
            input.SubsidyName.Trim());

        ApplySubsidy(entity, input);

        await _subsidies.InsertAsync(entity, autoSave: true);

        var dto = ToSubsidyDto(entity);
        dto.SubsidyTypeName = Lookup(await GetCodeNameMapAsync(SubsidyTypeCategory), entity.SubsidyTypeCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task<SubsidyDto> UpdateSubsidyAsync(Guid id, CreateUpdateSubsidyDto input)
    {
        var entity = await _subsidies.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplySubsidy(entity, input);

        await _subsidies.UpdateAsync(entity, autoSave: true);

        var dto = ToSubsidyDto(entity);
        dto.SubsidyTypeName = Lookup(await GetCodeNameMapAsync(SubsidyTypeCategory), entity.SubsidyTypeCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task DeleteSubsidyAsync(Guid id)
    {
        var entity = await _subsidies.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _subsidies.DeleteAsync(entity, autoSave: true);
    }

    private static void ApplySubsidy(StuSubsidy entity, CreateUpdateSubsidyDto input)
    {
        entity.AcademicYear = input.AcademicYear.Trim();
        entity.SubsidyTypeCode = NullIfEmpty(input.SubsidyTypeCode);
        entity.SubsidyName = input.SubsidyName.Trim();
        entity.Amount = input.Amount;
        entity.SubsidyDate = input.SubsidyDate;
        entity.FundingOrg = NullIfEmpty(input.FundingOrg);
        entity.Remark = NullIfEmpty(input.Remark);
    }

    private static SubsidyDto ToSubsidyDto(StuSubsidy entity)
    {
        return new SubsidyDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            Seq = entity.Seq,
            AcademicYear = entity.AcademicYear,
            SubsidyTypeCode = entity.SubsidyTypeCode,
            SubsidyName = entity.SubsidyName,
            Amount = entity.Amount,
            SubsidyDate = entity.SubsidyDate,
            FundingOrg = entity.FundingOrg,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 体质健康（体检）
    // ==================================================================

    [Authorize(QueryPermission)]
    public async Task<List<PhysicalDto>> GetPhysicalsAsync(Guid studentId)
    {
        await _guard.GetInScopeAsync(studentId);

        var rows = await _physicals.GetSortedListAsync(p => p.StudentId == studentId, "AcademicYear DESC");
        var health = await GetCodeNameMapAsync(HealthCategory);

        return rows.Select(p =>
        {
            var dto = ToPhysicalDto(p);
            dto.HealthName = Lookup(health, p.HealthCode);

            return dto;
        }).ToList();
    }

    [Authorize(UpdatePermission)]
    public async Task<PhysicalDto> CreatePhysicalAsync(Guid studentId, CreateUpdatePhysicalDto input)
    {
        await _guard.GetInScopeAsync(studentId);

        var year = input.AcademicYear.Trim();

        // 一个学年一条：重复录入会让"今年体检数据"出现两条，统计时不知取哪条。
        // 因此这里挡住，并告诉用户去改已有记录。
        var exists = await _physicals.AnyAsync(p => p.StudentId == studentId && p.AcademicYear == year);

        if (exists)
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", $"该学生 {year} 学年的体检记录已存在，请直接修改原记录");
        }

        var entity = new StuPhysical(GuidGenerator.Create(), studentId, year);

        ApplyPhysical(entity, input);

        await _physicals.InsertAsync(entity, autoSave: true);

        var dto = ToPhysicalDto(entity);
        dto.HealthName = Lookup(await GetCodeNameMapAsync(HealthCategory), entity.HealthCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task<PhysicalDto> UpdatePhysicalAsync(Guid id, CreateUpdatePhysicalDto input)
    {
        var entity = await _physicals.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        ApplyPhysical(entity, input);

        await _physicals.UpdateAsync(entity, autoSave: true);

        var dto = ToPhysicalDto(entity);
        dto.HealthName = Lookup(await GetCodeNameMapAsync(HealthCategory), entity.HealthCode);

        return dto;
    }

    [Authorize(UpdatePermission)]
    public async Task DeletePhysicalAsync(Guid id)
    {
        var entity = await _physicals.GetAsync(id);

        await _guard.GetInScopeAsync(entity.StudentId);

        await _physicals.DeleteAsync(entity, autoSave: true);
    }

    private static void ApplyPhysical(StuPhysical entity, CreateUpdatePhysicalDto input)
    {
        entity.AcademicYear = input.AcademicYear.Trim();
        entity.CheckDate = input.CheckDate;
        entity.Height = input.Height;
        entity.Weight = input.Weight;
        entity.LeftVision = input.LeftVision;
        entity.RightVision = input.RightVision;
        entity.HealthCode = NullIfEmpty(input.HealthCode);
        entity.DoctorAdvice = NullIfEmpty(input.DoctorAdvice);
        entity.Remark = NullIfEmpty(input.Remark);
    }

    private static PhysicalDto ToPhysicalDto(StuPhysical entity)
    {
        return new PhysicalDto
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            AcademicYear = entity.AcademicYear,
            CheckDate = entity.CheckDate,
            Height = entity.Height,
            Weight = entity.Weight,
            LeftVision = entity.LeftVision,
            RightVision = entity.RightVision,
            HealthCode = entity.HealthCode,
            DoctorAdvice = entity.DoctorAdvice,
            Remark = entity.Remark
        };
    }

    // ==================================================================
    // 公共
    // ==================================================================

    private async Task<Dictionary<string, string>> GetCodeNameMapAsync(string categoryCode)
    {
        try
        {
            var items = await _codes.GetItemsByCodeAsync(categoryCode);

            return items.ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 码表缺失不应该让详情页打不开（与列表页的处理保持一致）
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? Lookup(Dictionary<string, string> map, string? code)
    {
        return code.IsNullOrWhiteSpace() ? null : map.GetValueOrDefault(code!);
    }

    private static string? NullIfEmpty(string? value)
    {
        return value.IsNullOrWhiteSpace() ? null : value.Trim();
    }
}
