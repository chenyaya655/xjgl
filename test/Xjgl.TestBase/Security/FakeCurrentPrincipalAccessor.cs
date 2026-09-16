using System.Security.Claims;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Security.Claims;

namespace Xjgl;

/// <summary>
/// 可变的当前主体访问器，供集成测试模拟「已登录用户」。
///
/// ABP 默认用 <c>ThreadCurrentPrincipalAccessor</c>（读线程的 ClaimsPrincipal），
/// 在集成测试里没法直接设置。这里注册一个可写的实现，测试里调用
/// <see cref="SetUser"/> 即可让 <c>ICurrentUser</c> 认为已登录。
/// </summary>
public class FakeCurrentPrincipalAccessor : ICurrentPrincipalAccessor, ISingletonDependency
{
    private ClaimsPrincipal _principal = new(new ClaimsIdentity());

    public ClaimsPrincipal Principal => _principal;

    public IDisposable Change(ClaimsPrincipal principal)
    {
        var previous = _principal;
        _principal = principal;
        return new ActionDisposable(() => _principal = previous);
    }

    /// <summary>模拟某个用户登录。</summary>
    public void SetUser(Guid userId, string userName, params string[] roles)
    {
        var claims = new List<Claim>
        {
            // 注意：ABP 的 AbpClaimTypes 默认用 JWT 风格的名字（sub / preferred_username / role）
            new(AbpClaimTypes.UserId, userId.ToString()),
            new(AbpClaimTypes.UserName, userName)
        };

        claims.AddRange(roles.Select(role => new Claim(AbpClaimTypes.Role, role)));

        _principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
    }

    /// <summary>清除登录状态。</summary>
    public void Clear()
    {
        _principal = new ClaimsPrincipal(new ClaimsIdentity());
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _action;

        public ActionDisposable(Action action) => _action = action;

        public void Dispose() => _action();
    }
}
