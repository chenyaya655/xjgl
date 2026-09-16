using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Permissions;

namespace Xjgl.Organizations;

/// <summary>
/// 年级与班级管理实现。
/// </summary>
[Authorize]
public class GradeClassAppService : XjglAppService, IGradeClassAppService
{
    /// <summary>微代码里年级码的类别编码。</summary>
    private const string GradeCodeCategory = "GRADE";

    /// <summary>学校类型码 -> 默认年级码范围。与微代码 SCHOOL_TYPE 的码值对应。</summary>
    private static readonly Dictionary<string, string[]> DefaultGradeCodesBySchoolType = new()
    {
        ["1"] = new[] { "01", "02", "03", "04", "05", "06" },                   // 小学
        ["2"] = new[] { "07", "08", "09" },                                     // 初中
        ["3"] = new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09" }, // 九年一贯制
        ["4"] = new[] { "07", "08", "09", "10", "11", "12" },                   // 完全中学
        ["5"] = new[] { "10", "11", "12" },                                     // 普通高中
        ["6"] = new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12" } // 十二年一贯制
    };

    private static readonly string[] FallbackGradeCodes =
        { "01", "02", "03", "04", "05", "06", "07", "08", "09" };

    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly ICodeAppService _codeAppService;

    public GradeClassAppService(
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        IXjglRepository<SysOrg> orgs,
        ICodeAppService codeAppService)
    {
        _grades = grades;
        _classes = classes;
        _orgs = orgs;
        _codeAppService = codeAppService;
    }

    // ------------------------------------------------------------------
    // 年级
    // ------------------------------------------------------------------

    public async Task<List<GradeDto>> GetGradesAsync(Guid schoolOrgId, string academicYear)
    {
        var grades = await _grades.GetSortedListAsync(
            g => g.SchoolOrgId == schoolOrgId && g.AcademicYear == academicYear,
            "Sort ASC, GradeCode ASC");

        var counts = await GetClassCountsAsync(grades.Select(g => g.Id).ToList());

        return grades.Select(g =>
        {
            var dto = ToGradeDto(g);
            dto.ClassCount = counts.TryGetValue(g.Id, out var n) ? n : 0;
            return dto;
        }).ToList();
    }

