using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Volo.Abp.Timing;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Photos.Dtos;
using Xjgl.Students;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Photos;

/// <summary>
/// 照片缩略图链路的集成测试（真实图片 + 真实存储 + 真实数据库）。
///
/// 【为什么必须用真实 JPEG】
/// 照片模块原有的测试用的是"随机字节 + JPEG 魔数"的合成内容 —— 对上传/秒传/降级这些
/// 逻辑足够，但**缩略图根本生成不出来**（解码失败）。缩略图这条链路只有在真图片上
/// 才走得通，所以本文件用 ImageSharp 现画图来测。
///
/// 【守四件事】
///   1. 尺寸与缩略图被正确登记（列表页靠它决定加载什么）；
///   2. 缩略图**确实更小**（否则"优化流量"只是口号）；
///   3. 缩略图缺失时**回落原图**而不是白屏；
///   4. 缩略图同样受数据范围约束（照片是最容易被越权爬取的接口）。
/// </summary>
public class PhotoThumbnailTests : XjglIntegrationTestBase
{
    private const string Year = "2071-2072";

    private readonly IPhotoUploadService _uploadService;
    private readonly IPhotoCompensationService _compensationService;
    private readonly IPhotoContentService _contentService;
    private readonly IStudentAppService _studentAppService;
    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<StuPhoto> _photos;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly NasPhotoStorage _nasStorage;
    private readonly LocalFallbackPhotoStorage _fallbackStorage;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public PhotoThumbnailTests()
    {
        _uploadService = GetRequiredService<IPhotoUploadService>();
        _compensationService = GetRequiredService<IPhotoCompensationService>();
        _contentService = GetRequiredService<IPhotoContentService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _photos = GetRequiredService<IXjglRepository<StuPhoto>>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _nasStorage = GetRequiredService<NasPhotoStorage>();
        _fallbackStorage = GetRequiredService<LocalFallbackPhotoStorage>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    /// <summary>造一张真实 JPEG（宽高可控）。</summary>
    private static byte[] RealJpeg(int width = 1600, int height = 1200)
    {
        using var image = new Image<Rgba32>(width, height);

        image.Mutate(x => x.BackgroundColor(Color.LightBlue));

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);

        return buffer.ToArray();
    }

    private static (int Width, int Height) SizeOf(byte[] content)
    {
        using var image = Image.Load(content);

        return (image.Width, image.Height);
    }

    private static PhotoUploadRequest Request(Guid studentId, byte[] content, string fileName = "photo.jpg") =>
        new()
        {
            StudentId = studentId,
            FileName = fileName,
            Content = new MemoryStream(content),
            Length = content.Length
        };

