# 学籍管理系统 —— 前端工作区

Vue 3 + TypeScript + Element Plus，pnpm 工作区，一套代码支撑 PC / H5 / 小程序三端。

```
web/
├── apps/
│   └── pc/            PC 管理端（Vue3 + Element Plus + Vite）
├── packages/          预留：跨端共享的类型、字典、请求封装
├── pnpm-workspace.yaml
└── package.json       工作区脚本
```

## 快速开始

```bash
# 1) 安装依赖（Node ≥ 20）
corepack pnpm@9.15.0 install

# 2) 启动开发服务器（默认 http://127.0.0.1:5173）
corepack pnpm@9.15.0 --filter @xjgl/pc dev

# 3) 生产构建 + 类型检查
corepack pnpm@9.15.0 --filter @xjgl/pc run build
corepack pnpm@9.15.0 --filter @xjgl/pc run typecheck
```

后端默认跑在 `http://localhost:44300`。开发时前端通过 **Vite 代理**把 `/api` 与 `/connect`
转发过去，所以后端不需要为开发环境放宽 CORS，前端代码里也一律用相对路径 ——
生产环境前后端同域部署（全省统一入口，nginx 同一域名下 `/` 与 `/api`），
**不存在「开发能跑、上线 404」的切换成本**。

要指向别的后端，复制 `apps/pc/.env.example` 为 `apps/pc/.env.development.local` 并改
`VITE_API_BASE_URL`。

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

- `packages/shared`：把 API 类型与请求封装抽出来，供 H5 与小程序复用
- `apps/h5`：手机端（教师用，主要场景是异动审核与拍照上传）
- `apps/mp`：微信小程序（自定义授权类型 `urn:wechat:miniprogram`）
- Playwright 端到端用例（登录 → 学生列表 → 异动审批 → 照片上传）
