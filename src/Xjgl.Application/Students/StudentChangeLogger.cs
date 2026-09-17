using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Volo.Abp.Users;
using Xjgl.Codes;
using Xjgl.Data;
using Xjgl.Organizations;

namespace Xjgl.Students;

/// <summary>留痕字段的快照项。</summary>
/// <param name="Name">实体属性名（如 StudentStatusCode）。</param>
/// <param name="Label">中文字段名（如「学籍状态」）。</param>
/// <param name="Value">用于展示的值。</param>
/// <param name="CompareKey">
/// 用于**判断是否变化**的值。多数情况与 <paramref name="Value"/> 相同，
/// 但身份证号是个例外：它是 AES 密文，每次加密结果都不同，
/// 直接比密文会把"没改"误判成"改了"，因此比较用的是 HMAC 摘要。
/// </param>
public sealed record StudentFieldSnapshot(string Name, string Label, string? Value, string? CompareKey);

/// <summary>
/// 学籍变更留痕。
///
/// 用法是一对：**改之前先取快照**，改完再把快照交给它比对，它只写真正变化的字段。
/// 之所以用"前后快照比对"而不是"记录输入里带了哪些字段"，是因为后者会漏掉
/// 由业务逻辑推导出来的变化（例如按学校生成的辅号、按异动类型写入的状态），
/// 而审计恰恰要能回答"这条记录的哪个字段在什么时候变成了什么"。
/// </summary>
public interface IStudentChangeLogger
{
    /// <summary>采集快照（纯内存，不访问数据库）。</summary>
    Dictionary<string, StudentFieldSnapshot> Snapshot(StuStudent student);

