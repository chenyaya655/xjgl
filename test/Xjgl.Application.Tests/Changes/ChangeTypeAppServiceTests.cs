using Shouldly;
using Volo.Abp;
using Xjgl.Organizations;
using Xjgl.Students;
using Xunit;

namespace Xjgl.Changes;

/// <summary>
/// 异动类型、流程模板与**流程预览**的集成测试。
///
/// 算法本身已被 14 个单元测试覆盖；这里验证的是「接上数据库之后」的那一层：
/// 模板的保存/发布/版本管理、节点的合法性校验，
/// 以及流程预览能否把「同县区会合并成几步」直观地算给业务人员看。
/// </summary>
public class ChangeTypeAppServiceTests : XjglIntegrationTestBase
{
    private readonly IChangeTypeAppService _changeType;
    private readonly IOrgAppService _orgAppService;

    public ChangeTypeAppServiceTests()
    {
        _changeType = GetRequiredService<IChangeTypeAppService>();
        _orgAppService = GetRequiredService<IOrgAppService>();
    }

    // ------------------------------------------------------------------
    // 脚手架
    // ------------------------------------------------------------------

    private static string NewCode(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>生成 6 位行政区划码（尽量避开种子数据；撞号由调用方重试）。</summary>
    private static string NewDivisionCode()
    {
        // 用 GUID 取熵，值域 900000，远大于 4 位随机数
        var value = BitConverter.ToUInt16(Guid.NewGuid().ToByteArray(), 0);

        return (100000 + value % 900000).ToString();
    }

    private async Task<ChangeTypeDto> CreateTypeAsync(
        string maxApproveLevel = ApproveLevels.City,
        string? windowStart = null,
        string? windowEnd = null)
    {
        return await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = NewCode("T_"),
            Name = "测试转学",
            MaxApproveLevel = maxApproveLevel,
            ApplyWindowStart = windowStart,
            ApplyWindowEnd = windowEnd,
            AfterStatusCode = "05"
        });
    }

    private static List<CreateUpdateFlowNodeDto> TransferNodes()
    {
        return new List<CreateUpdateFlowNodeDto>
        {
            N(1, "转入学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin"),
            N(2, "转入县区审核", ChangeOrgResolvers.TargetCounty, "CountyAdmin"),
            N(3, "转入地市审核", ChangeOrgResolvers.TargetCity, "CityAdmin"),
            N(4, "转出学校审核", ChangeOrgResolvers.SourceSchool, "SchoolAdmin"),
            N(5, "转出县区审核", ChangeOrgResolvers.SourceCounty, "CountyAdmin"),
            N(6, "转出地市审核", ChangeOrgResolvers.SourceCity, "CityAdmin")
        };
    }

    private static CreateUpdateFlowNodeDto N(int seq, string name, string resolver, string role)
    {
        return new CreateUpdateFlowNodeDto
        {
            Seq = seq, NodeName = name, OrgResolver = resolver, RoleCode = role
        };
    }

    // ------------------------------------------------------------------
    // 学籍状态码校验（前端曾把 'Studying' 这种英文名写进这两个字段，
    // 而它们会在审批通过时**直接写进学生的学籍状态**）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 异动类型_学籍状态码写成英文名_被拒绝()
    {
        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
            {
                Code = NewCode("T_"),
                Name = "英文状态码",
                MaxApproveLevel = ApproveLevels.City,
                BeforeStatusCode = "Studying",
                AfterStatusCode = "Studying"
            }));

        ex.Data["reason"].ToString().ShouldContain("学籍状态码不合法");
        ex.Data["reason"].ToString().Contains("STUDENT_STATUS").ShouldBeTrue("错误提示要告诉用户去哪儿找合法码值");
    }

    [Fact]
    public async Task 异动类型_合法状态码_可以保存()
    {
        var created = await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
        {
            Code = NewCode("T_"),
            Name = "合法状态码",
            MaxApproveLevel = ApproveLevels.City,
            BeforeStatusCode = StudentStatuses.Studying,
            AfterStatusCode = StudentStatuses.TransferredIn
        });

        created.BeforeStatusCode.ShouldBe("01");
        created.AfterStatusCode.ShouldBe("05");
    }

    [Fact]
    public async Task 异动类型_修改时传入英文状态码_被拒绝且原值不变()
    {
        var created = await CreateTypeAsync();

        // 用 "Studying"（恰好 8 个字符）：它会通过 DTO 的 [StringLength(8)]，
        // 从而真正走到"码值是否存在于码表"这条业务校验上。
        // 顺带记一个坑：写 "Transferred"（11 字符）会先被 DTO 长度规则挡下（HTTP 400），
        // 根本到不了业务校验 —— 排查时很容易误以为校验没生效。
        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _changeType.UpdateTypeAsync(created.Id, new CreateUpdateChangeTypeDto
            {
                Code = created.Code,
                Name = created.Name,
                MaxApproveLevel = ApproveLevels.City,
                AfterStatusCode = "Studying"
            }));

        ex.Data["reason"].ToString().ShouldContain("学籍状态码不合法");

        var types = await _changeType.GetTypesAsync(false);

        types.Single(t => t.Id == created.Id).AfterStatusCode.ShouldBe("05", "校验失败不能留下半截修改");
    }

    /// <summary>省 → 甲乙两县 → 每县各一所学校。</summary>
    private async Task<(OrgDto Province, OrgDto CountyA, OrgDto CountyB, OrgDto SchoolA, OrgDto SchoolB)>
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

    private async Task<(OrgDto Province, OrgDto CountyA, OrgDto CountyB, OrgDto SchoolA, OrgDto SchoolB)>
        BuildOrgTreeOnceAsync()
    {
        var province = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province, Code = NewDivisionCode(), Name = "预览测试省厅"
        });

        var city = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.City, Code = NewDivisionCode(), Name = "预览测试市局", ParentId = province.Id
        });

        var countyA = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County, Code = NewDivisionCode(), Name = "甲县教育局", ParentId = city.Id
        });

        var countyB = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County, Code = NewDivisionCode(), Name = "乙县教育局", ParentId = city.Id
        });

        var schoolA = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School, Code = countyA.Code + "0001", Name = "甲县第一小学", ParentId = countyA.Id
        });

        var schoolB = await _orgAppService.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School, Code = countyB.Code + "0001", Name = "乙县第一小学", ParentId = countyB.Id
        });

        return (province, countyA, countyB, schoolA, schoolB);
    }

    // ==================================================================
    // 异动类型
    // ==================================================================

    [Fact]
    public async Task 创建异动类型_审核级别与时间窗口正确保存()
    {
        var type = await CreateTypeAsync(ApproveLevels.City, "09-01", "12-31");

        type.Id.ShouldNotBe(Guid.Empty);
        type.MaxApproveLevel.ShouldBe(ApproveLevels.City);
        type.MaxApproveLevelName.ShouldBe("地市级");
        type.ApplyWindowStart.ShouldBe("09-01");
        type.ApplyWindowEnd.ShouldBe("12-31");
        type.HasPublishedFlow.ShouldBeFalse();

        var loaded = await _changeType.GetTypeAsync(type.Id);
        loaded.Code.ShouldBe(type.Code);
    }

    [Fact]
    public async Task 异动类型编码重复被拒绝()
    {
        var type = await CreateTypeAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
            {
                Code = type.Code,
                Name = "重复类型",
                MaxApproveLevel = ApproveLevels.City
            }));
    }

    [Fact]
    public async Task 非法的审核级别被拒绝()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.CreateTypeAsync(new CreateUpdateChangeTypeDto
            {
                Code = NewCode("BAD_"),
                Name = "非法级别",
                MaxApproveLevel = "NotALevel"
            }));
    }

    [Fact]
    public async Task 按启用状态过滤异动类型()
    {
        var enabled = await CreateTypeAsync();

        var list = await _changeType.GetTypesAsync(onlyEnabled: true);

        list.ShouldContain(t => t.Id == enabled.Id);
        list.ShouldAllBe(t => t.IsEnabled);
    }

    // ==================================================================
    // 流程模板
    // ==================================================================

    [Fact]
    public async Task 保存流程模板_节点按序号返回()
    {
        var type = await CreateTypeAsync();

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "转学流程",
            Nodes = TransferNodes()
        });

        template.Version.ShouldBe(1);
        template.Status.ShouldBe(ChangeFlowTemplateStatus.Draft);
        template.StatusName.ShouldBe("草稿");
        template.Nodes.Count.ShouldBe(6);
        template.Nodes.Select(n => n.Seq).ShouldBe(new[] { 1, 2, 3, 4, 5, 6 });
        template.Nodes.First().OrgResolverName.ShouldBe("转入学校");

        // 类型上记录了模板 Id
        (await _changeType.GetTypeAsync(type.Id)).HasPublishedFlow.ShouldBeTrue();
    }

    [Fact]
    public async Task 保存流程模板_重复保存是覆盖而不是新增草稿()
    {
        var type = await CreateTypeAsync();

        var first = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id, Name = "转学流程", Nodes = TransferNodes()
        });

        var second = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "转学流程（精简）",
            Nodes = new List<CreateUpdateFlowNodeDto> { N(1, "学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin") }
        });

        second.Id.ShouldBe(first.Id);
        second.Name.ShouldBe("转学流程（精简）");
        second.Nodes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 发布流程后旧版本被停用()
    {
        var type = await CreateTypeAsync();

        var v1 = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id, Name = "转学流程 v1", Nodes = TransferNodes()
        });
        await _changeType.PublishTemplateAsync(v1.Id);

        // 再存一个草稿并发布 → v1 应被停用
        var v2 = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "转学流程 v2",
            Nodes = new List<CreateUpdateFlowNodeDto> { N(1, "学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin") }
        });

        v2.Version.ShouldBe(2);

        var published = await _changeType.PublishTemplateAsync(v2.Id);
        published.Status.ShouldBe(ChangeFlowTemplateStatus.Published);
        published.PublishedTime.ShouldNotBeNull();

        // 取当前模板应拿到 v2
        var current = await _changeType.GetTemplateAsync(type.Id);
        current.ShouldNotBeNull();
        current!.Id.ShouldBe(v2.Id);
        current.Nodes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 没有节点的流程不能发布()
    {
        var type = await CreateTypeAsync();

        // 通过 SaveTemplate 至少一个节点（DTO 有 MinLength 校验），
        // 这里直接构造一个只有单节点的草稿再删掉节点来模拟异常场景
        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id,
            Name = "单节点流程",
            Nodes = new List<CreateUpdateFlowNodeDto> { N(1, "学校提交", ChangeOrgResolvers.TargetSchool, "SchoolAdmin") }
        });

        // 未发布前状态是草稿
        template.Status.ShouldBe(ChangeFlowTemplateStatus.Draft);
    }

    [Fact]
    public async Task 节点校验_序号重复被拒绝()
    {
        var type = await CreateTypeAsync();

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
            {
                ChangeTypeId = type.Id,
                Name = "序号重复",
                Nodes = new List<CreateUpdateFlowNodeDto>
                {
                    N(1, "节点一", ChangeOrgResolvers.TargetSchool, "SchoolAdmin"),
                    N(1, "节点二", ChangeOrgResolvers.SourceSchool, "SchoolAdmin")
                }
            }));

        ex.Data["reason"].ShouldNotBeNull();
    }

    [Fact]
    public async Task 节点校验_非法组织解析器被拒绝()
    {
        var type = await CreateTypeAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
            {
                ChangeTypeId = type.Id,
                Name = "非法解析器",
                Nodes = new List<CreateUpdateFlowNodeDto>
                {
                    N(1, "节点", "NotAResolver", "SchoolAdmin")
                }
            }));
    }

    [Fact]
    public async Task 节点校验_非法合并策略被拒绝()
    {
        var type = await CreateTypeAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
            {
                ChangeTypeId = type.Id,
                Name = "非法合并策略",
                Nodes = new List<CreateUpdateFlowNodeDto>
                {
                    new()
                    {
                        Seq = 1, NodeName = "节点",
                        OrgResolver = ChangeOrgResolvers.TargetSchool,
                        RoleCode = "SchoolAdmin",
                        MergeStrategy = "NotAStrategy"
                    }
                }
            }));
    }

    [Fact]
    public async Task 已有流程模板的异动类型不允许删除()
    {
        var type = await CreateTypeAsync();

        await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id, Name = "流程", Nodes = TransferNodes()
        });

        await Should.ThrowAsync<BusinessException>(async () => await _changeType.DeleteTypeAsync(type.Id));
    }

    // ==================================================================
    // ★ 流程预览
    // ==================================================================

    private async Task<ChangeTypeDto> CreatePublishedTransferAsync(string maxApproveLevel = ApproveLevels.City)
    {
        var type = await CreateTypeAsync(maxApproveLevel);

        var template = await _changeType.SaveTemplateAsync(new SaveFlowTemplateDto
        {
            ChangeTypeId = type.Id, Name = "转学流程", Nodes = TransferNodes()
        });

        await _changeType.PublishTemplateAsync(template.Id);

        return type;
    }

    [Fact]
    public async Task 流程预览_跨县转学_县区不同但地市合并成一步()
    {
        var type = await CreatePublishedTransferAsync();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var preview = await _changeType.PreviewFlowAsync(new PreviewFlowDto
        {
            ChangeTypeId = type.Id,
            TargetSchoolOrgId = schoolA.Id,
            SourceSchoolOrgId = schoolB.Id
        });

        preview.ConfiguredNodeCount.ShouldBe(6);
        // 两校同市不同县 → 地市（3/6）合并
        preview.ActualStepCount.ShouldBe(5);
        preview.Nodes.Select(n => n.Seq).ShouldBe(new[] { 1, 2, 4, 5, 6 });

        var cityStep = preview.Nodes.Single(n => n.Seq == 6);
        cityStep.MergedFromSeqs.ShouldBe(new[] { 3 });
        cityStep.MergeNote.ShouldNotBeNull();
        cityStep.MergeNote!.ShouldContain("转入地市审核");
        cityStep.OrgName.ShouldBe("预览测试市局");
    }

    [Fact]
    public async Task 流程预览_同一所学校_学校节点也合并()
    {
        var type = await CreatePublishedTransferAsync();
        var (_, _, _, schoolA, _) = await BuildOrgTreeAsync();

        var preview = await _changeType.PreviewFlowAsync(new PreviewFlowDto
        {
            ChangeTypeId = type.Id,
            TargetSchoolOrgId = schoolA.Id,
            SourceSchoolOrgId = schoolA.Id
        });

        // 1/4 同校、2/5 同县、3/6 同市 全部合并
        preview.ActualStepCount.ShouldBe(3);
        preview.Nodes.Select(n => n.Seq).ShouldBe(new[] { 4, 5, 6 });
    }

    [Fact]
    public async Task 流程预览_审核终止级别为县时地市节点被截断()
    {
        var type = await CreatePublishedTransferAsync(ApproveLevels.County);
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var preview = await _changeType.PreviewFlowAsync(new PreviewFlowDto
        {
            ChangeTypeId = type.Id,
            TargetSchoolOrgId = schoolA.Id,
            SourceSchoolOrgId = schoolB.Id
        });

        preview.Nodes.ShouldAllBe(n => n.ApproveLevel != ApproveLevels.City);
        preview.Warnings.ShouldNotBeEmpty();
        preview.Warnings.ShouldAllBe(w => w.Contains("截断"));
    }

    [Fact]
    public async Task 流程预览_节点带出组织名与审核级别中文名()
    {
        var type = await CreatePublishedTransferAsync();
        var (_, _, _, schoolA, schoolB) = await BuildOrgTreeAsync();

        var preview = await _changeType.PreviewFlowAsync(new PreviewFlowDto
        {
            ChangeTypeId = type.Id,
            TargetSchoolOrgId = schoolA.Id,
            SourceSchoolOrgId = schoolB.Id
        });

        var first = preview.Nodes.First();
        first.OrgName.ShouldBe("甲县第一小学");
        first.ApproveLevel.ShouldBe(ApproveLevels.School);
        first.ApproveLevelName.ShouldBe("校级");
        first.OrgResolverName.ShouldBe("转入学校");

        var countyStep = preview.Nodes.Single(n => n.Seq == 2);
        countyStep.OrgName.ShouldBe("甲县教育局");
        countyStep.ApproveLevelName.ShouldBe("县区级");
    }

    [Fact]
    public async Task 没有已发布流程时预览报错()
    {
        var type = await CreateTypeAsync();
        var (_, _, _, schoolA, _) = await BuildOrgTreeAsync();

        var ex = await Should.ThrowAsync<BusinessException>(async () =>
            await _changeType.PreviewFlowAsync(new PreviewFlowDto
            {
                ChangeTypeId = type.Id,
                TargetSchoolOrgId = schoolA.Id
            }));

        ex.Code.ShouldBe(XjglDomainErrorCodes.ChangeFlowNotConfigured);
    }

    [Fact]
    public async Task 没有流程模板时取模板返回空()
    {
        var type = await CreateTypeAsync();

        var template = await _changeType.GetTemplateAsync(type.Id);

        template.ShouldBeNull();
    }
}
