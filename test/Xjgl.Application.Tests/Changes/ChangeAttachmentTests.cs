using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Timing;
using Volo.Abp.Uow;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Permissions;
using Xjgl.Students;
using Xjgl.Users;
using Xunit;

namespace Xjgl.Changes;

/// <summary>
/// 异动附件的端到端集成测试（ROADMAP 第 18 条）。
///
/// 这组用例守四件事：
///   1) **字节一致**：上传什么，下载就得到什么（顺带证明原始文件名与内容类型没丢）；
///   2) **收得严**：超限、扩展名不在白名单、伪装成图片/PDF 的非该格式文件都要被拒；
///   3) **跨组织读不到**：附件随申请单受数据范围约束（用 <see cref="FakeCurrentPrincipalAccessor"/>
///      换身份 + 新开 DI 作用域，与 DataScopeTests 同一套路）；
///   4) **必传材料是真的**：`RequireAttachment = true` 的类型，没有附件就批不了，
///      有附件才能过——这条正是"勾了等于没勾"那个字段的验收标准。
///
/// 另外守两条清理路径：删除附件、删除申请单都必须把**文件**一起清掉
/// （只软删记录会在共享卷上留下永远没人认领的孤儿文件）。
/// </summary>
public class ChangeAttachmentTests : XjglIntegrationTestBase
{
    private const string Year = "2068-2069";

    private readonly IChangeAttachmentAppService _attachments;
    private readonly IChangeAttachmentStore _store;
    private readonly IChangeFlowAppService _flow;
    private readonly IChangeTypeAppService _changeType;
    private readonly IOrgAppService _orgAppService;
    private readonly IStudentAppService _studentAppService;
    private readonly IXjglRepository<StuChangeAttachment> _attachmentRows;
    private readonly IXjglRepository<SysUserPredefine> _predefines;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IClock _clock;
    private readonly FakeCurrentPrincipalAccessor _principal;
    private readonly IPermissionManager _permissionManager;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public ChangeAttachmentTests()
    {
        _attachments = GetRequiredService<IChangeAttachmentAppService>();
        _store = GetRequiredService<IChangeAttachmentStore>();
        _flow = GetRequiredService<IChangeFlowAppService>();
        _changeType = GetRequiredService<IChangeTypeAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _attachmentRows = GetRequiredService<IXjglRepository<StuChangeAttachment>>();
        _predefines = GetRequiredService<IXjglRepository<SysUserPredefine>>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _clock = GetRequiredService<IClock>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
        _permissionManager = GetRequiredService<IPermissionManager>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    // ==================================================================
    // 脚手架
    // ==================================================================

    /// <summary>以超级管理员身份做数据准备（本类多数用例只关心业务行为）。</summary>
    private void SignInAsAdmin()
        => _principal.SetUser(Guid.NewGuid(), "change_attachment_tester", "admin");

    /// <summary>
    /// 以指定用户身份执行一段操作：**换 principal 并新开 DI 作用域**。
    /// 新作用域才会拿到新的 ICurrentUser（机制见 DataScopeTests 的类注释）。
    /// </summary>
    private async Task<T> InScopeAsAsync<T>(
        Guid userId,
        string userName,
        Func<IServiceProvider, Task<T>> action)
    {
        _principal.SetUser(userId, userName, Array.Empty<string>());

        using var scope = ServiceProvider.CreateScope();

        return await action(scope.ServiceProvider);
    }

    /// <summary>插入预置记录并把用户关联到组织（数据范围的唯一来源）。</summary>
    private async Task<Guid> LinkUserToOrgAsync(Guid orgId, string prefix)
    {
        var userId = _guidGenerator.Create();

        var predefine = new SysUserPredefine(
            _guidGenerator.Create(),
            $"{prefix}用户",
            "137" + Random.Shared.Next(10000000, 99999999),
            orgId,
            "TESTCODE",
            _clock.Now.AddDays(30));

        predefine.MarkActivated(userId, _clock.Now);
        await _predefines.InsertAsync(predefine, autoSave: true);

        // ★ 这个操作者必须**真的有权看单据**，不能只靠测试基类的放行垫片。
        //
        // 测试基类只替换 `IMethodInvocationAuthorizationService`，于是应用服务上的
        // `[Authorize(...)]` 一律放行；但 `ChangeApplyAccessGuard.EnsureReadPermissionAsync`
        // 用的是**真实**的 `IPermissionChecker`（基类刻意不替换它，否则"按权限过滤菜单"
        // 这类逻辑就完全测不出来）。因此只关联组织、不授权限的用户会在
        // "读单据"这一步先被权限挡住 —— 那不是本用例要验的数据范围。
        //
        // 两个权限都授上：读路径的语义是「申请查询 **或** 审核查询，任一即可」，
        // 而生产里的县区审核员这两条本来都会有。
        await GrantAsync(userId, MetadataPermissions.Chg.Apply.Query);
        await GrantAsync(userId, MetadataPermissions.Chg.Audit.Query);

        return userId;
    }

    /// <summary>
    /// 给某个**用户**授一条权限（测试里代替"他本来就有这个权限"）。
    ///
    /// **必须显式提交**：授权是写库，而后面每个身份都是**新作用域**（各自的工作单元），
    /// 不提交的话它们看不到这条授权 —— 报错会是"你没有权限"，
    /// 看起来像子集约束写错了，实际是测试自己的事务边界问题。
    /// </summary>
    private async Task GrantAsync(Guid userId, string permissionName)
    {
        var uow = _unitOfWorkManager.Begin(requiresNew: true);

        try
        {
            await _permissionManager.SetAsync(
                permissionName,
                UserPermissionValueProvider.ProviderName,
                userId.ToString(),
                true);

            await uow.CompleteAsync();
        }
        finally
        {
            uow.Dispose();
        }
    }

    /// <summary>省 → 市 → 甲县（两所学校）/ 乙县（一所学校）。</summary>
    private async Task<(
        OrgDto CountyA, OrgDto SchoolA1, OrgDto SchoolA2, OrgDto CountyB, OrgDto SchoolB1)>
        BuildOrgTreeAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await BuildOrgTreeOnceAsync();
            }
            catch (BusinessException)
            {
                // 区划码撞号（测试库会累积数据），换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的组织编码。");
    }

