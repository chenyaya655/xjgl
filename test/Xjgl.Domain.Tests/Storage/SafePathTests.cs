using Shouldly;
using Xunit;

namespace Xjgl.Storage;

/// <summary>
/// <see cref="SafePath"/> 的边界测试。
///
/// 【为什么单独测它】
///   它是照片存储 / 导出产物存储 / 异动附件存储**共用的那一道**防线。
///   这三处原先各写一份一模一样的 `ResolvePath`，而三份都用
///   `combined.StartsWith(_rootPath)` 做包含性判断 —— 根目录不带结尾分隔符时，
///   `../photos-evil/x.jpg` 会解析成 `.../photos-evil/x.jpg`，
///   它确实以 `.../photos` 开头，于是**校验通过、却已经跑到根目录外面**。
///   抽成一处之后，边界只需要在这里测一次。
///
///   这里的用例刻意**不需要真实目录**：SafePath 只做字符串层面的规范化与包含性判断，
///   所以测试跑得很快、也不依赖文件系统权限。
/// </summary>
public class SafePathTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "safe-path-root");

    // ==================================================================
    // 正常路径
    // ==================================================================

    [Fact]
    public void 子路径_落在根目录内()
    {
        var resolved = SafePath.ResolveWithin(Root, Path.Combine("photo", "a.jpg"), "照片路径");

        resolved.ShouldBe(Path.Combine(Path.GetFullPath(Root), "photo", "a.jpg"));
        SafePath.IsWithin(Path.GetFullPath(Root), resolved).ShouldBeTrue();
    }

    [Fact]
    public void 单点_解析为根目录本身_允许()
    {
        // "." 解析出来就是根目录：既要允许，也不能被"前缀"判断误杀
        var resolved = SafePath.ResolveWithin(Root, ".", "照片路径");

        resolved.ShouldBe(Path.GetFullPath(Root));
        SafePath.IsWithin(Path.GetFullPath(Root), resolved).ShouldBeTrue();
    }

    // ==================================================================
    // 穿越
    // ==================================================================

    [Fact]
    public void 上级目录_被拒绝()
    {
        Should.Throw<InvalidOperationException>(() => SafePath.ResolveWithin(Root, "../x.jpg", "照片路径"))
            .Message.ShouldContain("超出存储根目录");
    }

    [Fact]
    public void 兄弟目录只是名字前缀相同_也必须被拒绝()
    {
        // ★ 这一条是真实缺陷的回归。
        //   根目录 <temp>/safe-path-root，兄弟目录 <temp>/safe-path-root-evil。
        //   "../safe-path-root-evil/x.jpg" 解析后以 "<temp>/safe-path-root" 开头，
        //   旧实现（StartsWith 不带分隔符）会放行。
        var escape = $"../{Path.GetFileName(Root)}-evil/x.jpg";

        var ex = Should.Throw<InvalidOperationException>(
            () => SafePath.ResolveWithin(Root, escape, "照片路径"));

        ex.Message.ShouldContain("超出存储根目录");
        ex.Message.ShouldContain(
            escape,
            customMessage: "错误信息要带上原始相对路径，它是排查'谁写脏了库'的唯一线索");
    }

    [Fact]
    public void 根目录名后缀相同的兄弟目录_IsWithin_为假()
    {
        var root = Path.GetFullPath(Root);
        var separator = Path.DirectorySeparatorChar;

        SafePath.IsWithin(root, root).ShouldBeTrue("根目录本身算在范围内");
        SafePath.IsWithin(root, root + separator + "a.jpg").ShouldBeTrue();
        SafePath.IsWithin(root, root + "-evil" + separator + "a.jpg").ShouldBeFalse(
            "少补一个结尾分隔符，'前缀相同'就会被当成'在里面'");
        SafePath.IsWithin(root, root + "evil").ShouldBeFalse();
    }

    [Fact]
    public void 绝对路径_被拒绝()
    {
        // 相对路径被写成绝对路径时，Path.Combine 会丢弃根目录，
        // 结果必须被包含性检查拦下。
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";

        Should.Throw<InvalidOperationException>(() => SafePath.ResolveWithin(Root, absolute, "产物路径"))
            .Message.ShouldContain("超出存储根目录");
    }

    [Fact]
    public void 反斜杠穿越_只在Windows上算穿越()
    {
        // 反斜杠在 Linux 上只是普通文件名字符 —— 这是平台差异，
        // 用例必须跟着平台走，否则在 Linux/CI 上必红（此前就是这么红的）。
        if (OperatingSystem.IsWindows())
        {
            Should.Throw<InvalidOperationException>(() => SafePath.ResolveWithin(Root, @"..\x.jpg", "附件路径"));
        }
        else
        {
            var resolved = SafePath.ResolveWithin(Root, @"..\x.jpg", "附件路径");

            resolved.ShouldBe(Path.Combine(Path.GetFullPath(Root), @"..\x.jpg"),
                "Linux 上它是根目录里一个名字含反斜杠的普通文件");
        }
    }

    // ==================================================================
    // 入参
    // ==================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空路径_抛参数异常(string? relativePath)
    {
        Should.Throw<ArgumentException>(() => SafePath.ResolveWithin(Root, relativePath!, "附件路径"));
    }

    [Fact]
    public void 错误信息里带上标签_便于定位是哪一处存储()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => SafePath.ResolveWithin(Root, "../x", "产物路径"));

        ex.Message.ShouldContain("产物路径");
    }
}
