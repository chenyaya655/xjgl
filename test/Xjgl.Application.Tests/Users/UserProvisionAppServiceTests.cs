using Shouldly;
using Volo.Abp;
using Volo.Abp.Identity;
using Xjgl.Data;
using Xjgl.Organizations;
using Xunit;

namespace Xjgl.Users;

/// <summary>
/// 用户预置与本人激活的集成测试。
///
/// 这组用例覆盖的是政务系统里最关键的一条安全约束链：
///   系统不开放自助注册 —— 外人即使拿到接口，也必须同时猜中
///   「手机号 + 姓名 + 激活码（+ 身份证号）」才能建号。
/// </summary>
public class UserProvisionAppServiceTests : XjglIntegrationTestBase
{
    private const string ValidPassword = "Test@123456";

    private readonly IUserProvisionAppService _provision;
    private readonly IOrgAppService _orgs;
    private readonly IdentityUserManager _userManager;
    private readonly IXjglRepository<SysUserPredefine> _predefineRepository;

    public UserProvisionAppServiceTests()
    {
        _provision = GetRequiredService<IUserProvisionAppService>();
        _orgs = GetRequiredService<IOrgAppService>();
        _userManager = GetRequiredService<IdentityUserManager>();
        _predefineRepository = GetRequiredService<IXjglRepository<SysUserPredefine>>();
    }

    // ------------------------------------------------------------------
    // 测试辅助
    // ------------------------------------------------------------------

    /// <summary>生成不重复的手机号（13 开头 + 9 位随机）。</summary>
    private static string NewPhone() => "13" + Random.Shared.Next(100_000_000, 999_999_999);