    /// <summary>记录新建学籍。</summary>
    Task LogCreatedAsync(
        StuStudent student,
        string action = StudentChangeActions.Create,
        string source = "手工录入",
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>记录修改学籍（**只写变化了的字段**）。返回写入行数。</summary>
    Task<int> LogUpdatedAsync(
        StuStudent student,
        IReadOnlyDictionary<string, StudentFieldSnapshot> before,
        string source = "手工录入",
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>记录删除学籍（档案的"消失"本身也要留痕）。</summary>
    Task LogDeletedAsync(
        StuStudent student,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class StudentChangeLogger : IStudentChangeLogger, ITransientDependency
{
    private const string GenderCategory = "SEX";
    private const string StudentStatusCategory = "STUDENT_STATUS";

    private readonly IXjglRepository<StuChangeLog> _logs;
    private readonly IXjglRepository<SysOrg> _orgs;
    private readonly IXjglRepository<SysGrade> _grades;
    private readonly IXjglRepository<SysClass> _classes;
    private readonly ICodeAppService _codes;
    private readonly ICurrentUser _currentUser;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;

    public StudentChangeLogger(
        IXjglRepository<StuChangeLog> logs,
        IXjglRepository<SysOrg> orgs,
        IXjglRepository<SysGrade> grades,
        IXjglRepository<SysClass> classes,
        ICodeAppService codes,
        ICurrentUser currentUser,
        IGuidGenerator guidGenerator,
        IClock clock)
    {
        _logs = logs;
        _orgs = orgs;
        _grades = grades;
        _classes = classes;
        _codes = codes;
        _currentUser = currentUser;
        _guidGenerator = guidGenerator;
        _clock = clock;
    }

    /// <inheritdoc />
    public Dictionary<string, StudentFieldSnapshot> Snapshot(StuStudent student)
    {
        var items = new[]
        {
            F(nameof(StuStudent.Name), "姓名", student.Name),
            F(nameof(StuStudent.FormerName), "曾用名", student.FormerName),
            F(nameof(StuStudent.GenderCode), "性别", student.GenderCode),
            F(nameof(StuStudent.BirthDate), "出生日期", student.BirthDate.ToString("yyyy-MM-dd")),
            // 身份证号：比较用摘要（密文每次加密都不同），展示用摘要前缀（不把明文抄进日志表）
            F(nameof(StuStudent.IdCardHash), "身份证号", null, student.IdCardHash),
            F(nameof(StuStudent.NationCode), "民族", student.NationCode),
            F(nameof(StuStudent.HouseholdNatureCode), "户口性质", student.HouseholdNatureCode),
            F(nameof(StuStudent.HomeAddress), "家庭住址", student.HomeAddress),
            F(nameof(StuStudent.PhoneNumber), "联系电话", student.PhoneNumber),
            F(nameof(StuStudent.SchoolOrgId), "学校", student.SchoolOrgId.ToString()),
            F(nameof(StuStudent.AcademicYear), "学年", student.AcademicYear),
            F(nameof(StuStudent.GradeId), "年级", student.GradeId?.ToString()),
            F(nameof(StuStudent.ClassId), "班级", student.ClassId?.ToString()),
            F(nameof(StuStudent.EnrollDate), "入学日期", student.EnrollDate?.ToString("yyyy-MM-dd")),
            F(nameof(StuStudent.StudentStatusCode), "学籍状态", student.StudentStatusCode),
            F(nameof(StuStudent.IsMigrantChild), "随迁子女", Bool(student.IsMigrantChild)),
            F(nameof(StuStudent.IsLeftBehindChild), "留守儿童", Bool(student.IsLeftBehindChild)),
            F(nameof(StuStudent.IsPoor), "家庭经济困难", Bool(student.IsPoor)),
            F(nameof(StuStudent.IsDisabled), "残疾", Bool(student.IsDisabled)),
            F(nameof(StuStudent.Remark), "备注", student.Remark)
        };

        return items.ToDictionary(i => i.Name, i => i, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task LogCreatedAsync(
        StuStudent student,
        string action = StudentChangeActions.Create,
        string source = "手工录入",
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var row = NewRow(student, action, _guidGenerator.Create(), source, reason);

        row.FieldLabel = "建档";
        row.NewValue = $"{student.Name}（{student.StudentNo}）";

        await _logs.InsertAsync(row, autoSave: true, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> LogUpdatedAsync(
        StuStudent student,
        IReadOnlyDictionary<string, StudentFieldSnapshot> before,
        string source = "手工录入",
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var after = Snapshot(student);
        var changed = new List<StudentFieldSnapshot>();

        foreach (var (name, current) in after)
        {
            if (before.TryGetValue(name, out var previous)
                && !string.Equals(previous.CompareKey, current.CompareKey, StringComparison.Ordinal))
            {
                changed.Add(current);
            }
        }

        if (changed.Count == 0)
        {
            return 0;
        }

        var batchId = _guidGenerator.Create();

        // 只为"变化了的字段"去查码表：平时一次修改也就动一两个字段
        var genderNames = await CodeNamesAsync(changed, nameof(StuStudent.GenderCode), GenderCategory);
        var statusNames = await CodeNamesAsync(changed, nameof(StuStudent.StudentStatusCode), StudentStatusCategory);

        var rows = new List<StuChangeLog>();

        foreach (var field in changed)
        {
            var row = NewRow(student, StudentChangeActions.Update, batchId, source, reason);

            row.FieldName = field.Name;
            row.FieldLabel = field.Label;
            row.OldValue = await DescribeAsync(before[field.Name], genderNames, statusNames);
            row.NewValue = await DescribeAsync(field, genderNames, statusNames);

            rows.Add(row);
        }

        await _logs.InsertManyAsync(rows, autoSave: true, cancellationToken: cancellationToken);

        return rows.Count;
    }

    /// <inheritdoc />
    public async Task LogDeletedAsync(
        StuStudent student,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var row = NewRow(student, StudentChangeActions.Delete, _guidGenerator.Create(), "手工录入", reason);

        row.FieldLabel = "删除学籍";
        row.OldValue = $"{student.Name}（{student.StudentNo}）";

        await _logs.InsertAsync(row, autoSave: true, cancellationToken: cancellationToken);
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private StuChangeLog NewRow(StuStudent student, string action, Guid batchId, string source, string? reason)
    {
        var row = new StuChangeLog(
            _guidGenerator.Create(),
            student.Id,
            student.StudentNo,
            student.Name,
            batchId,
            action);

        row.Reason = reason;
        row.Source = source;
        row.SchoolPathCode = student.SchoolPathCode;
        row.OccurredAt = _clock.Now;
        row.OperatorId = _currentUser.Id;
        row.OperatorName = _currentUser.UserName;

        return row;
    }

    private async Task<Dictionary<string, string>> CodeNamesAsync(
        List<StudentFieldSnapshot> changed,
        string fieldName,
        string categoryCode)
    {
        if (changed.All(c => c.Name != fieldName))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var items = await _codes.GetItemsByCodeAsync(categoryCode);

            return items.ToDictionary(i => i.Code, i => i.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 码表取不到就退化成显示码值：**不能因为翻译不了就把留痕丢掉**
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>把快照值翻译成人能读的文本（组织 Id → 组织名、码值 → 中文名）。</summary>
    private async Task<string?> DescribeAsync(
        StudentFieldSnapshot field,
        Dictionary<string, string> genderNames,
        Dictionary<string, string> statusNames)
    {
        if (field.Name == nameof(StuStudent.IdCardHash))
        {
            return DescribeIdCard(field.CompareKey);
        }

        var value = field.Value;

        if (value.IsNullOrWhiteSpace())
        {
            return null;
        }

        return field.Name switch
        {
            nameof(StuStudent.SchoolOrgId) => await OrgNameAsync(value!),
            nameof(StuStudent.GradeId) => await GradeNameAsync(value!),
            nameof(StuStudent.ClassId) => await ClassNameAsync(value!),
            nameof(StuStudent.GenderCode) when genderNames.Count > 0 => Translate(genderNames, value!),
            nameof(StuStudent.StudentStatusCode) when statusNames.Count > 0 => Translate(statusNames, value!),
            _ => value
        };
    }

    /// <summary>
    /// 身份证号的展示值。
    ///
    /// 留痕里**不存明文**：审计要看的是"身份证号被谁改过"，
    /// 而不是把全校学生的身份证号再抄一份到日志表里（那等于多开一个泄露面）。
    /// 只留摘要前缀，够用来判断"还是不是同一条"。
    /// </summary>
    private static string DescribeIdCard(string? hash)
    {
        return hash.IsNullOrWhiteSpace()
            ? "（已清空）"
            : $"摘要 {hash![..Math.Min(8, hash.Length)]}…";
    }

    private async Task<string?> OrgNameAsync(string id)
        => Guid.TryParse(id, out var guid) ? (await _orgs.FindAsync(guid))?.Name : null;

    private async Task<string?> GradeNameAsync(string id)
        => Guid.TryParse(id, out var guid) ? (await _grades.FindAsync(guid))?.GradeName : null;

    private async Task<string?> ClassNameAsync(string id)
        => Guid.TryParse(id, out var guid) ? (await _classes.FindAsync(guid))?.ClassName : null;

    private static string Translate(Dictionary<string, string> map, string code)
        => map.TryGetValue(code, out var name) ? $"{name}（{code}）" : code;

    private static StudentFieldSnapshot F(
        string name,
        string label,
        string? value,
        string? compareKey = null)
    {
        var normalized = value.IsNullOrWhiteSpace() ? null : value!.Trim();

        return new StudentFieldSnapshot(name, label, normalized, compareKey ?? normalized);
    }

    private static string Bool(bool value) => value ? "是" : "否";
}
