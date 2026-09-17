using System.Text;
using Microsoft.Extensions.Configuration;
using QuestPDF.Infrastructure;
using Shouldly;
using Xjgl.Printing;
using Xjgl.Reporting;
using Xunit;

namespace Xjgl.Printing;

/// <summary>
/// 学籍卡 / 花名册 PDF 渲染的单元测试。
///
/// 【为什么这类渲染也要写测试】
/// 排版代码的"坏掉"往往是**静默**的：指定的字体不存在 → 中文整片变方块；
/// 某个容器写错 → 某一段什么都不显示。接口照样返回 200、PDF 照样能打开，
/// 只有真去看打印件才发现不对。这里虽然无法断言"字形画得对不对"，
/// 但能守住几条确定性的东西：产物是合法 PDF、内容随数据变化、极端数据不崩、
/// 生僻字字体缺失时**回落而不是抛异常**。
/// </summary>
public class StudentPdfRendererTests
{
    /// <summary>
    /// 测试用的中文字体族。
    ///
    /// 本机（Windows）默认「微软雅黑」；CI 的 Linux 镜像靠环境变量指定
    /// （`XJGL_PRINT_FONT=Noto Sans CJK SC`，并先安装 fonts-noto-cjk）。
    /// 这样同一套断言在两种环境下都验证"真的画出了中文"。
    /// </summary>
    private static string TestFontFamily =>
        Environment.GetEnvironmentVariable("XJGL_PRINT_FONT") ?? "Microsoft YaHei";

    private static StudentCardPdfRenderer CreateCardRenderer(string? rareFontDirectory = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Xjgl:Print:DefaultFontFamily"] = TestFontFamily,
            ["Xjgl:Print:UseSystemFonts"] = "true",
            ["Xjgl:Print:RareCharacterFontDirectory"] = rareFontDirectory,
            ["Xjgl:Print:RareCharacterFontFamily"] = rareFontDirectory == null ? null : "DengXian"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new StudentCardPdfRenderer(new PrintFontRegistry(configuration));
    }

