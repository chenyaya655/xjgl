namespace Xjgl.Entities;

/// <summary>
/// 业务实体标记接口。
///
/// 所有由 SqlSugar CodeFirst 建表的业务实体都要实现它；
/// DbMigrator 扫描这些类型并自动建表 / 加字段（幂等）。
///
/// 框架表（AbpUsers / AbpAuditLogs ...）不实现该接口，由 EF Core 迁移负责。
///
/// 注意：领域层**不引用任何 ORM**。
/// 表名、列名、主键、可空性、长文本列等映射规则，全部集中在
/// <c>Xjgl.SqlSugarCore</c> 的 <c>SqlSugarClientProvider</c> 里按约定推导，
/// 因此业务实体上没有任何 SqlSugar 特性。
/// </summary>
public interface IXjglBusinessEntity
{
}
