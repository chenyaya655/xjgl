using System.Text;
using QuestPDF.Drawing.Exceptions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Volo.Abp;
using Xjgl.Printing;

namespace Xjgl.Reporting;

/// <summary>
/// 学生花名册（A4 横向，带页眉页脚与页码）。
///
/// 几个刻意的取舍：
///   - **横向 + 固定列宽**：花名册的用途是"对照检查"，列挤在一起就没法用；
///   - **表头每页重复**：多页时第二页没有表头，等于让人猜哪列是什么；
///   - **页脚写"第 X 页 / 共 Y 页"**：纸质件散页后必须能拼回去；
///   - **姓名可整体脱敏**（`MaskNames`）：对外提供的名单不该带全名。
/// </summary>
public sealed class StudentRosterPdfRenderer : IStudentRosterPdfRenderer
{
    private readonly PrintFontRegistry _fonts;

    public StudentRosterPdfRenderer(PrintFontRegistry fonts)
    {
        _fonts = fonts;
    }

    public byte[] Render(StudentRosterModel model)
    {
        _fonts.EnsureInitialized();

        var subtitle = BuildSubtitle(model);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1f, Unit.Centimetre);
                page.DefaultTextStyle(style =>
                {
                    // 与学籍卡同一处取舍（理由与依据详见 StudentCardPdfRenderer）：
                    // 备用字体走 FontFamily 的候选列表，**不要用已 obsolete 且不生效的
                    // `style.Fallback(...)`**。
                    return _fonts.RareCharacterFontFamily == null
                        ? style.FontSize(9).FontFamily(_fonts.DefaultFontFamily)
                        : style.FontSize(9).FontFamily(_fonts.DefaultFontFamily, _fonts.RareCharacterFontFamily);
                });

                page.Header().Column(header =>
                {
                    header.Item().AlignCenter().Text(model.Title).FontSize(16).Bold();
                    header.Item().AlignCenter().Text(subtitle).FontSize(10);
                    header.Item().PaddingTop(3).Row(row =>
                    {
                        row.RelativeItem().Text($"学校：{model.SchoolName}").FontSize(9);
                        row.RelativeItem().AlignRight()
                            .Text($"共 {model.Rows.Count} 人　打印时间：{model.PrintedAt}").FontSize(9);
                    });
                    header.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Darken2);
                });

                page.Content().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(28);   // 序号
                        columns.ConstantColumn(88);   // 学籍辅号
                        columns.ConstantColumn(72);   // 姓名
                        columns.ConstantColumn(32);   // 性别
                        columns.ConstantColumn(62);   // 出生日期
                        columns.ConstantColumn(112);  // 身份证号
                        columns.RelativeColumn();     // 家庭住址
                        columns.ConstantColumn(70);   // 联系电话
                        columns.ConstantColumn(48);   // 学籍状态
                    });

                    table.Header(header =>
                    {
                        HeaderCell(header, "序号");
                        HeaderCell(header, "学籍辅号");
                        HeaderCell(header, "姓名");
                        HeaderCell(header, "性别");
                        HeaderCell(header, "出生日期");
                        HeaderCell(header, "身份证号");
                        HeaderCell(header, "家庭住址");
                        HeaderCell(header, "联系电话");
                        HeaderCell(header, "学籍状态");
                    });

                    foreach (var item in model.Rows)
                    {
                        BodyCell(table, item.Seq.ToString());
                        BodyCell(table, item.StudentNo);
                        BodyCell(table, model.MaskNames ? Mask(item.Name) : item.Name);
                        BodyCell(table, item.GenderName);
                        BodyCell(table, item.BirthDate);
                        BodyCell(table, item.MaskedIdCard ?? "—");
                        BodyCell(table, item.HomeAddress ?? "—");
                        BodyCell(table, item.PhoneNumber ?? "—");
                        BodyCell(table, item.StudentStatusName);
                    }

                    if (model.Rows.Count == 0)
                    {
                        table.Cell().ColumnSpan(9).Padding(8).AlignCenter()
                            .Text("没有符合条件的学生").FontColor(Colors.Grey.Darken1);
                    }
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"制表人：{model.PrintedBy ?? "—"}").FontSize(8)
                        .FontColor(Colors.Grey.Darken2);

                    row.RelativeItem().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken2));
                        text.Span("第 ");
                        text.CurrentPageNumber();
                        text.Span(" 页 / 共 ");
                        text.TotalPages();
                        text.Span(" 页");
                    });

                    row.RelativeItem().AlignRight().Text("（本表由学籍管理系统生成）").FontSize(8)
                        .FontColor(Colors.Grey.Darken2);
                });
            });
        });

        try
        {
            return document.GeneratePdf();
        }
        catch (DocumentDrawingException ex)
        {
            // 与学籍卡渲染器同一处理：把渲染库异常翻成域内异常，应用层不必依赖 QuestPDF
            throw new PrintRenderingException(
                "PDF 生成失败（很可能是缺少可用字体或字形）",
                missingFont: true,
                ex);
        }
    }

    private static string BuildSubtitle(StudentRosterModel model)
    {
        var parts = new List<string>();

        if (!model.AcademicYear.IsNullOrWhiteSpace())
        {
            parts.Add($"学年：{model.AcademicYear}");
        }

        if (!model.GradeName.IsNullOrWhiteSpace())
        {
            parts.Add($"年级：{model.GradeName}");
        }

        if (!model.ClassName.IsNullOrWhiteSpace())
        {
            parts.Add($"班级：{model.ClassName}");
        }

        return parts.Count == 0 ? "全部在册学生" : string.Join("　", parts);
    }

    /// <summary>整体脱敏：保留姓氏，其余用 * 代替（按 Unicode 码点处理，生僻字不会被切坏）。</summary>
    private static string Mask(string name)
    {
        var runes = name.EnumerateRunes().ToList();

        return runes.Count <= 1
            ? name
            : string.Concat(runes[0].ToString(), new string('*', runes.Count - 1));
    }

    /// <summary>表头单元格（QuestPDF 2026 的 header 参数是"格子游标"，见学籍卡渲染器里的说明）。</summary>
    private static void HeaderCell(TableCellDescriptor header, string text)
    {
        header.Cell().Background(Colors.Grey.Lighten3).BorderBottom(1)
            .BorderColor(Colors.Grey.Medium).Padding(3).Text(text).Bold().FontSize(8.5f);
    }

    private static void BodyCell(TableDescriptor table, string text)
    {
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(3)
            .Text(text).FontSize(8.5f);
    }
}
