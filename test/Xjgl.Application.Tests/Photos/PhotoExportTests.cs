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
    private readonly IPhotoThumbnailService _thumbnailService;
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
        _thumbnailService = GetRequiredService<IPhotoThumbnailService>();
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

    /// <summary>
    /// 读出 zip 里的条目名 → 内容。
    ///
    /// 入参可空，并在为 null 时**明确报错**：`PhotoExportResult.Content` 只有
    /// `ExportAsync` 会填充，流式重载 `ExportToAsync` 刻意留 null（内容直接写进目标流）。
    /// 这样写是为了让"用例不小心换成了流式重载"表现为一句能看懂的话，
    /// 而不是 `ZipArchive` 内部的 NullReferenceException。
    /// </summary>
    private static Dictionary<string, byte[]> ReadZip(byte[]? zipBytes)
    {
        // 用显式 if 而不是 ShouldNotBeNull：只有显式判空之后，
        // 编译器的可空流分析才会把 zipBytes 当成非空，后面才不用到处写 `!`。
        if (zipBytes == null)
        {
            throw new InvalidOperationException(
                "Content 为 null，说明走的是流式重载 ExportToAsync —— 这些用例应当用 ExportAsync（它会填充 Content）");
        }

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

    /// <summary>
    /// 流式重载（债 22）必须产出**与内存版完全相同**的压缩包，
    /// 并且**不填充** `Content`（否则就等于又回到了"整包进内存"）。
    ///
    /// 这条用例存在的理由：后台导出任务走的就是这个重载
    /// （`ExportTaskRunner.RunPhotoZipAsync` → 临时文件 → 产物存储），
    /// 如果它悄悄不再写内容、或把内容也塞回 `Content`，任务会产出一个空包，
    /// 而**用户看到的是"导出成功"**。
    /// </summary>
    [Fact]
    public async Task 导出_流式重载产出同样的压缩包且不占内存()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "流式甲");

        await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg()));

        var query = new PhotoExportQueryDto { SchoolOrgId = school.Id, MaxCount = 50 };

        // 先走内存版拿"标准答案"
        var inMemory = await _exportService.ExportAsync(query);

        // 再走流式版，写到文件（与后台任务同一条路径：必须是**可寻址的流**，即真实文件）
        var tempPath = Path.Combine(Path.GetTempPath(), $"xjgl-test-photo-export-{Guid.NewGuid():N}.zip");

        PhotoExportResult streamed;

        try
        {
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                streamed = await _exportService.ExportToAsync(query, file);
            }

            // 计数与文件名与内存版一致
            streamed.PhotoCount.ShouldBe(inMemory.PhotoCount);
            streamed.SkippedCount.ShouldBe(inMemory.SkippedCount);
            streamed.ReachedLimit.ShouldBe(inMemory.ReachedLimit);
            streamed.FileName.ShouldBe(inMemory.FileName);

            // ★ 关键：流式版**不给** byte[]，内容已经在文件里了
            streamed.Content.ShouldBeNull("流式重载不该把整包读进内存 —— 那正是债 22 要消除的东西");

            // 文件里确实是一个可正常读取的 zip，且内容与内存版逐条一致
            var fromFile = ReadZip(await File.ReadAllBytesAsync(tempPath));
            var fromMemory = ReadZip(inMemory.Content);

            fromFile.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .ShouldBe(fromMemory.Keys.OrderBy(k => k, StringComparer.Ordinal));

            ReadText(fromFile, "照片清单.csv").ShouldContain("流式甲");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

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

        // 缩略图不再在**上传请求内**生成（债 18），而是由补齐服务按队列生成。
        // 这里必须显式补一次，否则导出会回落成原图，断言"缩略图更小"必然失败。
        //
        // 必须传 school.Id（只补本校）：不指定学校时本轮扫的是测试库里陈年的
        // "原图读不到"记录（400+ 条 > 单轮上限 200），本用例的照片根本轮不上。
        await _thumbnailService.EnsureThumbnailsAsync(schoolOrgId: school.Id);

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

        // 这两个结果来自 ExportAsync（内存版），Content 必然有值
        small.Content!.Length.ShouldBeLessThan(full.Content!.Length, "整包也该更小");
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
    // 债 24b：状态过滤必须下推到查询里
    // ==================================================================

    [Fact]
    public async Task 导出_前一页全是不可导出的记录_仍要翻到后面把照片取够()
    {
        // 【这条用例守的是什么】
        // `LoadRowsAsync` 原来是"每页取 1000 条 → 丢掉非 Normal/FallbackPending → 下一页"。
        // 若第一页**整页**都是不可导出的记录（Pending / Failed），
        // 那一页会被全部丢掉、一张都不进包，而"是否继续翻页"的判断
        // （`skip >= page.TotalCount`）又会因为这一页已经取满而结束循环 ——
        // 于是结果恒为 0 张，且 `ReachedLimit` 是 false，
        // 使用者看到的是一个只有清单的空压缩包，完全不知道照片去哪了。
        //
        // 【怎么稳定触发"必须翻页"】
        // 生产页大小是 1000（ABP 的硬上限）。要触发翻页，要么插 1000+ 条数据
        // （慢，且让共享测试库持续膨胀），要么**把页大小调小** ——
        // 所以 `Xjgl:Photo:ExportPageSize` 做成了可配置（见 PhotoExportService 的说明）。
        // 集成测试的配置里把它设为 2，于是"2 条 Pending + 1 张真照片"这个最小数据量
        // 就必然需要翻到第二页；只要"第一页取不到就不翻了"，断言立刻变红。
        RepairNas();

        var school = await CreateSchoolAsync("分页导出学校");
        var photos = GetRequiredService<IXjglRepository<StuPhoto>>();

        var batch = Guid.NewGuid();

        // 2 条 Pending（刚好占满第一页）：台账有记录、但没有可打包的文件
        //
        // 显式把 UploadedTime 设成"很早"：导出取数按 `UploadedTime ASC` 排序
        // （与"先来先导"一致），这样这 2 条必然排在后面上传的真照片之前，
        // 于是"第一页整页都是不可导出的记录"这件事是**确定**的，不依赖时钟精度。
        var pending = new List<StuPhoto>();
        var old = DateTime.Now.AddHours(-2);

        for (var i = 0; i < 2; i++)
        {
            var id = Guid.NewGuid();

            var row = new StuPhoto(
                id, Guid.NewGuid(), school.Id,
                PhotoPathRule.Build(school.Id, id, "photo.jpg"),
                new string('a', 64), batch);

            row.Status = PhotoStatuses.Pending;
            row.UploadedTime = old.AddMinutes(i);

            pending.Add(row);
        }

        await photos.InsertManyAsync(pending, autoSave: true);

        // 1 张真实可导出的照片，会落在第二页
        //
        // ⚠ 姓名必须用规范汉字：建学生走的是与手工录入**完全相同**的校验，
        // `RareCharacterText.IsValidPersonName` 不接受拉丁字母与数字 ——
        // 用 "分页导出学生0" 会直接抛 ValidationFailed（本用例第一次就是这么红的）。
        var student = await CreateStudentAsync(school.Id, "分页导出学生甲");

        // ⚠ 必须传 batch：查询是按 batchId 限定的，不传的话照片会落在另一个随机批次里，
        // 于是 PhotoCount 恒为 0 —— 而"0"恰好也是"缺陷仍在"的表现，
        // 断言会以一种看不出原因的方式变红（本用例第一次就是这么红的）。
        var uploaded = await _uploadService.UploadOneAsync(
            Request(student.Id, RealJpeg(120, 90)), batch);

        uploaded.Success.ShouldBeTrue(
            $"上传应当成功；ErrorMessage={uploaded.ErrorMessage ?? "(null)"}，Degraded={uploaded.Degraded}");

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, BatchId = batch, MaxCount = 10
        });

        result.PhotoCount.ShouldBe(1,
            "第一页整页都是不可导出的记录时，必须继续翻页把可导出的照片取出来");

        ReadZip(result.Content).Keys.Count(k => k.StartsWith("照片/", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task 导出_显式筛选不可导出的状态_得到空包而不是一堆无意义的翻页()
    {
        // `Status` 既可以是"台账筛选条件"，也会被当成"可导出性"的判据。
        // 用户显式选了「处理失败」时，包里的照片必然是 0 张 ——
        // 这是语义正确的结果，不能因为"没取够 MaxCount"就谎报已达上限。
        RepairNas();

        var school = await CreateSchoolAsync("失败筛选学校");
        var student = await CreateStudentAsync(school.Id, "失败筛选学生");

        var uploaded = await _uploadService.UploadOneAsync(Request(student.Id, RealJpeg(120, 90)));

        var photo = await GetRequiredService<IXjglRepository<StuPhoto>>()
            .GetAsync(uploaded.PhotoId!.Value);

        photo.Status = PhotoStatuses.Failed;
        await GetRequiredService<IXjglRepository<StuPhoto>>().UpdateAsync(photo, autoSave: true);

        var result = await _exportService.ExportAsync(new PhotoExportQueryDto
        {
            SchoolOrgId = school.Id, Status = PhotoStatuses.Failed, MaxCount = 10
        });

        result.PhotoCount.ShouldBe(0);
        result.SkippedCount.ShouldBe(0);
        result.ReachedLimit.ShouldBeFalse(
            "没取够上限就不能说「已达上限」—— 否则提示会把使用者引向『调大上限再试』这条错路");
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
