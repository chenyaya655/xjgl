using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Text;

namespace Xjgl.Students;

/// <summary>
/// 学生批量导入导出实现。
///
/// 导入的核心是一个循环：逐行组装 DTO → 调 <see cref="IStudentAppService.CreateAsync"/> →
/// 收集失败原因。**复用 CreateAsync 是刻意的**：导入必须与手工录入走完全相同的校验
/// （身份证校验位、跨校查重、生僻字姓名规则、学籍辅号生成、数据范围），
/// 否则"导入进来的数据"就成了绕过校验的后门。
///
/// 另外两处值得注意：
///   - **按表头名取列**，不按列序（Excel 里插列、调序很常见）；
///   - **导入/导出都受数据范围约束** —— 导出走的是同一个查询服务，
///     导入时每一行的学校也要在自己范围内（由 CreateAsync 校验），
///     因此县区管理员没法用"导入"把学生塞进别的县的学校。
/// </summary>
[Authorize]
public class StudentImportExportAppService : XjglAppService, IStudentImportExportAppService
{
    /// <summary>导出上限。超过这个量级的导出应当走"后台任务 + 文件下载"（见方案文档 P4 余量）。</summary>
    private const int ExportRowLimit = 5000;

    /// <summary>
    /// 单页取数上限。
    ///
    /// **ABP 对单次请求的 MaxResultCount 有硬上限（默认 1000）**，
    /// 传更大的值会被方法参数校验直接拒掉（AbpValidationException）——
    /// 因此导出不能"一次要 5000 条"，只能分页累积。
    /// </summary>
    private const int ExportPageSize = 1000;

    private const string ImportPermission = MetadataPermissions.Stu.Student.Import;
    private const string ExportPermission = MetadataPermissions.Stu.Student.Export;

    private readonly IStudentAppService _students;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly ICodeAppService _codes;

    /// <summary>按编码找组织的缓存（同一批导入里同一所学校会出现很多次）。</summary>
    private readonly Dictionary<string, SysOrg?> _orgByCode = new(StringComparer.Ordinal);

    public StudentImportExportAppService(
        IStudentAppService students,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        ICodeAppService codes)
    {
        _students = students;
        _orgs = orgs;
        _grades = grades;
        _classes = classes;
        _codes = codes;
    }

    // ------------------------------------------------------------------
    // 列定义与模板
    // ------------------------------------------------------------------

    public List<ImportColumnDto> GetColumns() => new()
    {
        new() { Name = "姓名", Required = true, Remark = "生僻字按原样填写；不能含数字" },
        new() { Name = "性别", Required = true, Remark = "填「男」或「女」" },
        new() { Name = "出生日期", Required = true, Remark = "如 2015-06-01 或 2015/6/1" },
        new() { Name = "证件类型", Required = true, Remark = "如「居民身份证」" },
        new() { Name = "身份证号", Required = true, Remark = "18 位，需通过校验位校验；同一身份证号不能重复建籍" },
        new() { Name = "学校标识码", Required = true, Remark = "10 位学校标识码（与组织管理里的组织编码一致）" },
        new() { Name = "学年", Required = true, Remark = "如 2025-2026" },
        new() { Name = "年级编码", Required = false, Remark = "如 1；填了就会校验该年级是否存在" },
        new() { Name = "班级编码", Required = false, Remark = "如 1；需要先填年级编码" },
        new() { Name = "学籍状态", Required = false, Remark = "如「在读」；默认在读" },
        new() { Name = "入学日期", Required = false, Remark = "如 2025-09-01" },
        new() { Name = "联系电话", Required = false },
        new() { Name = "家庭住址", Required = false }
    };

    public string GetImportTemplate()
    {
        var header = GetColumns().Select(c => c.Name).ToArray();

        // 给一行示例：用户看到"该填成什么样"比看列说明快得多
        var example = new[]
        {
            "张三", "男", "2015-06-01", "居民身份证", "410102201506011230",
            "4101020001", "2025-2026", "1", "1", "在读", "2025-09-01",
            "13800000000", "河南省郑州市××区××路 1 号"
        };

        return Csv.WriteForExcel(new[] { header, example });
    }

    // ------------------------------------------------------------------
    // 导入
    // ------------------------------------------------------------------

