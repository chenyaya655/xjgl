namespace Xjgl.SqlSugarCore;

/// <summary>
/// 业务实体标记接口。
/// 所有由 SqlSugar CodeFirst 建表的业务实体都要实现它，
/// DbMigrator 会扫描这些类型并自动建表 / 加字段（幂等）。
///
/// 框架表（AbpUsers / AbpAuditLogs ...）不实现该接口，由 EF Core 迁移负责。
/// </summary>
public interface IXjglBusinessEntity
{
}
