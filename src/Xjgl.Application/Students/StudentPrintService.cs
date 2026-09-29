using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Exporting;
using Xjgl.Organizations;
using Xjgl.Photos;
using Xjgl.Printing;
using Xjgl.Text;

namespace Xjgl.Students;

/// <summary>
/// 学籍卡 / 花名册的打印实现（把业务数据映射成打印视图模型，再交给渲染器出 PDF）。
///
/// 三类数据要在这一层凑齐：
///   1. **主档与子表**：学生本人 + 监护人 + 学校/年级/班级名称；
///   2. **码值翻译**：性别、民族、户口性质、学籍状态都存的是码值，打印件上必须是中文；
///   3. **照片**：从照片存储（NAS 主存储 → 本地降级区）读出字节，嵌进卡片。
///
/// 另外，**数据范围校验必须在最前面**：学籍卡里有出生日期、家庭住址、监护人电话，
/// 拿到别人的卡片等于拿到一份个人信息表。
/// </summary>
public class StudentPrintService : IStudentPrintService, ITransientDependency
{
    private const string GenderCategory = "SEX";
    private const string NationCategory = "NATION";
    private const string HouseholdCategory = "HOUSEHOLD";
    private const string StudentStatusCategory = "STUDENT_STATUS";
    private const string GuardianRelationCategory = "GUARDIAN_RELATION";

    private readonly IStudentAppService _students;
    private readonly IStudentAccessGuard _guard;
    private readonly IIdCardProtector _idCards;
    private readonly ICodeAppService _codes;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly IXjglRepository<StuGuardian> _guardians;
    private readonly NasPhotoStorage _primaryPhotos;
    private readonly LocalFallbackPhotoStorage _fallbackPhotos;
    private readonly IStudentCardPdfRenderer _cardRenderer;
    private readonly IStudentRosterPdfRenderer _rosterRenderer;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<StudentPrintService> _logger;

