# 安全说明

## 报告安全问题

如果你发现了安全漏洞，请**不要**直接开公开 Issue。
请通过仓库的「Security → Report a vulnerability」私密报告，或在 Issue 中只描述
"存在某类问题、希望私下沟通"，不要附带可复现的攻击细节。
我们会在确认后尽快修复并在 CHANGELOG 中致谢。

## 仓库里的口令都是「本地开发用」，不是生产密钥

本仓库为了让任何人都能一条命令跑起来，把**本地开发环境**的口令写在了配置里：

| 位置 | 内容 | 用途 |
|---|---|---|
| `deploy/docker-compose.dev.yml` | PostgreSQL / Redis / RabbitMQ 的开发口令 | 只监听本机，容器仅用于本地开发 |
| `src/Xjgl.HttpApi.Host/appsettings.json` | 数据库连接串 | 本地默认值 |
| `src/Xjgl.DbMigrator/appsettings.json` | 数据库连接串 | 本地默认值 |
| `test/Xjgl.TestBase/XjglTestConfiguration.cs` | 测试库连接串 | 可用环境变量 `XJGL_TEST_CONNECTION` 覆盖 |

内置管理员初始密码 `1q2w3E*` 同理，来自 ABP 的默认种子。

**这些值在生产环境必须全部替换**，且不应以明文出现在任何提交进仓库的文件里。

## 生产部署的安全清单

### 必须做

- [ ] **改掉内置管理员密码**，并删除或停用不需要的内置账号。
- [ ] **数据库 / Redis / RabbitMQ 口令**通过环境变量或密钥管理服务注入，
      不要写进 `appsettings.json`。
      推荐做法：把敏感项放进 `appsettings.Secrets.json`（已在 `.gitignore` 中），
      或直接使用环境变量——.NET 的配置系统支持
      `ConnectionStrings__Default` 这种双下划线形式。
- [ ] **启用 HTTPS**，并**移除**开发环境的两处放宽配置：
      - `XjglHttpApiHostModule` 中 `DisableTransportSecurityRequirement()`
        （它在开发环境关闭了"令牌端点必须走 HTTPS"的强制校验）；
      - `appsettings.json` 中 `AuthServer:RequireHttpsMetadata`。
- [ ] **替换 OpenIddict 签名/加密证书**：开发环境用的是进程内临时密钥
      （重启即失效）。生产必须提供 `openiddict.pfx` 并通过配置注入口令；
      `*.pfx` 已在 `.gitignore` 中，不会被提交。
- [ ] **配置 CORS 白名单**为真实前端域名（`App:CorsOrigins`）。
- [ ] **限制数据库暴露面**：数据库端口不要对公网开放。
- [ ] **照片 NAS 的访问控制**：NAS 只对本系统所在网段开放写权限，
      并关闭匿名访问。

### 建议做

- [ ] 打开审计日志并接入集中日志（仓库已内置 Serilog + Seq 的编排）。
- [ ] 为学生身份证等敏感字段启用 ABP 的加密设置项，
      并确认 `IStringEncryptionService` 的密钥来自配置而非默认值。
- [ ] 对照片上传接口加限流（当前靠舱壁与熔断保护，未做令牌桶）。
- [ ] 定期备份数据库与 NAS，并演练恢复。

## 已知的安全相关设计说明

- **身份证号不以明文存库**：`stu_student` 只存加密值与摘要（`id_card_hash`），
  查重走摘要；接口返回的是脱敏串。
- **权限校验只在后端**：前端按权限码隐藏按钮**只是体验优化**，
  真正的拦截在应用服务的 `[Authorize]` 上。
- **排序参数不允许拼进 SQL**：仓储只接受实体上真实存在的属性名排序，
  非法字段返回业务错误而不是把用户输入拼进 SQL。
- **照片写入路径有目录穿越防护**：解析后的绝对路径必须落在存储根目录内。

## 依赖漏洞

仓库启用了 NuGet 的漏洞告警。当前已知一条：

- `AutoMapper 14.0.0`（ABP 传递依赖）命中 `GHSA-rvv3-g6hj-g44x`，
  升级到修复版本前需评估与 ABP 的兼容性。