    [Authorize(ImportPermission)]
    public async Task<ImportStudentsResultDto> ImportAsync(ImportStudentsDto input)
    {
        var rows = Csv.Parse(input.Content);

        if (rows.Count == 0)
        {
            throw new UserFriendlyException("导入内容为空，请先下载模板并按模板填写。");
        }

        if (rows.Count == 1)
        {
            throw new UserFriendlyException("导入内容只有表头，没有数据行。");
        }

        var header = Csv.BuildHeaderIndex(rows[0]);

        var missingColumns = GetColumns()
            .Where(c => c.Required && !header.ContainsKey(c.Name))
            .Select(c => c.Name)
            .ToList();

        if (missingColumns.Count > 0)
        {
            // 明确指出缺哪几列：模板被改坏了是最常见的原因
            throw new UserFriendlyException($"导入文件缺少必需列：{string.Join("、", missingColumns)}。请重新下载模板填写。");
        }

        var sexMap = await BuildCodeNameMapAsync("SEX");
        var idTypeMap = await BuildCodeNameMapAsync("ID_TYPE");
        var statusMap = await BuildCodeNameMapAsync("STUDENT_STATUS");

        var result = new ImportStudentsResultDto();
        var yearCache = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i < rows.Count; i++)
        {
            // 行号按用户看到的 Excel 行号报（表头是第 1 行，数据从第 2 行开始）
            var rowIndex = i + 1;
            var row = rows[i];

            // 整行为空：Excel 末尾常有空行，直接跳过而不是报错
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            result.Total++;

            var name = Csv.Cell(row, header, "姓名");
            var idCard = Csv.Cell(row, header, "身份证号");

            try
            {
                var dto = await BuildRowAsync(row, header, sexMap, idTypeMap, statusMap);

                await _students.CreateAsync(dto);

                result.Succeeded++;
            }
            catch (Exception ex)
            {
                // 逐行容错：单行失败不影响其它行，最后统一返回失败明细
                result.Failed++;
                result.Errors.Add(new ImportStudentErrorDto
                {
                    RowIndex = rowIndex,
                    Name = name,
                    IdCardNumber = idCard,
                    Message = DescribeRowError(ex)
                });
            }
        }

        result.Message = result.Failed == 0
            ? $"全部导入成功，共 {result.Succeeded} 人。"
            : $"共 {result.Total} 行：成功 {result.Succeeded} 人，失败 {result.Failed} 行（见下方明细）。";

