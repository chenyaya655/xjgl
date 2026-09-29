using Shouldly;
using Volo.Abp;
using Xjgl.Data;
using Xjgl.Organizations;
using Xjgl.Students;
using Xunit;

namespace Xjgl.Changes;

/// <summary>
/// 异动申请与审核的端到端集成测试。
///
/// 这组用例跑完整链路：配置异动类型与流程 → 提交申请（自动实例化待办）
/// → 逐级审核 → 学籍变更生效 → 撤销审核 → 学籍变更回滚。
/// </summary>
public class ChangeFlowAppServiceTests : XjglIntegrationTestBase
{
    private const string Year = "2060-2061";

    private readonly IChangeFlowAppService _flow;
    private readonly IChangeTypeAppService _changeType;
    private readonly IOrgAppService _orgAppService;
    private readonly IGradeClassAppService _gradeClass;
    private readonly IStudentAppService _studentAppService;
    private readonly FakeCurrentPrincipalAccessor _principal;

    public ChangeFlowAppServiceTests()
    {
        _flow = GetRequiredService<IChangeFlowAppService>();
        _changeType = GetRequiredService<IChangeTypeAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
        _gradeClass = GetRequiredService<IGradeClassAppService>();
        _studentAppService = GetRequiredService<IStudentAppService>();
        _principal = GetRequiredService<FakeCurrentPrincipalAccessor>();
    }

    // ------------------------------------------------------------------
    // 测试脚手架：省 → 市 → 县 → 两所学校
    // ------------------------------------------------------------------

    private async Task<(OrgDto Province, OrgDto City, OrgDto County, OrgDto SchoolA, OrgDto SchoolB)>
        BuildOrgTreeAsync()
    {
        // 区划码只有 6 位、值域有限，而测试库会累积历史数据，
        // 因此随机编码可能撞号；这里对整棵树做重试，避免测试偶发失败。
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await BuildOrgTreeOnceAsync();
            }
            catch (BusinessException)
            {
                // 编码冲突，换一组重试
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的组织编码。");
    }

