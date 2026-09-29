using QuestPDF.Drawing.Exceptions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Xjgl.Printing;

namespace Xjgl.Reporting;

/// <summary>
/// 学籍卡（A4 纵向，一人一页）。
///
/// 【版面即业务规则，几条刻意的取舍】
///   - **身份证号打掩码**：打印件会被拍照、传阅、归档，明文身份证号在纸质件上收不回来；
///   - **姓名含生僻字时用备用字体，并在卡片上标注「含生僻字」**：
///     纸质件上出现方块就没法核对了，标注能让经办人知道该核对什么；
///   - **没有照片时留出照片位并写「未采集照片」**：不能悄悄省略，
///     否则这张卡看起来像"资料齐全"，而实际上缺了关键一项；
///   - **打印时间与盖章/签名栏**：纸质件必须能追溯到"什么时候、由谁出的"。
/// </summary>
public sealed class StudentCardPdfRenderer : IStudentCardPdfRenderer
{
    private readonly PrintFontRegistry _fonts;

    public StudentCardPdfRenderer(PrintFontRegistry fonts)
    {
        _fonts = fonts;
    }

    public byte[] Render(StudentCardModel model)
    {
        _fonts.EnsureInitialized();

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.4f, Unit.Centimetre);
                page.DefaultTextStyle(style =>
                {
                    // 生僻字备用字体：把「主字体 + 备用字体」作为一个**字体族候选列表**
                    // 一起传给 FontFamily（`params string[]`），某个字形在主字体里找不到时
                    // QuestPDF 会依次往后找。
                    //
                    // ⚠ **不要用 `style.Fallback(x => x.FontFamily(...))`**：
                    //   该 API 已被 QuestPDF 标记为 obsolete，原文是
                    //   "This setting is **not supported** since the 2024.3 version"，
                    //   也就是**官方明确说这个写法不再被支持**。这类"不受支持的设置"
                    //   通常表现为**静默无效**：不报错，但也起不到回落作用 ——
                    //   而主字体一旦真的缺字形，就会直接撞上下面那条
                    //   `ThrowOnMissingTextGlyphs`（宁可报错也不出方块）。
                    //   因此这里按官方点名的方向改用"候选列表"。
                    //
                    //   顺带记一条：obsolete 提示里建议的替代品 `FontFamilyFallback`
                    //   在包里**并不存在** —— 整个 QuestPDF.dll 里这个字符串只出现在
                    //   那条提示自己里面（按成员名去查，一个都查不到）。
                    //   真正被支持的就是下面这种写法，依据是
                    //   `Settings.ThrowOnMissingFontFamilies` 的文档：
                    //   "the text is rendered with the first available font family
                    //    from the **fallback list**"。
                    return _fonts.RareCharacterFontFamily == null
                        ? style.FontSize(10.5f).FontFamily(_fonts.DefaultFontFamily)
                        : style.FontSize(10.5f).FontFamily(_fonts.DefaultFontFamily, _fonts.RareCharacterFontFamily);
                });

                page.Content().Column(col =>
                {
                    col.Spacing(8);

                    // ---- 标题区 ----
                    col.Item().AlignCenter().Text("河南省中小学生学籍卡").FontSize(19).Bold();
                    col.Item().AlignCenter().Text($"学校：{model.SchoolName}").FontSize(11);
                    col.Item().LineHorizontal(1).LineColor(Colors.Grey.Darken2);

                    // ---- 照片 + 基本信息 ----
                    col.Item().PaddingTop(6).Row(row =>
                    {
                        row.ConstantItem(86).Column(photo =>
                        {
                            RenderPhoto(photo, model.Photo);

                            photo.Item().PaddingTop(4).AlignCenter()
                                .Text($"学籍辅号\n{model.StudentNo}").FontSize(9).FontColor(Colors.Grey.Darken2);
                        });

                        row.ConstantItem(14);

                        row.RelativeItem().Column(info =>
                        {
                            Info(info, "姓名", model.Name);
                            Info(info, "性别", model.GenderName);
                            Info(info, "出生日期", model.BirthDate);
                            Info(info, "身份证号", model.MaskedIdCard ?? "—");
                            Info(info, "全国学籍号", model.NationalStudentNo ?? "—");
                            Info(info, "民族", model.NationName ?? "—");
                        });

                        row.ConstantItem(14);

                        row.RelativeItem().Column(info =>
                        {
                            Info(info, "年级", model.GradeName ?? "—");
                            Info(info, "班级", model.ClassName ?? "—");
                            Info(info, "学年", model.AcademicYear ?? "—");
                            Info(info, "学籍状态", model.StudentStatusName);
                            Info(info, "入学日期", model.EnrollDate ?? "—");
                            Info(info, "籍贯", model.NativePlace ?? "—");
                        });
                    });

                    // ---- 户籍与联系 ----
                    col.Item().PaddingTop(6).Column(info =>
                    {
                        Info(info, "户口性质", model.HouseholdName ?? "—");
                        Info(info, "家庭住址", model.HomeAddress ?? "—");
                        Info(info, "联系电话", model.PhoneNumber ?? "—");
                    });

                    // ---- 监护人 ----
                    col.Item().PaddingTop(8).Text("监护人").Bold().FontSize(11);
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(60);  // 姓名
                            columns.ConstantColumn(50);  // 关系
                            columns.RelativeColumn();    // 工作单位
                            columns.ConstantColumn(90);  // 联系电话
                        });

