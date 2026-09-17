# 学籍管理系统 —— 前端工作区

Vue 3 + TypeScript + Element Plus，pnpm 工作区，一套代码支撑 PC / H5 / 小程序三端。

```
web/
├── apps/
│   ├── pc/            PC 管理端（Vue3 + Element Plus + Vite，5173）
│   └── h5/            移动端 H5（教师用：待办审核、拍照上传，5174）
├── packages/
│   └── api/           三端共享的 API 层：请求封装、类型、权限码常量（@xjgl/api）
├── pnpm-workspace.yaml
└── package.json       工作区脚本
```

### 为什么 API 层要抽成包

`packages/api` 里是 `http.ts`（令牌续期、错误信封解包）、`types.ts`（约 770 行的 DTO）、
`permissions.ts`（权限码常量）与各业务模块接口。

抽包而不是各端复制的理由很直接：**类型一旦两端各存一份，后端改一个字段就会"改了 A 忘了 B"**，
而这类不一致的表现是运行期 `undefined` —— 前端只是静默显示空白，不报错、不崩溃，最难排查。

PC 端保留了 `src/api/*.ts` 三个薄壳（`export * from '@xjgl/api'`），
这样 PC 端十几处 `from '@/api'` 一行都不用改 —— 大规模改导入路径的风险远大于收益。
H5 是新应用，直接 `from '@xjgl/api'`，不留壳。

## 快速开始

```bash
# 1) 安装依赖（Node ≥ 20）
corepack pnpm@9.15.0 install

# 2) 启动开发服务器
corepack pnpm@9.15.0 --filter @xjgl/pc dev     # PC 端 http://127.0.0.1:5173
corepack pnpm@9.15.0 --filter @xjgl/h5 dev     # H5 端 http://127.0.0.1:5174

# 3) 生产构建 + 类型检查（`-r` 表示所有工作区项目）
corepack pnpm@9.15.0 -r typecheck
corepack pnpm@9.15.0 -r build

# 4) 端到端测试（Playwright，会自动拉起后端与 PC 端 dev server）
corepack pnpm@9.15.0 exec playwright install chromium   # 首次需要
corepack pnpm@9.15.0 test:e2e                            # 全套约 1 分钟
corepack pnpm@9.15.0 test:e2e:headed                     # 看着它点，排查用
```

> **端到端测试（`e2e/`）为什么值得单独一层**：集成测试直接调应用服务，
> 它证明不了"前端有没有按真实路径与动词调接口""按钮在权限与路由下是否真的可点"
> "页面在真实数据量下还活着吗"。本项目第一次跑通 E2E 就抓出三个集成测试与类型检查
> 都看不见的缺陷（刷新深链 404、导出按钮 400、学校下拉把 6,789 所学校全渲染成 DOM
> 导致页面假死 100 秒）。用例里刻意留了一条**与数据量无关**的守卫：
> 下拉选项的 DOM 节点数必须少于 200（即"必须走虚拟滚动"）。
> 详见 `e2e/playwright.config.ts` 与 `e2e/specs/support/auth.ts` 的注释。

> 在 CI 或脚本里跑安装时注意：`pnpm install` 在检测到 `node_modules` 结构不一致时会**交互式询问**
> 是否重建，非交互环境下会直接退出且**什么都不装**。加 `CI=true` 让它自动确认；
> 但 `CI=true` 同时隐含 `--frozen-lockfile`，改动依赖时需要
> `pnpm install --no-frozen-lockfile`（否则报 `ERR_PNPM_OUTDATED_LOCKFILE`）。

后端默认跑在 `http://localhost:44300`。开发时前端通过 **Vite 代理**把 `/api` 与 `/connect`
转发过去，所以后端不需要为开发环境放宽 CORS，前端代码里也一律用相对路径 ——
生产环境前后端同域部署（全省统一入口，nginx 同一域名下 `/` 与 `/api`），
**不存在「开发能跑、上线 404」的切换成本**。

要指向别的后端，复制 `apps/pc/.env.example` 为 `apps/pc/.env.development.local` 并改
`VITE_API_BASE_URL`。

## 移动端 H5（apps/h5）

面向**教师**的两个高频场景，只做这两件事：**待办审核**与**拍照上传**。

| 页签 | 内容 |
|---|---|
| 待办 | 我的待办（`/api/app/change-flow/todo-list`）、卡片式大按钮、通过/驳回、加载更多、申请详情（流程时间线） |
| 拍照上传 | 输入学籍辅号 → **人工确认学生姓名** → 调起摄像头拍照 → 上传 → 如实显示结果（含"降级暂存"） |
| 我的 | 身份、单位、角色、已授权限数、退出登录 |

### 与 PC 端刻意不同的四个决定

1. **不做动态路由，固定三个页签** —— PC 上模块会增删、菜单要跟着变；
   而移动端只承载两三个高频场景，把它做成"什么都能进"反而让教师在手机上面对一屏看不懂的菜单。
2. **待办卡片审完即从列表移除** —— 留在列表里会让教师怀疑"是不是没点上"。
3. **拍照上传一次只处理一个学生，且必须人工确认姓名** —— PC 是"拖 50 个文件按文件名批量匹配"，
   手机是在学校现场对着一个学生拍；照片装错人是很难发现的错误。
