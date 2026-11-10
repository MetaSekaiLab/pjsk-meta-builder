# syntax=docker/dockerfile:1
# pjsk-meta-builder 服务镜像（设计文档 §8）：.NET 运行时 + 本服务 + 固定版本的 Il2CppDumper。
# 编译阶段在构建机平台上跑，产物是与架构无关的 DLL（不带 apphost），所以多架构镜像不需要 QEMU 编译。

ARG DOTNET_VERSION=10.0

# ---- 本服务 ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS app
WORKDIR /src
COPY Directory.Build.props PjskMetaBuilder.slnx ./
COPY src/ src/
RUN dotnet publish src/PjskMetaBuilder.Cli/PjskMetaBuilder.Cli.csproj -c Release -o /out \
        -p:UseAppHost=false --nologo

# ---- Il2CppDumper（MetaMikuAI fork，固定 commit；与 pjsk-reverse-workflow sources.json 一致）----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS dumper
ARG IL2CPPDUMPER_REPO=https://github.com/MetaMikuAI/Il2CppDumper.git
ARG IL2CPPDUMPER_REV=4741d46ba9cd6159c5d853eb9d6fc48b4bfa2b1a
RUN git init -q /src && git -C /src fetch -q --depth 1 "$IL2CPPDUMPER_REPO" "$IL2CPPDUMPER_REV" \
    && git -C /src checkout -q --detach FETCH_HEAD \
    && test "$(git -C /src rev-parse HEAD)" = "$IL2CPPDUMPER_REV"
RUN dotnet publish /src/Il2CppDumper/Il2CppDumper.csproj -c Release -f net8.0 -o /out \
        -p:UseAppHost=false --nologo
# 非交互运行：不等待按键
RUN sed -i 's/"RequireAnyKey": *true/"RequireAnyKey": false/' /out/config.json \
    && grep -q '"RequireAnyKey": false' /out/config.json

# ---- 运行时 ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
LABEL org.opencontainers.image.source="https://github.com/MetaSekaiLab/pjsk-meta-builder" \
      org.opencontainers.image.description="Project SEKAI app metadata builder (identity, keys, Nuverse schema)"
COPY --from=app /out /opt/app
COPY --from=dumper /out /opt/il2cppdumper
ENV IL2CPPDUMPER_DLL=/opt/il2cppdumper/Il2CppDumper.dll \
    PJSK_WORK_ROOT=/tmp/pjsk-meta-jobs \
    PORT=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_gcServer=0
# 工作站 GC：单任务串行，省内存（cn 实测峰值约 2.2GB，含 Il2CppDumper 子进程，子进程继承此设置）
# 以镜像自带的非 root 用户运行；只往 /tmp 写（可以挂 emptyDir）
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "/opt/app/pjsk-meta-builder.dll"]
CMD ["serve"]