    public StudentPrintService(
        IStudentAppService students,
        IStudentAccessGuard guard,
        IIdCardProtector idCards,
        ICodeAppService codes,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        IXjglRepository<StuGuardian> guardians,
        NasPhotoStorage primaryPhotos,
        LocalFallbackPhotoStorage fallbackPhotos,
        IStudentCardPdfRenderer cardRenderer,
        IStudentRosterPdfRenderer rosterRenderer,
        IConfiguration configuration,
        IClock clock,
        ICurrentUser currentUser,
        ILogger<StudentPrintService> logger)
    {
        _students = students;
        _guard = guard;
        _idCards = idCards;
        _codes = codes;
        _orgs = orgs;
        _grades = grades;
        _classes = classes;
        _guardians = guardians;
        _primaryPhotos = primaryPhotos;
        _fallbackPhotos = fallbackPhotos;
        _cardRenderer = cardRenderer;
        _rosterRenderer = rosterRenderer;
        _configuration = configuration;
        _clock = clock;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PrintFileResult> RenderStudentCardAsync(
        Guid studentId,
        CancellationToken cancellationToken = default)
    {
        // 越权保护：卡片包含出生日期、住址、监护人电话
        var student = await _guard.GetInScopeAsync(studentId, cancellationToken);

        var model = new StudentCardModel
        {
            SchoolName = (await _orgs.FindAsync(student.SchoolOrgId, cancellationToken))?.Name ?? "—",
            StudentNo = student.StudentNo,
            NationalStudentNo = student.NationalStudentNo,
            Name = student.Name,
            HasRareCharacter = RareCharacterText.ContainsRareCharacter(student.Name),
            GenderName = await TranslateAsync(GenderCategory, student.GenderCode, cancellationToken),
            BirthDate = student.BirthDate.ToString("yyyy-MM-dd"),
            MaskedIdCard = _idCards.MaskEncrypted(student.IdCardEncrypted),
            NationName = await TranslateOrNullAsync(NationCategory, student.NationCode, cancellationToken),
            NativePlace = student.NativePlace,
            HouseholdName = await TranslateOrNullAsync(HouseholdCategory, student.HouseholdNatureCode, cancellationToken),
            HomeAddress = student.HomeAddress ?? student.CurrentAddress,
            PhoneNumber = student.PhoneNumber,
            AcademicYear = student.AcademicYear,
            GradeName = student.GradeId.HasValue
                ? (await _grades.FindAsync(student.GradeId.Value, cancellationToken))?.GradeName
                : null,
            ClassName = student.ClassId.HasValue
                ? (await _classes.FindAsync(student.ClassId.Value, cancellationToken))?.ClassName
                : null,
            StudentStatusName = await TranslateAsync(StudentStatusCategory, student.StudentStatusCode, cancellationToken),
            EnrollDate = student.EnrollDate?.ToString("yyyy-MM-dd"),
            Photo = await TryReadPhotoAsync(student.PhotoPath, cancellationToken),
            PrintedAt = _clock.Now.ToString("yyyy-MM-dd HH:mm")
        };

        model.Guardians = await LoadGuardiansAsync(student.Id, cancellationToken);

        var pdf = Render(
            () => _cardRenderer.Render(model),
            $"学籍卡-{student.Name}-{student.StudentNo}");

        return new PrintFileResult(pdf, $"学籍卡_{Sanitize(student.Name)}_{student.StudentNo}.pdf");
    }

    /// <inheritdoc />
    public async Task<PrintFileResult> RenderRosterAsync(
        StudentRosterQueryDto input,
        CancellationToken cancellationToken = default)
    {
        var model = new StudentRosterModel
        {
            AcademicYear = input.AcademicYear,
            MaskNames = input.MaskNames,
            PrintedAt = _clock.Now.ToString("yyyy-MM-dd HH:mm"),
            PrintedBy = _currentUser.UserName
        };

        if (input.SchoolOrgId.HasValue)
        {
            var school = await _orgs.FindAsync(input.SchoolOrgId.Value, cancellationToken);

            // 请求了范围外的学校 → 复用统一守卫，行为与列表页一致（403 而不是空表）
            if (school != null)
            {
                await _guard.EnsureOrgInScopeAsync(school, "该学校不在您的数据范围内，不能打印其花名册", cancellationToken);
                model.SchoolName = school.Name;
            }
        }

        if (model.SchoolName.IsNullOrWhiteSpace())
        {
            model.SchoolName = "（按数据范围）";
        }

        if (input.GradeId.HasValue)
        {
            model.GradeName = (await _grades.FindAsync(input.GradeId.Value, cancellationToken))?.GradeName;
        }

        if (input.ClassId.HasValue)
        {
            model.ClassName = (await _classes.FindAsync(input.ClassId.Value, cancellationToken))?.ClassName;
        }

        model.Title = !model.ClassName.IsNullOrWhiteSpace() ? "学生花名册" : "学生名册";

        // 复用列表查询：它已经带了数据范围约束与码值翻译，
        // 花名册不能另起一套查询，否则会出现"界面看不到、打印却打得出"
        var rows = new List<StudentListItemDto>();

        var configuredPageSize = _configuration.GetValue<int?>(PageSizeConfigKey) ?? DefaultPageSize;

        var pageSize = Math.Clamp(configuredPageSize < 1 ? DefaultPageSize : configuredPageSize, 1, 1000);

        for (var skip = 0; skip < input.MaxRows; skip += pageSize)
        {
            var page = await _students.GetListAsync(new StudentQueryDto
            {
                SchoolOrgId = input.SchoolOrgId,
                IncludeDescendantOrgs = input.IncludeDescendantOrgs,
                AcademicYear = input.AcademicYear,
                GradeId = input.GradeId,
                ClassId = input.ClassId,
                StudentStatusCode = input.StudentStatusCode,
                Keyword = input.Keyword,
                Sorting = input.Sorting ?? "StudentNo ASC",
                SkipCount = skip,
                MaxResultCount = Math.Min(pageSize, input.MaxRows - skip)
            });

            rows.AddRange(page.Items);

            // ★ 债 23：花名册是**分页取数**的，所以"取完第 N 页"就是一个真实的中间进度。
            //
            // 注意分母用 `input.MaxRows` 而不是 `page.TotalCount`：这一种同样有上限
            // （`MaxRows`，默认 2000），而它的循环条件本身就是"取满 MaxRows 就停"。
            // 用原总数的话，2000 人以上的花名册进度会永远停在某个百分比上 ——
            // 那正是"看起来卡住了"，比没有百分比更难解释。
            await ExportProgress.ReportAsync(
                rows.Count,
                Math.Min(page.TotalCount, input.MaxRows),
                cancellationToken);

            if (page.Items.Count < pageSize || rows.Count >= page.TotalCount)
            {
                break;
            }
        }

        var seq = 1;

        model.Rows = rows.Select(r => new StudentRosterRow
        {
            Seq = seq++,
            StudentNo = r.StudentNo,
            Name = r.Name,
            GenderName = r.GenderName ?? r.GenderCode,
            BirthDate = r.BirthDate.ToString("yyyy-MM-dd"),
            MaskedIdCard = r.MaskedIdCard,
            HomeAddress = null, // 列表项不含住址：花名册不打印住址，避免大面积泄露个人信息
            PhoneNumber = null,
            StudentStatusName = r.StudentStatusName ?? r.StudentStatusCode,
            Remark = r.HasRareCharacter ? "姓名含生僻字" : null
        }).ToList();

        var pdf = Render(
            () => _rosterRenderer.Render(model),
            $"花名册（{model.SchoolName} {model.GradeName} {model.ClassName}）");

        var fileParts = new[] { "花名册", model.SchoolName, model.GradeName, model.ClassName }
            .Where(p => !p.IsNullOrWhiteSpace())
            .Select(p => Sanitize(p!));

        return new PrintFileResult(pdf, $"{string.Join("_", fileParts)}.pdf", rowCount: model.Rows.Count);
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>
    /// 单次取数上限（ABP 对 MaxResultCount 有 1000 的硬上限，超过会被参数校验拒绝）。
    ///
    /// 与 `StudentCsvExportBuilder` / `PhotoExportService` 一样做成**可配置**，
    /// 理由也相同：花名册的"每页之后上报一次进度"（债 23）只有跨页才谈得上，
    /// 而用 1000 的页大小意味着集成测试要往共享测试库里插 1000+ 名学生
    /// （实测 30 秒上下，还每跑一次多留一批垃圾）。默认值就是那个硬上限，
    /// 生产上不需要改。
    /// </summary>
    private const string PageSizeConfigKey = "Xjgl:Print:RosterPageSize";

    private const int DefaultPageSize = 1000;

    private async Task<List<StudentCardGuardian>> LoadGuardiansAsync(
        Guid studentId,
        CancellationToken cancellationToken)
    {
        var rows = await _guardians.GetSortedListAsync(
            g => g.StudentId == studentId,
            "Seq ASC",
            cancellationToken);

        var relations = await CodeMapAsync(GuardianRelationCategory, cancellationToken);

        return rows.Take(4).Select(g => new StudentCardGuardian
        {
            Name = g.Name,
            RelationName = relations.GetValueOrDefault(g.RelationCode ?? string.Empty),
            PhoneNumber = g.PhoneNumber,
            WorkUnit = g.WorkUnit
        }).ToList();
    }

    private async Task<byte[]?> TryReadPhotoAsync(string? relativePath, CancellationToken cancellationToken)
    {
        if (relativePath.IsNullOrWhiteSpace())
        {
            return null;
        }

        // 与写入顺序相反：先主存储，再本地降级区（NAS 熔断期间上传的照片只存在于降级区）
        foreach (var storage in new IPhotoBlobReader[] { _primaryPhotos, _fallbackPhotos })
        {
            try
            {
                await using var stream = await storage.OpenReadAsync(relativePath!, cancellationToken);

                if (stream == null)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);

                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                // 读照片失败不该让整张卡片打不出来：卡片上会显示"未采集照片"
                _logger.LogWarning(ex, "读取学生照片失败：{Path}", relativePath);
            }
        }

        return null;
    }

    private async Task<string> TranslateAsync(
        string categoryCode,
        string? code,
        CancellationToken cancellationToken)
    {
        if (code.IsNullOrWhiteSpace())
        {
            return "—";
        }

        var map = await CodeMapAsync(categoryCode, cancellationToken);

        return map.TryGetValue(code!, out var name) ? name : code!;
    }

    private async Task<string?> TranslateOrNullAsync(
        string categoryCode,
        string? code,
        CancellationToken cancellationToken)
    {
        if (code.IsNullOrWhiteSpace())
        {
            return null;
        }

        var map = await CodeMapAsync(categoryCode, cancellationToken);

        return map.TryGetValue(code!, out var name) ? name : code;
    }

    private async Task<Dictionary<string, string>> CodeMapAsync(
        string categoryCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await _codes.GetItemsByCodeAsync(categoryCode);

            return items.ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 码表取不到就退化成显示码值：打印件上显示 "01" 总好过整张卡打不出来
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 渲染并把"字体缺失"这类基础设施异常翻译成用户看得懂的提示。
    ///
    /// 渲染器刻意开着 `ThrowOnMissingTextGlyphs`（画不出字形必须报错，
    /// 而不是悄悄出一张满是方块的学籍卡），因此这里接住
    /// <see cref="PrintRenderingException"/>，把运维问题说成运维问题。
    /// </summary>
    private byte[] Render(Func<byte[]> render, string what)
    {
        try
        {
            return render();
        }
        catch (PrintRenderingException ex)
        {
            _logger.LogError(ex, "渲染 PDF 失败：{What}", what);

            throw new BusinessException(XjglDomainErrorCodes.ValidationFailed)
                .WithData(
                    "reason",
                    ex.MissingFont
                        ? "生成 PDF 失败：服务器缺少可用的中文字体。" +
                          "请在服务器上安装中文字体，或把 Xjgl:Print:RareCharacterFontDirectory 指向随应用部署的字体目录。"
                        : "生成 PDF 失败，请查看服务端日志。");
        }
    }

    /// <summary>去掉文件名里的非法字符（学生姓名是用户输入，可能带 / 或 *）。</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();

        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