4. **用 `createWebHashHistory`（hash 路由）** —— H5 常挂在子路径下，
   history 模式漏配一条 nginx rewrite 的表现就是"能打开首页、一刷新 404"；hash 模式不依赖服务端配置。
   同时 `base: './'`（相对路径），避免挂在 `/xjgl/h5/` 下时资源 404。

构建目标也调低到 `es2015`：教师手机型号很杂，仍有 Android 7/8 的老机器，
默认的 `esnext` 会让老 WebView 直接白屏 —— 这类问题在开发机上永远看不到。

### 已知待优化

- **首屏体积偏大**：完整引入 Element Plus 使 `element` 分包约 925KB（gzip 后仍约 260KB）。
  已按 vue / element / 业务分包（业务代码仅 67KB，改动能被浏览器缓存），
  但要真正压下去得改成**按需引入**（`unplugin-vue-components`），那会动构建链，留作后续。
- **组件库选型**：H5 沿用 Element Plus 而不是 Vant。取舍是"一套组件 API、一套经验"对政务小团队更划算，
  且当前界面复杂度低；若将来 H5 要做成面向家长的高频应用，应换成 Vant。
- **微信小程序未做**：小程序不能用 Vue3 + Element Plus 直接构建，需要 uni-app/Taro 重写壳，
  且没有微信开发者工具无法验证，因此单独立项（见方案文档 P5）。

## 登录与默认账号

后端用 OpenIddict 的**密码模式**（`POST /connect/token`）。
本项目是全省统一部署的自有系统，登录页就是自己的页面，不需要授权码那两次跳转；
授权码流程留给后续对外 SSO（学分系统等）的场景。

开发环境首次初始化后，内置管理员为 `admin` / `1q2w3E*`（注意大写 `E`）。

## 菜单与路由为什么是「动态」的

本系统的「模块管理」是一个**业务功能**：管理员可以新增模块、改路径、改图标、停用模块。
因此前端**不写死路由表**，而是：

1. 登录成功后调 `GET /api/app/my-profile`，一次拿到身份、组织、角色、**权限码**、**可见菜单树**；
2. 用菜单树注册动态路由（`src/router/index.ts` 的 `registerDynamicRoutes`）；
3. 左侧菜单直接渲染这棵树；按钮显隐用权限码（`src/constants/permissions.ts`）。

`component` 字段的约定是 `student/list/index` → `src/views/student/list/index.vue`。
**解析不到时不会报错**，而是回落到 `src/views/Placeholder.vue`：
模块是运行期数据，管理员完全可能先建好模块、再等开发补页面，
这时让用户看到「该模块尚未实现」远好过一个白屏。

## 关键实现说明

| 位置 | 解决的坑 |
|---|---|
| `src/api/http.ts` | 令牌过期自动续期。并发 401 只触发**一次**刷新（共享 Promise 去重）—— 否则一个页面 5 个并发请求会同时刷新，后 4 次因 refresh_token 已被旋转而失败，表现成「随机掉线」 |
| `src/api/http.ts` | ABP 错误信封解包。直接弹 `err.message` 只能得到 "Request failed with status code 403"，这里按 `validationErrors → details → message` 的优先级取能给用户看的话 |
| `src/views/photo/list/index.vue` | 批量上传**先匹配再上传**：文件与学生的对应关系先让用户确认，避免张冠李戴；匹配时先用「缺照片名单」建索引（1 次请求），未命中的再逐个精确查 |
| `src/views/photo/list/index.vue` | 「降级暂存」如实展示为 `已暂存（待补传）`，**不谎报上传成功** |
| `src/styles/index.css` | 生僻字字体用 `unicode-range` 指向 CJK 扩展区，浏览器只下载真正用到的分片 |

## 生僻字字体（必须手动放置）

姓名里的生僻字（CJK 扩展 B 及以上）常见系统字体都不包含，会显示成方块。
请把字体文件放到 `apps/pc/public/fonts/` 下：

```
public/fonts/PlangothicP1-Regular.woff2   # 扩展 B~F
public/fonts/PlangothicP2-Regular.woff2   # 扩展 G/H/I
```

字体文件体积大，**不进代码仓库**。缺失时 `@font-face` 会静默失败，
界面回落到系统字体 —— 生僻字显示为方块，但功能不受影响。

## 已知环境限制

本仓库的开发沙箱禁止 Node 派生带管道的子进程，而 **Vite 必须派生 esbuild**，
因此 `vite build` / `vite dev` 在此环境需要放宽执行权限才能运行。
在普通开发机上没有这个限制。

## 下一步

- `packages/shared`：把 API 类型与请求封装抽出来，供 H5 与小程序复用（已完成大半，见 `packages/api`）
- `apps/h5`：手机端（教师用，主要场景是异动审核与拍照上传）
- `apps/mp`：微信小程序（自定义授权类型 `urn:wechat:miniprogram`）
- ~~Playwright 端到端用例~~ **已补上**（`e2e/`，27 个用例，约 1 分钟跑完）；
  还没覆盖的是 **H5 端**、**非 Chromium 内核**与 **6 张学生子表的界面增删改**