                        table.Header(header =>
                        {
                            HeaderCell(header, "姓名");
                            HeaderCell(header, "关系");
                            HeaderCell(header, "工作单位");
                            HeaderCell(header, "联系电话");
                        });

                        if (model.Guardians.Count == 0)
                        {
                            table.Cell().ColumnSpan(4).Padding(4)
                                .Text("未填报监护人信息").FontSize(9).FontColor(Colors.Grey.Darken1);
                        }
                        else
                        {
                            foreach (var guardian in model.Guardians)
                            {
                                BodyCell(table, guardian.Name);
                                BodyCell(table, guardian.RelationName ?? "—");
                                BodyCell(table, guardian.WorkUnit ?? "—");
                                BodyCell(table, guardian.PhoneNumber ?? "—");
                            }
                        }
                    });

                    // ---- 页脚：可追溯 ----
                    col.Item().PaddingTop(14).Row(row =>
                    {
                        row.RelativeItem().Text($"打印时间：{model.PrintedAt}").FontSize(9)
                            .FontColor(Colors.Grey.Darken2);

                        if (model.HasRareCharacter)
                        {
                            row.RelativeItem().AlignRight().Text("注：姓名含生僻字，请核对原件").FontSize(9)
                                .FontColor(Colors.Red.Medium);
                        }
                    });

                    col.Item().PaddingTop(18).Row(row =>
                    {
                        row.RelativeItem().Text("学校（盖章）：____________________").FontSize(10);
                        row.RelativeItem().AlignRight().Text("经办人签字：________________").FontSize(10);
                    });
                });
            });
        });

        return Generate(() => document.GeneratePdf());
    }

    /// <summary>
    /// 渲染并**把渲染库的异常翻译成域内的 <see cref="PrintRenderingException"/>**。
    ///
    /// 之所以把"缺字体"单独标出来：本项目刻意开启了
    /// <c>QuestPDF.Settings.ThrowOnMissingTextGlyphs</c>（画不出字形必须报错，
    /// 而不是出一张满是方块的学籍卡），而这类失败几乎总是"服务器没装中文字体"——
    /// 属于运维问题，提示运维去装字体，而不是让业务人员以为是系统坏了。
    /// </summary>
    private static byte[] Generate(Func<byte[]> render)
    {
        try
        {
            return render();
        }
        catch (DocumentDrawingException ex)
        {
            throw new PrintRenderingException(
                "PDF 生成失败（很可能是缺少可用字体或字形）",
                missingFont: true,
                ex);
        }
    }

    private static void RenderPhoto(ColumnDescriptor column, byte[]? photo)
    {
        var box = column.Item()
            .Width(30, Unit.Millimetre)
            .Height(40, Unit.Millimetre)
            .Border(1)
            .BorderColor(Colors.Grey.Medium);

        if (photo is { Length: > 0 })
        {
            box.AlignCenter().AlignMiddle().Image(photo).FitArea();
        }
        else
        {
            // 缺照片时明确写出来，而不是留白：留白看起来像"排版省略"，
            // 写清楚才能让经办人知道要去补照片
            box.AlignCenter().AlignMiddle().Text("未采集\n照片").FontSize(9).FontColor(Colors.Grey.Darken1);
        }
    }

    private static void Info(ColumnDescriptor column, string label, string value)
    {
        column.Item().PaddingBottom(4).Row(row =>
        {
            row.ConstantItem(62).Text(label).FontColor(Colors.Grey.Darken2);
            row.RelativeItem().Text(value);
        });
    }

    /// <summary>
    /// 表头单元格。
    ///
    /// 注意 QuestPDF 2026 的表头 API：`TableDescriptor.Header(Action&lt;TableCellDescriptor&gt;)`
    /// 传进来的是**一个游标**，每次调 <c>header.Cell()</c> 前进到下一格 ——
    /// 所以这里不能写成 `header.Cell()` 之外再套一层（我第一版按"参数是 TableDescriptor"
    /// 写，编译直接报类型不匹配，省了运行时才发现）。
    /// </summary>
    private static void HeaderCell(TableCellDescriptor header, string text)
    {
        header.Cell().Background(Colors.Grey.Lighten3).BorderBottom(1)
            .BorderColor(Colors.Grey.Medium).Padding(4).Text(text).FontSize(9).Bold();
    }

    private static void BodyCell(TableDescriptor table, string text)
    {
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4)
            .Text(text).FontSize(9);
    }
}