    [Authorize(MetadataPermissions.Sys.Org.Create)]
    public async Task<GradeDto> CreateGradeAsync(CreateUpdateGradeDto input)
    {
        var school = await EnsureSchoolAsync(input.SchoolOrgId);
        var academicYear = NormalizeAcademicYear(input.AcademicYear);
        var gradeCode = input.GradeCode.Trim();

        if (await _grades.AnyAsync(g => g.SchoolOrgId == school.Id
                                        && g.AcademicYear == academicYear
                                        && g.GradeCode == gradeCode))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData)
                .WithData("reason", $"{academicYear} 学年已存在年级码 {gradeCode}");
        }

        var entity = new SysGrade(
            GuidGenerator.Create(),
            school.Id,
            academicYear,
            gradeCode,
            input.GradeName.Trim(),
            input.Sort)
        {
            HeadTeacherUserId = input.HeadTeacherUserId,
            IsEnabled = input.IsEnabled,
            Remark = input.Remark
        };

        await _grades.InsertAsync(entity, autoSave: true);

        return ToGradeDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Org.Update)]
    public async Task<GradeDto> UpdateGradeAsync(Guid id, CreateUpdateGradeDto input)
    {
        var entity = await _grades.GetAsync(id);

        entity.GradeName = input.GradeName.Trim();
        entity.HeadTeacherUserId = input.HeadTeacherUserId;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;
        entity.Remark = input.Remark;
        // 学年与年级码是班级归属的依据，已有班级后不允许改
        if (!string.Equals(entity.AcademicYear, NormalizeAcademicYear(input.AcademicYear), StringComparison.Ordinal)
            || !string.Equals(entity.GradeCode, input.GradeCode.Trim(), StringComparison.Ordinal))
        {
            if (await _classes.AnyAsync(c => c.GradeId == id))
            {
                throw new BusinessException(XjglDomainErrorCodes.SystemCodeNotAllowedToModify)
                    .WithData("reason", "该年级下已有班级，不能修改学年或年级码");
            }

            entity.AcademicYear = NormalizeAcademicYear(input.AcademicYear);
            entity.GradeCode = input.GradeCode.Trim();
        }

        await _grades.UpdateAsync(entity, autoSave: true);

        return ToGradeDto(entity);
    }

    [Authorize(MetadataPermissions.Sys.Org.Delete)]
    public async Task DeleteGradeAsync(Guid id)
    {
        var entity = await _grades.GetAsync(id);

        if (await _classes.AnyAsync(c => c.GradeId == id))
        {
            throw new BusinessException(XjglDomainErrorCodes.DataInUse)
                .WithData("reason", "该年级下还有班级，请先删除班级");
        }

        await _grades.DeleteAsync(entity, autoSave: true);
    }

    // ------------------------------------------------------------------
    // 班级
    // ------------------------------------------------------------------

    public async Task<List<ClassDto>> GetClassesAsync(Guid schoolOrgId, string? academicYear = null, Guid? gradeId = null)
    {
        var hasYear = !academicYear.IsNullOrWhiteSpace();
        var year = academicYear;
        var hasGrade = gradeId.HasValue;
        var gid = gradeId;

        var classes = await _classes.GetSortedListAsync(
            c => c.SchoolOrgId == schoolOrgId
                 && (!hasYear || c.AcademicYear == year)
                 && (!hasGrade || c.GradeId == gid),
            "Sort ASC, ClassNo ASC");

        var gradeIds = classes.Select(c => c.GradeId).Distinct().ToList();
        var grades = gradeIds.Count == 0
            ? new List<SysGrade>()
            : await _grades.GetListAsync(g => gradeIds.Contains(g.Id));

        var gradeNames = grades.ToDictionary(g => g.Id, g => g.GradeName);

        return classes.Select(c => ToClassDto(c, gradeNames.TryGetValue(c.GradeId, out var name) ? name : string.Empty))
            .ToList();
    }

    [Authorize(MetadataPermissions.Sys.Org.Create)]
    public async Task<ClassDto> CreateClassAsync(CreateUpdateClassDto input)
    {
        var grade = await _grades.GetAsync(input.GradeId);
        var classNo = input.ClassNo.Trim();

        if (await _classes.AnyAsync(c => c.GradeId == grade.Id && c.ClassNo == classNo))
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateData)
                .WithData("reason", $"{grade.GradeName} 已存在班号 {classNo}");
        }

        var entity = new SysClass(
            GuidGenerator.Create(),
            grade.SchoolOrgId,
            grade.AcademicYear,
            grade.Id,
            classNo,
            input.ClassName.Trim(),
            input.Sort)
        {
            HeadTeacherUserId = input.HeadTeacherUserId,
            Classroom = input.Classroom,
            IsEnabled = input.IsEnabled
        };

        await _classes.InsertAsync(entity, autoSave: true);

        return ToClassDto(entity, grade.GradeName);
    }

    [Authorize(MetadataPermissions.Sys.Org.Update)]
    public async Task<ClassDto> UpdateClassAsync(Guid id, CreateUpdateClassDto input)
    {
        var entity = await _classes.GetAsync(id);
        var grade = await _grades.GetAsync(entity.GradeId);

        entity.ClassName = input.ClassName.Trim();
        entity.HeadTeacherUserId = input.HeadTeacherUserId;
        entity.Classroom = input.Classroom;
        entity.Sort = input.Sort;
        entity.IsEnabled = input.IsEnabled;

        await _classes.UpdateAsync(entity, autoSave: true);

        return ToClassDto(entity, grade.GradeName);
    }

    [Authorize(MetadataPermissions.Sys.Org.Delete)]
    public async Task DeleteClassAsync(Guid id)
    {
        var entity = await _classes.GetAsync(id);

        // 学生模块（P2）完成后，这里要补上「班级下还有学生则拒绝删除」的校验
        await _classes.DeleteAsync(entity, autoSave: true);
    }

    // ------------------------------------------------------------------
    // 批量生成
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Sys.Org.Create)]
    public async Task<BatchGenerateResultDto<GradeDto>> BatchGenerateGradesAsync(BatchGenerateGradesDto input)
    {
        var school = await EnsureSchoolAsync(input.SchoolOrgId);
        var academicYear = NormalizeAcademicYear(input.AcademicYear);

        var gradeCodes = input.GradeCodes is { Count: > 0 }
            ? input.GradeCodes.Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToArray()
            : ResolveDefaultGradeCodes(input.SchoolTypeCode);

        // 年级名称从微代码 GRADE 里取，保证与全系统一致；取不到则用「年级{码}」兜底
        var gradeNameMap = (await _codeAppService.GetItemsByCodeAsync(GradeCodeCategory))
            .ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);

        var existing = await _grades.GetListAsync(g => g.SchoolOrgId == school.Id && g.AcademicYear == academicYear);
        var existingCodes = existing.Select(g => g.GradeCode).ToHashSet(StringComparer.Ordinal);

        var result = new BatchGenerateResultDto<GradeDto>();

        var order = existing.Count * 10;
        foreach (var code in gradeCodes)
        {
            if (existingCodes.Contains(code))
            {
                result.Skipped++;
                continue;
            }

            order += 10;

            var name = gradeNameMap.TryGetValue(code, out var n) ? n : $"年级{code}";

            var entity = new SysGrade(GuidGenerator.Create(), school.Id, academicYear, code, name, order);

            await _grades.InsertAsync(entity, autoSave: true);

            result.Created++;
            result.Items.Add(ToGradeDto(entity));
        }

        return result;
    }

    [Authorize(MetadataPermissions.Sys.Org.Create)]
    public async Task<BatchGenerateResultDto<ClassDto>> BatchGenerateClassesAsync(BatchGenerateClassesDto input)
    {
        var grade = await _grades.GetAsync(input.GradeId);

        var pattern = input.NamePattern.IsNullOrWhiteSpace() ? "{grade}{no}班" : input.NamePattern!;

        var existing = await _classes.GetListAsync(c => c.GradeId == grade.Id);
        var existingNos = existing.Select(c => c.ClassNo).ToHashSet(StringComparer.Ordinal);

        var result = new BatchGenerateResultDto<ClassDto>();

        for (var i = 0; i < input.ClassCount; i++)
        {
            var no = input.StartNo + i;
            var classNo = no.ToString("00");

            if (existingNos.Contains(classNo))
            {
                result.Skipped++;
                continue;
            }

            var className = pattern
                .Replace("{grade}", grade.GradeName, StringComparison.Ordinal)
                .Replace("{no}", no.ToString(), StringComparison.Ordinal);

            var entity = new SysClass(
                GuidGenerator.Create(),
                grade.SchoolOrgId,
                grade.AcademicYear,
                grade.Id,
                classNo,
                className,
                no * 10);

            await _classes.InsertAsync(entity, autoSave: true);

            result.Created++;
            result.Items.Add(ToClassDto(entity, grade.GradeName));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private async Task<SysOrg> EnsureSchoolAsync(Guid schoolOrgId)
    {
        var org = await _orgs.FindAsync(schoolOrgId)
                  ?? throw new BusinessException(XjglDomainErrorCodes.OrgNotFound);

        if (!string.Equals(org.OrgType, OrgTypes.School, StringComparison.Ordinal))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "年级班级只能挂在「学校」类型的组织下");
        }

        return org;
    }

    private static string[] ResolveDefaultGradeCodes(string? schoolTypeCode)
    {
        if (!schoolTypeCode.IsNullOrWhiteSpace()
            && DefaultGradeCodesBySchoolType.TryGetValue(schoolTypeCode!.Trim(), out var codes))
        {
            return codes;
        }

        return FallbackGradeCodes;
    }

    /// <summary>学年统一写成 2025-2026；只传起始年时自动补下一年。</summary>
    private static string NormalizeAcademicYear(string academicYear)
    {
        var value = academicYear.Trim();

        if (value.Length == 4 && int.TryParse(value, out var startYear))
        {
            return $"{startYear}-{startYear + 1}";
        }

        return value;
    }

    private async Task<Dictionary<Guid, int>> GetClassCountsAsync(List<Guid> gradeIds)
    {
        if (gradeIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var classes = await _classes.GetListAsync(c => gradeIds.Contains(c.GradeId));

        return classes.GroupBy(c => c.GradeId).ToDictionary(g => g.Key, g => g.Count());
    }

    private static GradeDto ToGradeDto(SysGrade g) => new()
    {
        Id = g.Id,
        SchoolOrgId = g.SchoolOrgId,
        AcademicYear = g.AcademicYear,
        GradeCode = g.GradeCode,
        GradeName = g.GradeName,
        HeadTeacherUserId = g.HeadTeacherUserId,
        Sort = g.Sort,
        IsEnabled = g.IsEnabled,
        Remark = g.Remark
    };

    private static ClassDto ToClassDto(SysClass c, string gradeName) => new()
    {
        Id = c.Id,
        SchoolOrgId = c.SchoolOrgId,
        AcademicYear = c.AcademicYear,
        GradeId = c.GradeId,
        GradeName = gradeName,
        ClassNo = c.ClassNo,
        ClassName = c.ClassName,
        HeadTeacherUserId = c.HeadTeacherUserId,
        Classroom = c.Classroom,
        Sort = c.Sort,
        IsEnabled = c.IsEnabled
    };
}
