namespace Xjgl.Printing;

/// <summary>
/// 学籍卡的数据（**只包含打印需要的字段**）。
///
/// 为什么不让渲染器直接吃 `StudentDto`：
///   打印是"确定性的排版"，而 DTO 是"接口契约"—— 后者一改（加字段/改名字），
///   排版代码就跟着受影响；反过来，渲染器为了拿到数据也会被迫依赖整个业务模型。
///   中间隔一层纯数据的视图模型，两边各自演进，也能在单元测试里手工构造极端数据
///   （超长姓名、生僻字、缺照片、没有监护人）而不必先造一堆业务数据。
/// </summary>
public sealed class StudentCardModel
{
    // ---- 页眉 ----
    public string SchoolName { get; set; } = string.Empty;

    // ---- 标识 ----
    public string StudentNo { get; set; } = string.Empty;
    public string? NationalStudentNo { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>是否含生僻字：打印时用备用字体，并在卡片上标注，便于人工核对。</summary>
    public bool HasRareCharacter { get; set; }

    // ---- 基本信息 ----
    public string GenderName { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;

    /// <summary>脱敏后的身份证号（打印件上不留明文）。</summary>
    public string? MaskedIdCard { get; set; }

    public string? NationName { get; set; }
    public string? NativePlace { get; set; }
    public string? HouseholdName { get; set; }
    public string? HomeAddress { get; set; }
    public string? PhoneNumber { get; set; }

    // ---- 就学 ----
    public string? AcademicYear { get; set; }
    public string? GradeName { get; set; }
    public string? ClassName { get; set; }
    public string StudentStatusName { get; set; } = string.Empty;
    public string? EnrollDate { get; set; }

    /// <summary>学生照片（JPEG/PNG 字节）。为空时卡片上留出照片位并标注"未采集"。</summary>
    public byte[]? Photo { get; set; }

    /// <summary>监护人（最多打印前几位）。</summary>
    public List<StudentCardGuardian> Guardians { get; set; } = new();

    /// <summary>打印时间（由调用方按服务器时钟写入，保证可追溯）。</summary>
    public string PrintedAt { get; set; } = string.Empty;
}

/// <summary>学籍卡上的监护人一行。</summary>
public sealed class StudentCardGuardian
{
    public string Name { get; set; } = string.Empty;
    public string? RelationName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? WorkUnit { get; set; }
}

/// <summary>
/// 花名册的数据。
/// </summary>
public sealed class StudentRosterModel
{
    public string Title { get; set; } = "学生花名册";
    public string SchoolName { get; set; } = string.Empty;
    public string? GradeName { get; set; }
    public string? ClassName { get; set; }
    public string? AcademicYear { get; set; }

    public List<StudentRosterRow> Rows { get; set; } = new();

    /// <summary>姓名是否脱敏（脱敏件用于对外提供，避免泄露学生名单）。</summary>
    public bool MaskNames { get; set; }

    public string PrintedAt { get; set; } = string.Empty;

    /// <summary>制表人（打印件上要能看出是谁出的表）。</summary>
    public string? PrintedBy { get; set; }
}

/// <summary>花名册的一行。</summary>
public sealed class StudentRosterRow
{
    public int Seq { get; set; }
    public string StudentNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GenderName { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string? MaskedIdCard { get; set; }
    public string? HomeAddress { get; set; }
    public string? PhoneNumber { get; set; }
    public string StudentStatusName { get; set; } = string.Empty;
    public string? Remark { get; set; }
}

/// <summary>渲染学籍卡 PDF。</summary>
public interface IStudentCardPdfRenderer
{
    byte[] Render(StudentCardModel model);
}

/// <summary>渲染花名册 PDF。</summary>
public interface IStudentRosterPdfRenderer
{
    byte[] Render(StudentRosterModel model);
}

/// <summary>
/// PDF 渲染失败（不含具体渲染库的异常类型）。
///
/// 为什么要包一层：渲染库的异常类型属于基础设施细节，
/// 应用层若直接 catch 它，就等于让业务代码依赖具体渲染库
/// （换库要改业务代码，分层也白做了）。
/// </summary>
public sealed class PrintRenderingException : Exception
{
    public PrintRenderingException(string message, bool missingFont, Exception? innerException = null)
        : base(message, innerException)
    {
        MissingFont = missingFont;
    }

    /// <summary>是否由"缺少可用字体/字形"导致（运维问题，提示语不同）。</summary>
    public bool MissingFont { get; }
}