    private async Task<OrgDto> CreateOrgAsync()
    {
        return await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "集成测试学校"
        });
    }

    /// <summary>返回 DTO 与**未脱敏**的手机号（激活时要用真实手机号）。</summary>
    private async Task<(PredefineUserDto Dto, string Phone)> PredefineAsync(
        Guid orgId,
        string? idCardNumber = null,
        string? roleNames = null,
        string realName = "张三")
    {
        var phone = NewPhone();

        var dto = await _provision.CreateAsync(new CreatePredefineUserDto
        {
            RealName = realName,
            PhoneNumber = phone,
            IdCardNumber = idCardNumber,
            OrgId = orgId,
            RoleNames = roleNames,
            Position = "教师",
            ActivationValidDays = 30
        });

        return (dto, phone);
    }

    // ------------------------------------------------------------------
    // 预置
    // ------------------------------------------------------------------

    [Fact]
    public async Task 预置用户_初始状态为待激活_并生成激活码()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        dto.Id.ShouldNotBe(Guid.Empty);
        dto.Status.ShouldBe(UserPredefineStatus.Pending);
        dto.StatusName.ShouldBe("待激活");
        dto.ActivationCode.Length.ShouldBe(8);
        dto.ActivationExpiresAt.ShouldBeGreaterThan(DateTime.Now);
        dto.ActivatedUserId.ShouldBeNull();
        dto.OrgId.ShouldBe(org.Id);

        // 手机号在返回时脱敏
        dto.PhoneNumber.ShouldNotBe(phone);
        dto.PhoneNumber.ShouldContain("****");
    }

    [Fact]
    public async Task 手机号重复时被拒绝()
    {
        var org = await CreateOrgAsync();
        var (_, phone) = await PredefineAsync(org.Id);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.CreateAsync(new CreatePredefineUserDto
            {
                RealName = "李四",
                PhoneNumber = phone,
                OrgId = org.Id
            }));
    }

    [Fact]
    public async Task 非法手机号被拒绝()
    {
        var org = await CreateOrgAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.CreateAsync(new CreatePredefineUserDto
            {
                RealName = "王五",
                PhoneNumber = "123",
                OrgId = org.Id
            }));
    }

    [Fact]
    public async Task 生僻字姓名可以通过预置校验()
    {
        var org = await CreateOrgAsync();

        // U+20BB7，CJK 扩展 B 区
        var (dto, _) = await PredefineAsync(org.Id, realName: "\U00020BB7田");

        dto.RealName.ShouldBe("\U00020BB7田");
    }

    [Fact]
    public async Task 组织不存在时被拒绝()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.CreateAsync(new CreatePredefineUserDto
            {
                RealName = "赵六",
                PhoneNumber = NewPhone(),
                OrgId = Guid.NewGuid()
            }));
    }

    [Fact]
    public async Task 批量导入_合法行成功且非法行进入错误明细()
    {
        var org = await CreateOrgAsync();

        var result = await _provision.ImportAsync(new ImportPredefineUsersDto
        {
            Users = new List<CreatePredefineUserDto>
            {
                new() { RealName = "教师甲", PhoneNumber = NewPhone(), OrgId = org.Id },
                new() { RealName = "教师乙", PhoneNumber = "123", OrgId = org.Id },          // 手机号非法
                new() { RealName = "教师丙", PhoneNumber = NewPhone(), OrgId = org.Id }
            }
        });

        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(2);
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].RowIndex.ShouldBe(2);
        result.BatchNo.ShouldNotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------
    // 激活：这是核心链路
    // ------------------------------------------------------------------

    [Fact]
    public async Task 激活成功_创建账号并标记已激活()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id, roleNames: "admin");

        var result = await _provision.ActivateAsync(new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            ActivationCode = dto.ActivationCode,
            Password = ValidPassword
        });

        result.UserId.ShouldNotBe(Guid.Empty);
        result.UserName.ShouldBe(phone);
        result.OrgId.ShouldBe(org.Id);
        result.Roles.ShouldContain("admin");

        // ABP 用户确实建出来了
        var user = await _userManager.FindByIdAsync(result.UserId.ToString());
        user.ShouldNotBeNull();
        user!.Name.ShouldBe("张三");
        user.UserName.ShouldBe(phone);

        // 预置记录状态已流转
        var after = await _provision.GetAsync(dto.Id);
        after.Status.ShouldBe(UserPredefineStatus.Activated);
        after.ActivatedUserId.ShouldBe(result.UserId);
        after.ActivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task 激活后可以用该账号校验密码()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        var result = await _provision.ActivateAsync(new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            ActivationCode = dto.ActivationCode,
            Password = ValidPassword
        });

        var user = await _userManager.FindByIdAsync(result.UserId.ToString());
        (await _userManager.CheckPasswordAsync(user!, ValidPassword)).ShouldBeTrue();
    }

    [Fact]
    public async Task 激活时姓名不匹配被拒绝()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id, realName: "张三");

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "李四",
                ActivationCode = dto.ActivationCode,
                Password = ValidPassword
            }));
    }

    [Fact]
    public async Task 激活码错误被拒绝()
    {
        var org = await CreateOrgAsync();
        var (_, phone) = await PredefineAsync(org.Id);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "张三",
                ActivationCode = "WRONGCOD",
                Password = ValidPassword
            }));
    }

    [Fact]
    public async Task 不在预置名单里的手机号无法激活_外人注册不了()
    {
        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = NewPhone(),
                RealName = "陌生人",
                ActivationCode = "ABCDEFGH",
                Password = ValidPassword
            }));
    }

    [Fact]
    public async Task 重复激活被拒绝()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        var input = new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            ActivationCode = dto.ActivationCode,
            Password = ValidPassword
        };

        await _provision.ActivateAsync(input);

        await Should.ThrowAsync<BusinessException>(async () => await _provision.ActivateAsync(input));
    }

    [Fact]
    public async Task 激活码过期被拒绝_且状态流转为已过期()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        // 把过期时间改到过去
        var entity = await _predefineRepository.GetAsync(dto.Id);
        entity.ActivationExpiresAt = DateTime.Now.AddDays(-1);
        await _predefineRepository.UpdateAsync(entity, autoSave: true);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "张三",
                ActivationCode = dto.ActivationCode,
                Password = ValidPassword
            }));

        var after = await _provision.GetAsync(dto.Id);
        after.Status.ShouldBe(UserPredefineStatus.Expired);
    }

    [Fact]
    public async Task 续期后过期的记录可以重新激活()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        var entity = await _predefineRepository.GetAsync(dto.Id);
        entity.ActivationExpiresAt = DateTime.Now.AddDays(-1);
        entity.Status = UserPredefineStatus.Expired;
        await _predefineRepository.UpdateAsync(entity, autoSave: true);

        var renewed = await _provision.RenewActivationAsync(dto.Id, validDays: 7);
        renewed.Status.ShouldBe(UserPredefineStatus.Pending);
        renewed.ActivationCode.ShouldNotBeNullOrWhiteSpace();

        var result = await _provision.ActivateAsync(new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            ActivationCode = renewed.ActivationCode,
            Password = ValidPassword
        });

        result.UserId.ShouldNotBe(Guid.Empty);
    }

    // ------------------------------------------------------------------
    // 身份证号校验
    // ------------------------------------------------------------------

    [Fact]
    public async Task 预置登记了身份证号_激活时必须匹配()
    {
        var org = await CreateOrgAsync();
        const string idCard = "11010519491231002X";
        var (dto, phone) = await PredefineAsync(org.Id, idCardNumber: idCard);

        // 不提供身份证号 → 拒绝
        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "张三",
                ActivationCode = dto.ActivationCode,
                Password = ValidPassword
            }));

        // 提供错误的身份证号 → 拒绝
        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "张三",
                IdCardNumber = "110105194912310021",
                ActivationCode = dto.ActivationCode,
                Password = ValidPassword
            }));

        // 提供正确的身份证号 → 成功
        var result = await _provision.ActivateAsync(new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            IdCardNumber = idCard,
            ActivationCode = dto.ActivationCode,
            Password = ValidPassword
        });

        result.UserId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task 预置时非法身份证号被拒绝()
    {
        var org = await CreateOrgAsync();

        await Should.ThrowAsync<BusinessException>(async () =>
            await PredefineAsync(org.Id, idCardNumber: "110105194912310021"));
    }

    // ------------------------------------------------------------------
    // 撤销与删除
    // ------------------------------------------------------------------

    [Fact]
    public async Task 已撤销的预置记录不能激活()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        var revoked = await _provision.RevokeAsync(dto.Id);
        revoked.Status.ShouldBe(UserPredefineStatus.Revoked);

        await Should.ThrowAsync<BusinessException>(async () =>
            await _provision.ActivateAsync(new ActivateUserDto
            {
                PhoneNumber = phone,
                RealName = "张三",
                ActivationCode = dto.ActivationCode,
                Password = ValidPassword
            }));
    }

    [Fact]
    public async Task 已激活的预置记录不允许撤销也不允许删除()
    {
        var org = await CreateOrgAsync();
        var (dto, phone) = await PredefineAsync(org.Id);

        await _provision.ActivateAsync(new ActivateUserDto
        {
            PhoneNumber = phone,
            RealName = "张三",
            ActivationCode = dto.ActivationCode,
            Password = ValidPassword
        });

        await Should.ThrowAsync<BusinessException>(async () => await _provision.RevokeAsync(dto.Id));
        await Should.ThrowAsync<BusinessException>(async () => await _provision.DeleteAsync(dto.Id));
    }

    [Fact]
    public async Task 未激活的预置记录可以删除()
    {
        var org = await CreateOrgAsync();
        var (dto, _) = await PredefineAsync(org.Id);

        await _provision.DeleteAsync(dto.Id);

        // 软删除后查不到
        await Should.ThrowAsync<Exception>(async () => await _provision.GetAsync(dto.Id));
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    [Fact]
    public async Task 按组织子树查询预置名单()
    {
        var province = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.Province,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "测试省厅"
        });

        // 补齐"市 → 县"两级：学校只能挂在县区下（见 OrgAppService.EnsureHierarchy）。
        // 本用例查的是**子树**，链条加长不影响断言（省厅仍在最上层）。
        var city = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.City,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "测试市局",
            ParentId = province.Id
        });

        var county = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.County,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "测试县局",
            ParentId = city.Id
        });

        var school = await _orgs.CreateAsync(new CreateUpdateOrgDto
        {
            OrgType = OrgTypes.School,
            Code = "9" + Guid.NewGuid().ToString("N")[..8],
            Name = "测试学校",
            ParentId = county.Id
        });

        var (schoolRecord, _) = await PredefineAsync(school.Id, realName: "下属学校教师");

        var result = await _provision.GetListAsync(new PredefineUserQueryDto
        {
            OrgId = province.Id,
            IncludeDescendantOrgs = true,
            MaxResultCount = 100
        });

        result.Items.ShouldContain(i => i.Id == schoolRecord.Id);
    }

    [Fact]
    public async Task 按关键字与状态过滤预置名单()
    {
        var org = await CreateOrgAsync();
        var (dto, _) = await PredefineAsync(org.Id, realName: "关键字教师");

        var byKeyword = await _provision.GetListAsync(new PredefineUserQueryDto
        {
            Keyword = "关键字教师",
            MaxResultCount = 50
        });
        byKeyword.Items.ShouldContain(i => i.Id == dto.Id);

        var byStatus = await _provision.GetListAsync(new PredefineUserQueryDto
        {
            Status = UserPredefineStatus.Pending,
            MaxResultCount = 50
        });
        byStatus.Items.ShouldContain(i => i.Id == dto.Id);
    }
}
