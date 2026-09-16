using Microsoft.Extensions.Logging;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Guids;
using Xjgl.Data;

namespace Xjgl.Codes;

/// <summary>
/// 内置码表种子数据。
///
/// 设计原则（与方案文档第 7.5 节一致）：
///   1) **码类别（结构）全部预置**，并标注标准来源，方便对号入座；
///   2) 只预置**取值确定**的码值（如性别码 GB/T 2261.1）；
///   3) 取值需按标准原文或省厅口径校核的，预置时把
///      <see cref="SysCodeCategory.StdSource"/> 写成「待校核…」，
///      由业务人员通过「微代码管理 → 导入」替换为正式码表，
///      **绝不凭记忆写死国标码值**（民族码、行政区划码尤其如此）。
///
/// 幂等：已存在的码类别与码项不会被重复插入，也不会覆盖人工修改过的数据。
/// </summary>
public class CodeDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IXjglRepository<SysCodeCategory> _categories;
    private readonly IXjglRepository<SysCodeItem> _items;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ILogger<CodeDataSeedContributor> _logger;

    public CodeDataSeedContributor(
        IXjglRepository<SysCodeCategory> categories,
        IXjglRepository<SysCodeItem> items,
        IGuidGenerator guidGenerator,
        ILogger<CodeDataSeedContributor> logger)
    {
        _categories = categories;
        _items = items;
        _guidGenerator = guidGenerator;
        _logger = logger;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        _logger.LogInformation("[码表种子] 开始写入内置码表 ...");

        try
        {
            await SeedInternalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[码表种子] 写入失败");
            throw;
        }

        var categoryCount = await _categories.GetCountAsync();
        var itemCount = await _items.GetCountAsync();
        _logger.LogInformation("[码表种子] 完成：码类别 {Categories} 个，码项 {Items} 条",
            categoryCount, itemCount);
    }

    private async Task SeedInternalAsync()
    {
        // ---- 基础人员类 ----
        await AddAsync("SEX", "性别码", "GB/T 2261.1", 10, new[]
        {
            ("1", "男"), ("2", "女"), ("0", "未知")
        });

        await AddAsync("NATION", "民族码", "GB/T 3304（待按标准原文导入 56 个民族码值）", 20);

        await AddAsync("COUNTRY", "国别/地区码", "GB/T 2659（待按标准原文导入）", 30);

        await AddAsync("ID_TYPE", "身份证件类型码", "JY/T 1001-2012（待校核）", 40, new[]
        {
            ("1", "居民身份证"),
            ("2", "军官证"),
            ("3", "港澳居民来往内地通行证"),
            ("4", "台湾居民来往大陆通行证"),
            ("5", "护照"),
            ("9", "其他")
        });

        await AddAsync("POLITICAL", "政治面貌码", "GB/T 4762（待校核）", 50, new[]
        {
            ("01", "中共党员"),
            ("02", "中共预备党员"),
            ("03", "共青团员"),
            ("04", "民革会员"),
            ("05", "民盟盟员"),
            ("06", "民建会员"),
            ("07", "民进会员"),
            ("08", "农工党党员"),
            ("09", "致公党党员"),
            ("10", "九三学社社员"),
            ("11", "台盟盟员"),
            ("12", "无党派人士"),
            ("13", "群众")
        });

        // ---- 学生属性类 ----
        await AddAsync("STUDENT_STATUS", "学籍状态码", "待与省厅口径校核后导入", 100, new[]
        {
            ("01", "在读"),
            ("02", "休学"),
            ("03", "复学"),
            ("04", "转出"),
            ("05", "转入"),
            ("06", "毕业"),
            ("07", "结业"),
            ("08", "退学"),
            ("09", "辍学"),
            ("10", "开除"),
            ("11", "死亡"),
            ("12", "其他")
        });

        await AddAsync("CHANGE_TYPE", "学生异动类型码", "待与省厅口径校核后导入", 110, new[]
        {
            ("01", "转入"),
            ("02", "转出"),
            ("03", "休学"),
            ("04", "复学"),
            ("05", "退学"),
            ("06", "辍学"),
            ("07", "开除"),
            ("08", "死亡"),
            ("09", "毕业"),
            ("10", "结业"),
            ("11", "升学"),
            ("12", "跳级"),
            ("13", "留级"),
            ("14", "失踪"),
            ("15", "其他")
        });

        await AddAsync("HOUSEHOLD", "户口性质码", "待校核", 120, new[]
        {
            ("1", "农业户口"), ("2", "非农业户口"), ("3", "居民户口"), ("9", "其他")
        });

        await AddAsync("HEALTH", "健康状况码", "待校核", 130, new[]
        {
            ("1", "健康"), ("2", "一般"), ("3", "较差")
        });

        await AddAsync("DISABILITY", "残疾类别码", "待按标准导入", 140, new[]
        {
            ("1", "视力残疾"),
            ("2", "听力残疾"),
            ("3", "言语残疾"),
            ("4", "肢体残疾"),
            ("5", "智力残疾"),
            ("6", "精神残疾"),
            ("7", "多重残疾"),
            ("9", "其他残疾")
        });

        await AddAsync("LEFT_BEHIND", "留守儿童类型码", "待校核", 150, new[]
        {
            ("1", "双亲外出"), ("2", "单亲外出"), ("3", "其他")
        });

        await AddAsync("MIGRANT", "随迁子女类型码", "待校核", 160, new[]
        {
            ("1", "进城务工人员随迁子女"), ("2", "其他随迁子女")
        });

        // ---- 就学类 ----
        await AddAsync("ENROLL_TYPE", "入学方式码", "待校核", 200, new[]
        {
            ("1", "正常入学"), ("2", "就近入学"), ("3", "择校入学"), ("4", "转入"), ("9", "其他")
        });

        await AddAsync("STUDY_MODE", "就读方式码", "待校核", 210, new[]
        {
            ("1", "走读"), ("2", "住校"), ("3", "半住校")
        });

        await AddAsync("SCHOOL_LENGTH", "学制码", "待校核", 220, new[]
        {
            ("6", "六年制"), ("5", "五年制"), ("4", "四年制"), ("3", "三年制")
        });

        await AddAsync("GRADE", "年级码", "待校核", 230, new[]
        {
            ("01", "一年级"),
            ("02", "二年级"),
            ("03", "三年级"),
            ("04", "四年级"),
            ("05", "五年级"),
            ("06", "六年级"),
            ("07", "七年级"),
            ("08", "八年级"),
            ("09", "九年级"),
            ("10", "高一"),
            ("11", "高二"),
            ("12", "高三")
        });

        await AddAsync("GRADUATE_DEST", "毕业去向码", "待校核", 240, new[]
        {
            ("1", "升学"), ("2", "就业"), ("3", "参军"), ("4", "待业"), ("9", "其他")
        });

        // ---- 学校类 ----
        await AddAsync("SCHOOL_RUN_TYPE", "学校办别码", "待校核", 300, new[]
        {
            ("1", "公办"), ("2", "民办"), ("3", "中外合作办学"), ("9", "其他")
        });

        await AddAsync("SCHOOL_TYPE", "学校类型码", "待校核", 310, new[]
        {
            ("1", "小学"),
            ("2", "初中"),
            ("3", "九年一贯制学校"),
            ("4", "完全中学"),
            ("5", "普通高中"),
            ("6", "十二年一贯制学校"),
            ("9", "其他")
        });

        await AddAsync("URBAN_RURAL", "城乡码", "待校核", 320, new[]
        {
            ("1", "城区"), ("2", "镇区"), ("3", "乡村")
        });

        // ---- 系统内置类（不可删除）----
        await AddAsync("ORG_TYPE", "组织机构类型码", "系统内置", 400, new[]
        {
            ("Province", "省级教育行政部门"),
            ("City", "市级教育行政部门"),
            ("County", "县级教育行政部门"),
            ("School", "学校")
        }, isSystem: true);

        await AddAsync("DATA_SCOPE", "数据范围码", "系统内置", 410, new[]
        {
            ("All", "全部数据"),
            ("OrgAndChildren", "本级及下级"),
            ("OrgOnly", "仅本级"),
            ("SelfOnly", "仅本人"),
            ("Custom", "自定义组织集合")
        }, isSystem: true);

        await AddAsync("ACTION_TYPE", "操作类型码", "系统内置", 420, new[]
        {
            ("Query", "查看"),
            ("Create", "添加"),
            ("Update", "修改"),
            ("Delete", "删除"),
            ("Import", "导入"),
            ("Export", "导出"),
            ("Print", "打印"),
            ("Approve", "审核"),
            ("RevokeApprove", "撤销审核"),
            ("Upload", "上传"),
            ("Download", "下载")
        }, isSystem: true);

        await AddAsync("APPROVE_LEVEL", "审核级别码", "系统内置", 430, new[]
        {
            ("School", "校级"),
            ("County", "县区级"),
            ("City", "地市级"),
            ("Province", "省级")
        }, isSystem: true);

        await AddAsync("FLOW_TASK_STATUS", "流程任务状态码", "系统内置", 440, new[]
        {
            ("Pending", "待处理"),
            ("Approved", "已通过"),
            ("Rejected", "已驳回"),
            ("Skipped", "已跳过"),
            ("Merged", "已合并"),
            ("Revoked", "已撤销")
        }, isSystem: true);

        await AddAsync("CHANGE_APPLY_STATUS", "异动申请状态码", "系统内置", 450, new[]
        {
            ("Draft", "草稿"),
            ("Submitted", "待审核"),
            ("InProgress", "审核中"),
            ("Approved", "已通过"),
            ("Rejected", "已驳回"),
            ("Withdrawn", "已撤回"),
            ("Archived", "已归档")
        }, isSystem: true);

        await AddAsync("PHOTO_STATUS", "照片状态码", "系统内置", 460, new[]
        {
            ("Pending", "待处理"),
            ("Normal", "正常"),
            ("FallbackPending", "降级待补偿"),
            ("Failed", "处理失败")
        }, isSystem: true);

        // ---- 行政区划（树形，只建类别，数据由初始化脚本导入）----
        await AddAsync(
            "ADMIN_DIVISION",
            "行政区划码",
            "GB/T 2260（树形；河南省数据由 deploy/initdb 初始化脚本导入）",
            500,
            isTree: true);
    }

    /// <summary>
    /// 幂等新增一个码类别及其码项。
    /// 已存在的类别不重复创建；码项按 (类别, 码值) 判重；
    /// 标准码值默认与内部码值相同。
    /// </summary>
    private async Task AddAsync(
        string code,
        string name,
        string? stdSource,
        int sort,
        (string Code, string Name)[]? items = null,
        bool isTree = false,
        bool isSystem = false)
    {
        var category = await _categories.FindAsync(c => c.Code == code);

        if (category == null)
        {
            category = new SysCodeCategory(
                _guidGenerator.Create(),
                code,
                name,
                stdSource,
                isTree,
                isSystem,
                sort);

            await _categories.InsertAsync(category, autoSave: true);
        }

        if (items == null || items.Length == 0)
        {
            return;
        }

        var existingCodes = (await _items.GetListAsync(i => i.CategoryId == category.Id))
            .Select(i => i.Code)
            .ToHashSet(StringComparer.Ordinal);

        var order = 0;
        foreach (var (itemCode, itemName) in items)
        {
            order += 10;

            if (existingCodes.Contains(itemCode))
            {
                continue;
            }

            var item = new SysCodeItem(
                _guidGenerator.Create(),
                category.Id,
                itemCode,
                itemName,
                order,
                parentId: null,
                pathCode: itemCode,
                treeLevel: 1,
                stdCode: itemCode);

            await _items.InsertAsync(item, autoSave: true);
        }
    }
}