    private async Task<(OrgDto CountyA, OrgDto SchoolA1, OrgDto SchoolA2, OrgDto CountyB, OrgDto SchoolB1)>
        BuildOrgTreeOnceAsync()
    {
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province, Code = NewDivisionCode(), Name = "附件测试省厅"
        });

        var city = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = province.Id, OrgType = OrgTypes.City,
            Code = NewDivisionCode(), Name = "附件测试市局"
        });

        var countyA = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = city.Id, OrgType = OrgTypes.County,
            Code = NewDivisionCode(), Name = "附件测试甲县"
        });

        var countyB = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = city.Id, OrgType = OrgTypes.County,
            Code = NewDivisionCode(), Name = "附件测试乙县"
        });

        var schoolA1 = await CreateSchoolAsync(countyA.Id, "附件测试甲县一中");
        var schoolA2 = await CreateSchoolAsync(countyA.Id, "附件测试甲县二中");
        var schoolB1 = await CreateSchoolAsync(countyB.Id, "附件测试乙县一中");

        return (countyA, schoolA1, schoolA2, countyB, schoolB1);
    }

    private async Task<OrgDto> CreateSchoolAsync(Guid parentId, string name)
    {
        return await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            ParentId = parentId, OrgType = OrgTypes.School,
            Code = new string('4', 5) + Random.Shared.Next(10000, 99999), Name = name
        });
    }

    private static string NewDivisionCode()
        => "46" + Random.Shared.Next(1000, 9999) + Random.Shared.Next(10, 99);

    private Task<StudentDto> CreateStudentAsync(Guid schoolId)
    {
        return RetryOnDuplicateIdCardAsync(() => _studentAppService.CreateAsync(
            new CreateUpdateStudentDto
            {
                Name = "附件测试学生",
                GenderCode = "1",
                BirthDate = new DateTime(2015, 6, 1),
                IdCardTypeCode = "1",
                IdCardNumber = NewUniqueIdCard(),
                SchoolOrgId = schoolId,
                AcademicYear = Year
            }));
    }

    /// <summary>建一个两节点的异动类型（转入校 → 转入县区）并发布。</summary>
    private async Task<ChangeTypeDto> CreateTypeAsync(bool requireAttachment)
    {
        var type = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "ATTACH_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "附件测试转学",
            MaxApproveLevel = ApproveLevels.County,
            RequireAttachment = requireAttachment,
            AffectClass = true,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "附件测试流程",
            Nodes = new List<CreateUpdateFlowNodeDto>
            {
                new()
                {
                    Seq = 1, NodeName = "转入学校审核",
                    OrgResolver = ChangeOrgResolvers.TargetSchool, RoleCode = "SchoolAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast
                },
                new()
                {
                    Seq = 2, NodeName = "转入县区审核",
                    OrgResolver = ChangeOrgResolvers.TargetCounty, RoleCode = "CountyAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast
                }
            }
        });

        await _changeType.PublishTemplateAsync(template.Id);

        return type;
    }

    private Task<ChangeApplyDto> SubmitAsync(ChangeTypeDto type, Guid studentId, Guid targetSchoolId)
    {
        return _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = studentId,
            TargetSchoolOrgId = targetSchoolId,
            Reason = "家庭搬迁"
        });
    }

    /// <summary>把全部节点批完（测试里不关心是谁在审，只关心"能不能批"）。</summary>
    private async Task<ChangeApplyDto> ApproveAllAsync(Guid applyId, int nodeCount = 2)
    {
        ChangeApplyDto result = null!;

        for (var i = 0; i < nodeCount; i++)
        {
            result = await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = applyId, Opinion = "同意" });
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 测试用文件内容
    // ------------------------------------------------------------------

    /// <summary>
    /// 一份**真的能被解码**的 1×1 位图。
    ///
    /// 为什么用 BMP 而不是 PNG/JPG：BMP 没有压缩、没有校验和，可以手写出来。
    /// 硬编码一段 base64 图片虽然更短，但一旦哪一位抄错，失败信息会是
    /// "上传被拒"——看起来像**校验太严**（回归），实际是测试数据写错了，
    /// 排查方向完全被带偏。手写则每字节都有名字。
    /// </summary>
    private static byte[] NewBitmapBytes()
    {
        const int pixelDataSize = 4; // 1 个像素 3 字节 + 1 字节补齐（BMP 行按 4 字节对齐）
        var total = 14 + 40 + pixelDataSize;
        var bytes = new byte[total];

        void WriteInt(int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        void WriteShort(int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        // BITMAPFILEHEADER
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        WriteInt(2, total);      // 文件大小
        WriteInt(6, 0);          // 保留
        WriteInt(10, 54);        // 像素数据偏移

        // BITMAPINFOHEADER
        WriteInt(14, 40);        // 头大小
        WriteInt(18, 1);         // 宽
        WriteInt(22, 1);         // 高
        WriteShort(26, 1);       // 平面数
        WriteShort(28, 24);      // 位深
        WriteInt(30, 0);         // 压缩方式 BI_RGB
        WriteInt(34, pixelDataSize);
        WriteInt(38, 2835);      // 水平分辨率（72 DPI）
        WriteInt(42, 2835);      // 垂直分辨率
        WriteInt(46, 0);         // 调色板颜色数
        WriteInt(50, 0);         // 重要颜色数

        // 唯一一个像素：BGR = (0, 0, 255) → 纯红
        bytes[54] = 0;
        bytes[55] = 0;
        bytes[56] = 255;

        return bytes;
    }

    private static byte[] NewPdfBytes()
    {
        // 只要文件头是对的，附件功能就不再看内容 —— 它不做 PDF 解析（见实现里的说明）
        return Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");
    }

    private static byte[] NewTextBytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Stream AsStream(byte[] bytes) => new MemoryStream(bytes, writable: false);

    // ==================================================================
    // 上传 → 列表 → 下载
    // ==================================================================

    [Fact]
    public async Task 上传_列表_下载_字节一致且文件名与类型正确()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewBitmapBytes();

        var uploaded = await _attachments.UploadAsync(
            apply.Id, "户口簿.bmp", "application/octet-stream", AsStream(content), content.Length);

        // 客户端声明的 application/octet-stream **不采信**：类型由扩展名推导
        uploaded.ContentType.ShouldBe("image/bmp");
        uploaded.FileName.ShouldBe("户口簿.bmp");
        uploaded.FileSize.ShouldBe(content.Length);
        uploaded.ApplyId.ShouldBe(apply.Id);
        uploaded.UploadedByName.ShouldBe("change_attachment_tester");

        var list = await _attachments.GetListAsync(apply.Id);
        list.Count.ShouldBe(1);
        list.Single().Id.ShouldBe(uploaded.Id);

        var (stream, fileName, contentType) = await _attachments.DownloadAsync(uploaded.Id);

        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            buffer.ToArray().ShouldBe(content, "下载得到的字节必须与上传的完全一致");
        }

        // 非 ASCII 的原始文件名要原样回来（下载接口靠它生成 filename*）
        fileName.ShouldBe("户口簿.bmp");
        contentType.ShouldBe("image/bmp");
    }

    [Fact]
    public async Task 申请单详情_带出附件数量与要求满足情况()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: true);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var before = await _flow.GetApplyAsync(apply.Id);
        before.RequireAttachment.ShouldBeTrue();
        before.AttachmentCount.ShouldBe(0);
        before.AttachmentRequirementSatisfied.ShouldBeFalse("审核人要一眼看到材料还没齐");

        var content = NewPdfBytes();
        await _attachments.UploadAsync(apply.Id, "接收函.pdf", "application/pdf", AsStream(content), content.Length);

        var after = await _flow.GetApplyAsync(apply.Id);
        after.AttachmentCount.ShouldBe(1);
        after.AttachmentRequirementSatisfied.ShouldBeTrue();
    }

    // ==================================================================
    // 校验：大小 / 扩展名 / 伪装文件
    // ==================================================================

    [Fact]
    public async Task 超过10MB的附件被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        // 10MB + 1 字节，且带合法的 PDF 文件头 —— 证明被拒的原因是**大小**，不是格式
        var oversized = new byte[ChangeAttachmentConventions.MaxFileSizeBytes + 1];
        NewPdfBytes().CopyTo(oversized, 0);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.UploadAsync(
                apply.Id, "材料.pdf", "application/pdf", AsStream(oversized), oversized.Length));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentTooLarge);
        ex.Data["reason"].ShouldNotBeNull();
        ((string)ex.Data["reason"]!).ShouldContain("10 MB");

        (await _attachments.GetListAsync(apply.Id)).ShouldBeEmpty("被拒的附件不能留下记录");
    }

    [Fact]
    public async Task 扩展名不在白名单的附件被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewTextBytes("MZ...");

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.UploadAsync(
                apply.Id, "木马.exe", "application/octet-stream", AsStream(content), content.Length));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported);
        (await _attachments.GetListAsync(apply.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task 伪装成jpg的非图片文件被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        // 这正是照片模块踩过的坑（债 20）：改了后缀的文本一旦放行，
        // 存储里就躺着根本不是图片的文件，而所有下游都假设"有文件 = 有图可看"。
        var content = NewTextBytes("这不是图片，只是一段文字。");

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.UploadAsync(
                apply.Id, "照片.jpg", "image/jpeg", AsStream(content), content.Length));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported);
        ((string)ex.Data["reason"]!).ShouldContain("图片");

        (await _attachments.GetListAsync(apply.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task 伪装成pdf的文本文件被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewTextBytes("我没有 PDF 文件头。");

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.UploadAsync(
                apply.Id, "材料.pdf", "application/pdf", AsStream(content), content.Length));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentFormatNotSupported);
        ((string)ex.Data["reason"]!).ShouldContain("%PDF-");
    }

    [Fact]
    public async Task 空文件被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.UploadAsync(apply.Id, "空文件.pdf", "application/pdf", AsStream(Array.Empty<byte>()), 0));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ValidationFailed);
    }

    // ==================================================================
    // 数据范围：跨组织读不到
    // ==================================================================

    [Fact]
    public async Task 跨组织_附件列表与下载都读不到()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);

        var pdf = NewPdfBytes();

        // 甲县的单据（有附件）
        var studentA = await CreateStudentAsync(tree.SchoolA1.Id);
        var applyA = await SubmitAsync(type, studentA.Id, tree.SchoolA2.Id);
        var attachmentA = await _attachments.UploadAsync(
            applyA.Id, "甲县材料.pdf", "application/pdf", AsStream(pdf), pdf.Length);

        // 乙县的单据（有附件）
        var studentB = await CreateStudentAsync(tree.SchoolB1.Id);
        var applyB = await SubmitAsync(type, studentB.Id, tree.SchoolB1.Id);
        var attachmentB = await _attachments.UploadAsync(
            applyB.Id, "乙县材料.pdf", "application/pdf", AsStream(pdf), pdf.Length);

        var countyAUser = await LinkUserToOrgAsync(tree.CountyA.Id, "甲县");

        // 列表：本县看得到（否则下面的"看不到"可能只是因为什么都查不到），跨县读不到
        var inScope = await InScopeAsAsync(countyAUser, "甲县", sp =>
            sp.GetRequiredService<IChangeAttachmentAppService>().GetListAsync(applyA.Id));

        inScope.Single().Id.ShouldBe(attachmentA.Id);

        var listEx = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyAUser, "甲县", sp =>
                sp.GetRequiredService<IChangeAttachmentAppService>().GetListAsync(applyB.Id)));

        listEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);

        // 按 Id 直接下载：附件 Id 是可枚举的，只靠"知道 Id"不能放行
        var downloadEx = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyAUser, "甲县", sp =>
                sp.GetRequiredService<IChangeAttachmentAppService>().DownloadAsync(attachmentB.Id)));

        downloadEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);

        // 本县的在范围内照常下载得到
        var bytes = await InScopeAsAsync(countyAUser, "甲县", async sp =>
        {
            var (stream, _, _) = await sp.GetRequiredService<IChangeAttachmentAppService>()
                .DownloadAsync(attachmentA.Id);

            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                return buffer.ToArray();
            }
        });

        bytes.ShouldBe(pdf);
    }

    [Fact]
    public async Task 跨组织_上传与删除都被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);

        var pdf = NewPdfBytes();

        var studentB = await CreateStudentAsync(tree.SchoolB1.Id);
        var applyB = await SubmitAsync(type, studentB.Id, tree.SchoolB1.Id);
        var attachmentB = await _attachments.UploadAsync(
            applyB.Id, "乙县材料.pdf", "application/pdf", AsStream(pdf), pdf.Length);

        var countyAUser = await LinkUserToOrgAsync(tree.CountyA.Id, "甲县");

        var uploadEx = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyAUser, "甲县", sp =>
                sp.GetRequiredService<IChangeAttachmentAppService>().UploadAsync(
                    applyB.Id, "偷传.pdf", "application/pdf", AsStream(pdf), pdf.Length)));

        uploadEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);

        var deleteEx = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyAUser, "甲县", async sp =>
            {
                await sp.GetRequiredService<IChangeAttachmentAppService>().DeleteAsync(attachmentB.Id);
                return true;
            }));

        deleteEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);

        // 被拒之后乙县的材料必须原样还在
        (await _attachments.GetListAsync(applyB.Id)).Count.ShouldBe(1);
    }

    // ==================================================================
    // 数据范围：申请单本身的读路径（本轮顺带修掉的真实缺陷）
    // ==================================================================

    /// <summary>
    /// ★ 这条用例守的是一个**真实缺陷**（本轮才发现并修复）：
    /// `ChangeFlowAppService` 的所有读路径原来**完全没有数据范围校验** ——
    /// `GetAppliesAsync` 只按调用方传来的条件查（不传条件就是查全省），
    /// `GetApplyAsync` 直接按 Id 取，`GetTodoListAsync` 则**采信请求里的 OrgId**。
    /// 于是任何一个持有 Chg.Apply.Query 的用户都能读到全省任意一张异动申请单
    /// （学生姓名、学籍辅号、转出/转入学校、申请单号）——这正是本项目为学生 /
    /// 照片 / 用户预置 / 学籍留痕都修过的那一类越权，异动模块当时被漏掉了。
    /// </summary>
    [Fact]
    public async Task 跨组织_申请单列表与按Id读取都读不到()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);

        var studentA = await CreateStudentAsync(tree.SchoolA1.Id);
        var applyA = await SubmitAsync(type, studentA.Id, tree.SchoolA2.Id);

        var studentB = await CreateStudentAsync(tree.SchoolB1.Id);
        var applyB = await SubmitAsync(type, studentB.Id, tree.SchoolB1.Id);

        var countyAUser = await LinkUserToOrgAsync(tree.CountyA.Id, "甲县");

        // 不传任何业务条件（原来的代码在这里等于"查全省"）
        var list = await InScopeAsAsync(countyAUser, "甲县", sp =>
            sp.GetRequiredService<IChangeFlowAppService>()
                .GetAppliesAsync(new ChangeApplyQueryDto { MaxResultCount = 200 }));

        list.Items.ShouldContain(i => i.Id == applyA.Id, "本县的申请单必须看得到（否则下面的断言可能只是空集合导致的）");
        list.Items.ShouldNotContain(i => i.Id == applyB.Id, "跨县的申请单读不到");

        // 按 Id 直读：越权抛 Xjgl:00204（与学生模块同一语义：区分"看不到"与"不存在"）
        var readA = await InScopeAsAsync(countyAUser, "甲县", sp =>
            sp.GetRequiredService<IChangeFlowAppService>().GetApplyAsync(applyA.Id));

        readA.Id.ShouldBe(applyA.Id);

        var readEx = await Should.ThrowAsync<BusinessException>(async () =>
            await InScopeAsAsync(countyAUser, "甲县", sp =>
                sp.GetRequiredService<IChangeFlowAppService>().GetApplyAsync(applyB.Id)));

        readEx.Code.ShouldBe(XjglDomainErrorCodes.UserNotInDataScope);
    }

    /// <summary>
    /// 待办也一样：**请求里的 OrgId 不可信**。
    ///
    /// 这条特意把乙县的单据推到"当前待办就在乙县"的状态，否则"查不到"
    /// 有可能只是因为那个组织压根没有待办 —— 用例会变成永远绿的装饰。
    /// </summary>
    [Fact]
    public async Task 跨组织_待办列表传入范围外的组织Id得到空页()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);

        // 甲县的单据：批过第一节点，待办落在甲县
        var studentA = await CreateStudentAsync(tree.SchoolA1.Id);
        var applyA = await SubmitAsync(type, studentA.Id, tree.SchoolA2.Id);
        await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = applyA.Id, Opinion = "同意" });

        // 乙县的单据：同样批过第一节点，待办落在乙县
        var studentB = await CreateStudentAsync(tree.SchoolB1.Id);
        var applyB = await SubmitAsync(type, studentB.Id, tree.SchoolB1.Id);
        await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = applyB.Id, Opinion = "同意" });

        var countyAUser = await LinkUserToOrgAsync(tree.CountyA.Id, "甲县");

        // 前提：乙县确实有一条待办（用超管身份确认），否则下面的断言没有意义
        var adminTodo = await _flow.GetTodoListAsync(new ChangeTodoQueryDto
        {
            OrgId = tree.CountyB.Id, RoleCode = "CountyAdmin", MaxResultCount = 200
        });

        adminTodo.Items.ShouldContain(i => i.Id == applyB.Id, "测试前提：乙县此刻应有待办");

        // 甲县用户传乙县的组织 Id → 空页（不是"照着传的值去查"）
        var outOfScope = await InScopeAsAsync(countyAUser, "甲县", sp =>
            sp.GetRequiredService<IChangeFlowAppService>().GetTodoListAsync(new ChangeTodoQueryDto
            {
                OrgId = tree.CountyB.Id, RoleCode = "CountyAdmin", MaxResultCount = 200
            }));

        outOfScope.Items.ShouldBeEmpty();

        // 范围之内照常看得到
        var inScope = await InScopeAsAsync(countyAUser, "甲县", sp =>
            sp.GetRequiredService<IChangeFlowAppService>().GetTodoListAsync(new ChangeTodoQueryDto
            {
                OrgId = tree.CountyA.Id, RoleCode = "CountyAdmin", MaxResultCount = 200
            }));

        inScope.Items.ShouldContain(i => i.Id == applyA.Id);
    }

    // ==================================================================
    // RequireAttachment：批准前必须有附件
    // ==================================================================

    [Fact]
    public async Task 必传材料_没有附件时批准被拒()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: true);

        // 配置入口本身也要能读回来（这个字段上一轮被删过，就是因为"没有任何读取方"）
        (await _changeType.GetTypeAsync(type.Id)).RequireAttachment.ShouldBeTrue();

        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = apply.Id, Opinion = "同意" }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentRequired);

        // 原因必须说清缺什么（哪个异动类型要求材料），而不是一句"操作失败"
        ((string)ex.Data["reason"]!).ShouldContain("附件测试转学");

        // 被拒之后申请单必须原样停在原处（不能"报错了但状态已经改了"）
        var unchanged = await _flow.GetApplyAsync(apply.Id);

        unchanged.Status.ShouldBe(ChangeApplyStatus.Submitted);
        unchanged.CurrentSeq.ShouldBe(1);
        unchanged.Tasks.ShouldContain(t => t.Status == ChangeTaskStatus.Pending);
        unchanged.Tasks.ShouldNotContain(t => t.Status == ChangeTaskStatus.Approved);
    }

    [Fact]
    public async Task 必传材料_有附件后可以批准通过()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: true);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewPdfBytes();
        await _attachments.UploadAsync(apply.Id, "接收函.pdf", "application/pdf", AsStream(content), content.Length);

        var final = await ApproveAllAsync(apply.Id);

        final.Status.ShouldBe(ChangeApplyStatus.Approved);
        final.FinalApprovedTime.ShouldNotBeNull();
        final.AttachmentRequirementSatisfied.ShouldBeTrue();
    }

    [Fact]
    public async Task 不要求材料的类型_没有附件也能批准通过()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var final = await ApproveAllAsync(apply.Id);

        final.Status.ShouldBe(ChangeApplyStatus.Approved);
        final.RequireAttachment.ShouldBeFalse();
        final.AttachmentRequirementSatisfied.ShouldBeTrue("不需要附件时恒为已满足");
    }

    // ==================================================================
    // 删除：记录与文件一起清掉
    // ==================================================================

    [Fact]
    public async Task 删除附件_列表里没有了且文件也被删掉()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewPdfBytes();
        var uploaded = await _attachments.UploadAsync(
            apply.Id, "待删材料.pdf", "application/pdf", AsStream(content), content.Length);

        // 取出存储路径：DTO 刻意不带它（那是服务端的事），所以这里从库里读
        var row = await _attachmentRows.GetAsync(uploaded.Id);

        (await _store.OpenReadAsync(row.RelativePath)).ShouldNotBeNull("前提：文件确实写进去了");

        await _attachments.DeleteAsync(uploaded.Id);

        (await _attachments.GetListAsync(apply.Id)).ShouldBeEmpty();

        (await _store.OpenReadAsync(row.RelativePath))
            .ShouldBeNull("删附件必须连文件一起删，否则共享卷上会留下永远没人认领的孤儿文件");
    }

    [Fact]
    public async Task 删除申请单_级联删掉附件记录与文件()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewPdfBytes();
        var uploaded = await _attachments.UploadAsync(
            apply.Id, "随单材料.pdf", "application/pdf", AsStream(content), content.Length);

        var row = await _attachmentRows.GetAsync(uploaded.Id);

        // 只有草稿 / 已撤回 / 已驳回的单据能删
        await _flow.WithdrawAsync(apply.Id);

        await _flow.DeleteAsync(apply.Id);

        (await _attachmentRows.FindAsync(uploaded.Id))
            .ShouldBeNull("申请单删了还留着附件记录，就是台账里的孤儿行");

        (await _store.OpenReadAsync(row.RelativePath))
            .ShouldBeNull("附件文件也必须一起删掉");

        await Should.ThrowAsync<EntityNotFoundException>(async () => await _flow.GetApplyAsync(apply.Id));
    }

    [Fact]
    public async Task 存储文件丢失时下载报附件不存在()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewPdfBytes();
        var uploaded = await _attachments.UploadAsync(
            apply.Id, "会丢的材料.pdf", "application/pdf", AsStream(content), content.Length);

        var row = await _attachmentRows.GetAsync(uploaded.Id);

        // 模拟"共享卷没挂上 / 文件被人工清理"：记录还在，文件没了
        await _store.DeleteAsync(row.RelativePath);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _attachments.DownloadAsync(uploaded.Id));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeAttachmentNotFound);
    }

    [Fact]
    public async Task 下载不存在的附件_报实体不存在()
    {
        SignInAsAdmin();

        await Should.ThrowAsync<EntityNotFoundException>(async () =>
            await _attachments.DownloadAsync(Guid.NewGuid()));
    }

    // ==================================================================
    // 存储约定
    // ==================================================================

    [Fact]
    public async Task 存储路径按申请单分目录且文件名用Id()
    {
        SignInAsAdmin();

        var tree = await BuildOrgTreeAsync();
        var type = await CreateTypeAsync(requireAttachment: false);
        var student = await CreateStudentAsync(tree.SchoolA1.Id);
        var apply = await SubmitAsync(type, student.Id, tree.SchoolA2.Id);

        var content = NewPdfBytes();

        // 文件名里带路径分隔符与生僻字：物理文件名只能由 Id + 扩展名拼成，
        // 原始名只作为展示名入库（否则既要处理编码，又要防路径穿越）。
        // 用正斜杠而不是反斜杠：Windows 上两种都算分隔符，Linux 上只有斜杠算 ——
        // 写反斜杠会让这条断言在 Linux CI 上变成"文件名没被剥掉路径"。
        var uploaded = await _attachments.UploadAsync(
            apply.Id, "../../户口簿·扫描件.pdf", "application/pdf", AsStream(content), content.Length);

        var row = await _attachmentRows.GetAsync(uploaded.Id);

        row.RelativePath.ShouldBe($"change/{apply.Id:N}/{uploaded.Id:N}.pdf");
        row.FileName.ShouldBe("户口簿·扫描件.pdf", "展示名只去掉路径部分，其余原样保留");

        // 文件真的落在这个路径上（根目录就是配置里那个）
        var fullPath = Path.Combine(
            _store.RootPath, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(fullPath).ShouldBeTrue("附件应当真的落在配置的根目录下");
    }
}
