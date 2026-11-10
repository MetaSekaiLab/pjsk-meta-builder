# pjsk-meta-builder

从 Project SEKAI 安装包中提取 App 元数据（appVersion、appHash、密钥，Nuverse 服另有结构文件），
发布到公开产物仓库 `StarMoe-org/moesekai-app-meta`，供 masterdata 同步服务使用。设计见 [docs/design.md](docs/design.md)。

当前进度：M1（CP 服核心与命令行）。

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
dotnet test -c Release
```

## 命令行

```sh
# 只读：清单、身份、密钥的 SHA-256 前缀（不打印密钥）
dotnet src/PjskMetaBuilder.Cli/bin/Release/net10.0/pjsk-meta-builder.dll inspect --apk base.apk [--apk split.apk …]

# 生成 meta.json 和 moe-info.json；--il2cppdumper 可选（M1 只验证能跑通）
dotnet src/PjskMetaBuilder.Cli/bin/Release/net10.0/pjsk-meta-builder.dll build \
    --region jp --apk base.apk --out ./out [--source local] [--expected-version-code N] \
    [--il2cppdumper /path/to/Il2CppDumper.dll] [--dotnet /path/to/dotnet]
```

退出码：0 成功；75 网络错误或下载源未就绪（可重试）；65 数据错误；64 参数错误；70 内部错误。

## 注意

- `moe-info.json` 含密钥（已确认放进公开产物仓库）。不要把真实安装包、解密后的 metadata、DummyDll 提交到本仓库。
- `tests/PjskMetaBuilder.Tests/Fixtures/` 下的 metadata 样本是随机内容，由 pjsk-reverse-workflow `sekai_metadata.py` 生成，用来保证解密算法与其一致。
