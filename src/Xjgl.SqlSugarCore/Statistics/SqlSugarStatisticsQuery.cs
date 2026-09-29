using SqlSugar;
using Volo.Abp.DependencyInjection;
using Xjgl.Statistics;

namespace Xjgl.SqlSugarCore.Statistics;

/// <summary>
/// 统计聚合的 SqlSugar 实现。
///
/// 【为什么用原生 SQL 而不是 SqlSugar 的 LINQ GroupBy】
/// 这里的分组表达式随维度变化（学校 / 年级 / 班级 / 码值 / 月份 / 是否为空），
/// 而每个维度还要带一个"名称列"（有的来自本表、有的来自关联表、有的根本没有）。
/// 用 LINQ 拼这种动态投影会退化成一大堆 <c>Expression</c> 拼装，
/// 出问题时既没有 SQL 可看、也没有编译期检查 —— 而这条路径恰恰是最需要能直接
/// 拿去 <c>psql</c> 里跑一遍确认的地方。原生 SQL 换来的是"能验证"。
///
/// 【为什么必须自己拼 ORDER BY / LIMIT】
/// 学校维度可能返回几千行。不设限的话，一次统计请求就会把几千行 JSON 推给前端；
/// 而"按学校统计"真正有用的做法是**排序后取前 N**（人数最多的若干所学校），
/// 所以这里固定 <c>ORDER BY count DESC LIMIT 200</c>，并把被截断这件事
/// 通过行数暴露给调用方（凑满 200 行就说明可能还有更多）。
/// </summary>
public class SqlSugarStatisticsQuery : IStatisticsQuery, ITransientDependency
{
    /// <summary>单次统计返回的最大分组数。</summary>
    private const int MaxGroups = 200;

    private readonly ISqlSugarClientAccessor _clientAccessor;

