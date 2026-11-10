# pjsk-meta-builder

从 Project SEKAI 安装包中提取 App 元数据（appVersion、appHash、密钥，Nuverse 服另有结构文件），
发布到公开产物仓库 `StarMoe-org/moesekai-app-meta`，供 masterdata 同步服务使用。设计见 [docs/design.md](docs/design.md)。

支持地区：jp、en（CP）；hmt、kr、cn（Nuverse）。

## 构建与测试

需要 .NET 10 SDK。

```sh
dotnet build -c Release
dotnet test -c Release
```

用真实安装包跑完整测试（安装包不进仓库）：

```sh
PJSK_SAMPLE_JP_APK=/path/to/pjsk-jp-7.1.0-original.apk \
PJSK_SAMPLE_CN_APK=/path/to/cn-6.4.1.apk \
PJSK_IL2CPPDUMPER_DLL=/path/to/Il2CppDumper.dll \
dotnet test -c Release
```

## 命令行

```sh
CLI="dotnet src/PjskMetaBuilder.Cli/bin/Release/net10.0/pjsk-meta-builder.dll"

# 只读：清单、身份、密钥的 SHA-256 前缀（不打印密钥）
$CLI inspect --apk base.apk [--apk split.apk …]

# 只下载（cn 走官网链接，其余走 QooApp）
$CLI fetch --region en [--source qooapp] [--expected-version-code N]

# 生成 meta.json、moe-info.json（Nuverse 另有 schema.json）；不给 --apk 时自动下载
$CLI build --region cn --out ./out [--apk base.apk …] [--expected-version-code N] \
    --il2cppdumper /path/to/Il2CppDumper.dll [--dotnet /path/to/dotnet]

# 发布到产物仓库（GitHub API 提交）；--dry-run 只判定不写入
$CLI publish --region cn --dir ./out [--dry-run]

# build + publish
$CLI run --region cn --out ./out --il2cppdumper … [--dry-run]

# HTTP 服务（设计文档 §7）
$CLI serve [--listen http://0.0.0.0:8080]
```

退出码：0 成功；75 网络错误或下载源未就绪（可重试）；65 数据错误；64 参数错误；78 凭据/配置问题；70 内部错误。

### 环境变量

| 变量 | 用途 |
| --- | --- |
| `GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY`（或 `_FILE`） | 发布到产物仓库（`--dry-run` 不需要） |
| `QOOAPP_TOKEN`（或 `QOOAPP_TOKEN_FILE`） | jp、en、hmt、kr 下载 |
| `MOE_HMAC_SECRET`（或 `_FILE`） | `serve`：与同步服务双向签名，至少 32 个字符 |
| `IL2CPPDUMPER_DLL`、`PJSK_DOTNET` | Il2CppDumper 位置和运行它的 dotnet |
| `APP_META_REPO` | 产物仓库，默认 `StarMoe-org/moesekai-app-meta` |
| `PJSK_CACHE_DIR` | 命令行的下载缓存，默认 `~/.cache/pjsk-meta-builder` |
| `PJSK_WORK_ROOT`、`PORT` | `serve` 的任务临时目录和端口 |

## 镜像

`.github/workflows/ci.yml` 在 main 分支和 `v*` 标签上构建 `ghcr.io/metasekailab/pjsk-meta-builder`（amd64 + arm64），
镜像内含固定 commit 的 Il2CppDumper，默认命令 `serve`。部署参考 [deploy/](deploy/)。

## 注意

- `moe-info.json` 含密钥（已确认放进公开产物仓库）。不要把真实安装包、解密后的 metadata、DummyDll、私钥提交到本仓库。
- `tests/PjskMetaBuilder.Tests/Fixtures/` 下的 metadata 样本是随机内容，由 pjsk-reverse-workflow `sekai_metadata.py` 生成，用来保证解密算法与其一致。