    private async Task<(OrgDto Province, OrgDto City, OrgDto County, OrgDto SchoolA, OrgDto SchoolB)>
        BuildOrgTreeOnceAsync()
    {
        // 行政区划码 6 位；学校标识码 10 位（前 6 位即所属行政区划码），
        // 长度必须精确，否则学籍辅号规则 {SchoolCode:10} 会失败
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province, Code = NewDivisionCode(), Name = "测试省厅"
        });

        var city = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.City, Code = NewDivisionCode(), Name = "测试市局", ParentId = province.Id
        });

        var county = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County, Code = NewDivisionCode(), Name = "测试县局", ParentId = city.Id
        });

        var schoolA = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School, Code = county.Code + "0001", Name = "测试第一小学", ParentId = county.Id
        });

        var schoolB = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School, Code = county.Code + "0002", Name = "测试第二小学", ParentId = county.Id
        });

        return (province, city, county, schoolA, schoolB);
    }

    /// <summary>生成 6 位行政区划码（用 GUID 取熵；撞号由调用方重试）。</summary>
    private static string NewDivisionCode()
    {
        var value = BitConverter.ToUInt16(Guid.NewGuid().ToByteArray(), 0);

        return (100000 + value % 900000).ToString();
    }

    /// <summary>
    /// 生成校验位正确的身份证号。
    ///
    /// 注意：值域要足够大且要配合重试 —— 测试库会累积历史数据，
    /// 早先版本用「固定地区 + 固定生日 + 3 位顺序码」只有 900 种可能，
    /// 跑多轮之后必然撞号，表现为随机的 DuplicateIdCard 让用例失败。
    /// </summary>
    private static string NewIdCard()
    {
        var birth = $"20{Random.Shared.Next(10, 30):D2}{Random.Shared.Next(1, 13):D2}{Random.Shared.Next(1, 28):D2}";
        var first17 = "410102" + birth + Random.Shared.Next(0, 1000).ToString("D3");

        var weights = new[] { 7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2 };
        const string checkCodes = "10X98765432";

        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += (first17[i] - '0') * weights[i];
        }

        return first17 + checkCodes[sum % 11];
    }

    private async Task<StudentDto> CreateStudentAsync(Guid schoolId, Guid? gradeId, Guid? classId)
    {
        // 身份证号撞号时换一个重试，避免测试偶发失败
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return await _studentAppService.CreateAsync(new CreateUpdateStudentDto
                {
                    Name = "异动测试学生",
                    GenderCode = "1",
                    BirthDate = new DateTime(2015, 6, 1),
                    IdCardTypeCode = "1",
                    IdCardNumber = NewIdCard(),
                    SchoolOrgId = schoolId,
                    AcademicYear = Year,
                    GradeId = gradeId,
                    ClassId = classId
                });
            }
            catch (BusinessException)
            {
                // 身份证号已被占用，重新生成
            }
        }

        throw new InvalidOperationException("多次尝试后仍无法生成唯一的身份证号。");
    }

    /// <summary>建一个「转学」异动类型 + 6 节点流程模板并发布。</summary>
    private async Task<ChangeTypeDto> CreateTransferTypeAsync(
        string maxApproveLevel = ApproveLevels.City,
        string? windowStart = null,
        string? windowEnd = null,
        string? gradeScope = null)
    {
        var type = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "TRANSFER_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "转学",
            MaxApproveLevel = maxApproveLevel,
            ApplyWindowStart = windowStart,
            ApplyWindowEnd = windowEnd,
            GradeScope = gradeScope,
            AffectClass = true,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "转学流程",
            Nodes = new List<CreateUpdateFlowNodeDto>
            {
                Node(1, "转入学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin"),
                Node(2, "转入县区审核", ChangeOrgResolvers.TargetCounty, "CountyAdmin"),
                Node(3, "转入地市审核", ChangeOrgResolvers.TargetCity, "CityAdmin"),
                Node(4, "转出学校审核", ChangeOrgResolvers.SourceSchool, "SchoolAdmin"),
                Node(5, "转出县区审核", ChangeOrgResolvers.SourceCounty, "CountyAdmin"),
                Node(6, "转出地市审核", ChangeOrgResolvers.SourceCity, "CityAdmin")
            }
        });

        await _changeType.PublishTemplateAsync(template.Id);

        return type;
    }

    private static CreateUpdateFlowNodeDto Node(int seq, string name, string resolver, string role)
    {
        return new CreateUpdateFlowNodeDto
        {
            Seq = seq, NodeName = name, OrgResolver = resolver, RoleCode = role,
            MergeStrategy = ChangeMergeStrategies.MergeToLast
        };
    }

    private void SignIn()
    {
        // 以管理员身份跑流程用例：本类只验证"流程实例化与合并、审核推进"是否正确，
        // 与"谁有权看哪些数据"无关。数据范围另有一组专门的用例（DataScopeTests）。
        _principal.SetUser(Guid.NewGuid(), "change_tester", "admin");
    }

    // ==================================================================
    // 债 1：多步写的事务原子性
    //
    // 【用什么表做验证】`stu_guardian`：它的必填列只有 (student_id, seq, name)，
    // 可以直接构造入库。为什么不用 `sys_org`：它还有一个**必须由服务层计算的**
    // `path_code`（物化路径），直接构造入库会撞 NOT NULL —— 用例会红在
    // "插入失败"上，而不是红在事务语义上，等于没测到要测的东西。
    // ==================================================================

    /// <summary>造一条守卫者记录（只依赖必填列，便于做事务验证）。</summary>
    private static StuGuardian NewGuardian(string name)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, name);

    [Fact]
    public async Task 事务范围_中途抛异常时前面的写全部回滚()
    {
        // 【为什么这条用例是债 1 的验收标准】
        // "包了事务"这件事本身无法从代码看出来 —— 必须证明**回滚真的生效**：
        // 前半段成功写入的数据，在异常之后**必须查不到**。
        // 只断言"没抛异常/正常提交"是不合格的：那连没包事务的实现也能通过。
        var tx = GetRequiredService<IXjglTransactionScope>();
        var guardians = GetRequiredService<IXjglRepository<StuGuardian>>();

        var marker = "事务回滚测试" + Guid.NewGuid().ToString("N")[..8];

        (await guardians.GetCountAsync(g => g.Name == marker))
            .ShouldBe(0, "标记名应当是本轮新造的，起点必须为 0");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await tx.ExecuteAsync(async () =>
            {
                // 前半段：真的写库（不是内存里的对象）
                await guardians.InsertAsync(NewGuardian(marker), autoSave: true);

                // 确认这一条**确实已经写进库了** —— 否则下面的"回滚后查不到"
                // 无法区分"被回滚了"与"压根没写进去"
                (await guardians.GetCountAsync(g => g.Name == marker))
                    .ShouldBe(1, "事务内应当能查到自己刚写入的数据");

                // 后半段：失败
                throw new InvalidOperationException("故意失败，用来验证回滚");
            });
        });

        (await guardians.GetCountAsync(g => g.Name == marker))
            .ShouldBe(0, "事务回滚后，前半段写入的数据必须查不到 —— 否则事务等于没包");
    }

    [Fact]
    public async Task 事务范围_正常结束时数据确实落库()
    {
        // 反向用例：只有"异常回滚"一条会让"永远不提交"的实现也通过。
        var tx = GetRequiredService<IXjglTransactionScope>();
        var guardians = GetRequiredService<IXjglRepository<StuGuardian>>();

        var marker = "事务提交测试" + Guid.NewGuid().ToString("N")[..8];

        await tx.ExecuteAsync(async () =>
        {
            await guardians.InsertAsync(NewGuardian(marker), autoSave: true);
        });

        (await guardians.GetCountAsync(g => g.Name == marker))
            .ShouldBe(1, "事务正常结束后数据必须真的落库");
    }

    [Fact]
    public async Task 事务范围_嵌套时内层不提交_由最外层统一决定()
    {
        // 嵌套场景在本系统里是真实存在的：应用服务 A 开事务范围后调用了
        // 也开了事务范围的应用服务 B。若内层自己提交，外层的回滚就回不到
        // 内层已提交的部分 —— 批量审批这类操作会留下半成品。
        var tx = GetRequiredService<IXjglTransactionScope>();
        var guardians = GetRequiredService<IXjglRepository<StuGuardian>>();

        var outer = "嵌套外层" + Guid.NewGuid().ToString("N")[..8];
        var inner = "嵌套内层" + Guid.NewGuid().ToString("N")[..8];

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await tx.ExecuteAsync(async () =>
            {
                await guardians.InsertAsync(NewGuardian(outer), autoSave: true);

                // 内层"成功"结束
                await tx.ExecuteAsync(async () =>
                {
                    await guardians.InsertAsync(NewGuardian(inner), autoSave: true);
                });

                throw new InvalidOperationException("外层失败，内层也必须一起回滚");
            });
        });

        (await guardians.GetCountAsync(g => g.Name == outer)).ShouldBe(0, "外层的数据应被回滚");
        (await guardians.GetCountAsync(g => g.Name == inner))
            .ShouldBe(0, "内层**不能**自己提交 —— 否则外层回滚后它会残留");
    }

    // ==================================================================
    // 提交与实例化
    // ==================================================================

    [Fact]
    public async Task 同县区转学_提交后只生成四个待办_县区与地市各合并一次()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        apply.ApplyNo.ShouldStartWith("YC");
        apply.Status.ShouldBe(ChangeApplyStatus.Submitted);

        // 两校同县同市 → 县区（2/5）与地市（3/6）各合并，学校不同所以 1/4 都保留
        apply.Tasks.Select(t => t.Seq).ShouldBe(new[] { 1, 4, 5, 6 });
        apply.Tasks.Single(t => t.Seq == 5).MergedFromSeqs.ShouldBe(new[] { 2 });
        apply.Tasks.Single(t => t.Seq == 6).MergedFromSeqs.ShouldBe(new[] { 3 });
        apply.CurrentSeq.ShouldBe(1);
        apply.Logs.ShouldContain(l => l.Action == ChangeFlowActions.Submit);
    }

    [Fact]
    public async Task 不在允许的时间窗口内_提交被拒绝()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        // 窗口设置为 1 月 1 日 ~ 1 月 2 日，当前必然不在窗口内
        var type = await CreateTransferTypeAsync(windowStart: "01-01", windowEnd: "01-02");
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _flow.SubmitAsync(new SubmitChangeApplyDto
            {
                ChangeTypeId = type.Id,
                StudentId = student.Id,
                TargetSchoolOrgId = schoolB.Id,
                Reason = "家庭搬迁"
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.OutOfApplyTimeWindow);
    }

    [Fact]
    public async Task 年级不在允许范围内_提交被拒绝()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var grade = (await _gradeClass.BatchGenerateGradesAsync(new BatchGenerateGradesDto
        {
            SchoolOrgId = schoolA.Id, AcademicYear = Year, GradeCodes = new List<string> { "06" }
        })).Items.Single();

        // 白名单只允许 01（一年级），学生是 06（六年级）
        var type = await CreateTransferTypeAsync(gradeScope: "01");
        var student = await CreateStudentAsync(schoolA.Id, grade.Id, null);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _flow.SubmitAsync(new SubmitChangeApplyDto
            {
                ChangeTypeId = type.Id,
                StudentId = student.Id,
                TargetSchoolOrgId = schoolB.Id,
                Reason = "家庭搬迁"
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.GradeNotAllowedForChange);
    }

    [Fact]
    public async Task 没有已发布的流程模板_提交被拒绝()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var type = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "NOFLOW_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "无流程异动",
            MaxApproveLevel = ApproveLevels.City
        });

        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _flow.SubmitAsync(new SubmitChangeApplyDto
            {
                ChangeTypeId = type.Id,
                StudentId = student.Id,
                TargetSchoolOrgId = schoolB.Id,
                Reason = "测试"
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeFlowNotConfigured);
    }

    // ==================================================================
    // 完整审批链 + 业务联动
    // ==================================================================

    [Fact]
    public async Task 逐级审核走完_学籍状态与学校变更生效()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        // 按实例化出的节点顺序逐个通过
        var seqs = apply.Tasks.Select(t => t.Seq).ToList();

        foreach (var seq in seqs)
        {
            var current = await _flow.GetApplyAsync(apply.Id);
            current.CurrentSeq.ShouldBe(seq);

            var result = await _flow.ApproveAsync(new ChangeAuditActionDto
            {
                ApplyId = apply.Id,
                Opinion = $"节点 {seq} 同意"
            });

            result.Tasks.Single(t => t.Seq == seq).Status.ShouldBe(ChangeTaskStatus.Approved);
        }

        var final = await _flow.GetApplyAsync(apply.Id);

        final.Status.ShouldBe(ChangeApplyStatus.Approved);
        final.CurrentSeq.ShouldBeNull();
        final.FinalApprovedTime.ShouldNotBeNull();
        final.Tasks.ShouldAllBe(t => t.Status == ChangeTaskStatus.Approved);
        final.Logs.ShouldContain(l => l.Action == ChangeFlowActions.Archive);

        // ★ 业务联动：学生的学籍状态与学校都变了
        var updatedStudent = await _studentAppService.GetAsync(student.Id);
        updatedStudent.StudentStatusCode.ShouldBe(StudentStatuses.TransferredIn);
        updatedStudent.StudentStatusName.ShouldBe("转入");
        updatedStudent.SchoolOrgId.ShouldBe(schoolB.Id);
        updatedStudent.SchoolName.ShouldBe("测试第二小学");
    }

    [Fact]
    public async Task 驳回后申请单结束_学生学籍不变()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var rejected = await _flow.RejectAsync(new ChangeAuditActionDto
        {
            ApplyId = apply.Id,
            Opinion = "材料不全"
        });

        rejected.Status.ShouldBe(ChangeApplyStatus.Rejected);
        rejected.CurrentSeq.ShouldBeNull();

        var updatedStudent = await _studentAppService.GetAsync(student.Id);
        updatedStudent.StudentStatusCode.ShouldBe(StudentStatuses.Studying);
        updatedStudent.SchoolOrgId.ShouldBe(schoolA.Id);
    }

    /// <summary>
    /// 流程节点设为「不允许驳回」时，驳回必须被**真的**拒绝。
    ///
    /// 这条守的是一个"配了等于没配"的字段：`AllowReject` 一直存在模板节点上、
    /// 也一路带进了实例化出的待办，但审核时**没有任何人读它** ——
    /// 也就是说把节点设成不允许驳回，驳回照样成功，而且不报错。
    ///
    /// 用例把**所有**节点都设成不允许驳回，这样无论当前待办落在哪一步都必须被拦住
    /// （不去依赖"合并后当前是第几步"这种细节，那是流程实例化算法自己的用例在守）。
    /// </summary>
    [Fact]
    public async Task 节点不允许驳回时_驳回被拒绝()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var type = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = "NOREJECT_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "不允许驳回的转学",
            MaxApproveLevel = ApproveLevels.City,
            AffectClass = true,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "不允许驳回的流程",
            Nodes = new List<CreateUpdateFlowNodeDto>
            {
                new()
                {
                    Seq = 1, NodeName = "转入学校审核",
                    OrgResolver = ChangeOrgResolvers.TargetSchool, RoleCode = "SchoolAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast,
                    // ★ 本用例的关键：这个节点只允许「通过」
                    AllowReject = false
                },
                new()
                {
                    Seq = 2, NodeName = "转入县区审核",
                    OrgResolver = ChangeOrgResolvers.TargetCounty, RoleCode = "CountyAdmin",
                    MergeStrategy = ChangeMergeStrategies.MergeToLast,
                    AllowReject = false
                }
            }
        });

        await _changeType.PublishTemplateAsync(template.Id);

        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _flow.RejectAsync(new ChangeAuditActionDto
            {
                ApplyId = apply.Id,
                Opinion = "材料不全"
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.RejectNotAllowed);

        // 驳回被拒之后，申请单必须原样停在原处（不能出现"报错了但状态已经改了"）
        var unchanged = await _flow.GetApplyAsync(apply.Id);
        unchanged.Status.ShouldBe(apply.Status);
        unchanged.Tasks.ShouldContain(t => t.Status == ChangeTaskStatus.Pending,
            "驳回被拒之后流程应当仍在等待处理");
        unchanged.Tasks.ShouldNotContain(t => t.Status == ChangeTaskStatus.Rejected);
    }

    /// <summary>
    /// 回归用例：流程被终止后，剩下的节点不能继续显示为「待处理」。
    ///
    /// 这个缺陷是启动后端、用真实 HTTP 走一遍「提交 → 通过 → 驳回」才发现的：
    /// 申请单已经是「已驳回」，详情里却还挂着两个「待处理」节点，
    /// 用户会以为还要有人继续审。集成测试当时只断言了申请单状态与学籍，
    /// 没有看**其余节点**的状态。
    /// </summary>
    [Fact]
    public async Task 驳回后_后续未处理节点应标记为已跳过()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        // 同县区转学：转入校 → 转出校 → 县区(合并) → 地市(合并)
        apply.Tasks.Count.ShouldBeGreaterThan(1);

        var rejected = await _flow.RejectAsync(new ChangeAuditActionDto
        {
            ApplyId = apply.Id,
            Opinion = "材料不全"
        });

        rejected.Status.ShouldBe(ChangeApplyStatus.Rejected);
        rejected.Tasks.ShouldContain(t => t.Status == ChangeTaskStatus.Rejected);
        rejected.Tasks.ShouldNotContain(t => t.Status == ChangeTaskStatus.Pending,
            "流程已终止，不应再有待处理节点");
    }

    [Fact]
    public async Task 撤回后_所有未处理节点应标记为已跳过()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var withdrawn = await _flow.WithdrawAsync(apply.Id);

        withdrawn.Status.ShouldBe(ChangeApplyStatus.Withdrawn);
        withdrawn.Tasks.ShouldNotContain(t => t.Status == ChangeTaskStatus.Pending);
    }

    // ==================================================================
    // 撤销审核
    // ==================================================================

    [Fact]
    public async Task 撤销审核_未走完流程时退回上一节点()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var firstSeq = apply.CurrentSeq!.Value;

        await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = apply.Id, Opinion = "同意" });

        var afterApprove = await _flow.GetApplyAsync(apply.Id);
        afterApprove.CurrentSeq.ShouldNotBe(firstSeq);

        var revoked = await _flow.RevokeApprovalAsync(new ChangeAuditActionDto
        {
            ApplyId = apply.Id,
            Opinion = "撤回重审"
        });

        revoked.Status.ShouldBe(ChangeApplyStatus.InProgress);
        revoked.CurrentSeq.ShouldBe(firstSeq);
        revoked.Tasks.Single(t => t.Seq == firstSeq).Status.ShouldBe(ChangeTaskStatus.Pending);
        revoked.Logs.ShouldContain(l => l.Action == ChangeFlowActions.Revoke);
    }

    [Fact]
    public async Task 撤销已通过的审核_学籍变更被回滚()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        // 全部节点通过
        foreach (var _ in apply.Tasks)
        {
            await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = apply.Id, Opinion = "同意" });
        }

        (await _studentAppService.GetAsync(student.Id)).SchoolOrgId.ShouldBe(schoolB.Id);

        // 撤销最后一个节点的审核
        var revoked = await _flow.RevokeApprovalAsync(new ChangeAuditActionDto
        {
            ApplyId = apply.Id,
            Opinion = "发现材料问题，撤销"
        });

        revoked.Status.ShouldBe(ChangeApplyStatus.InProgress);
        revoked.FinalApprovedTime.ShouldBeNull();

        // ★ 学籍变更被回滚
        var reverted = await _studentAppService.GetAsync(student.Id);
        reverted.SchoolOrgId.ShouldBe(schoolA.Id);
        reverted.StudentStatusCode.ShouldBe(StudentStatuses.Studying);
    }

    [Fact]
    public async Task 后续节点已处理时_不允许再撤销()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        // 前两个节点都通过
        await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = apply.Id });
        await _flow.ApproveAsync(new ChangeAuditActionDto { ApplyId = apply.Id });

        // 此时试图撤销「第一个节点」——但它是相对最后处理的节点而言的，
        // 服务会针对最后处理的节点撤销；这里验证撤销后回到第二个节点
        var revoked = await _flow.RevokeApprovalAsync(new ChangeAuditActionDto { ApplyId = apply.Id });

        revoked.CurrentSeq.ShouldBe(apply.Tasks[1].Seq);
    }

    // ==================================================================
    // 待办与查询
    // ==================================================================

    [Fact]
    public async Task 待办列表只显示当前节点()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var todo = await _flow.GetTodoListAsync(new ChangeTodoQueryDto { MaxResultCount = 100 });

        todo.Items.ShouldContain(i => i.Id == apply.Id);
        todo.Items.Single(i => i.Id == apply.Id).CurrentSeq.ShouldBe(apply.CurrentSeq);
    }

    [Fact]
    public async Task 撤回申请后待办消失()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var withdrawn = await _flow.WithdrawAsync(apply.Id);

        withdrawn.Status.ShouldBe(ChangeApplyStatus.Withdrawn);

        var todo = await _flow.GetTodoListAsync(new ChangeTodoQueryDto { MaxResultCount = 100 });
        todo.Items.ShouldNotContain(i => i.Id == apply.Id);
    }

    [Fact]
    public async Task 按异动类型与学生查询申请单()
    {
        SignIn();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();
        var type = await CreateTransferTypeAsync();
        var student = await CreateStudentAsync(schoolA.Id, null, null);

        var apply = await _flow.SubmitAsync(new SubmitChangeApplyDto
        {
            ChangeTypeId = type.Id,
            StudentId = student.Id,
            TargetSchoolOrgId = schoolB.Id,
            Reason = "家庭搬迁"
        });

        var byType = await _flow.GetAppliesAsync(new ChangeApplyQueryDto
        {
            ChangeTypeId = type.Id, MaxResultCount = 50
        });
        byType.Items.ShouldContain(i => i.Id == apply.Id);

        var byStudent = await _flow.GetAppliesAsync(new ChangeApplyQueryDto
        {
            StudentId = student.Id, MaxResultCount = 50
        });
        byStudent.Items.ShouldContain(i => i.Id == apply.Id);
    }
}