    public SqlSugarStatisticsQuery(ISqlSugarClientAccessor clientAccessor)
    {
        _clientAccessor = clientAccessor;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StatCountItem>> CountStudentsAsync(
        string dimension,
        string? scopePathCode,
        StudentStatFilter filter,
        CancellationToken cancellationToken = default)
    {
        var (keyExpr, nameExpr, joinExpr) = dimension switch
        {
            StudentStatDimensions.School =>
                ("s.school_org_id::text", "o.name", "left join sys_org o on o.id = s.school_org_id and o.is_deleted = false"),

            StudentStatDimensions.Grade =>
                ("s.grade_id::text", "g.grade_name", "left join sys_grade g on g.id = s.grade_id and g.is_deleted = false"),

            StudentStatDimensions.Class =>
                ("s.class_id::text", "c.class_name", "left join sys_class c on c.id = s.class_id and c.is_deleted = false"),

            StudentStatDimensions.StudentStatus => ("s.student_status_code", "null::text", string.Empty),

            StudentStatDimensions.Gender => ("s.gender_code", "null::text", string.Empty),

            StudentStatDimensions.AcademicYear => ("s.academic_year", "null::text", string.Empty),

            // 有/无照片是"缺照片名单补全进度"的口径，用学生主表的冗余列判断
            // （与缺照片名单接口同一判据，两处不能各写一套）
            StudentStatDimensions.HasPhoto =>
                ("case when s.photo_path is null then 'no' else 'yes' end", "null::text", string.Empty),

            _ => throw new ArgumentException(
                $"未知的学生统计维度「{dimension}」。可用维度：{string.Join("、", StudentStatDimensions.All)}。",
                nameof(dimension))
        };

        // 【"本级及下级"必须写成 `path = @scopePath or path like @scopeLike`，不能只写 like】
        //
        // 物化路径是 `省.市.县.校` 的形式，**叶子节点（学校）后面没有点**。
        // 因此 `like '学校路径.%'` 对学校自己**永远不匹配**：
        //   - 校级账号的默认范围就是本校（scopePath = 学校路径）→ 统计恒为 0；
        //   - 界面选了具体某所学校时同样恒为 0。
        // 实测抓到的就是这条：psql 里 `path = '...4250645634'` 得 3，
        // 而 `path like '...4250645634.%'` 得 0。
        // 这两个数字对不上，而界面上只是"统计出来是 0"，极难往路径匹配上想。
        var scopeIsExact = filter.IncludeDescendantOrgs ? "false" : "true";

        var sql = $"""
            select {keyExpr} as "Key", {nameExpr} as "Name", count(*)::bigint as "Count"
            from stu_student s
            {joinExpr}
            where s.is_deleted = false
              and (@scopePath is null
                   or s.school_path_code = @scopePath
                   or (not {scopeIsExact} and s.school_path_code like @scopeLike))
              and (@academicYear is null or s.academic_year = @academicYear)
              and (@gradeId is null or s.grade_id = @gradeId::uuid)
              and (@classId is null or s.class_id = @classId::uuid)
              and (@statusCode is null or s.student_status_code = @statusCode)
              and (@genderCode is null or s.gender_code = @genderCode)
            group by {keyExpr}, {nameExpr}
            order by count(*) desc
            limit {MaxGroups}
            """;

        return await QueryAsync(sql, BuildStudentParameters(scopePathCode, filter), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StatCountItem>> CountChangesAsync(
        string dimension,
        string? scopePathCode,
        ChangeStatFilter filter,
        CancellationToken cancellationToken = default)
    {
        // 维度只决定"分组键 / 名称列"，**不影响 join**（见下面的说明）。
        var (keyExpr, nameExpr) = dimension switch
        {
            ChangeStatDimensions.ChangeType =>
                ("a.change_type_id::text", "a.change_type_name"),

            ChangeStatDimensions.Status => ("a.status", "null::text"),

            ChangeStatDimensions.Month =>
                ("to_char(a.apply_date, 'YYYY-MM')", "null::text"),

            ChangeStatDimensions.SourceSchool =>
                ("a.source_school_org_id::text", "so.name"),

            ChangeStatDimensions.TargetSchool =>
                ("a.target_school_org_id::text", "to2.name"),

            _ => throw new ArgumentException(
                $"未知的异动统计维度「{dimension}」。可用维度：{string.Join("、", ChangeStatDimensions.All)}。",
                nameof(dimension))
        };

        // 【为什么要**无条件**把转出校(so)与转入校(to2)两个外连接都带上】
        //
        // 1) 数据范围对异动必须"两侧都算"：转出校与转入校都可能"是我校"。
        //    只按转出校过滤会让接收学校看不到转入的申请 —— 而它恰恰是要审的那一方。
        // 2) 更硬的一条：**范围条件里同时引用了 so 与 to2**。如果按维度
        //    只 join 其中一个（早期版本就是这么写的：`dimension is SourceSchool or
        //    TargetSchool ? joinExpr : 两个都 join`），那么查"按转出学校"时
        //    WHERE 里的 to2 在 FROM 里不存在 —— PostgreSQL 直接报
        //    `42P01: missing FROM-clause entry for table "to2"`，
        //    于是**恰恰在"按转出/转入学校"这两个维度上统计接口是 500**。
        //    实测抓到的就是这条（StatisticsAppServiceTests 的两个用例）。
        //    两个 join 都是 LEFT JOIN，多带一个不影响任何维度的计数。
        const string scopeJoin =
            "left join sys_org so on so.id = a.source_school_org_id and so.is_deleted = false " +
            "left join sys_org to2 on to2.id = a.target_school_org_id and to2.is_deleted = false";

        // 同学生统计：范围条件必须显式包含"路径本身"（叶子上没有点，
        // `like '叶子.%'` 匹配不到自己），详见 CountStudentsAsync 里的说明
        var scopeIsExact = filter.IncludeDescendantOrgs ? "false" : "true";

        var sql = $"""
            select {keyExpr} as "Key", {nameExpr} as "Name", count(*)::bigint as "Count"
            from stu_change_apply a
            {scopeJoin}
            where a.is_deleted = false
              and (@scopePath is null
                   or so.path_code = @scopePath or to2.path_code = @scopePath
                   or (not {scopeIsExact}
                       and (so.path_code like @scopeLike or to2.path_code like @scopeLike)))
              and (@changeTypeId is null or a.change_type_id = @changeTypeId::uuid)
              and (@status is null or a.status = @status)
              and (@from is null or a.apply_date >= @from::timestamp)
              and (@to is null or a.apply_date <= @to::timestamp)
            group by {keyExpr}, {nameExpr}
            order by count(*) desc
            limit {MaxGroups}
            """;

        return await QueryAsync(sql, BuildChangeParameters(scopePathCode, filter), cancellationToken);
    }

    private async Task<IReadOnlyList<StatCountItem>> QueryAsync(
        string sql,
        List<SugarParameter> parameters,
        CancellationToken cancellationToken)
    {
        var rows = await _clientAccessor.Client.Ado.SqlQueryAsync<StatCountRow>(sql, parameters);

        return rows
            .Select(r => new StatCountItem(r.Key ?? "（空）", r.Name, r.Count))
            .ToList();
    }

    private static List<SugarParameter> BuildStudentParameters(string? scopePathCode, StudentStatFilter filter)
    {
        return new List<SugarParameter>
        {
            // 前缀匹配统一补上 "."：不补的话 path_code = '990101' 会把 '9901010' 这种
            // 同前缀的**兄弟子树**也算进来（看着像"多算了几个人"，实际是范围泄漏）
            new("@scopePath", (object?)scopePathCode ?? DBNull.Value),
            new("@scopeLike", scopePathCode == null ? DBNull.Value : scopePathCode + ".%"),
            new("@academicYear", (object?)filter.AcademicYear ?? DBNull.Value),
            new("@gradeId", (object?)filter.GradeId ?? DBNull.Value),
            new("@classId", (object?)filter.ClassId ?? DBNull.Value),
            new("@statusCode", (object?)filter.StudentStatusCode ?? DBNull.Value),
            new("@genderCode", (object?)filter.GenderCode ?? DBNull.Value)
        };
    }

    private static List<SugarParameter> BuildChangeParameters(string? scopePathCode, ChangeStatFilter filter)
    {
        return new List<SugarParameter>
        {
            new("@scopePath", (object?)scopePathCode ?? DBNull.Value),
            new("@scopeLike", scopePathCode == null ? DBNull.Value : scopePathCode + ".%"),
            new("@changeTypeId", (object?)filter.ChangeTypeId ?? DBNull.Value),
            new("@status", (object?)filter.Status ?? DBNull.Value),
            new("@from", (object?)filter.From ?? DBNull.Value),
            new("@to", (object?)filter.To ?? DBNull.Value)
        };
    }

    /// <summary>SQL 投影用的行类型（用可写属性而不是 record：交给 ORM 填充）。</summary>
    private sealed class StatCountRow
    {
        public string? Key { get; set; }

        public string? Name { get; set; }

        public long Count { get; set; }
    }
}
