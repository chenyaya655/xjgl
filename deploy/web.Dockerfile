# 河南省学籍管理系统 —— PC 端静态资源镜像（多阶段构建）
#
# 构建（在仓库根目录执行）：
#   docker build -f deploy/web.Dockerfile -t xjgl/web:latest .
#
# 前端调用的是**同源相对路径**（`/api/app/...`、`/connect/token`），
# 因此不需要注入任何 VITE_* 环境变量 —— 由 nginx 把 /api 与 /connect 反代到 API 容器。

# ---------------------------------------------------------------------------
# 构建阶段
# ---------------------------------------------------------------------------
FROM node:20-bookworm-slim AS build
WORKDIR /src

# corepack 让 pnpm 版本跟着 web/package.json 的 packageManager 走，避免"本机能装、CI 装不上"
RUN corepack enable

COPY web/ ./

# 用锁文件安装：镜像可复现，也不会因为依赖漂移导致"昨天能构建今天不行"
RUN corepack pnpm@9.15.0 install --frozen-lockfile

RUN corepack pnpm@9.15.0 --filter @xjgl/pc run build

# ---------------------------------------------------------------------------
# 运行阶段
# ---------------------------------------------------------------------------
FROM nginx:1.27-alpine

COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/apps/pc/dist /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q --spider http://localhost/ || exit 1
