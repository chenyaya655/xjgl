using Volo.Abp.DependencyInjection;

namespace Xjgl.Data;

/// <summary>
/// 业务事务范围（**与 ORM 无关**，放在领域层 —— 债 1）。
///
/// 【为什么必须放在 Domain，而不能留在 SqlSugarCore】
/// 它原先定义在 <c>Xjgl.SqlSugarCore</c>，而**应用层不引用那个项目**
/// （分层约定：Application 只依赖 Domain / Domain.Shared / Application.Contracts）。
/// 结果就是：这个能力**存在但没人能用** —— 应用层想给"异动审批"这类多步写加事务时，
/// 既不能引用接口，也没有别的办法，只能让每一步各自自动提交，
/// 于是"学生学籍改了、审批状态没写成"这类半成品状态成为可能。
/// **能力放在调用方够不着的地方，等于没有这个能力。**
///
/// 【为什么用显式事务范围，而不是 ABP 的 [UnitOfWork] 自动事务】
/// 见实现类 <c>Xjgl.SqlSugarCore.SqlSugarTransactionScope</c> 的说明：
/// ABP 的 <c>IUnitOfWork</c> 公开接口不含 <c>TransactionApi</c>
/// （已用**枚举全部成员**的方式核对过，不是"按名字搜不到"），
/// 第三方 ORM 无法把自己的事务接到 UoW 的提交链路上。
///
/// 用法：
/// <code>
/// await _tx.ExecuteAsync(async () =>
/// {
///     await _students.UpdateAsync(student);
///     await _changeLogs.InsertAsync(log);
/// });
/// </code>
/// </summary>
/// <summary>
/// ⚠ 这里**必须**带 <see cref="ITransientDependency"/>。
///
/// 【为什么】ABP 的约定式注册对"实现了 <c>ITransientDependency</c> 的类"默认
/// **只把它注册成它自己**，并不会自动把它注册成它实现的**自定义接口** ——
/// 实测（用容器探针确认过）：
///   解析 <c>SqlSugarTransactionScope</c> 类 → OK
///   解析 <c>IXjglTransactionScope</c> 接口 → "has not been registered"
/// 于是应用层注入接口时直接构造失败，整条审批链路全挂。
///
/// 让**接口本身**继承这个标记接口，ABP 就会为每个实现把该接口也注册进去
/// （这是 ABP 支持的用法：标记接口放在接口上时不产生继承约束）。
///
/// 这个坑值得记住：**"类能被解析"不等于"接口能被解析"** ——
/// 只探实现类会得出"注册没问题"的错误结论。
/// </summary>
public interface IXjglTransactionScope : ITransientDependency
{
    Task<T> ExecuteAsync<T>(Func<Task<T>> action);

    Task ExecuteAsync(Func<Task> action);
}
