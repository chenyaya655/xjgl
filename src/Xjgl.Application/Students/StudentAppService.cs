using System.Text;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Settings;
using Volo.Abp.Timing;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Sequences;
using Xjgl.Settings;
using Xjgl.Text;
using Xjgl.Validation;

namespace Xjgl.Students;

/// <summary>
/// 学生学籍管理实现。
/// </summary>
[Authorize]
public class StudentAppService : XjglAppService, IStudentAppService
{
    private const string GenderCodeCategory = "SEX";
    private const string StudentStatusCodeCategory = "STUDENT_STATUS";

    private readonly IXjglRepository<StuStudent> _students;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly ISequenceService _sequenceService;
    private readonly ICodeAppService _codeAppService;
    private readonly ISettingProvider _settingProvider;
    private readonly IClock _clock;
    private readonly IUserDataScopeProvider _dataScopeProvider;

    /// <summary>身份证件的加密 / 摘要 / 脱敏（与监护人子表共用同一套规则）。</summary>
    private readonly IIdCardProtector _idCards;

    /// <summary>数据范围守卫（与学生子表服务共用）。</summary>
    private readonly IStudentAccessGuard _guard;

    public StudentAppService(
        IXjglRepository<StuStudent> students,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        ISequenceService sequenceService,
        ICodeAppService codeAppService,
        ISettingProvider settingProvider,
        IClock clock,
        IUserDataScopeProvider dataScopeProvider,
        IIdCardProtector idCards,
        IStudentAccessGuard guard)
    {
        _students = students;
        _orgs = orgs;
        _grades = grades;
        _classes = classes;
        _sequenceService = sequenceService;
        _codeAppService = codeAppService;
        _settingProvider = settingProvider;
        _clock = clock;
        _dataScopeProvider = dataScopeProvider;
        _idCards = idCards;
        _guard = guard;
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    public async Task<PagedResultDto<StudentListItemDto>> GetListAsync(StudentQueryDto input)
    {
        var scope = await _dataScopeProvider.GetAsync();

        // 未关联组织且非超管：**返回空，而不是报错**。
        // 列表接口报错会把"这个人没有数据权限"变成界面上的一个红条，
        // 而正确的产品行为是他本来就看不到任何数据（空列表）。
        if (scope.IsEmpty)
        {
            return new PagedResultDto<StudentListItemDto>(0, new List<StudentListItemDto>());
        }

        // 请求的组织必须在数据范围内：不在范围内**直接返回空**，不去查库。
        //
        // 注意这里不能用 `s => false` 之类"恒假谓词"来短路 ——
        // SqlSugar 无法把它翻译成 SQL，会直接抛异常（实测踩过）。
        // 空结果的判断放在调用方（也就是这里），谓词构造只负责拼条件。
        if (input.SchoolOrgId.HasValue)
        {
            var requestedOrg = await _orgs.FindAsync(input.SchoolOrgId.Value);

            if (requestedOrg == null || !scope.AllowsRequestedOrgPath(requestedOrg.PathCode))
            {
                return new PagedResultDto<StudentListItemDto>(0, new List<StudentListItemDto>());
            }
        }

        var predicate = await BuildPredicateAsync(input, scope);

        var items = await _students.GetPagedListAsync(
            predicate,
            input.Sorting ?? "StudentNo ASC",
            input.SkipCount,
            input.MaxResultCount);

        var total = await _students.GetCountAsync(predicate);

        return new PagedResultDto<StudentListItemDto>(total, await ToListItemsAsync(items));
    }

    public async Task<StudentDto> GetAsync(Guid id)
    {
        var student = await _students.GetAsync(id);

        // 单条记录：越权要**明确拒绝**而不是返回空 —— 否则调用方会误以为"学生不存在"
        await _guard.EnsureInScopeAsync(student);

        var listItem = (await ToListItemsAsync(new List<StuStudent> { student })).Single();

        var dto = new StudentDto();
        CopyListFields(listItem, dto);

        dto.FormerName = student.FormerName;
        dto.Pinyin = student.Pinyin;
        dto.IdCardTypeCode = student.IdCardTypeCode;
        dto.NationCode = student.NationCode;
        dto.CountryCode = student.CountryCode;
        dto.PoliticalCode = student.PoliticalCode;
        dto.NativePlace = student.NativePlace;
        dto.BirthPlace = student.BirthPlace;
        dto.HouseholdNatureCode = student.HouseholdNatureCode;
        dto.HouseholdAddress = student.HouseholdAddress;
        dto.HomeAddress = student.HomeAddress;
        dto.CurrentAddress = student.CurrentAddress;
        dto.PhoneNumber = student.PhoneNumber;
        dto.PostalCode = student.PostalCode;
        dto.Email = student.Email;
        dto.EnrollDate = student.EnrollDate;
        dto.EnrollTypeCode = student.EnrollTypeCode;
        dto.StudyModeCode = student.StudyModeCode;
        dto.SchoolLengthCode = student.SchoolLengthCode;
        dto.IsMigrantChild = student.IsMigrantChild;
        dto.MigrantTypeCode = student.MigrantTypeCode;
        dto.IsLeftBehindChild = student.IsLeftBehindChild;
        dto.LeftBehindTypeCode = student.LeftBehindTypeCode;
        dto.IsPoor = student.IsPoor;
        dto.IsDisabled = student.IsDisabled;
        dto.DisabilityCode = student.DisabilityCode;
        dto.HealthCode = student.HealthCode;
        dto.PhotoPath = student.PhotoPath;
        dto.Remark = student.Remark;

        return dto;
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    [Authorize(MetadataPermissions.Stu.Student.Create)]
    public async Task<StudentDto> CreateAsync(CreateUpdateStudentDto input)
    {
        if (!RareCharacterText.IsValidPersonName(input.Name, out var nameError))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", nameError);
        }

        var school = await EnsureSchoolAsync(input.SchoolOrgId);

        // 只能往自己数据范围内的学校建学生
        await _guard.EnsureOrgInScopeAsync(school, "该学校不在您的数据范围内，不能为其新增学生");

        var idCard = NormalizeIdCard(input.IdCardNumber);
        var idCardHash = _idCards.ComputeHash(idCard);

        // 写入前查重：同一身份证号在系统内只能有一份有效学籍
        var duplicated = await _students.FindAsync(s => s.IdCardHash == idCardHash);
        if (duplicated != null)
        {
            throw new BusinessException(XjglDomainErrorCodes.DuplicateIdCard)
                .WithData("reason", $"身份证号已存在学籍记录：{duplicated.Name}（学籍辅号 {duplicated.StudentNo}）");
        }

        var studentNo = await GenerateStudentNoAsync(school);

        var entity = new StuStudent(
            GuidGenerator.Create(),
            studentNo,
            input.Name.Trim(),
            input.GenderCode.Trim(),
            input.BirthDate.Date,
            school.Id,
            NormalizeAcademicYear(input.AcademicYear));

        ApplyInput(entity, input, idCard, idCardHash, school);

        await _students.InsertAsync(entity, autoSave: true);

        return await GetAsync(entity.Id);
    }

    [Authorize(MetadataPermissions.Stu.Student.Update)]
    public async Task<StudentDto> UpdateAsync(Guid id, CreateUpdateStudentDto input)
    {
        if (!RareCharacterText.IsValidPersonName(input.Name, out var nameError))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", nameError);
        }

        var entity = await _students.GetAsync(id);

        // 原学校与目标学校都必须在自己范围内（防止把学生"改"到自己看不到的学校）
        await _guard.EnsureInScopeAsync(entity);

        var school = await EnsureSchoolAsync(input.SchoolOrgId);
        await _guard.EnsureOrgInScopeAsync(school, "该学校不在您的数据范围内，不能把学生转到该校");

        var idCard = NormalizeIdCard(input.IdCardNumber);
        var idCardHash = _idCards.ComputeHash(idCard);

        if (!string.Equals(entity.IdCardHash, idCardHash, StringComparison.Ordinal))
        {
            var duplicated = await _students.FindAsync(s => s.IdCardHash == idCardHash && s.Id != id);
            if (duplicated != null)
            {
                throw new BusinessException(XjglDomainErrorCodes.DuplicateIdCard)
                    .WithData("reason", $"身份证号已被 {duplicated.Name}（学籍辅号 {duplicated.StudentNo}）占用");
            }
        }

        entity.Name = input.Name.Trim();
        entity.GenderCode = input.GenderCode.Trim();
        entity.BirthDate = input.BirthDate.Date;
        entity.SchoolOrgId = school.Id;
        entity.AcademicYear = NormalizeAcademicYear(input.AcademicYear);

        ApplyInput(entity, input, idCard, idCardHash, school);

        await _students.UpdateAsync(entity, autoSave: true);

        return await GetAsync(entity.Id);
    }

