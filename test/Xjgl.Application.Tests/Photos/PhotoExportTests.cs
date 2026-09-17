using System.IO.Compression;
using System.Text;
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
/// 照片打包导出的集成测试（真实图片 + 真实存储 + 真实数据库）。
///
/// 【守三件事】
///   1. **压缩包里到底有什么**：照片文件（名字能对上花名册）+ 清单 CSV；
///   2. **读不到的照片必须出现在清单里**："台账有记录、存储里没文件"时静默少一张，
///      教师会以为照片都齐了 —— 这是最不能接受的结果；
///   3. **导出范围受数据范围约束**：与台账一致，不能"界面看不到、导出却拿得到"。
/// </summary>
public class PhotoExportTests : XjglIntegrationTestBase
{
    private const string Year = "2072-2073";

    private readonly IPhotoUploadService _uploadService;
    private readonly IPhotoExportService _exportService;
    private readonly IStudentAppService _studentAppService;
    private readonly IOrgAppService _orgAppService;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly NasPhotoStorage _nasStorage;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public PhotoExportTests()
    {
        _uploadService = GetRequiredService<IPhotoUploadService>();
        _exportService = GetRequiredService<IPhotoExportService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _nasStorage = GetRequiredService<NasPhotoStorage>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    private static byte[] RealJpeg(int width = 800, int height = 600)
    {
        using var image = new Image<Rgba32>(width, height);

        image.Mutate(x => x.BackgroundColor(Color.BlanchedAlmond));

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);

        return buffer.ToArray();
    }

    private static PhotoUploadRequest Request(Guid studentId, byte[] content, string fileName = "photo.jpg") =>
        new()
        {
            StudentId = studentId,
            FileName = fileName,
            Content = new MemoryStream(content),
            Length = content.Length
        };

