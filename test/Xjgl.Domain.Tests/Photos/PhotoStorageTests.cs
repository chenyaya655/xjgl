using Shouldly;
using Xunit;

namespace Xjgl.Photos;

/// <summary>
/// 照片路径规则与文件系统存储的单元测试。
///
/// 存储实现直接面对 NAS 的目录结构，是「照片会不会串到别人目录里」
/// 这类事故的最后一道防线，所以路径解析的安全边界要单独测：
///   - 路径穿越（../../）必须被拒绝；
///   - 写入必须是原子替换（先写临时文件再改名），不能留下半截文件；
///   - NAS 未挂载时构造函数不能把整个应用带崩。
/// </summary>
public class PhotoPathRuleTests
{
    private static readonly Guid School = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Student = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void 路径规则_按学校分目录且文件名用学生Id()
    {
        var path = PhotoPathRule.Build(School, Student, "photo.jpg");

        // photo/{学校组织Id:N}/{学生Id:N}.jpg
        path.ShouldBe($"photo/{School:N}/{Student:N}.jpg");
    }

    [Fact]
    public void 路径规则_不包含学籍号等身份信息()
    {
        var path = PhotoPathRule.Build(School, Student, "4101020001000123.jpg");

        // 原始文件名只用来取扩展名，不能出现在路径里
        path.ShouldNotContain("4101020001000123");
    }

    [Fact]
    public void 路径规则_一名学生只有一个固定路径()
    {
        // 反复上传只会覆盖同一个路径，不会在 NAS 上堆历史文件
        PhotoPathRule.Build(School, Student, "a.jpg")
            .ShouldBe(PhotoPathRule.Build(School, Student, "b.png").Replace(".png", ".jpg"));
    }

    [Theory]
    [InlineData("a.jpg", ".jpg")]
    [InlineData("a.JPG", ".jpg")]
    [InlineData("a.jpeg", ".jpg")]
    [InlineData("a.JPEG", ".jpg")]
    [InlineData("a.png", ".png")]
    [InlineData("a.bmp", ".bmp")]
    [InlineData("a.gif", ".jpg")]
    [InlineData("a.txt", ".jpg")]
    [InlineData("noextension", ".jpg")]
    [InlineData(null, ".jpg")]
    public void 扩展名归一化_白名单外一律回落为jpg(string? fileName, string expected)
    {
        PhotoPathRule.NormalizeExtension(fileName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.png", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.gif", false)]
    [InlineData("a.txt", false)]
    [InlineData("a", false)]
    [InlineData(null, false)]
    public void 扩展名白名单校验(string? fileName, bool expected)
    {
        PhotoPathRule.IsAllowedExtension(fileName).ShouldBe(expected);
    }

    [Fact]
    public void 缩略图路径_与学生照片同校目录下的thumb子目录()
    {
        PhotoPathRule.BuildThumbnail(School, Student)
            .ShouldBe($"photo/{School:N}/thumb/{Student:N}.jpg");
    }
}

/// <summary>
/// 文件系统存储的行为测试。用临时目录当存储根，不碰真实 NAS。
/// </summary>
public class FileSystemPhotoStorageTests : IDisposable
{
    private readonly string _root;

    public FileSystemPhotoStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xjgl-photo-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 测试清理失败不影响结论
            }
        }
    }

    private FileSystemPhotoStorage CreateStorage() => new("测试存储", _root);

    private static MemoryStream Content(string text = "hello")
        => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task 写入后可读回_删除后不存在()
    {
        var storage = CreateStorage();

        var write = await storage.WriteAsync("photo/a/b.jpg", Content("照片内容"));

        write.Success.ShouldBeTrue();
        write.Degraded.ShouldBeFalse();
        write.StoredPath.ShouldBe("photo/a/b.jpg");

        (await storage.ExistsAsync("photo/a/b.jpg")).ShouldBeTrue();

        await using (var stream = await storage.OpenReadAsync("photo/a/b.jpg"))
        {
            stream.ShouldNotBeNull();

            using var reader = new StreamReader(stream!);
            (await reader.ReadToEndAsync()).ShouldBe("照片内容");
        }

        await storage.DeleteAsync("photo/a/b.jpg");

        (await storage.ExistsAsync("photo/a/b.jpg")).ShouldBeFalse();
    }

    [Fact]
    public async Task 重复写入同一路径_覆盖旧内容且不留临时文件()
    {
        var storage = CreateStorage();

        await storage.WriteAsync("photo/a/b.jpg", Content("第一次"));
        await storage.WriteAsync("photo/a/b.jpg", Content("第二次"));

        await using (var stream = await storage.OpenReadAsync("photo/a/b.jpg"))
        {
            using var reader = new StreamReader(stream!);
            (await reader.ReadToEndAsync()).ShouldBe("第二次");
        }

        // 原子替换：目录里不应残留 .tmp
        Directory.GetFiles(Path.Combine(_root, "photo", "a")).ShouldNotContain(f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task 路径穿越_写入失败且给出明确原因()
    {
        var storage = CreateStorage();

        var write = await storage.WriteAsync("../逃逸.jpg", Content());

        write.Success.ShouldBeFalse();
        write.Error!.ShouldContain("超出存储根目录");
    }

    [Fact]
    public async Task 路径穿越_存在性与删除直接拒绝()
    {
        var storage = CreateStorage();

        await Should.ThrowAsync<InvalidOperationException>(async () => await storage.ExistsAsync("../../etc/passwd"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await storage.DeleteAsync("../x.jpg"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await storage.OpenReadAsync("..\\x.jpg"));
    }

    [Fact]
    public async Task 空路径_被拒绝()
    {
        var storage = CreateStorage();

        await Should.ThrowAsync<ArgumentException>(async () => await storage.ExistsAsync("   "));
    }

    [Fact]
    public async Task 读取不存在的文件_返回null()
    {
        var storage = CreateStorage();

        (await storage.OpenReadAsync("photo/不存在.jpg")).ShouldBeNull();
    }

    [Fact]
    public async Task 删除不存在的文件_不报错()
    {
        var storage = CreateStorage();

        await Should.NotThrowAsync(async () => await storage.DeleteAsync("photo/不存在.jpg"));
    }

    [Fact]
    public void 根目录不可创建_构造不抛异常()
    {
        // 模拟 NAS 未挂载：把根目录的父级做成一个**文件**
        var blocker = Path.Combine(Path.GetTempPath(), "xjgl-photo-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "占位文件");

        try
        {
            var storage = new FileSystemPhotoStorage("NAS", Path.Combine(blocker, "sub"));

            // 构造成功，但写入必然失败 —— 由上层熔断器识别并降级
            Should.NotThrow(() => storage.RootPath.ShouldNotBeNullOrEmpty());
        }
        finally
        {
            if (File.Exists(blocker))
            {
                File.Delete(blocker);
            }
        }
    }

    [Fact]
    public async Task 根目录不可创建_写入返回失败而不是抛异常()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "xjgl-photo-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "占位文件");

        try
        {
            var storage = new FileSystemPhotoStorage("NAS", Path.Combine(blocker, "sub"));

            var write = await storage.WriteAsync("photo/a.jpg", Content());

            write.Success.ShouldBeFalse();
            write.Error.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            if (File.Exists(blocker))
            {
                File.Delete(blocker);
            }
        }
    }
}