        return result;
    }

    /// <summary>把一行业务数据组装成创建入参，顺带把"码值名称"翻译成码值。</summary>
    private async Task<CreateUpdateStudentDto> BuildRowAsync(
        string[] row,
        Dictionary<string, int> header,
        Dictionary<string, string> sexMap,
        Dictionary<string, string> idTypeMap,
        Dictionary<string, string> statusMap)
    {
        string Required(string column)
        {
            var value = Csv.Cell(row, header, column);

            if (value.Length == 0)
            {
                throw new UserFriendlyException($"「{column}」不能为空");
            }

            return value;
        }

        var name = Required("姓名");
        var sex = ResolveCode(sexMap, Required("性别"), "性别");
        var birth = ParseDate(Required("出生日期"), "出生日期");
        var idType = ResolveCode(idTypeMap, Required("证件类型"), "证件类型");
        var idCard = Required("身份证号");
        var schoolCode = Required("学校标识码");
        var year = Required("学年");

        var school = await FindOrgByCodeAsync(schoolCode)
                     ?? throw new UserFriendlyException($"学校标识码「{schoolCode}」在系统中不存在");

        if (school.OrgType != OrgTypes.School)
        {
            throw new UserFriendlyException($"「{schoolCode}」不是学校（{school.Name} 是{OrgTypes.GetDisplayName(school.OrgType)}）");
        }

        // 年级/班级可选：填了就必须能在该学校该学年下找到
        Guid? gradeId = null;
        Guid? classId = null;

        var gradeCode = Csv.Cell(row, header, "年级编码");
        var classCode = Csv.Cell(row, header, "班级编码");

        if (gradeCode.Length > 0)
        {
            var grade = await _grades.FindAsync(g =>
                g.SchoolOrgId == school.Id && g.AcademicYear == year && g.GradeCode == gradeCode);

            gradeId = grade?.Id
                      ?? throw new UserFriendlyException(
                          $"该校 {year} 学年下不存在年级编码「{gradeCode}」");
        }

        if (classCode.Length > 0)
        {
            if (gradeId == null)
            {
                throw new UserFriendlyException("填了班级编码就必须同时填年级编码");
            }

            var klass = await _classes.FindAsync(c => c.GradeId == gradeId && c.ClassNo == classCode);

            classId = klass?.Id
                      ?? throw new UserFriendlyException($"该年级下不存在班级编码「{classCode}」");
        }

        var statusText = Csv.Cell(row, header, "学籍状态");
        var enrollDate = Csv.Cell(row, header, "入学日期");

        return new CreateUpdateStudentDto
        {
            Name = name,
            GenderCode = sex,
            BirthDate = birth,
            IdCardTypeCode = idType,
            IdCardNumber = idCard,
            SchoolOrgId = school.Id,
            AcademicYear = year,
            GradeId = gradeId,
            ClassId = classId,
            StudentStatusCode = statusText.Length == 0
                ? StudentStatuses.Studying
                : ResolveCode(statusMap, statusText, "学籍状态"),
            EnrollDate = enrollDate.Length == 0 ? null : ParseDate(enrollDate, "入学日期"),
            PhoneNumber = NullIfEmpty(Csv.Cell(row, header, "联系电话")),
            HomeAddress = NullIfEmpty(Csv.Cell(row, header, "家庭住址"))
        };
    }

    // ------------------------------------------------------------------
    // 导出
    // ------------------------------------------------------------------

    /// <summary>
    /// 导出 CSV。
    ///
    /// **方法名必须以 <c>Get</c> 开头，这不是风格问题**：ABP 的自动 API 约定按方法名前缀
    /// 决定 HTTP 动词（<c>Get*</c> → GET，<c>Create*</c> → POST，其余**默认 POST**），
    /// 路由则剥掉动词前缀后按 kebab-case 取。
    /// 之前的 <c>ExportAsync</c> 被注册成了 POST，而前端是带查询串下载（GET）——
    /// 线上表现为 405 Method Not Allowed。
    ///
    /// 这个坑进程内单元测试**发现不了**（直接调方法，不经过路由），
    /// 只有真的发一次 HTTP 请求才会暴露；所以本仓库的验收里保留了一步真实的 HTTP 探针。
    /// </summary>
    [Authorize(ExportPermission)]
    public async Task<string> GetExportAsync(StudentQueryDto input)
    {
        StudentQueryDto BuildQuery(int skipCount) => new()
        {
            SchoolOrgId = input.SchoolOrgId,
            IncludeDescendantOrgs = input.IncludeDescendantOrgs,
            AcademicYear = input.AcademicYear,
            GradeId = input.GradeId,
            ClassId = input.ClassId,
            StudentStatusCode = input.StudentStatusCode,
            GenderCode = input.GenderCode,
            Keyword = input.Keyword,
            Sorting = input.Sorting ?? "StudentNo ASC",
            SkipCount = skipCount,
            MaxResultCount = ExportPageSize
        };

        // 复用列表查询：它已经带了数据范围约束与码值翻译，
        // 导出不能另起一套查询 —— 否则很容易出现"界面看不到、导出却能拿到"
        var students = new List<StudentListItemDto>();
        var total = 0L;

        for (var skip = 0; skip < ExportRowLimit; skip += ExportPageSize)
        {
            var page = await _students.GetListAsync(BuildQuery(skip));

            total = page.TotalCount;
            students.AddRange(page.Items);

            // 取完了，或者已经到导出行数上限
            if (page.Items.Count < ExportPageSize || students.Count >= total)
            {
                break;
            }
        }

        var rows = new List<IEnumerable<string?>>
        {
            new[]
            {
                "学籍辅号", "姓名", "性别", "出生日期", "身份证号", "学校",
                "学年", "年级", "班级", "学籍状态", "是否有照片", "含生僻字"
            }
        };

        rows.AddRange(students.Select(s => new[]
        {
            s.StudentNo,
            s.Name,
            s.GenderName ?? s.GenderCode,
            s.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s.MaskedIdCard,
            s.SchoolName,
            s.AcademicYear,
            s.GradeName,
            s.ClassName,
            s.StudentStatusName ?? s.StudentStatusCode,
            s.HasPhoto ? "是" : "否",
            s.HasRareCharacter ? "是" : "否"
        }));

        return Csv.WriteForExcel(rows);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>把码值名称或码值本身解析成码值（用户写"男"或"1"都能通过）。</summary>
    private static string ResolveCode(Dictionary<string, string> map, string text, string columnName)
    {
        if (map.TryGetValue(text, out var code))
        {
            return code;
        }

        throw new UserFriendlyException($"「{columnName}」取值「{text}」不在码表中，请核对");
    }

    /// <summary>「名称 → 码值」与「码值 → 码值」两张表合一，便于用户写中文或写码值。</summary>
    private async Task<Dictionary<string, string>> BuildCodeNameMapAsync(string categoryCode)
    {
        var items = await _codes.GetItemsByCodeAsync(categoryCode, onlyEnabled: true);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            map[item.Name] = item.Code;
            map[item.Code] = item.Code;
        }

        return map;
    }

    private async Task<SysOrg?> FindOrgByCodeAsync(string code)
    {
        if (_orgByCode.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var org = await _orgs.FindAsync(o => o.Code == code);
        _orgByCode[code] = org;

        return org;
    }

    private static DateTime ParseDate(string text, string columnName)
    {
        var formats = new[] { "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d", "yyyyMMdd", "yyyy.MM.dd" };

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
        {
            return exact.Date;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            return loose.Date;
        }

        throw new UserFriendlyException($"「{columnName}」日期格式无法识别：{text}（建议 2025-09-01）");
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// 把行内异常翻译成用户能照着改的一句话。
    /// 业务异常自带 reason（例如"身份证号已存在学籍记录：张三"），优先用它。
    /// </summary>
    private static string DescribeRowError(Exception ex)
    {
        if (ex is BusinessException be)
        {
            if (be.Data.Contains("reason") && be.Data["reason"] is string reason && reason.Length > 0)
            {
                return reason;
            }

            return be.Message;
        }

        return ex.Message;
    }
}
