# 部署

镜像：`ghcr.io/metasekailab/pjsk-meta-builder`。main 分支由 `deploy.yml` 构建，标签为 12 位 commit sha 和 `main`（amd64）；`v*` 标签由 `ci.yml` 构建版本号和 `latest`（amd64 + arm64）。
默认命令 `serve`，监听 `PORT`（默认 8080），只往 `/tmp` 写。

## 必需的 Secret

| 变量 | 说明 |
| --- | --- |
| `MOE_HMAC_SECRET` | 与同步服务共用，至少 32 个字符 |
| `GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY` | GitHub App `moesekai-bot`；token 只申请产物仓库一个 |
| `QOOAPP_TOKEN` | jp、en、hmt、kr 下载 |

可选：`APP_META_REPO`（默认 `StarMoe-org/moesekai-app-meta`）、`QOOAPP_USER_ID`、`QOOAPP_DEVICE_ID`、`PJSK_SELFTEST=1`（启动后跑一次出网自检）。

`GITHUB_APP_PRIVATE_KEY` 可以填原样 PEM、把换行写成 `\n` 的 PEM，或整个 PEM 文件的 base64（面板不支持多行时用这种）：

```sh
base64 < app.pem | tr -d '\n'
```

## 资源

- 内存：cn 构建实测峰值约 2.2GB（含 Il2CppDumper 子进程），上限设 3Gi；同一时间只跑一个任务。
- 临时存储：任务目录在 `/tmp`，峰值约 2GB（cn 安装包 + 解密文件 + DummyDll），任务结束删除。
- 单副本：任务状态只在内存里。

## 出网

需要能访问：`api.github.com`、`api.qoo-app.com`、`*.qoo-apk.com`、`pjskyy.ugurl.cn` → `ugapk.com` → `*.ugapk.cn`。

## 自检

设 `PJSK_SELFTEST=1` 启动后，日志里会逐项打印 `自检 OK` / `自检 FAIL`：Il2CppDumper、工作目录、QooApp（jp/en/hmt/kr）、cn 官网链接、GitHub App。
每一项只查接口或取 1 个字节，不下载安装包、不写产物仓库。`/healthz` 里的 `selfTest` 字段给出通过数和失败项。
也可以在容器里手动跑：`dotnet /opt/app/pjsk-meta-builder.dll selftest`。

```sh
curl http://pjsk-meta-builder.apps.svc.cluster.local:8080/healthz
```

其余接口需要签名，见设计文档 §7。

## 首次创建（kubectl）

[k8s.yaml](k8s.yaml) 里是 Deployment 和 Service，标签和命名按 Zeabur 面板的约定，建好后面板能把它们识别成一个服务，可以正常改变量、看日志、回滚。

1. 准备一个 env 文件，一行一个 `KEY=VALUE`，不要加引号。`GITHUB_APP_PRIVATE_KEY` 填单行 base64。用完删掉这个文件。
2. 创建 Secret 和服务。**用 `create`，不要用 `apply`**：`apply` 会在对象上留下 `last-applied-configuration` 注解，Secret 的值会以明文存在注解里；以后再 `apply` 一次，还会把 Ferry 和面板改过的东西覆盖回去。

```sh
kubectl -n apps create secret generic pjsk-meta-builder-env --from-env-file=builder.env
kubectl -n apps label secret pjsk-meta-builder-env app.kubernetes.io/name=pjsk-meta-builder
# 先把 k8s.yaml 里的镜像标签换成一个已存在的 12 位 sha 标签
kubectl -n apps create -f k8s.yaml
kubectl -n apps rollout status deploy/pjsk-meta-builder
kubectl -n apps logs deploy/pjsk-meta-builder | grep 自检
```

也可以不落文件：在本机把这些行通过 ssh 管道传过去，服务器上用 `--from-env-file=/dev/stdin`。

不要用 `--from-file` 建 Secret：文件末尾的换行会一起存进去，`MOE_HMAC_SECRET` 多一个换行，签名就对不上。

建好之后集群里的对象就是准的：镜像由 CI 更新，变量和资源在面板里改，这个文件不再应用到集群。

## 自动更新

和 StarMoe 下其他项目一样，用标准模板 [`deploy.yml`](../.github/workflows/deploy.yml)，不要改它：推送到 main 后构建镜像，再用 `CNB_TOKEN` 运行 infra 仓库里的 `hack/bump.sh` 通知 Ferry。Ferry 会把所有使用这个镜像的工作负载换成新的 12 位 sha 标签，只改镜像，不动环境变量和资源。

这个仓库不在 StarMoe-org 下，继承不到组织级的 `CNB_TOKEN`，需要在仓库设置里单独添加这个 Secret。

想临时固定某个版本：给 Deployment 加注解 `ferry.star.moe/auto-deploy: "false"`。
