using Shouldly;
using Xunit;

namespace Xjgl.Text;

/// <summary>
/// CSV 解析/生成的单元测试。
///
/// 导入功能的第一道关口就是解析：解析错一个字段，后面所有校验都在错误的数据上做，
/// 而用户看到的只是"某一列全空"或"姓名串到班级里"这类莫名其妙的报错。
/// 因此这里把引号、换行、BOM、空行这些边界逐条钉死。
/// </summary>
public class CsvTests
{
    [Fact]
    public void 简单行_按逗号拆分()
    {
        var rows = Csv.Parse("a,b,c\r\nd,e,f");

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new[] { "a", "b", "c" });
        rows[1].ShouldBe(new[] { "d", "e", "f" });
    }

    [Fact]
    public void 引号包裹_字段内的逗号不当分隔符()
    {
        var rows = Csv.Parse("姓名,住址\r\n张三,\"河南省郑州市,金水区1号\"");

        rows[1][1].ShouldBe("河南省郑州市,金水区1号");
    }

    [Fact]
    public void 两个连续双引号_表示一个字面双引号()
    {
        var rows = Csv.Parse("a\r\n\"他说\"\"你好\"\"\"");

        rows[1][0].ShouldBe("他说\"你好\"");
    }

    [Fact]
    public void 引号内的换行_属于同一个字段()
    {
        var rows = Csv.Parse("住址\r\n\"第一行\n第二行\"");

        rows.Count.ShouldBe(2);
        rows[1][0].ShouldContain("第一行");
        rows[1][0].ShouldContain("第二行");
    }

    [Fact]
    public void 带BOM的表头_第一个列名不应带不可见字符()
    {
        // Excel 另存为 CSV 时会带 UTF-8 BOM；不处理的话第一个表头会变成 "\uFEFF姓名"，
        // 于是"姓名"这一列永远匹配不上 —— 用户看到的只是"姓名列全是空"
        var rows = Csv.Parse("\uFEFF姓名,性别\r\n张三,男");

        var header = Csv.BuildHeaderIndex(rows[0]);

        header.ShouldContainKey("姓名");
        Csv.Cell(rows[1], header, "姓名").ShouldBe("张三");
    }

    [Fact]
    public void CRLF行尾_不应产生多余空行()
    {
        var rows = Csv.Parse("a,b\r\nc,d\r\n");

        rows.Count.ShouldBe(2);
    }

    [Fact]
    public void 最后一行没有换行结尾_也要收进来()
    {
        var rows = Csv.Parse("a,b\r\nc,d");

        rows.Count.ShouldBe(2);
        rows[1][1].ShouldBe("d");
    }

    [Fact]
    public void 空文本_返回空集合()
    {
        Csv.Parse(null).ShouldBeEmpty();
        Csv.Parse(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void 按表头名取列_列序变化也能取对()
    {
        // Excel 里调列序是常事：按列序解析会把"班级"读成"年级"
        var rows = Csv.Parse("班级,姓名,年级\r\n1,张三,3");
        var header = Csv.BuildHeaderIndex(rows[0]);

        Csv.Cell(rows[1], header, "姓名").ShouldBe("张三");
        Csv.Cell(rows[1], header, "年级").ShouldBe("3");
        Csv.Cell(rows[1], header, "班级").ShouldBe("1");
    }

    [Fact]
    public void 表头里不存在的列_取到空串而不是越界()
    {
        var rows = Csv.Parse("姓名\r\n张三");
        var header = Csv.BuildHeaderIndex(rows[0]);

        Csv.Cell(rows[1], header, "不存在的列").ShouldBe(string.Empty);
    }

    [Fact]
    public void 表头名忽略大小写与首尾空格()
    {
        var rows = Csv.Parse(" 姓名 ,性别\r\n张三,男");
        var header = Csv.BuildHeaderIndex(rows[0]);

        Csv.Cell(rows[1], header, "姓名").ShouldBe("张三");
    }

    [Fact]
    public void 写入_含逗号或引号的字段自动加引号并转义()
    {
        var csv = Csv.Write(new[]
        {
            new[] { "姓名", "住址" },
            new[] { "张三", "郑州市,金水区" },
            new[] { "李四", "他说\"你好\"" }
        });

        csv.ShouldContain("\"郑州市,金水区\"");
        csv.ShouldContain("\"他说\"\"你好\"\"\"");
    }

    [Fact]
    public void 写入给Excel用_带UTF8_BOM()
    {
        // 不带 BOM，中文 Windows 上的 Excel 会按 GBK 打开 UTF-8 文件 → 整份名单乱码
        var csv = Csv.WriteForExcel(new[] { new[] { "姓名" } });

        csv[0].ShouldBe('\uFEFF');
    }

    [Fact]
    public void 往返_写出再读回内容一致()
    {
        var source = new[]
        {
            new[] { "姓名", "住址", "备注" },
            new[] { "张三", "郑州市,金水区", "他说\"你好\"" },
            new[] { "李四", "北京市", "" }
        };

        var rows = Csv.Parse(Csv.Write(source));

        rows.Count.ShouldBe(3);
        rows[1].ShouldBe(source[1]);
        rows[2].ShouldBe(source[2]);
    }
}
