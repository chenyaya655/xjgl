using Shouldly;
using Volo.Abp;
using Xjgl.Organizations;
using Xjgl.Photos;
using Xjgl.Photos.Dtos;
using Xjgl.Students;
using Xunit;

namespace Xjgl.Application.Tests.Photos;

/// <summary>
/// 照片管理（上传 / 秒传 / 熔断降级 / 补偿回放 / 台账 / 缺照片名单）的集成测试。
///
/// 这些测试刻意走**真实的文件系统 + 真实数据库**：
///   - 「NAS 主存储」指向测试输出目录下的一个本地目录；
///   - 需要模拟 NAS 故障时，把这个目录换成一个**同名文件** ——
///     于是目录创建必然失败，写入失败、熔断、降级全部真实发生；
///   - 「修复 NAS」只需把这个文件删掉。
///
/// 这样做的好处：不用搭 NAS、不用 mock、毫秒级完成，
/// 但覆盖的是与生产完全相同的代码路径（同一套 DI 装配、同一个熔断器）。
/// </summary>
public class PhotoAppServiceTests : XjglIntegrationTestBase
{
    private const string Year = "2050-2051";

    private readonly IPhotoUploadService _uploadService;
    private readonly IPhotoAppService _photoAppService;
    private readonly IPhotoCompensationService _compensationService;
    private readonly IStudentAppService _studentAppService;
    private readonly IOrgAppService _orgAppService;
    private readonly NasPhotoStorage _nasStorage;
    private readonly LocalFallbackPhotoStorage _fallbackStorage;

    public PhotoAppServiceTests()
    {
        _uploadService = GetRequiredService<IPhotoUploadService>();
        _photoAppService = GetRequiredService<IPhotoAppService>();
        _compensationService = GetRequiredService<IPhotoCompensationService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _nasStorage = GetRequiredService<NasPhotoStorage>();
        _fallbackStorage = GetRequiredService<LocalFallbackPhotoStorage>();
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    /// <summary>把「NAS」弄坏：删掉目录，在原地放一个同名文件，任何目录创建都会失败。</summary>
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

        File.WriteAllText(root, "NAS 故障演练：这里故意放一个文件占位。");
    }

    /// <summary>修好「NAS」。</summary>
    private void RepairNas()
    {
        var root = _nasStorage.RootPath;

        if (File.Exists(root))
        {
            File.Delete(root);
        }

        Directory.CreateDirectory(root);
    }