    private async Task<OrgDto> CreateSchoolAsync(string name = "导出测试学校")
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
                // 学校标识码撞号，换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的学校标识码。");
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name)
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

    private async Task<T> InScopeAsAsync<T>(Guid userId, string userName, Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, Array.Empty<string>());

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    /// <summary>读出 zip 里的条目名 → 内容。</summary>
    private static Dictionary<string, byte[]> ReadZip(byte[] zipBytes)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();

            stream.CopyTo(buffer);
            result[entry.FullName] = buffer.ToArray();
        }

        return result;
    }

    private static string ReadText(Dictionary<string, byte[]> zip, string name)
    {
        zip.ContainsKey(name).ShouldBeTrue($"压缩包里应当有 {name}，实际有：{string.Join("、", zip.Keys)}");

        // 去掉 UTF-8 BOM，方便断言
        return Encoding.UTF8.GetString(zip[name]).TrimStart('\uFEFF');
    }

    // ==================================================================
    // 基本导出
    // ==================================================================

    [Fact]
    public async Task 导出_压缩包内含照片与清单()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var a = await CreateStudentAsync(school.Id, "导出甲");
        var b = await CreateStudentAsync(school.Id, "导出乙");

        await _uploadService.UploadOneAsync(Request(a.Id, RealJpeg()));
        await _uploadService.UploadOneAsync(Request(b.Id, RealJpeg(400, 300)));

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 50
        });

        result.PhotoCount.ShouldBe(2);
        result.SkippedCount.ShouldBe(0);
        result.FileName.ShouldEndWith(".zip");

        var zip = ReadZip(result.Content);

        var photos = zip.Keys.Where(k => k.StartsWith("照片/", StringComparison.Ordinal)).ToList();

        photos.Count.ShouldBe(2);
        photos.ShouldContain(n => n.Contains(a.StudentNo) && n.Contains("导出甲"), "文件名要能对上花名册");
        photos.ShouldContain(n => n.Contains(b.StudentNo) && n.Contains("导出乙"));

        // 照片本身是完整 JPEG（不是空文件）
        zip[photos.First(n => n.Contains("导出甲"))][0].ShouldBe((byte)0xFF);

        var manifest = ReadText(zip, "照片清单.csv");

        manifest.ShouldContain("学籍辅号");
        manifest.ShouldContain("导出甲");
        manifest.ShouldContain("导出乙");
    }

    [Fact]
    public async Task 导出_存储里文件缺失时写进清单而不是少一张()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "缺失照片学生");

        var upload = await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg()));

        // 模拟运维事故：NAS 上的文件被清掉了，但台账里还有记录
        File.Delete(NasPath(upload.RelativePath!));

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 50
        });

        result.PhotoCount.ShouldBe(0);
        result.SkippedCount.ShouldBe(1, "读不到的照片必须计数，不能悄悄跳过");

        var manifest = ReadText(ReadZip(result.Content), "照片清单.csv");

        manifest.ShouldContain("缺失照片学生");
        manifest.Contains("文件缺失").ShouldBeTrue("清单里必须明确标出「台账有记录但存储里读不到」");
    }

    [Fact]
    public async Task 导出_可附缺照片名单()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var withPhoto = await CreateStudentAsync(school.Id, "有照片学生");
        var withoutPhoto = await CreateStudentAsync(school.Id, "没照片学生");

        await _uploadService.UploadOneAsync(Request(withPhoto.Id, RealJpeg()));

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 50, IncludeMissingList = true
        });

        var zip = ReadZip(result.Content);

        zip.Keys.Any(k => k.Contains("照片清单")).ShouldBeTrue();
        zip.Keys.Any(k => k.Contains("缺照片名单")).ShouldBeTrue();

        var missing = ReadText(zip, "缺照片名单.csv");

        missing.Contains("没照片学生").ShouldBeTrue("缺照片名单是用来催收的，必须包含没照片的人");
        missing.Contains("有照片学生").ShouldBeFalse("已经采集过的不该出现在催收名单里");
        missing.Contains("在读").ShouldBeTrue("学籍状态要翻译成中文名，名单是给人看的（不能是 01）");

        // 关掉开关就不生成
        var withoutList = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 50, IncludeMissingList = false
        });

        ReadZip(withoutList.Content).Keys.Any(k => k.Contains("缺照片名单")).ShouldBeFalse();
    }

    [Fact]
    public async Task 导出_可选择只导出缩略图()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "缩略图导出学生");

        var original = RealJpeg(1600, 1200);
        await _uploadService.UploadOneAsync(Request(student.Id, original));

        var full = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 10, UseThumbnail = false
        });

        var small = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, MaxCount = 10, UseThumbnail = true
        });

        var fullPhoto = ReadZip(full.Content).First(kv => kv.Key.StartsWith("照片/", StringComparison.Ordinal)).Value;
        var smallPhoto = ReadZip(small.Content).First(kv => kv.Key.StartsWith("照片/", StringComparison.Ordinal)).Value;

        fullPhoto.Length.ShouldBe(original.Length, "导出原图时应当是原样字节");

        using var image = Image.Load(smallPhoto);

        Math.Max(image.Width, image.Height).ShouldBeLessThanOrEqualTo(480, "缩略图导出的长边不超过 480");
        small.Content.Length.ShouldBeLessThan(full.Content.Length, "整包也该更小");
    }

    [Fact]
    public async Task 导出_按批次过滤()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var a = await CreateStudentAsync(school.Id, "批次甲");
        var b = await CreateStudentAsync(school.Id, "批次乙");

        var batch1 = Guid.NewGuid();
        var batch2 = Guid.NewGuid();

        await _uploadService.UploadOneAsync(Request(a.Id, RealJpeg()), batch1);
        await _uploadService.UploadOneAsync(Request(b.Id, RealJpeg(300, 200)), batch2);

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, BatchId = batch1, MaxCount = 50
        });

        result.PhotoCount.ShouldBe(1, "按批次导出应当只拿到这个批次上传的照片");
        ReadText(ReadZip(result.Content), "照片清单.csv").ShouldContain("批次甲");
    }

    // ==================================================================
    // 数据范围
    // ==================================================================

    [Fact]
    public async Task 导出_外校账号拿不到别人的照片()
    {
        RepairNas();

        var schoolA = await CreateSchoolAsync("导出甲校");
        var schoolB = await CreateSchoolAsync("导出乙校");
        var studentInA = await CreateStudentAsync(schoolA.Id, "甲校导出学生");

        await _uploadService.UploadOneAsync(Request(studentInA.Id, RealJpeg()));

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "导出乙校");

        // 不指定学校时按数据范围导出：范围外的照片一张都不该出现
        var result = await InScopeAsAsync(outsider, "导出乙校", sp =>
            sp.GetRequiredService<IPhotoExportService>().ExportAsync(new PhotoExportQueryDto { MaxCount = 100 }));

        result.PhotoCount.ShouldBe(0, "导出必须与台账同口径，否则就是越权拿数据");
        ReadZip(result.Content).Keys.Any(k => k.StartsWith("照片/", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task 导出_指定范围外学校时被拒绝()
    {
        RepairNas();

        var schoolA = await CreateSchoolAsync("导出丙校");
        var schoolB = await CreateSchoolAsync("导出丁校");
        await CreateStudentAsync(schoolA.Id, "丙校学生");

        var outsider = await LinkUserToOrgAsync(schoolB.Id, "导出丁校");

        // 明确指定了范围外的学校：与照片台账一致 —— 返回空（列表查询的口径），而不是报错
        var result = await InScopeAsAsync(outsider, "导出丁校", sp =>
            sp.GetRequiredService<IPhotoExportService>().ExportAsync(new PhotoExportQueryDto
            {
                SchoolOrgId = schoolA.Id, MaxCount = 100
            }));

        result.PhotoCount.ShouldBe(0);
    }
}