    private async Task<OrgDto> CreateSchoolAsync(string name = "缩略图测试学校")
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
                {
                    OrgType = OrgTypes.School,
                    Code = "41" + Random.Shared.Next(10000000, 99999999),
                    Name = name
                });
            }
            catch (BusinessException)
            {
                // 学校标识码撞号（测试库跨轮累积），换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的学校标识码。");
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name = "缩略图学生")
    {
        return await RetryOnDuplicateIdCardAsync(() =>
            _studentAppService.CreateAsync(new CreateUpdateStudentDto
            {
                Name = name,
                GenderCode = "1",
                BirthDate = new DateTime(2016, 5, 6),
                IdCardTypeCode = "1",
                IdCardNumber = NewUniqueIdCard(2016),
                SchoolOrgId = schoolId,
                AcademicYear = Year,
                StudentStatusCode = StudentStatuses.Studying
            }));
    }

    /// <summary>把「NAS」弄坏：删目录、原地放同名文件，任何目录创建都会失败。</summary>
    private void BreakNas()
    {
        var root = _nasStorage.RootPath;

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var parent = Path.GetDirectoryName(root);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(root, "NAS 故障演练：占位文件。");
    }

    private void RepairNas()
    {
        var root = _nasStorage.RootPath;

        if (File.Exists(root))
        {
            File.Delete(root);
        }

        Directory.CreateDirectory(root);
    }

    private string NasPath(string relativePath)
        => Path.Combine(_nasStorage.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string FallbackPath(string relativePath)
        => Path.Combine(_fallbackStorage.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<Guid> LinkUserToOrgAsync(Guid orgId, string prefix)
    {
        var userId = _guidGenerator.Create();

        var predefine = new SysUserPredefine(
            _guidGenerator.Create(),
            $"{prefix}用户",
            "139" + Random.Shared.Next(10000000, 99999999),
            orgId,
            "TESTCODE",
            _clock.Now.AddDays(30));

        predefine.MarkActivated(userId, _clock.Now);
        await _predefines.InsertAsync(predefine, autoSave: true);

        return userId;
    }

    private async Task<T> InScopeAsAsync<T>(
        Guid userId,
        string userName,
        Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, Array.Empty<string>());

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    // ==================================================================
    // 上传：尺寸与缩略图
    // ==================================================================

    [Fact]
    public async Task 上传真实照片_登记尺寸并生成缩略图()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var result = await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg(1600, 1200)));

        result.Success.ShouldBeTrue();
        result.Width.ShouldBe(1600);
        result.Height.ShouldBe(1200);
        result.ThumbnailPath.ShouldNotBeNull();

        var row = await _photos.FindAsync(p => p.StudentId == student.Id);

        row.ShouldNotBeNull();
        row!.Width.ShouldBe(1600, "台账里的宽高是列表页决定「要不要拉原图」的依据");
        row.Height.ShouldBe(1200);
        row.ThumbnailPath.ShouldNotBeNullOrWhiteSpace();
        row.ThumbnailPath!.Contains("thumb").ShouldBeTrue();

        File.Exists(NasPath(row.ThumbnailPath!)).ShouldBeTrue("缩略图应当与照片一起写到主存储");
    }

    [Fact]
    public async Task 缩略图_明显小于原图且长边不超上限()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var original = RealJpeg(2400, 1800);

        await _uploadService.UploadOneAsync(Request(student.Id, original));

        var row = await _photos.FindAsync(p => p.StudentId == student.Id);
        var thumbnail = await File.ReadAllBytesAsync(NasPath(row!.ThumbnailPath!));

        thumbnail.Length.ShouldBeLessThan(original.Length / 4, "缩略图不减体积就等于没做");

        var size = SizeOf(thumbnail);

        Math.Max(size.Width, size.Height).ShouldBeLessThanOrEqualTo(480);
        size.Width.ShouldBe(480);
        size.Height.ShouldBe(360, "等比缩放：脸不能被压扁");
    }

    [Fact]
    public async Task 非图片内容_上传仍成功但没有尺寸与缩略图()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        // 改了后缀的文本：扩展名校验能过（.jpg），但内容不是图片
        var result = await _uploadService.UploadOneAsync(
            Request(student.Id, System.Text.Encoding.UTF8.GetBytes("这不是图片，只是文本。")));

        result.Success.ShouldBeTrue("照片本身照样存下来了，不该因为缩略图生成不了就判定上传失败");
        result.Width.ShouldBe(0);
        result.Height.ShouldBe(0);
        result.ThumbnailPath.ShouldBeNull();

        var row = await _photos.FindAsync(p => p.StudentId == student.Id);

        row!.ThumbnailPath.ShouldBeNull();
        row.Width.ShouldBe(0);
    }

    // ==================================================================
    // 读取：缩略图 / 原图 / 回落
    // ==================================================================

    [Fact]
    public async Task 读取_缩略图接口返回缩小后的图()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg(1600, 1200)));

        var content = await _contentService.GetAsync(student.Id, thumbnail: true);

        content.ShouldNotBeNull();
        content!.IsThumbnail.ShouldBeTrue();
        content.ContentType.ShouldBe("image/jpeg");
        content.ETag.ShouldNotBeNullOrWhiteSpace("内容哈希要作为 ETag，照片换新后浏览器才会重新拉");

        var size = SizeOf(content.Content);

        Math.Max(size.Width, size.Height).ShouldBeLessThanOrEqualTo(480);
    }

    [Fact]
    public async Task 读取_原图接口返回原始尺寸()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var original = RealJpeg(1600, 1200);
        await _uploadService.UploadOneAsync(Request(student.Id, original));

        var content = await _contentService.GetAsync(student.Id, thumbnail: false);

        content.ShouldNotBeNull();
        content!.IsThumbnail.ShouldBeFalse();
        content.Content.Length.ShouldBe(original.Length);

        var size = SizeOf(content.Content);

        size.Width.ShouldBe(1600);
        size.Height.ShouldBe(1200);
    }

    [Fact]
    public async Task 读取_缩略图缺失时回落原图()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var original = RealJpeg(1200, 900);
        await _uploadService.UploadOneAsync(Request(student.Id, original));

        // 模拟"老数据没有缩略图"（本功能上线之前上传的照片）
        var row = await _photos.FindAsync(p => p.StudentId == student.Id);
        row!.ThumbnailPath = null;
        await _photos.UpdateAsync(row, autoSave: true);

        var content = await _contentService.GetAsync(student.Id, thumbnail: true);

        content.ShouldNotBeNull();
        content!.IsThumbnail.ShouldBeFalse("拿不到缩略图就给原图，页面不该白屏");
        content.Content.Length.ShouldBe(original.Length);
    }

    [Fact]
    public async Task 读取_没有照片返回null()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var content = await _contentService.GetAsync(student.Id, thumbnail: true);

        content.ShouldBeNull("没照片就是没照片，控制器据此回 404");
    }

    [Fact]
    public async Task 读取_外校账号被拒绝()
    {
        RepairNas();

        var schoolA = await CreateSchoolAsync("缩略图甲校");
        var schoolB = await CreateSchoolAsync("缩略图乙校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "甲校缩略图学生");

        await _uploadService.UploadOneAsync(Request(studentInA.Id, RealJpeg(800, 600)));

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "缩略图乙校");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            InScopeAsAsync(outsider, "缩略图乙校", sp =>
                sp.GetRequiredService<IPhotoContentService>().GetAsync(studentInA.Id, thumbnail: true)));

        ex.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    // ==================================================================
    // 降级：缩略图也要跟着走
    // ==================================================================

    [Fact]
    public async Task 降级上传_缩略图暂存本地_补偿后回放到主存储()
    {
        RepairNas();
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        try
        {
            BreakNas();

            var degraded = await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg(1600, 1200)));

            degraded.Success.ShouldBeTrue();
            degraded.Degraded.ShouldBeTrue();

            var row = await _photos.FindAsync(p => p.StudentId == student.Id);

            row!.Status.ShouldBe(PhotoStatuses.FallbackPending);
            row.ThumbnailPath.ShouldNotBeNullOrWhiteSpace();
            File.Exists(FallbackPath(row.ThumbnailPath!)).ShouldBeTrue("NAS 熔断期间，缩略图也要随原图落到本地降级区");
        }
        finally
        {
            RepairNas();
        }

        var row2 = await _photos.FindAsync(p => p.StudentId == student.Id);
        var compensation = await _compensationService.CompensateAsync();

        compensation.Replayed.ShouldBeGreaterThanOrEqualTo(1);

        File.Exists(NasPath(row2!.RelativePath)).ShouldBeTrue();
        File.Exists(NasPath(row2.ThumbnailPath!)).ShouldBeTrue(
            "缩略图必须一起回放：只回放原图的话，NAS 上永远缺缩略图，列表页会退化成拉原图");
        File.Exists(FallbackPath(row2.ThumbnailPath!)).ShouldBeFalse("回放成功后要清掉本地副本");
    }
}