    private string PhysicalNasPath(string relativePath) =>
        Path.Combine(_nasStorage.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string PhysicalFallbackPath(string relativePath) =>
        Path.Combine(_fallbackStorage.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<OrgDto> CreateSchoolAsync()
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "41" + Random.Shared.Next(10000000, 99999999),
            Name = "照片测试学校"
        });
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, string name = "照片学生")
    {
        // 身份证号由基类按计数器生成（跨轮几乎不重复），并在此处自动重试兜底：
        // 测试库是长期累积的，撞上历史学生会让用例**偶发**变红（详见基类里的说明）
        return await RetryOnDuplicateIdCardAsync(() =>
            _studentAppService.CreateAsync(new CreateUpdateStudentDto
            {
                Name = name,
                GenderCode = "1",
                BirthDate = new DateTime(2015, 6, 1),
                IdCardTypeCode = "1",
                IdCardNumber = NewUniqueIdCard(),
                SchoolOrgId = schoolId,
                AcademicYear = Year,
                StudentStatusCode = StudentStatuses.Studying
            }));
    }

    private static byte[] JpegBytes(int size = 512, int seed = 0)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);

        // 给一个 JPEG 魔数，方便人工排查时能认出是图片
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;

        return bytes;
    }

    private static PhotoUploadRequest Request(Guid studentId, byte[] content, string fileName = "photo.jpg") =>
        new()
        {
            StudentId = studentId,
            FileName = fileName,
            Content = new MemoryStream(content),
            Length = content.Length
        };

    // ------------------------------------------------------------------
    // 正常上传
    // ------------------------------------------------------------------

    [Fact]
    public async Task 上传照片_写入主存储并登记台账()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var result = await _uploadService.UploadOneAsync(
            Request(student.Id, JpegBytes(seed: 1)));

        result.Success.ShouldBeTrue();
        result.Degraded.ShouldBeFalse();
        result.Skipped.ShouldBeFalse();
        result.ErrorMessage.ShouldBeNull();

        // 文件真的落到「NAS」上了
        File.Exists(PhysicalNasPath(result.RelativePath!)).ShouldBeTrue();

        // 台账登记正确
        var photo = await _photoAppService.GetAsync(result.PhotoId!.Value);
        photo.StudentId.ShouldBe(student.Id);
        photo.Status.ShouldBe(PhotoStatuses.Normal);
        photo.StatusText.ShouldBe("正常");
        photo.FileSize.ShouldBe(512);
        photo.FileHash.Length.ShouldBe(64);
        photo.BatchId.ShouldNotBe(Guid.Empty);

        // 学生主表的照片标记被同步维护（缺照片名单的判据）
        var reloaded = await _studentAppService.GetAsync(student.Id);
        reloaded.PhotoPath.ShouldBe(result.RelativePath);

        // 路径规则：photo/{学校组织Id}/{学生Id}.jpg
        result.RelativePath!.ShouldBe(PhotoPathRule.Build(school.Id, student.Id, "photo.jpg"));
    }

    [Fact]
    public async Task 重复上传相同内容_命中秒传不重复写存储()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);
        var content = JpegBytes(seed: 2);

        var first = await _uploadService.UploadOneAsync(Request(student.Id, content));
        var second = await _uploadService.UploadOneAsync(Request(student.Id, content));

        first.Skipped.ShouldBeFalse();
        second.Skipped.ShouldBeTrue();
        second.Success.ShouldBeTrue();
        second.PhotoId.ShouldBe(first.PhotoId);

        // 一名学生只有一条照片台账
        var list = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            StudentId = student.Id,
            MaxResultCount = 10
        });

        list.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task 重新上传不同内容_覆盖同一路径并更新哈希()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var first = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 3)));
        var firstHash = (await _photoAppService.GetAsync(first.PhotoId!.Value)).FileHash;

        var second = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 4)));

        second.Skipped.ShouldBeFalse();
        second.Success.ShouldBeTrue();
        second.RelativePath.ShouldBe(first.RelativePath);

        var photo = await _photoAppService.GetAsync(second.PhotoId!.Value);
        photo.FileHash.ShouldNotBe(firstHash);

        var list = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            StudentId = student.Id,
            MaxResultCount = 10
        });

        list.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task 不支持的格式_被拒绝且不落台账()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var result = await _uploadService.UploadOneAsync(
            Request(student.Id, JpegBytes(), "恶意脚本.txt"));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("不支持的图片格式");

        var list = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            StudentId = student.Id,
            MaxResultCount = 10
        });

        list.TotalCount.ShouldBe(0);

        var reloaded = await _studentAppService.GetAsync(student.Id);
        reloaded.PhotoPath.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task 上传不存在的学生_返回失败而不抛异常()
    {
        var result = await _uploadService.UploadOneAsync(Request(Guid.NewGuid(), JpegBytes()));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("学生不存在。");
    }

    [Fact]
    public async Task 批量上传_个别失败不影响其它照片()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        // 姓名校验会拒收数字（生僻字/姓名规则），因此用中文序号
        var names = new[] { "批量学生甲", "批量学生乙", "批量学生丙" };
        var students = new List<StudentDto>();

        for (var i = 0; i < names.Length; i++)
        {
            students.Add(await CreateStudentAsync(school.Id, names[i]));
        }

        var batchId = Guid.NewGuid();

        var result = await _uploadService.UploadBatchAsync(batchId, new[]
        {
            Request(students[0].Id, JpegBytes(seed: 10)),
            Request(students[1].Id, JpegBytes(seed: 11), "bad.gif"),
            Request(students[2].Id, JpegBytes(seed: 12))
        });

        result.BatchId.ShouldBe(batchId);
        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(2);
        result.Failed.ShouldBe(1);
        result.Message.ShouldContain("成功 2 张");

        // 成功的两张都能按批次查出来
        var byBatch = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            BatchId = batchId,
            MaxResultCount = 10
        });

        byBatch.TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task 超过单批上限_抛业务异常()
    {
        var requests = Enumerable.Range(0, PhotoUploadService.MaxBatchSize + 1)
            .Select(_ => Request(Guid.NewGuid(), JpegBytes(16)))
            .ToList();

        var exception = await Should.ThrowAsync<UserFriendlyException>(
            async () => await _uploadService.UploadBatchAsync(Guid.NewGuid(), requests));

        exception.Message.ShouldContain("分批上传");
    }

    // ------------------------------------------------------------------
    // 熔断降级
    // ------------------------------------------------------------------

    [Fact]
    public async Task NAS不可用_降级写入本地且台账标记待补偿()
    {
        try
        {
            BreakNas();

            var school = await CreateSchoolAsync();
            var student = await CreateStudentAsync(school.Id);

            var result = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 20)));

            // 关键：教师侧**感知不到失败**
            result.Success.ShouldBeTrue();
            result.Degraded.ShouldBeTrue();
            result.ErrorMessage.ShouldNotBeNullOrEmpty();

            // 文件落在降级区，没有进 NAS
            File.Exists(PhysicalFallbackPath(result.RelativePath!)).ShouldBeTrue();
            File.Exists(PhysicalNasPath(result.RelativePath!)).ShouldBeFalse();

            var photo = await _photoAppService.GetAsync(result.PhotoId!.Value);
            photo.Status.ShouldBe(PhotoStatuses.FallbackPending);
            photo.StatusText.ShouldBe("降级待补偿");

            // 有照片（虽然是降级的），所以不该出现在缺照片名单里
            var missing = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
            {
                SchoolOrgId = school.Id,
                MaxResultCount = 100
            });

            missing.Items.ShouldNotContain(x => x.StudentId == student.Id);
        }
        finally
        {
            RepairNas();
        }
    }

    [Fact]
    public async Task 存储健康度_NAS不可用时报告不可写()
    {
        try
        {
            BreakNas();

            var health = await _photoAppService.GetStorageHealthAsync();

            health.PrimaryAvailable.ShouldBeFalse();
            health.FallbackAvailable.ShouldBeTrue();
            health.PrimaryName.ShouldContain("NAS");
            health.FallbackName.ShouldContain("本地");
        }
        finally
        {
            RepairNas();
        }
    }

    [Fact]
    public async Task 存储健康度_NAS正常时报告可写()
    {
        RepairNas();

        var health = await _photoAppService.GetStorageHealthAsync();

        health.PrimaryAvailable.ShouldBeTrue();
        health.FallbackAvailable.ShouldBeTrue();
        health.CircuitStateText.ShouldNotBeNullOrEmpty();
    }

    // ------------------------------------------------------------------
    // 补偿回放
    // ------------------------------------------------------------------

    [Fact]
    public async Task 补偿任务_NAS恢复后把降级照片搬回主存储()
    {
        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);
        Guid photoId;
        string relativePath;

        try
        {
            BreakNas();

            var degraded = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 30)));
            degraded.Degraded.ShouldBeTrue();

            photoId = degraded.PhotoId!.Value;
            relativePath = degraded.RelativePath!;

            File.Exists(PhysicalFallbackPath(relativePath)).ShouldBeTrue();
        }
        finally
        {
            RepairNas();
        }

        // NAS 恢复后执行补偿
        var result = await _compensationService.CompensateAsync();

        result.Aborted.ShouldBeFalse();
        result.Replayed.ShouldBeGreaterThanOrEqualTo(1);
        result.Failed.ShouldBe(0);

        // 照片回到 NAS，本地副本清掉，台账转正常
        File.Exists(PhysicalNasPath(relativePath)).ShouldBeTrue();
        File.Exists(PhysicalFallbackPath(relativePath)).ShouldBeFalse();

        var photo = await _photoAppService.GetAsync(photoId);
        photo.Status.ShouldBe(PhotoStatuses.Normal);
        photo.ErrorMessage.ShouldBeNull();

        // 再补一次：已经没事可做（幂等）
        var second = await _compensationService.CompensateAsync();
        second.Replayed.ShouldBe(0);
    }

    [Fact]
    public async Task 补偿任务_NAS仍未恢复时直接跳过()
    {
        try
        {
            BreakNas();

            var result = await _compensationService.CompensateAsync();

            result.Aborted.ShouldBeTrue();
            result.Replayed.ShouldBe(0);
            result.Message.ShouldContain("不可写");
        }
        finally
        {
            RepairNas();
        }
    }

    // ------------------------------------------------------------------
    // 缺照片名单与删除
    // ------------------------------------------------------------------

    [Fact]
    public async Task 缺照片名单_列出未上传的学生并在上传后移除()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var withPhoto = await CreateStudentAsync(school.Id, "有照片学生");
        var withoutPhoto = await CreateStudentAsync(school.Id, "无照片学生");

        var before = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
        {
            SchoolOrgId = school.Id,
            MaxResultCount = 100
        });

        before.Items.ShouldContain(x => x.StudentId == withoutPhoto.Id);
        before.Items.ShouldContain(x => x.StudentId == withPhoto.Id);
        before.Items.First(x => x.StudentId == withoutPhoto.Id).StudentNo.ShouldBe(withoutPhoto.StudentNo);
        before.Items.First(x => x.StudentId == withoutPhoto.Id).SchoolName.ShouldBe(school.Name);

        await _uploadService.UploadOneAsync(Request(withPhoto.Id, JpegBytes(seed: 40)));

        var after = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
        {
            SchoolOrgId = school.Id,
            MaxResultCount = 100
        });

        after.Items.ShouldNotContain(x => x.StudentId == withPhoto.Id);
        after.Items.ShouldContain(x => x.StudentId == withoutPhoto.Id);

        // 分页总数必须是**过滤后**的总数（先分页再过滤会把总数算错）
        after.TotalCount.ShouldBe(after.Items.Count);
    }

    [Fact]
    public async Task 缺照片名单_支持关键字检索()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "关键字检索专用学生");

        var result = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
        {
            SchoolOrgId = school.Id,
            Keyword = "关键字检索专用",
            MaxResultCount = 100
        });

        result.Items.ShouldContain(x => x.StudentId == student.Id);
    }

    [Fact]
    public async Task 删除照片_清理存储与台账并重新回到缺照片名单()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id);

        var uploaded = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 50)));
        File.Exists(PhysicalNasPath(uploaded.RelativePath!)).ShouldBeTrue();

        await _photoAppService.DeleteAsync(uploaded.PhotoId!.Value);

        // 文件、台账、学生标记三处都要干净
        File.Exists(PhysicalNasPath(uploaded.RelativePath!)).ShouldBeFalse();

        var list = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            StudentId = student.Id,
            MaxResultCount = 10
        });
        list.TotalCount.ShouldBe(0);

        var reloaded = await _studentAppService.GetAsync(student.Id);
        reloaded.PhotoPath.ShouldBeNullOrEmpty();

        var missing = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
        {
            SchoolOrgId = school.Id,
            MaxResultCount = 100
        });
        missing.Items.ShouldContain(x => x.StudentId == student.Id);

        // 删除后可以重新上传，不会因为历史台账而命中秒传
        var again = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 50)));
        again.Success.ShouldBeTrue();
        again.Skipped.ShouldBeFalse();
    }

    [Fact]
    public async Task 批量删除照片_返回实际删除条数()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var a = await CreateStudentAsync(school.Id, "批删甲");
        var b = await CreateStudentAsync(school.Id, "批删乙");

        var first = await _uploadService.UploadOneAsync(Request(a.Id, JpegBytes(seed: 60)));
        var second = await _uploadService.UploadOneAsync(Request(b.Id, JpegBytes(seed: 61)));

        var deleted = await _photoAppService.DeleteManyAsync(new List<Guid>
        {
            first.PhotoId!.Value,
            second.PhotoId!.Value,
            Guid.NewGuid()
        });

        deleted.ShouldBe(2);

        File.Exists(PhysicalNasPath(first.RelativePath!)).ShouldBeFalse();
        File.Exists(PhysicalNasPath(second.RelativePath!)).ShouldBeFalse();

        var missing = await _photoAppService.GetMissingPhotoListAsync(new MissingPhotoQueryDto
        {
            SchoolOrgId = school.Id,
            MaxResultCount = 100
        });

        missing.Items.ShouldContain(x => x.StudentId == a.Id);
        missing.Items.ShouldContain(x => x.StudentId == b.Id);
    }

    [Fact]
    public async Task 照片台账_支持按状态与关键字过滤()
    {
        RepairNas();

        var school = await CreateSchoolAsync();
        var student = await CreateStudentAsync(school.Id, "台账检索学生");

        var uploaded = await _uploadService.UploadOneAsync(Request(student.Id, JpegBytes(seed: 70)));

        var byStatus = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            Status = PhotoStatuses.Normal,
            Keyword = "台账检索学生",
            MaxResultCount = 50
        });

        byStatus.Items.ShouldContain(x => x.Id == uploaded.PhotoId);

        var byFailed = await _photoAppService.GetListAsync(new PhotoQueryDto
        {
            Status = PhotoStatuses.Failed,
            Keyword = "台账检索学生",
            MaxResultCount = 50
        });

        byFailed.Items.ShouldNotContain(x => x.Id == uploaded.PhotoId);
    }

    [Fact]
    public async Task 不带任何过滤条件查询列表_不应报SQL语法错误()
    {
        RepairNas();

        // 回归用例：仓储的 GetCountAsync 收到 null 谓词时曾拼出以 WHERE 结尾的
        // 残缺 SQL（PostgreSQL 报 42601: syntax error at end of input），
        // 表现是「查询全部」报 500、「带条件查询」一切正常 —— 之前所有测试都带了
        // 过滤条件，因此这个缺陷一路躲过了 138 个集成测试，最后是靠真实调用接口才暴露。
        var photos = await _photoAppService.GetListAsync(new PhotoQueryDto { MaxResultCount = 5 });
        photos.TotalCount.ShouldBeGreaterThanOrEqualTo(0);

        var missing = await _photoAppService.GetMissingPhotoListAsync(
            new MissingPhotoQueryDto { MaxResultCount = 5 });
        missing.TotalCount.ShouldBeGreaterThanOrEqualTo(0);

        var students = await _studentAppService.GetListAsync(new StudentQueryDto { MaxResultCount = 5 });
        students.TotalCount.ShouldBeGreaterThanOrEqualTo(0);
    }
}
