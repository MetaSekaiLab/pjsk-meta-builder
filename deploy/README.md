# 部署

镜像：`ghcr.io/metasekailab/pjsk-meta-builder`（CI 在 main 分支和 `v*` 标签上构建，amd64 + arm64）。
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

[k8s.yaml](k8s.yaml) 是通用参考；用面板部署时按同样的镜像、环境变量和资源设置填写。
