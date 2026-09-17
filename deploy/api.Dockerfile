# 河南省学籍管理系统 —— API 镜像（多阶段构建）
#
# 构建（在仓库根目录执行，因为要 COPY 多个项目）：
#   docker build -f deploy/api.Dockerfile -t xjgl/api:latest .
#
# 镜像里同时装了两样东西，**都不是可选项**：
#   1. 中文字体（fonts-noto-cjk）：学籍卡与花名册的 PDF 要靠它画中文。
#      本项目刻意不做"缺字体就画方块"的降级（纸质件上出现方块无法接受），
#      而是让渲染直接失败并提示"服务器缺少中文字体"——所以字体必须随镜像装好。
#   2. curl：容器 healthcheck 要用（见 compose 里的 api.healthcheck）。
#
# 生僻字字体（Plangothic 等）体积大、授权各异，**不进仓库也不进镜像**，
# 由部署时挂载到 /app/fonts/rare（compose 里已配好挂载点）。

# ---------------------------------------------------------------------------
# 构建阶段
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只拷贝构建脚本与项目文件：这一层能命中缓存，改代码不会重新还原依赖
COPY Directory.Build.props global.json nuget.config ./
COPY src/ ./src/

RUN dotnet publish src/Xjgl.HttpApi.Host/Xjgl.HttpApi.Host.csproj \
        -c Release -o /app/api /p:UseAppHost=false

# 迁移工具也打进同一个镜像：生产上"初始化数据库"应当是
# `docker compose run --rm migrate` 一条命令，而不是让运维去装 SDK
RUN dotnet publish src/Xjgl.DbMigrator/Xjgl.DbMigrator.csproj \
        -c Release -o /app/migrator /p:UseAppHost=false

# ---------------------------------------------------------------------------
# 运行阶段
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-noto-cjk curl \
    && rm -rf /var/lib/apt/lists/* \
    && fc-cache -f

WORKDIR /app

COPY --from=build /app/api ./api
COPY --from=build /app/migrator ./migrator

# 生僻字字体挂载点（默认空目录：缺失时 QuestPDF 自动回落，不报错）
RUN mkdir -p /app/fonts/rare /data/photo-fallback

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    # 容器里没有 Windows 字体，默认字体族必须换成 Linux 上的 Noto CJK；
    # 生僻字仍从 /app/fonts/rare 里找
    Xjgl__Print__DefaultFontFamily="Noto Sans CJK SC" \
    Xjgl__Print__UseSystemFonts="true" \
    Xjgl__Print__RareCharacterFontDirectory="/app/fonts/rare"

EXPOSE 8080

# ENTRYPOINT 用 dotnet 而 CMD 指定 dll：这样 compose 里可以
# `command: ["dotnet", "/app/migrator/Xjgl.DbMigrator.dll"]` 复用同一个镜像跑迁移
ENTRYPOINT ["dotnet"]
CMD ["/app/api/Xjgl.HttpApi.Host.dll"]