    [Authorize(MetadataPermissions.Stu.Student.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        var entity = await _students.GetAsync(id);

        await _guard.EnsureInScopeAsync(entity);

        // 说明：学生若处于在途异动流程中，P3 完成后要在这里拒绝删除
        await _students.DeleteAsync(entity, autoSave: true);
    }

    // 注意：单条学生的数据范围校验已上提到 Xjgl.Students.StudentAccessGuard。
    // 学生详情下的六张子表是**独立路由**，不会自动继承主档的过滤条件，
    // 因此这段判断必须放在两个服务都能用到的地方 —— 放在本类的私有方法里，
    // 子表接口就只能复制一份，迟早会漏掉一处（那就是越权读家长身份信息）。

    // ------------------------------------------------------------------
    // 重复学籍检测
    // ------------------------------------------------------------------

    public async Task<StudentDuplicateCheckResultDto> CheckDuplicateAsync(CheckStudentDuplicateDto input)
    {
        var result = new StudentDuplicateCheckResultDto();

        if (!IdCardValidator.TryNormalize(input.IdCardNumber, out var idCard, out var error))
        {
            result.Reasons.Add(error ?? "身份证号不合法");
            return result;
        }

        var hash = _idCards.ComputeHash(idCard);
        var matched = await _students.GetListAsync(s => s.IdCardHash == hash);

        if (input.ExcludeId.HasValue)
        {
            matched = matched.Where(m => m.Id != input.ExcludeId.Value).ToList();
        }

        if (matched.Count > 0)
        {
            result.HasDuplicate = true;
            result.Reasons.Add("身份证号已存在学籍记录");

            var items = await ToListItemsAsync(matched);
            result.Matched = items;

            // 同名同生日能进一步提示「疑似重复建籍」
            var sameNameAndBirth = matched.Any(m =>
                string.Equals(m.Name, input.Name.Trim(), StringComparison.Ordinal)
                && (!input.BirthDate.HasValue || m.BirthDate.Date == input.BirthDate.Value.Date));

            if (sameNameAndBirth)
            {
                result.Reasons.Add("姓名与出生日期也一致，疑似重复建籍");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 内部实现
    // ------------------------------------------------------------------

    private void ApplyInput(
        StuStudent entity,
        CreateUpdateStudentDto input,
        string idCard,
        string idCardHash,
        SysOrg school)
    {
        entity.FormerName = input.FormerName?.Trim();
        entity.IdCardTypeCode = input.IdCardTypeCode.Trim();
        entity.IdCardEncrypted = _idCards.Encrypt(idCard);
        entity.IdCardHash = idCardHash;

        entity.NationCode = input.NationCode;
        entity.CountryCode = input.CountryCode;
        entity.PoliticalCode = input.PoliticalCode;
        entity.NativePlace = input.NativePlace;
        entity.BirthPlace = input.BirthPlace;
        entity.HouseholdNatureCode = input.HouseholdNatureCode;
        entity.HouseholdAddress = input.HouseholdAddress;
        entity.HomeAddress = input.HomeAddress;
        entity.CurrentAddress = input.CurrentAddress;
        entity.PhoneNumber = input.PhoneNumber;
        entity.PostalCode = input.PostalCode;
        entity.Email = input.Email;

        entity.GradeId = input.GradeId;
        entity.ClassId = input.ClassId;
        entity.ClassStudentNo = input.ClassStudentNo;
        entity.EnrollDate = input.EnrollDate;
        entity.EnrollTypeCode = input.EnrollTypeCode;
        entity.StudyModeCode = input.StudyModeCode;
        entity.SchoolLengthCode = input.SchoolLengthCode;
        entity.StudentStatusCode = input.StudentStatusCode.IsNullOrWhiteSpace()
            ? StudentStatuses.Studying
            : input.StudentStatusCode!;

        entity.IsMigrantChild = input.IsMigrantChild;
        entity.IsLeftBehindChild = input.IsLeftBehindChild;
        entity.IsPoor = input.IsPoor;
        entity.IsDisabled = input.IsDisabled;
        entity.DisabilityCode = input.DisabilityCode;
        entity.HealthCode = input.HealthCode;
        entity.Remark = input.Remark;

        // 冗余学校物化路径，便于按数据范围前缀过滤学生
        entity.SchoolPathCode = school.PathCode;
    }

    /// <summary>
    /// 生成学籍辅号。
    ///
    /// 规则来自系统参数 <see cref="XjglSettings.StudentNumberRule"/>，默认
    /// <c>{SchoolCode:10}{Seq:6}</c>（学校标识码 10 位 + 流水号 6 位 = 16 位）。
    /// 流水号按**学校**维度自增，因此同一学校内唯一，叠加学校标识码后全局唯一。
    /// </summary>
    private async Task<string> GenerateStudentNoAsync(SysOrg school)
    {
        var template = await _settingProvider.GetOrNullAsync(XjglSettings.StudentNumberRule);

        if (!StudentNumberRule.TryParse(template, out var rule, out _) || rule == null)
        {
            rule = StudentNumberRule.Default;
        }

        var sequence = _sequenceService.NextValue($"STU_NO:{school.Code}", length: 6);

        try
        {
            return rule.Format(school.Code, sequence);
        }
        catch (InvalidOperationException ex)
        {
            throw new BusinessException(XjglDomainErrorCodes.StudentNumberGenerateFailed)
                .WithData("reason", ex.Message);
        }
    }

    private async Task<SysOrg> EnsureSchoolAsync(Guid schoolOrgId)
    {
        var org = await _orgs.FindAsync(schoolOrgId)
                  ?? throw new BusinessException(XjglDomainErrorCodes.OrgNotFound);

        if (!string.Equals(org.OrgType, OrgTypes.School, StringComparison.Ordinal))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", "学生只能挂在学校类型的组织下");
        }

        return org;
    }

    private static string NormalizeAcademicYear(string academicYear)
    {
        var value = academicYear.Trim();

        if (value.Length == 4 && int.TryParse(value, out var year))
        {
            return $"{year}-{year + 1}";
        }

        return value;
    }

    private string NormalizeIdCard(string idCardNumber)
    {
        if (!IdCardValidator.TryNormalize(idCardNumber, out var normalized, out var error))
        {
            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData("reason", error);
        }

        return normalized;
    }

    private async Task<System.Linq.Expressions.Expression<Func<StuStudent, bool>>?> BuildPredicateAsync(
        StudentQueryDto input,
        UserDataScope scope)
    {
        Guid? exactSchoolId = null;
        string? pathPrefix = null;

        if (input.SchoolOrgId.HasValue)
        {
            var org = await _orgs.FindAsync(input.SchoolOrgId.Value);

            // 组织不存在：当作"查不到"（上层已保证组织在数据范围内时才走到这里）
            if (org == null)
            {
                return null;
            }

            if (input.IncludeDescendantOrgs)
            {
                pathPrefix = org.PathCode + ".";
            }
            else
            {
                exactSchoolId = org.Id;
            }
        }

        // 数据范围作为**叠加**在业务条件之上的一层约束。
        //
        // 这里刻意把"是否仅本级"在**表达式之外**折算成一个前缀：
        //   - 本级及下级 → 前缀是 "我的路径."，用 StartsWith 匹配后代；
        //   - 仅本级     → 前缀是一个不可能出现的字符，于是 StartsWith 永不命中，
        //                  只剩 "路径完全相等" 这一条生效。
        // 为什么不直接在表达式里写三元（`exact ? a : b`）：
        //   SqlSugar 会把条件表达式翻译成 SqlFunc.IIF(...)，而嵌套的路径比较
        //   它翻译不了，会直接抛 "argument do not support"（实测踩过）。
        //   在表达式外先算好，树里只剩 SqlSugar 熟悉的 `&&` 与 `StartsWith`。
        var scopePathCode = scope.IsUnrestricted ? null : scope.OrgPathCode;
        var selfOnly = !scope.IsUnrestricted && scope.Scope == DataScopes.SelfOnly;
        var selfUserId = selfOnly ? scope.UserId : null;
        var scopeChildPrefix = scopePathCode == null
            ? null
            : (scope.Scope == DataScopes.OrgOnly ? ImpossiblePathPrefix : scopePathCode + ".");

        return BuildPredicate(input, exactSchoolId, pathPrefix, scopePathCode, scopeChildPrefix, selfUserId);
    }

    /// <summary>
    /// 用于表达"仅本级"的哨兵前缀：一个在真实物化路径里不可能出现的字符。
    /// 这样"仅本级"就退化成"前缀永不匹配、只剩路径相等"，从而不必在表达式里写三元。
    /// </summary>
    private const string ImpossiblePathPrefix = "\u0001";

    private static System.Linq.Expressions.Expression<Func<StuStudent, bool>>? BuildPredicate(
        StudentQueryDto input,
        Guid? exactSchoolId,
        string? pathPrefix,
        string? scopePathCode,
        string? scopeChildPrefix,
        Guid? selfUserId)
    {
        var hasSchool = exactSchoolId.HasValue;
        var schoolId = exactSchoolId.GetValueOrDefault();
        var hasPath = !pathPrefix.IsNullOrWhiteSpace();
        var hasScopePath = !scopePathCode.IsNullOrWhiteSpace();
        var hasScopeChildPrefix = !scopeChildPrefix.IsNullOrWhiteSpace();
        var hasSelfOnly = selfUserId.HasValue;
        var selfId = selfUserId.GetValueOrDefault();
        var hasYear = !input.AcademicYear.IsNullOrWhiteSpace();
        var hasGrade = input.GradeId.HasValue;
        var hasClass = input.ClassId.HasValue;
        var hasStatus = !input.StudentStatusCode.IsNullOrWhiteSpace();
        var hasGender = !input.GenderCode.IsNullOrWhiteSpace();
        var hasKeyword = !input.Keyword.IsNullOrWhiteSpace();

        if (!hasSchool && !hasPath && !hasScopePath && !hasYear && !hasGrade && !hasClass
            && !hasStatus && !hasGender && !hasKeyword)
        {
            return null;
        }

        var year = input.AcademicYear;
        var gradeId = input.GradeId.GetValueOrDefault();
        var classId = input.ClassId.GetValueOrDefault();
        var status = input.StudentStatusCode;
        var gender = input.GenderCode;
        var keyword = input.Keyword?.Trim();

        return s =>
            (!hasSchool || s.SchoolOrgId == schoolId) &&
            (!hasPath || (s.SchoolPathCode != null && s.SchoolPathCode.StartsWith(pathPrefix!))) &&
            // 数据范围：路径完全相等，或落在"我的路径."前缀之下（仅本级时前缀不可能命中）
            (!hasScopePath || (s.SchoolPathCode != null &&
                (s.SchoolPathCode == scopePathCode
                 || (hasScopeChildPrefix && s.SchoolPathCode.StartsWith(scopeChildPrefix!))))) &&
            // 仅本人：只看自己录入的数据
            (!hasSelfOnly || s.CreatorId == selfId) &&
            (!hasYear || s.AcademicYear == year) &&
            (!hasGrade || s.GradeId == gradeId) &&
            (!hasClass || s.ClassId == classId) &&
            (!hasStatus || s.StudentStatusCode == status) &&
            (!hasGender || s.GenderCode == gender) &&
            (!hasKeyword || s.Name.Contains(keyword!) || s.StudentNo.Contains(keyword!));
    }

    private async Task<List<StudentListItemDto>> ToListItemsAsync(List<StuStudent> students)
    {
        if (students.Count == 0)
        {
            return new List<StudentListItemDto>();
        }

        var genderNames = await GetCodeNameMapAsync(GenderCodeCategory);
        var statusNames = await GetCodeNameMapAsync(StudentStatusCodeCategory);

        var gradeIds = students.Where(s => s.GradeId.HasValue).Select(s => s.GradeId!.Value).Distinct().ToList();
        var classIds = students.Where(s => s.ClassId.HasValue).Select(s => s.ClassId!.Value).Distinct().ToList();
        var schoolIds = students.Select(s => s.SchoolOrgId).Distinct().ToList();

        var grades = gradeIds.Count == 0
            ? new List<SysGrade>()
            : await _grades.GetListAsync(g => gradeIds.Contains(g.Id));

        var classes = classIds.Count == 0
            ? new List<SysClass>()
            : await _classes.GetListAsync(c => classIds.Contains(c.Id));

        var schools = await _orgs.GetListAsync(o => schoolIds.Contains(o.Id));

        var gradeMap = grades.ToDictionary(g => g.Id, g => g.GradeName);
        var classMap = classes.ToDictionary(c => c.Id, c => c.ClassName);
        var schoolMap = schools.ToDictionary(o => o.Id, o => o.Name);

        var today = _clock.Now.Date;

        return students.Select(s =>
        {
            var item = new StudentListItemDto
            {
                Id = s.Id,
                StudentNo = s.StudentNo,
                NationalStudentNo = s.NationalStudentNo,
                Name = s.Name,
                HasRareCharacter = RareCharacterText.ContainsRareCharacter(s.Name),
                GenderCode = s.GenderCode,
                GenderName = genderNames.TryGetValue(s.GenderCode, out var g) ? g : null,
                BirthDate = s.BirthDate,
                Age = CalculateAge(s.BirthDate, today),
                SchoolOrgId = s.SchoolOrgId,
                SchoolName = schoolMap.TryGetValue(s.SchoolOrgId, out var sn) ? sn : null,
                AcademicYear = s.AcademicYear,
                GradeId = s.GradeId,
                GradeName = s.GradeId.HasValue && gradeMap.TryGetValue(s.GradeId.Value, out var gn) ? gn : null,
                ClassId = s.ClassId,
                ClassName = s.ClassId.HasValue && classMap.TryGetValue(s.ClassId.Value, out var cn) ? cn : null,
                StudentStatusCode = s.StudentStatusCode,
                StudentStatusName = statusNames.TryGetValue(s.StudentStatusCode, out var st) ? st : null,
                MaskedIdCard = _idCards.MaskEncrypted(s.IdCardEncrypted),
                HasPhoto = !string.IsNullOrWhiteSpace(s.PhotoPath)
            };

            return item;
        }).ToList();
    }

    private async Task<Dictionary<string, string>> GetCodeNameMapAsync(string categoryCode)
    {
        try
        {
            var items = await _codeAppService.GetItemsByCodeAsync(categoryCode);
            return items.ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 码表缺失不应导致学生列表打不开
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static int CalculateAge(DateTime birthDate, DateTime today)
    {
        var age = today.Year - birthDate.Year;

        if (birthDate.Date > today.AddYears(-age))
        {
            age--;
        }

        return age < 0 ? 0 : age;
    }

    // 身份证脱敏与摘要已移到 Xjgl.Students.IdCardProtector：
    // 监护人子表同样存身份证号，规则（密文入库 + 摘要查重 + 默认只给掩码）必须完全一致，
    // 各写一份迟早会出现某个界面把明文吐出去。

    private static void CopyListFields(StudentListItemDto source, StudentDto target)
    {
        target.Id = source.Id;
        target.StudentNo = source.StudentNo;
        target.NationalStudentNo = source.NationalStudentNo;
        target.Name = source.Name;
        target.HasRareCharacter = source.HasRareCharacter;
        target.GenderCode = source.GenderCode;
        target.GenderName = source.GenderName;
        target.BirthDate = source.BirthDate;
        target.Age = source.Age;
        target.SchoolOrgId = source.SchoolOrgId;
        target.SchoolName = source.SchoolName;
        target.AcademicYear = source.AcademicYear;
        target.GradeId = source.GradeId;
        target.GradeName = source.GradeName;
        target.ClassId = source.ClassId;
        target.ClassName = source.ClassName;
        target.StudentStatusCode = source.StudentStatusCode;
        target.StudentStatusName = source.StudentStatusName;
        target.MaskedIdCard = source.MaskedIdCard;
        target.HasPhoto = source.HasPhoto;
    }
}