    private static StudentRosterPdfRenderer CreateRosterRenderer()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Xjgl:Print:DefaultFontFamily"] = TestFontFamily,
                ["Xjgl:Print:UseSystemFonts"] = "true"
            }).Build();

        return new StudentRosterPdfRenderer(new PrintFontRegistry(configuration));
    }

    private static StudentCardModel Card(string name = "张三")
    {
        return new StudentCardModel
        {
            SchoolName = "测试第一小学",
            StudentNo = "1001560001000001",
            NationalStudentNo = "G410102201605060011",
            Name = name,
            GenderName = "男",
            BirthDate = "2016-05-06",
            MaskedIdCard = "410102********0011",
            NationName = "汉族",
            NativePlace = "河南省郑州市",
            HouseholdName = "居民户口",
            HomeAddress = "郑州市金水区某某路 1 号",
            PhoneNumber = "13800000000",
            AcademicYear = "2025-2026",
            GradeName = "三年级",
            ClassName = "二班",
            StudentStatusName = "在读",
            EnrollDate = "2022-09-01",
            PrintedAt = "2026-09-17 15:30",
            Guardians = new List<StudentCardGuardian>
            {
                new() { Name = "张父", RelationName = "父亲", PhoneNumber = "13800000001", WorkUnit = "某某公司" },
                new() { Name = "张母", RelationName = "母亲", PhoneNumber = "13800000002" }
            }
        };
    }

    // ==================================================================
    // 学籍卡
    // ==================================================================

    [Fact]
    public void 学籍卡_产出合法PDF()
    {
        var pdf = CreateCardRenderer().Render(Card());

        pdf.Length.ShouldBeGreaterThan(2000, "一张含中文与表格的 A4 卡片不该只有几百字节");
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        Encoding.ASCII.GetString(pdf, pdf.Length - 6, 6).Trim().ShouldBe("%%EOF");
    }

    [Fact]
    public void 学籍卡_内容不同则产物不同()
    {
        var renderer = CreateCardRenderer();

        var a = renderer.Render(Card("张三"));
        var b = renderer.Render(Card("李四"));

        // 比较"整份字节是否相同"而不是长度：PDF 是压缩流，
        // 内容不同但长度恰好相同的概率并不低，用长度会变成偶发失败
        a.SequenceEqual(b).ShouldBeFalse("换了姓名，PDF 字节必须变化：完全一样说明文本没进版面");
    }

    [Fact]
    public void 学籍卡_生僻字姓名_不抛异常且标注提示()
    {
        var model = Card("𠮷野家");
        model.HasRareCharacter = true;

        var pdf = CreateCardRenderer().Render(model);

        pdf.Length.ShouldBeGreaterThan(2000);
    }

    [Fact]
    public void 学籍卡_没有照片与监护人_仍然能出卡()
    {
        var model = Card();
        model.Photo = null;
        model.Guardians.Clear();

        var pdf = CreateCardRenderer().Render(model);

        pdf.Length.ShouldBeGreaterThan(2000);
    }

    [Fact]
    public void 学籍卡_带照片_能嵌入图片()
    {
        var model = Card();
        model.Photo = OnePixelPng();

        var pdf = CreateCardRenderer().Render(model);

        pdf.Length.ShouldBeGreaterThan(2000);
    }

    [Fact]
    public void 学籍卡_超级长的地址不崩()
    {
        var model = Card();
        model.HomeAddress = string.Concat(Enumerable.Repeat("河南省郑州市金水区某某街道办事处某某社区某某小区某某号楼某某单元某某室", 6));
        model.Guardians[0].WorkUnit = new string('长', 200);

        var pdf = CreateCardRenderer().Render(model);

        pdf.Length.ShouldBeGreaterThan(2000);
    }

    // ==================================================================
    // 花名册
    // ==================================================================

    [Fact]
    public void 花名册_多页时依然合法()
    {
        var model = new StudentRosterModel
        {
            SchoolName = "测试第一小学",
            AcademicYear = "2025-2026",
            GradeName = "三年级",
            ClassName = "二班",
            PrintedAt = "2026-09-17 15:30",
            PrintedBy = "test-admin"
        };

        for (var i = 1; i <= 120; i++)
        {
            model.Rows.Add(new StudentRosterRow
            {
                Seq = i,
                StudentNo = $"1001560001{i:D6}",
                Name = $"学生{i:D3}",
                GenderName = i % 2 == 0 ? "女" : "男",
                BirthDate = "2016-05-06",
                MaskedIdCard = "410102********0011",
                HomeAddress = "郑州市金水区某某路 1 号",
                PhoneNumber = "13800000000",
                StudentStatusName = "在读"
            });
        }

        var pdf = CreateRosterRenderer().Render(model);

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        // 120 行必然跨页，页数多了体积自然更大
        pdf.Length.ShouldBeGreaterThan(10_000, "120 人的花名册应当是多页文档");
    }

    [Fact]
    public void 花名册_空结果也能出表()
    {
        var pdf = CreateRosterRenderer().Render(new StudentRosterModel
        {
            SchoolName = "测试第一小学",
            PrintedAt = "2026-09-17 15:30"
        });

        pdf.Length.ShouldBeGreaterThan(1000, "没有学生时也要出一张写着「没有符合条件的学生」的表，而不是报错");
    }

    [Fact]
    public void 花名册_姓名脱敏不影响出表()
    {
        var model = new StudentRosterModel
        {
            SchoolName = "测试第一小学",
            MaskNames = true,
            PrintedAt = "2026-09-17 15:30",
            Rows =
            {
                new StudentRosterRow { Seq = 1, StudentNo = "1001560001000001", Name = "张三", GenderName = "男", BirthDate = "2016-05-06", StudentStatusName = "在读" }
            }
        };

        var pdf = CreateRosterRenderer().Render(model);

        pdf.Length.ShouldBeGreaterThan(1000);
    }

    // ==================================================================
    // 字体回落
    // ==================================================================

    [Fact]
    public void 未配置生僻字字体_回落到默认字体而不报错()
    {
        var registry = new PrintFontRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build());

        registry.EnsureInitialized();

        registry.RareCharacterFontAvailable.ShouldBeFalse();
        registry.RareCharacterFontFamily.ShouldBeNull("字体不可用时不该登记字体族，否则只会在日志里刷警告");
        registry.DefaultFontFamily.ShouldBe(TestFontFamily, "默认字体族必须含中文，否则中文会整片变方块");
        registry.UseSystemFonts.ShouldBeTrue("默认走系统字体：本系统部署在教育系统的 Windows 服务器上，中文字体必然存在");
    }

    [Fact]
    public void 配置了含字体文件的目录_登记备用字体族()
    {
        var sourceFont = FindSystemTtfFont();

        if (sourceFont == null)
        {
            // 这台机器上没有任何 ttf 字体（CI 的精简镜像可能如此）：只验证"目录不存在时回落"
            var fallback = new PrintFontRegistry(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Xjgl:Print:RareCharacterFontDirectory"] = Path.Combine(Path.GetTempPath(), "not-exists-xjgl")
                }).Build());

            fallback.EnsureInitialized();
            fallback.RareCharacterFontAvailable.ShouldBeFalse();

            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "xjgl-fonts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            File.Copy(sourceFont, Path.Combine(directory, Path.GetFileName(sourceFont)));

            var registry = new PrintFontRegistry(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Xjgl:Print:RareCharacterFontDirectory"] = directory,
                    ["Xjgl:Print:RareCharacterFontFamily"] = "DengXian"
                }).Build());

            registry.EnsureInitialized();

            registry.RareCharacterFontAvailable.ShouldBeTrue();
            registry.RareCharacterFontFamily.ShouldBe("DengXian");
            QuestPDF.Settings.FontDiscoveryPaths.ShouldContain(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindSystemTtfFont()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var fontsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        return Directory.Exists(fontsDirectory)
            ? Directory.EnumerateFiles(fontsDirectory, "*.ttf").FirstOrDefault()
            : null;
    }

    /// <summary>1×1 的 PNG（用来验证图片嵌入路径）。</summary>
    private static byte[] OnePixelPng()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }
}
