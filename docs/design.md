# pjsk-meta-builder 设计文档

> 状态：M1–M5 已实现并部署到 JP2；M6（主动发现新安装包）已实现，待部署
> 代码仓库：`MetaSekaiLab/pjsk-meta-builder`（公开）
> 产物仓库：`StarMoe-org/moesekai-app-meta`（公开）
> 最后更新：2026-11-11

## 1. 背景与目标

masterdata 同步服务（另一个独立项目，Go）需要每个服当前版本的「App 元数据」才能工作：

- CP 服（jp / en）登录要用 appVersion、appHash，所有 API 请求都要用 API 密钥加解密；
- Nuverse 服（hmt / kr / cn）的 master 是数组格式，要用和版本对应的**结构文件**还原字段名，CDN 上的 master 要用客户端里的密钥解密。

这些都只能从安装包里取得，而且每次 App 更新都可能变化。pjsk-meta-builder 负责：**发现新安装包 → 拿到安装包 → 提取元数据 → 发布到公开产物仓库 → 通知同步服务**。

和同步服务的分工（2026-11-11 定，B14）：**跟安装包有关的都归 builder**（来源轮询、下载、凭据、处理、发布）；**跟游戏 API 有关的都归同步服务**（登录、master、数据仓库）。两边只经 `moesekai-app-meta` 和带签名的回调交接。

**第一版目标**

- 地区：jp、cn（en / hmt / kr 以后再加）。
- 先做核心库和命令行，在本机用真实安装包验证稳定；再包装成 HTTP 服务，部署到 JP2。
- 产物发布到 `moesekai-app-meta`，格式见 §4。

**不做的事**

- 不拉取、不处理 master 本身（那是同步服务的事），只在测试中用 master 做核对。
- 不下载、不解包素材资源。
- 不保存任何账号凭据，不调用需要登录的游戏 API。
- 不调用游戏 API：「游戏服务器认为当前版本过期」这类信号由同步服务发现，再经 `POST /v1/builds` 告诉 builder（§7.6）。

## 2. 已定决策

| # | 主题 | 决策 |
| --- | --- | --- |
| B1 | 技术栈 | C# / .NET。Unity 资源用 AssetsTools.NET，读程序集用 Mono.Cecil |
| B2 | Il2CppDumper | 第一版作为**外部进程**调用，固定 MetaMikuAI fork `4741d46`（与 pjsk-reverse-workflow 相同）。以后再考虑替换 |
| B3 | 结构文件生成 | 自己实现，不用 Haruki 生成器；**表名 ↔ 类取自客户端 `SuiteMaster` 的字段** |
| B4 | 地区 | 第一版 jp、cn |
| B5 | 形态 | 核心库 + 命令行 → HTTP 服务，都算第一版 |
| B6 | 下载源 | 第一版：cn 用官网链接，jp 用 QooApp。playfetch、APKPure 以后再加 |
| B7 | 产物仓库 | `StarMoe-org/moesekai-app-meta`，公开；每个版本一个目录 |
| B8 | 更新规则 | appVersion 只前进；同版本不同 appHash 时按 versionCode 自动判断（§4.5） |
| B9 | 密钥 | 放进产物仓库的 `moe-info.json`，0/1 位串，数组 [0]=API 密钥、[1]=ab 密钥 |
| B10 | 写入凭据 | StarMoe-org 的 GitHub App（与同步服务共用），换 token 时**只申请 `moesekai-app-meta`** |
| B11 | 与同步服务的交互 | 同步服务发请求 → 处理 → **先推产物仓库，再回调**；双向 HMAC-SHA256 |
| B12 | 部署 | JP2，与同步服务同一集群，只走内网 |
| B13 | 输出要求 | 不要求和 Haruki 逐字节一致 |
| B14 | 发现新安装包 | **builder 自己轮询**安装包来源（官网 / Google Play 网页 / QooApp），发现新版本就处理并发布，再通知同步服务；同步服务不轮询安装包来源，只在游戏 API 报版本过期或数据对不上时催 builder（§7.5、§7.6） |

## 3. 处理流程

```
输入：地区 + 安装包来源（本地文件 / 官网链接 / QooApp）+ 可选的期望 versionCode
  │
  ├─ 1. 取得安装包（§5.1）        → 一个或多个 APK（主包 + 拆分包），视为一个虚拟文件系统
  ├─ 2. 读清单（§5.1）            → 包名、versionName、versionCode；核对包名和期望的 versionCode
  ├─ 3. 解密 global-metadata（§5.2）
  ├─ 4. 提取身份（§5.3）          → appHash、客户端版本号、dataVersion、abVersion、unityVersion
  ├─ 5. Il2CppDumper（§5.4）      → DummyDll
  ├─ 6. 提取密钥（§5.5）          → moe-info.json
  ├─ 7. 生成结构文件（§5.6，Nuverse 服）→ schema.json
  ├─ 8. 内容检查（§5.7）
  └─ 9. 发布（§5.8）              → 按更新规则提交到 moesekai-app-meta
输出：发布结果（advanced / snapshot_only / noop / rejected）+ commit + 文件 sha256
```

第 1 到第 8 步不需要任何凭据，可以在本机离线跑（`build` 命令）；第 9 步需要 GitHub App（`publish` 命令）。

## 4. 产物格式（与同步服务的契约）

### 4.1 目录结构

```
<region>/latest.json                    # 当前生效版本，内容与该版本的 meta.json 完全相同
<region>/<appVersion>/meta.json         # 身份与安装包信息
<region>/<appVersion>/moe-info.json     # 密钥
<region>/<appVersion>/schema.json       # 结构文件，只有 Nuverse 服有
<region>/<appVersion>+<versionCode>/…   # 同版本号的新构建（§4.5）
```

已经写入的版本目录**永不改写**。结构文件不做跨版本引用：内容相同的文件 git 只存一份。

### 4.2 meta.json

```jsonc
{
  "schema": 1,                    // meta.json 格式版本
  "region": "cn",
  "appVersion": "6.4.1",          // CP：Unity 配置 clientMajor.Minor.Build；Nuverse：安装包 versionName
  "appHash": "d67a688b-…",
  "dataVersion": "6.4.1.x",       // Unity 配置 clientDataMajor.Minor.Build.Revision
  "unityVersion": "2022.3.62f3",
  "clientVersion": "6.4.1",       // Unity 配置里的版本号；Nuverse 可能和 appVersion 不同，留作排查
  "abVersion": "6.4.x",           // clientMajor.Minor.clientDataRevision；Nuverse 素材路径用
  "package": {
    "name": "com.hermes.mk",
    "versionName": "6.4.1",
    "versionCode": 4647,
    "sha256": "…",                // 主包（base APK）的 sha256
    "source": "official"          // official / qooapp / playfetch / apkpure / local
  },
  "files": {                      // 同目录其他文件的 sha256
    "moe-info.json": "…",
    "schema.json": "…"            // 只有 Nuverse 服
  },
  "generatedAt": "2026-11-10T06:00:00Z",
  "builder": { "version": "0.1.0", "il2cppDumper": "4741d46" }
}
```

**appVersion 的取值依据**：hmt 客户端请求头 `X-App-Version` 取自 `GameConfig.appVersion`，Haruki 的 tc 数据 appVersion 为 6.4.0（等于 versionName），而 Unity 配置里是 6.4.1。所以 Nuverse 服取 versionName，CP 服取 Unity 配置。

### 4.3 moe-info.json

```jsonc
[
  { "Moe-Key": "<128 个 0/1>", "Moe-Secret": "<128 个 0/1>" },   // [0] API 密钥
  { "Moe-Key": "<128 个 0/1>", "Moe-Secret": "<128 个 0/1>" }    // [1] ab 密钥，只有 Nuverse 服有
]
```

| 下标 | 客户端字段 | 用途 |
| --- | --- | --- |
| 0 | `APIManager.dummydata` / `stab`（`crypt`） | jp：API 请求与 master 分片；Nuverse：**CDN master**，以及（待确认）API 请求 |
| 1 | `APIManager.abdummydata` / `abstab`（`abCrypt`） | 只有 Nuverse 服有。数值与 jp 的 [0] 相同，也就是 sssekai 给 tw/kr/cn 写的那套 |

**编码**：每个字节写成 8 个字符，高位在前（`0x12` → `00010010`），16 字节按顺序拼成 128 个字符。读取时校验长度为 128 且只含 `0`/`1`。Key 记为 `Moe-Key`，IV 记为 `Moe-Secret`。

> 注意：产物仓库是公开的，这样存放等于公开了这两套密钥（它们本来也能从公开发布的安装包里提取）。这是已确认的决定。

### 4.4 schema.json（Nuverse 服）

自定格式（M2 已实现）：

```jsonc
{
  "schema": 1,
  "source": { "appVersion": "6.4.1", "assembly": "Assembly-CSharp", "suiteMaster": "Sekai.SuiteMaster" },
  "tables": {                         // 来自 SuiteMaster 成员的 [Key("表名")]，即 master 的顶层键
    "areas": { "class": "Sekai.MasterArea", "container": "array" },
    "bondsLive2ds": { "class": "Sekai.MasterBondLive2d", "container": "array", "field": "bondLive2ds" },
    "compactCostume3dShopItemCosts": { "class": "Sekai.CompactMasterCostume3dShopItemCosts", "container": "object", "compact": true }
  },
  "classes": {
    "Sekai.MasterArea": {
      "fields": [
        { "key": 0, "name": "id", "type": "int" },
        { "key": 2, "name": "label", "type": "string", "nullable": true }
      ]
    }
  }
}
```

- `container`：`array`（`T[]`、`List<T>`）、`map`（`Dictionary<K,V>`，元素取 V）、`object`（单个对象，compact 表就是这种）。
- `field`：只在成员名与表名不同时出现（例：字段 `bondLive2ds` 的 Key 是 `bondsLive2ds`）。
- `compact`：表名以 `compact` 开头的 object 表。它的类每个成员是一列（数组），`__ENUM__` 成员指向取值表类（成员为 `string[]`）；按列展开成行由同步服务完成。
- `key`：`[Key(n)]` 为整数，`[Key("x")]` 为字符串。
- `name`：去掉开头的下划线后转 camelCase（与 Haruki 一致：`_assetbundleName` → `assetbundleName`）。
- `type`：`int` `long` `float` `double` `bool` `string` `bytes`；`{"array": T}`；`{"map": [K, V]}`；`{"pair": [K, V]}`；`{"class": "全名"}`；`{"enum": "全名", "underlying": "int", "values": {名字: 数值}}`；无法描述时 `{"unknown": "全名"}` 并告警。
- `nullable`：引用类型或 `Nullable<T>`。
- 字段和带 `[Key]` 的属性都算（SuiteMaster 里有些表是自动属性）；基类成员一并收集；`[IgnoreMember]` 排除。
- 规范化输出：键排序、紧凑、无末尾换行；同一个 DummyDll 每次生成的 sha256 相同（已验证）。

### 4.5 更新规则

| 情况 | 处理 | 结果 |
| --- | --- | --- |
| 该地区还没有 latest，或新 appVersion 更大 | 写 `<appVersion>/`，更新 latest | `advanced` |
| 新 appVersion 更小 | 版本目录不存在则写入，latest 不动 | `snapshot_only` |
| appVersion 相同、appHash 相同 | 不改动 | `noop` |
| appVersion 相同、appHash 不同、versionCode 更大 | 写 `<appVersion>+<versionCode>/`，更新 latest | `advanced` |
| appVersion 相同、appHash 不同、versionCode 不大于 latest | 不改动 | `rejected` |

版本号按数字逐段比较（`6.10.0 > 6.9.1`）。写入的目录如果已经存在、但内容不同，直接报错（已发布的文件不可修改）。

## 5. 各步骤

### 5.1 取得安装包、读清单

不给 `--apk` 时按地区的默认来源下载：cn 用官网链接，其余（jp、en、hmt、kr）用 QooApp。下载缓存在 `PJSK_CACHE_DIR`（默认 `~/.cache/pjsk-meta-builder`），目录为 `<region>/qooapp-<versionCode>/` 或 `<region>/official-<文件名>/`；已下载且校验一致的文件直接复用，有 `.part` 时用 Range 续传；下载成功后删除同一地区、同一来源的旧版本目录。

- **官网链接（cn）**：`https://pjskyy.ugurl.cn/d9WHL`，两次 302 后到 `lf9-apk.ugapk.cn` 上的单个 APK（约 586MB，文件名里带 versionCode，如 `…_v23509_4647_….apk`）。HEAD 返回 404，所以用 `Range: bytes=0-0` 的 GET 拿最终地址和总大小（`Content-Range`）。官网不给 MD5，只校验大小；版本号要读清单才知道，“来源还没更新”由清单检查判断。
- **QooApp（jp、en、hmt、kr）**：参照 pjsk-reverse-workflow `scripts/qooapp_dl.py`：
  1. `GET https://api.qoo-app.com/store/v11/apps/<条目id>` → `packageId`、`isApkReady`、`apk.{versionName, versionCode, baseApkMd5, dataPackNeeded}`、`splitApks[]{url, md5, size}`；
  2. 主包 `GET /v11/apps/<包名>/download?type=app`，302 跳转到文件地址；拆分包直接用 `splitApks[].url`；
  3. API 请求带 `X-User-Token` 等一组客户端头；文件下载（`d*.qoo-apk.com`）不带 token。没有 token 时条目接口返回 HTTP 401；body 里的 `code` 为 401/403 同样视为 token 失效；
  4. 条目 id：jp 9038、en 18337、hmt 18298、kr 20082；
  5. 版本号在下载前就知道：比期望的 versionCode 旧时直接报“来源未就绪”，不浪费流量；主包和拆分包都校验 MD5（拆分包还校验大小）。
  6. 2026-11-10 实测：en 6.1.0（109）主包 + 1 个拆分包共 241MB；kr 6.4.0（22011）主包 + 2 个拆分包共 481MB；本机下载分别约 14 秒、27 秒。
- **跳转**：手动跟随，最多 10 次，只允许 https。
- **本地文件**：命令行用 `--apk` 给一个或多个 APK，`package.source` 记为 `local`。
- **错误分类**：

  | 情况 | 类别 | 退出码 |
  | --- | --- | --- |
  | 超时、5xx、429、下载中断、大小或 MD5 不一致（文件已删除或保留 .part） | 网络类，可重试 | 75 |
  | 下载源版本比期望旧、`isApkReady=false`、文件地址 404 | 来源未就绪，稍后重试 | 75 |
  | 包名不对、需要额外数据包、条目 404 | 数据类 | 65 |
  | QooApp token 缺失或失效、文件地址 401/403 | 凭据/配置，要人处理 | 78 |

- **不合并拆分包**：把主包和拆分包当作一个虚拟文件系统按路径查找（`lib/arm64-v8a/libil2cpp.so` 在 arm64 拆分包里，`assets/bin/Data/...` 在主包里）。这样就不需要 APKEditor（Java）。
- **读清单**：自己解析二进制 AndroidManifest.xml（AXML）的字符串池和 manifest 元素属性，取 package、versionName、versionCode，不依赖 aapt2。sssekai 的 `apphash.py` 里有字符串池解析可参考。
- **核对**：包名必须与地区配置一致（jp `com.sega.pjsekai`，cn `com.hermes.mk`）；给了期望 versionCode 时，下载到的更小就失败（下载源还没更新完），由调用方稍后重试。
- **网络错误与数据错误分开**：网络类失败（超时、5xx、429 等）可以重试；数据类失败（包名不对、文件损坏）重试也没用。

### 5.2 解密 global-metadata

路径 `assets/bin/Data/Managed/Metadata/global-metadata.dat`。移植 pjsk-reverse-workflow `scripts/sekai_metadata.py` 的 `decrypt_metadata`：

- **Nuverse 格式**：开头 8 字节是固定哨兵值，后面是带魔数和版本号（31）的头；按头里的偏移和长度对一段数据做 XOR，密钥表由内置的一组 CRC32 原像生成。
- **jp/en 格式**：在 `libil2cpp.so` 里找唯一的 NDK 特征串，密钥紧跟其后。
- 已经是明文（以魔数开头）时报错提示。
- 解密结果只放在私有工作目录，不打印、不上传、不进日志。

### 5.3 提取身份

需要读 Unity 的 `production_android` 配置（MonoBehaviour）。release 构建里没有 typetree，按固定字段顺序读：MonoBehaviour 基础头（m_GameObject、m_Enabled、m_Script、m_Name）之后依次是 16 个字符串：

`memo, clientMajorVersion, clientMinorVersion, clientBuildVersion, snapshot, clientVersionSuffix, clientDataMajorVersion, clientDataMinorVersion, clientDataBuildVersion, clientDataRevision, companyName, productName, bundleIdentifier, bundleVersion, assetHash, clientAppHash`

（与 workflow `read_player_config` 一致。）

| 服 | 资源位置 | 依据 |
| --- | --- | --- |
| jp 7.1.0 | `assets/bin/Data/globalgamemanagers` 里 ResourceManager 的容器 `playersettings/android/production_android`，指向 `assets/bin/Data/6350e2ec327334c8a9b7f494f344a761` 中的对象 | 两个文件在 7.1.0 里都存在（已核对）；容器路径来自 workflow `app_info`，文件名来自 sssekai `PROD_BUNDLES` |
| cn 6.4.1（Nuverse） | `assets/bin/Data/data.unity3d`（UnityFS 包），在其中找 `m_Name == "production_android"` 的 MonoBehaviour，必须正好一个 | 文件存在（已核对）；查找方式来自 workflow `apk_info` |

- **dataVersion**：production_android 里的 clientData* 字段在 jp 7.1.0 和 cn 6.4.1 上都是占位的 `1.0.0.0`。jp 的真实值在一个名为 `JenkinsBuildManifest` 的 MonoBehaviour 里（m_Name 之后的第一个字符串，形如 `tags/7.1.0_7.1.0.10_1007`，即「客户端版本_dataVersion_构建号」，jp 7.1.0 在 `assets/bin/Data/a18e73db…` 中）。en 6.1.0 的 production_android 同样是占位值，构建标签是分支构建的形式 `release/6.1.0_6.1.0.0`（没有构建号），同样按「客户端版本_dataVersion」解析。这是安装包内置的 dataVersion，服务器当前的值会更新（例如 en 服务器当前 6.1.1.0），以同步服务从服务器拿到的为准。CP 服优先用它，并校验其中的客户端版本与 production_android 一致；Nuverse 服没有这个对象，dataVersion 只能记占位值，真实值以同步服务登录拿到的为准。
- cn 6.4.1 的 `bundleIdentifier` 是 `com.sega.pjsekai`（与包名 `com.hermes.mk` 不同，sssekai 也注意到了），只给警告。
- appHash 必须是 UUID 格式；版本号必须匹配 `\d+(\.\d+)+`。
- unityVersion 取序列化文件头里的版本。
- appVersion 按 §4.2 的规则取值。

### 5.4 生成 DummyDll

- 调用 Il2CppDumper（固定 `4741d46`，`config.json` 里 `RequireAnyKey=false`）：`Il2CppDumper libil2cpp.so global-metadata.dat <out>`，得到 `DummyDll/Assembly-CSharp.dll`。
- 输出只写进日志文件，不打到标准输出。
- 镜像里预装 .NET 运行时和编译好的 Il2CppDumper。

### 5.5 提取密钥（moe-info.json）

客户端 `Sekai.APIManager` 的静态构造函数用 `RuntimeHelpers.InitializeArray` 从 `<PrivateImplementationDetails>` 的字段初始化 `dummydata`、`stab`、`abdummydata`、`abstab`。**这些字段的名字就是数据内容的 SHA-256**（新版编译器的命名方式），数据本身在 global-metadata 的默认值数据区里。

已验证的事实：

| 安装包 | dummydata | stab | abdummydata | abstab |
| --- | --- | --- | --- | --- |
| jp 7.1.0 | `5B30CBC1…` | `7C1CF10C…` | — | — |
| hmt 6.4.0 | `BA2B1D52…` | `3673BB77…` | `5B30CBC1…` | `7C1CF10C…` |
| cn 6.4.1 | `BA2B1D52…` | `3673BB77…`（在 metadata 中按哈希找到并成功解开 master 186） | `5B30CBC1…` | `7C1CF10C…` |
| kr 6.4.0 | `BA2B1D52…` | `3673BB77…` | `5B30CBC1…` | `7C1CF10C…` |
| en 6.1.0 | `D01AC7F5…` | `2DBB0FDF…`（与 jp 不同；哈希由 sssekai 的 en 密钥算出，在 en 6.1.0 的 metadata 中命中） | — | — |

hmt、kr、cn 三个 Nuverse 服的密钥完全相同（`moe-info.json` 字节相同）；kr 与 hmt 的 appHash 也相同（`d67a688b…`）。

难点在于：**DummyDll 里没有方法体**，看不到静态构造函数把哪个 `<PrivateImplementationDetails>` 字段赋给了哪个静态字段。

**已实现方案 1（反汇编 cctor，自动跟随密钥更换）**，流程（`Keys/KeyResolver.cs`）：

1. 从 Il2CppDumper 的 `script.json` 取 `APIManager..cctor` 的地址（`ScriptMethod` 里 Name 以 `APIManager$$.cctor` 结尾），以及 `Field$<PrivateImplementationDetails>.<哈希>` 的「使用槽 VA → 哈希」映射（`ScriptMetadata`）。script.json 可达数百 MB，用 `Utf8JsonReader` 流式读取，只取这两样。
2. 自己写了个只认 cctor 里那几种指令的最小 arm64 解码器（`Keys/Arm64CctorReader.cs`，无原生依赖）：按声明顺序，cctor 对每个 byte[] 常量字段做 `new byte[16]` + `RuntimeHelpers.InitializeArray(字段, <PrivateImplementationDetails>.<哈希>)`。解码器追踪寄存器，取出每次 `InitializeArray` 调用的「数据源槽 VA」（InitializeArray 不写死地址：它是所有调用点都带非空且互不相同源槽的那个 `bl` 目标）。
3. 源槽在文件里是 0，真实目标在 `.rela.dyn` 的 `R_AARCH64_RELATIVE` 重定位 addend 里（`Keys/Elf64.cs` 解析段表和重定位）：源槽 VA → addend → 字段使用槽 VA → 映射到字段哈希。
4. 按顺序：第 1、2 个是 API 的 key/iv（`dummydata`/`stab`），Nuverse 再加第 3、4 个 ab 的 key/iv（`abdummydata`/`abstab`）。CP 应恰好 2 个、Nuverse 恰好 4 个，否则报错回退。
5. 拿到有序哈希后，在 metadata 默认值数据区按 SHA-256 取 16 字节（与方案 3 共用 `KeyExtractor.Find`）。

**兜底方案 3（已知哈希表）**：没有 Il2CppDumper 输出（CP 服未传 `--il2cppdumper`），或方案 1 任一步失败时，用上表的已知哈希命中；命中不了报错并人工处理。

**交叉校验**：方案 1 跑通时，把它得到的有序哈希与方案 3 的已知哈希表比对；不同则告警并以反汇编结果为准（说明密钥已更换）。实测 jp、cn、en 三服方案 1 的结果与已知哈希完全一致（en 的密钥与 jp 不同，由反汇编动态定位，证明不是在重认硬编码值）。

> 开发时用 capstone 写的 Python 原型交叉验证过 C# 解码器（不在本仓库）；生产代码不依赖它。

### 5.6 生成结构文件（Nuverse 服）

用 Mono.Cecil 读 DummyDll（`Schema/SchemaGenerator.cs`）：

1. 找到 `Sekai.SuiteMaster`，枚举带 `[Key]` 的字段**和属性**：**Key 字符串就是表名**（不是成员名），元素类型就是行的类。
2. 从这些类出发递归收集引用到的类型，读每个成员的 `[Key]`、名字、类型。
3. 按 §4.4 写出。Nuverse 服 build 时必须提供 Il2CppDumper。

**M2 核对结果（cn 6.4.1，master 186）**：

| 项 | 结果 |
| --- | --- |
| 结构文件 | 435 张表、531 个类、0 条警告；连续两次生成 sha256 相同 |
| master 顶层键 | 424 个；数组行表 402 张全部有映射，42 万行行宽和类型核对通过；8 张 compact 表的列都在对应类里 |
| SuiteMaster 之外的 9 张表 | `unitAssets`、`backgroundMusics` 等，行全是对象（自带字段名）或空表，原样输出即可 |
| 与 Haruki 生成器比对 | master 中实际存在的 389 张表，Key → 字段名全部一致；我们多覆盖 29 张（含 8 张 compact、`billingShopItemRandomBoxGroups`、`bondsLive2ds` 等） |
| hmt 6.4.0 | 表和类与 cn 6.4.1 完全相同 |

需要同步服务处理的情况：

- `cardCostume3ds` 数据每行 3 列，类 `MasterCardCostume3D` 只有 2 个 Key：结构文件照实描述，多出的列由同步服务告警（不应判失败）。
- 对象行里会有类中没有的键（服务器多发、客户端不读，例：`ingameJudgeFrames` 的 `perfect`），原样保留。
- master 里出现结构文件没有的表时：对象行原样输出；数组行无法还原，告警。

### 5.7 内容检查

发布前对每个文件做白名单检查：

- `meta.json`：只允许 §4.2 列出的键；appHash 为 UUID；sha256 为 64 位小写十六进制；其他字符串里不允许出现 32 位以上的十六进制串，也不允许出现 KEY、IV、XOR 这类词。
- `moe-info.json`：数组长度 jp 为 1、Nuverse 为 2；每项只有 `Moe-Key`、`Moe-Secret` 两个键，值是 128 个 0/1 字符。**它是唯一允许出现密钥的文件**。
- `schema.json`：只允许 §4.4 的结构，字符串只能是类名、字段名、类型名。
- 任何文件都不能出现 metadata 的 XOR 密钥、`libil2cpp.so` 里的偏移、账号凭据、QooApp token。

### 5.8 发布

1. 用 GitHub App（`moesekai-bot`，App ID 5693240，归 StarMoe-org）的私钥换 installation token，**只申请 `moesekai-app-meta` 一个仓库**的 `contents: write`。token 只在内存里用于 API 请求，不落盘、不进命令行参数。
2. **全程走 GitHub API，不克隆、不用 git 命令行**：
   1. 读分支头：`GET /repos/{o}/{r}/git/ref/heads/main`，得到提交 `H`。
   2. 固定在 `H` 上读 `<region>/latest.json` 和目标目录的 `meta.json`：`GET /repos/{o}/{r}/contents/{path}?ref=H`（raw）。判定和写入看到的是同一个版本。
   3. 按 §4.5 判定。
   4. 用 GraphQL `createCommitOnBranch` 一次写入版本目录的全部文件和 `latest.json`，`expectedHeadOid = H`：一次调用就是一个提交，不会出现只写了一半的状态。
   5. 分支头已经不是 `H`（别人先提交了，GitHub 返回 `STALE_DATA`）时，从第 1 步重来，最多 5 次，间隔指数退避（上限 20 秒）。
3. 提交由 GitHub 以 App 的 bot 身份 `moesekai-bot[bot]` 创建并签名，在 GitHub 上显示 Verified。作者无需（也不能）指定；同步服务写 `moesekai-masterdata-<region>` 也用同一个 App、同样的方式。
4. 提交信息：`<region>: <旧版本> → <新目录> (versionCode <n>)`；补旧版本快照时为 `<region>: 补充 <版本> (versionCode <n>)`。只有标题行。
5. 返回结果、commit sha 和每个文件的 sha256，回调时带上。
6. API 不能在空仓库上创建第一个提交：新仓库需要先有 `main` 分支（例如建仓库时带 README）。`moesekai-app-meta` 已经满足。
7. 错误分类：5xx、429、限流（403 且剩余额度为 0）→ 网络类，可重试（退出码 75）；其他 4xx 和 GraphQL 错误 → 内部错误（多为权限或配置问题）。
8. 读写都在 `IMetaRepository` 接口后面：GitHub 用 `GitHubApiRepository`；本地裸仓库用 `LocalGitRepository`（测试和离线演练用，底层命令 `commit-tree` + 带旧值的 `update-ref`，同样是乐观并发；提交身份固定为上面的 bot 账号，并清掉外部 `GIT_AUTHOR_*` / `GIT_COMMITTER_*`）。

**M3 状态**（2026-11-10）：

- `publish` / `run` 已实现。本地裸仓库测试覆盖首发、重复（noop）、旧版本补目录、同版本新构建（`+versionCode` 目录）、拒绝、已存在目录属于其他构建时报错、dry-run 不写入、**提交前分支被别人更新后的重试**；`GitHubApiRepository` 用假的 HTTP 处理器测试了请求形状、路径转义（`+`）、`STALE_DATA`、错误分类；JWT 签名有单元测试。
- 已建公开仓库 `StarMoe-org/moesekai-app-meta` 和 GitHub App `moesekai-bot`（只装在这个仓库上，权限 contents 写 + metadata 读）。验证了 token 只能看到这一个仓库。
- 首次真实发布（当时还是 git push 方式）：jp 7.1.0（`85f73c6`）、cn 6.4.1（`6606808`），作者和提交者均为 `moesekai-bot[bot]`，但 git push 的提交没有签名，GitHub 上不显示 Verified。之后改为上面的 API 方式；这两个提交保留原样，不改写历史。
- 改为 API 方式后，对真实仓库验证了只读路径：jp、cn 重复发布判为 `noop`，hmt dry-run（带 token 和匿名各一次）判为 `advanced`，仓库没有新提交。随后用 API 真实发布 hmt 6.4.0（`820bd91`）：作者 `moesekai-bot[bot]`、提交者 `web-flow`，GitHub 显示 Verified（reason=valid）；`latest.json` 与 `meta.json` 字节相同。en、kr 还没有安装包，等 M4 下载源。

### 5.9 M4 状态（2026-11-10）

- 下载源已实现：`QooAppSource`、`OfficialUrlSource`、公共的 `Downloader`（跳转、续传、大小和 MD5 校验、错误分类），命令行新增 `fetch`，`build` / `run` 不给 `--apk` 时自动下载。单元测试用假的 HTTP 服务覆盖：主包和拆分包下载与校验、缓存复用与旧版本清理、`.part` 续传、MD5 不一致、版本比期望旧（下载前就失败）、包名不对、token 失效（HTTP 401 和 body code 401 两种）、5xx、官网多次跳转、拒绝跳转到 http。
- 真实验证：
  - jp：从 QooApp 下载 7.1.0（300131），`run --dry-run` 判为 `noop`：meta 身份、`moe-info.json` 与已发布的（来自 workflow Release 包）一致；appHash `833770f2…` 与公开的 jp 服务器版本信息一致。
  - cn：从官网下载 6.4.1（4647，559MB），`run --dry-run` 判为 `noop`，`schema.json`、`moe-info.json` 与已发布的一致。
  - en、kr：本来没有安装包，从 QooApp 下载后 build 并真实发布：en 6.1.0（`8a17559`）、kr 6.4.0（`e37ab49`），均为 Verified。en 的 appHash `35d80a91…` 与公开的 en 服务器版本信息一致。
- 为 en 补了两处：构建标签 `release/…` 的格式（§5.3）、en 自己的 API 密钥哈希（§5.5）。

## 6. 命令行

```
pjsk-meta-builder build   --region cn --apk cn.apk [--apk split.apk …] --out ./out
pjsk-meta-builder build   --region jp --source qooapp [--expected-version-code N] --out ./out
pjsk-meta-builder fetch   --region en [--source qooapp] [--expected-version-code N]     # 只下载
pjsk-meta-builder publish --region cn --dir ./out [--dry-run]
pjsk-meta-builder run     --region cn --source official          # build + publish
pjsk-meta-builder serve   --listen :8080                         # HTTP 服务（§7）
pjsk-meta-builder inspect --apk x.apk                            # 只读：清单、身份、密钥哈希前缀（不打印密钥）
```

- `build` 不需要任何凭据；`--dry-run` 时判定逻辑完全相同，但不提交。有 App 凭据就带 token 读，没有就匿名读公开仓库（匿名 API 每小时 60 次）。
- `--repo` 可以是 `owner/name`（走 GitHub API）或本地裸仓库路径（走 git 命令行）。
- 退出码：0 成功；75 网络类失败或来源未就绪（可重试）；65 数据类失败；64 用法错误；78 凭据/配置问题（要人处理）；其他为内部错误。
- `fetch` 只下载并打印各 APK 路径；`build` / `run` 不给 `--apk` 时先 `fetch`。

## 7. HTTP 服务

只在集群内网提供，Service 不对外。

### 7.1 接口

```
POST /v1/builds          同步服务发起处理
  { "requestId": "…", "region": "cn", "source": "official|qooapp", "expectedVersionCode": 4647,
    "callback": "http://<同步服务>.apps.svc.cluster.local/v1/app-meta/callback" }
  → 202 { "requestId": "…", "status": "queued" }
     202：同一 requestId 重复提交（幂等，返回已有任务的状态）
     409 { "requestId": "<已有任务>", "status": "queued|running" }：同一地区已有任务排队或在跑，等那个任务的回调即可
     400：请求体不合法（requestId 只能是 1–64 位字母、数字或 ._:-；source 与地区要匹配；callback 是 http(s) 绝对地址）
     401：签名无效（不告诉调用方具体原因，原因只写日志）
GET  /v1/builds/{requestId}   查状态（排查用；同样要签名，body 为空）
GET  /healthz                 存活检查（不需要签名，只返回版本、排队数、是否在跑）
```

- `source` 可省略：cn 默认官网链接，其余默认 QooApp。
- 请求体上限 16KB。

### 7.2 回调

产物**推到仓库之后**才回调：

```
POST <callback>
  { "requestId": "…", "region": "cn", "status": "succeeded|failed",
    "result": "advanced|snapshot_only|noop|rejected",
    "appVersion": "6.4.1", "versionCode": 4647,
    "commit": "<sha>", "path": "cn/6.4.1",
    "files": { "meta.json": "<sha256>", "moe-info.json": "…", "schema.json": "…" },
    "error": { "kind": "network|not_ready|data|config|internal", "message": "…" } }
```

| error.kind | 含义 | 同步服务的处理 |
| --- | --- | --- |
| `network` | 超时、5xx、429、下载中断、MD5 不一致 | 稍后重试 |
| `not_ready` | 下载源上的版本比期望的旧、文件还没上传完 | 过一段时间再发起 |
| `data` | 包名不对、格式不认识、已发布目录冲突 | 告警，重试没用 |
| `config` | QooApp token 失效、服务缺少配置 | 告警，要人处理 |
| `internal` | 程序错误 | 告警 |

- 发布判定为 `rejected` 也算跑完（`status: succeeded`，`result: rejected`）。
- `noop` / `rejected` 时 `commit`、`path` 为空，仓库里以 `latest.json` 为准。

- 回调失败按 10 秒、30 秒、2 分钟重试 3 次，之后放弃：同步服务会低频检查仓库来兜底。
- 仓库是权威来源，回调只是通知。

### 7.3 认证

两个方向都用共享密钥签名：

```
X-Moe-Timestamp: <unix 秒>
X-Moe-Signature: sha256=<hex(HMAC_SHA256(secret, timestamp + "." + body))>
```

时间戳与本机时间相差超过 5 分钟就拒绝；用常量时间比较签名。

### 7.4 任务执行

- 同一时间只跑一个任务（内存考虑），其余排队；同一地区已有任务时返回 409（见 §7.1），同一 requestId 幂等。
- 每个任务一个临时工作目录（`PJSK_WORK_ROOT` 下），权限 700，结束后删除：安装包也在里面，不跨任务缓存（只有版本变化才会触发任务，重新下载可以接受，避免临时存储被几个服的安装包占满）。
- 服务里总是跑 Il2CppDumper：Nuverse 要生成结构文件，CP 用它反汇编 cctor 定位密钥（§5.5）。
- 回调在后台重试，不占用下一个任务。
- 任务状态只在内存里（保留最近 200 个已结束任务），重启后丢失；同步服务收不到回调时会重新发起或检查仓库。

### 7.5 主动发现新安装包（M6）

服务里加一个后台轮询器，按区服查安装包来源，和产物仓库的 `<region>/latest.json` 比较；有更新就往现有的单任务队列里放一个任务（和同步服务发来的请求走同一条路：同一地区不重复、同一时间只跑一个）。builder 自己不存状态，基准永远是产物仓库。

| 来源 | 区服 | 间隔（默认） | 能拿到 | 判定为新版本 |
| --- | --- | --- | --- | --- |
| 官网链接 | cn | 10 分钟 | 跟随重定向、只取 1 字节，从文件名（`pjsk_…_v23509_4647…apk`）读 versionCode | versionCode 大于 latest 的 `package.versionCode` |
| QooApp 详情 | jp / en / hmt / kr | 15 分钟 | versionName、versionCode、是否可下载（只查条目，`QooAppSource.Current`，不碰文件） | versionCode 更大，或 versionName 更大，且已可下载 |
| Google Play 网页 | jp / en / hmt / kr | 15 分钟 | `play.google.com/store/apps/details?id=…&gl=<地区>` 内嵌数据 `ds:5` 里的 versionName、最后更新时间；**没有 versionCode**；各服要用自己的商店地区（jp→JP、en→US、hmt→TW、kr→KR；hmt、kr 在美区页面上没有数据）；cn 不在 Google Play 上 | versionName 更大 → 提前信号：立即复查 QooApp，下载仍走 QooApp |

- 间隔都加 ±10% 随机抖动，可用环境变量调整；某个来源连续失败时单独退避（最长 2 小时），不影响其他来源。
- Google Play 先于 QooApp 看到新版本时（实现时改定）：**不发起任务**，轮询器立即复查一次 QooApp；没跟上就按退避复查 QooApp（10 分钟起，翻倍，最长 2 小时），QooApp 跟上后再发起任务。这样不会空跑一个以 `not_ready` 结束的任务。这个「领先」标记要等 `latest.json` 追上才清除，避免任务发布前 Google Play 反复触发。
- 任务带 `expectedVersionCode`（官网文件名或 QooApp 的 versionCode），`callback` 为空、用 `SYNC_CALLBACK_URL`。同一 (区服, versionCode) 触发后 2 小时内不再触发：`latest.json` 经 GitHub raw 读取，可能有几分钟缓存，避免这期间重复下载；任务失败（比如网络）时，冷却期过后会再试。
- 平时安静：各来源第一次查到、或观察到的版本变化时才记一条日志。
- Google Play 网页是 Google 的内部数据格式，解析失败只记告警、把这个来源标成失效，不影响其他来源。2026-11-11 从本机和 JP2 实测四个包都能取到（每次约 0.3 秒，无需凭据）。匿名 playfetch 不用：依赖第三方公共账号池，本机实测被 Cloudflare 拦截。
- 只在发布结果为 `advanced` 时通知同步服务（§7.6）；`snapshot_only` / `noop` / `rejected` 只写日志。
- 自检增加「Google Play（jp/en/hmt/kr）」四项；cn 官网一项同时显示文件名里的 versionCode。
- 2026-11-12 本机实测：九个来源（四服 QooApp + Google Play、cn 官网）约 3 秒查完，版本都与 app-meta 的 latest.json 一致，没有发起任务。

### 7.6 和同步服务的配合

- **回调地址固定配置**：`SYNC_CALLBACK_URL`。builder 自己发起的任务完成后发到这个地址，格式同 §7.2，`requestId` 以 `auto-` 开头。同步服务通过 `POST /v1/builds` 发起的任务仍按请求里的 `callback` 回调（可省略，省略时用 `SYNC_CALLBACK_URL`）。
- **同步服务先发现的情况**（builder 漏掉了，或来源还没更新）：游戏 API 返回 426 / `appVersionStatus: not_available`，或 master 和结构文件对不上。同步服务先重读 `latest.json`，没有更新的版本才调用 `POST /v1/builds`；builder 正在处理同一区服时返回 409，同步服务等回调即可。
- 两边启动时都先读一次 `latest.json`。

## 8. 配置与部署

| 配置 | 来源 | 说明 |
| --- | --- | --- |
| `GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY` | Secret | StarMoe-org 的 App，与同步服务共用 |
| `MOE_HMAC_SECRET`（或 `_FILE`） | Secret | 与同步服务共用，至少 32 个字符，不够时拒绝启动 |
| `QOOAPP_TOKEN`（或 `QOOAPP_TOKEN_FILE`） | Secret | jp、en、hmt、kr 下载；可选 `QOOAPP_USER_ID`、`QOOAPP_DEVICE_ID` |
| `SYNC_CALLBACK_URL` | 配置 | 自己发起的任务完成后、以及请求里没给 `callback` 时，通知同步服务的地址（§7.6，M6）；JP2 上为 `http://moesekai-masterdata-sync.apps.svc.cluster.local:8080/v1/app-meta/callback` |
| `PJSK_WATCH`、`PJSK_WATCH_REGIONS` | 配置 | 是否启用主动轮询、轮询哪些区服（M6；默认关，部署时打开） |
| `PJSK_CACHE_DIR` | 配置 | 命令行的下载缓存目录（服务不用，见 §7.4） |
| `PJSK_WORK_ROOT`、`PORT` | 配置 | 服务的任务临时目录（镜像默认 `/tmp/pjsk-meta-jobs`）和端口（默认 8080） |
| `IL2CPPDUMPER_DLL`、`PJSK_DOTNET` | 配置 | 镜像里已设好 `/opt/il2cppdumper/Il2CppDumper.dll` |
| `APP_META_REPO` | 配置 | 默认 `StarMoe-org/moesekai-app-meta` |
| 地区表 | 配置 | 包名、QooApp 条目 id、官网链接、是否 Nuverse |

- **部署位置**：JP2，`apps` 命名空间，单副本。
- **镜像**：.NET 运行时 + Il2CppDumper；在 MetaSekaiLab 仓库的 Actions 里构建，推到公开的 GHCR，JP2 不用凭据就能拉取。
  - `Dockerfile`：编译阶段在构建机平台上跑，产物是与架构无关的 DLL（不带 apphost），最终阶段只有 COPY，所以 amd64 + arm64 两个架构都不需要 QEMU 编译。
  - Il2CppDumper 按固定 commit `4741d46ba9cd6159c5d853eb9d6fc48b4bfa2b1a` 从 MetaMikuAI fork 构建（net8.0，用 .NET 10 运行时加 `DOTNET_ROLL_FORWARD=Major` 运行），`RequireAnyKey=false`。
  - 以镜像自带的非 root 用户运行，工作站 GC（`DOTNET_gcServer=0`，子进程继承），只往 `/tmp` 写，可以只读根文件系统。
  - `.github/workflows/ci.yml`：每次推送跑测试，PR 上试构建镜像，`v*` 标签发版本号和 `latest`。`deploy.yml`（StarMoe 标准模板）：main 构建 12 位 sha 和 `main` 标签，再通知 Ferry 更新集群里的服务（见 deploy/README.md）。
  - 部署参考 `deploy/k8s.yaml`、`deploy/README.md`（通用写法，不含集群地址）。
- **资源**：cn 构建实测内存峰值约 2.2GB（含 Il2CppDumper 子进程），耗时约 20 秒（本机）；内存上限 3Gi，按 StarMoe 要求必须设置 request 和 limit；临时存储上限 3Gi。
- **待验证**：JP2 能否访问 cn 官网链接和 QooApp。

### 8.1 M5 状态（2026-11-10）

- `serve` 已实现（`src/PjskMetaBuilder.Service`）：HMAC 双向签名、单任务串行队列、同地区 409、幂等 requestId、下载 → build → 发布流水线、带签名的回调与重试、错误分类。
- 测试：HMAC（含与文档定义逐字比对、篡改、过期）；服务在本机随机端口跑起来，配假的流水线和假的同步服务，覆盖健康检查、未签名/错签/过期 401、非法请求 400、正常流程与回调签名、幂等与 409、串行顺序、五类错误的回调、回调失败重试到送达、未知任务 404。共 87 个测试通过。
- 本机端到端：真实 `serve` + 真实流水线，en 请求 202、同地区再发 409、未签名 401；从 QooApp 下载、build、发布判为 `noop`（仓库无新提交），回调送达且用 Python 独立校验签名通过；任务目录已删除。
- 已建公开仓库 `MetaSekaiLab/pjsk-meta-builder`，CI 测试与镜像构建通过；GHCR 包已设为公开，匿名可拉取（amd64 + arm64，压缩后约 93MB）。
- 为部署补了**出网自检**：`selftest` 命令，或 `PJSK_SELFTEST=1` 时服务启动后自动跑一次，结果写日志、`/healthz` 显示通过数。检查 Il2CppDumper、工作目录、QooApp 四个条目（查条目 + 主包和拆分包各取 1 字节）、cn 官网链接（取 1 字节）、GitHub App（换 token + 读分支），不下载安装包、不写仓库。本机 8/8 通过。
- `GITHUB_APP_PRIVATE_KEY` 接受原样 PEM、`\n` 转义的 PEM 或 base64（面板环境变量常不支持多行）。
- **已部署到 JP2**（2026-11-11）：在节点本机用 `kubectl create` 首次创建（`deploy/k8s.yaml`，按 Zeabur 面板约定的标签和命名，面板可识别、可编辑），Secret 为 `pjsk-meta-builder-env`。自检 8/8 通过（§9 Q6）。
- 之后的更新和 StarMoe 其他项目一样：标准模板 `deploy.yml` 在 main 推送后构建 12 位 sha 镜像，经 `hack/bump.sh` 通知 Ferry 滚动更新；环境变量和资源在面板里改。

## 9. 未决项与待验证

| # | 问题 | 当前打算 |
| --- | --- | --- |
| Q1 | ~~密钥字段的定位方法（§5.5）~~ | **已解决**：反汇编 `APIManager..cctor`（§5.5 方案 1）动态定位密钥字段，能跟随密钥更换；已知哈希表降为兜底 + 交叉校验。jp/cn/en 两法结果一致 |
| Q2 | ~~`compact*` 表和 SuiteMaster 之外的顶层键怎么描述~~ | **已解决（M2）**：compact 表就是 SuiteMaster 的成员（按列存放的类），见 §4.4；SuiteMaster 之外的表行都是对象，原样输出 |
| Q3 | ~~Nuverse 服 API 请求实际用 [0] 还是 [1]~~ | **已解决（2026-11-11 cn 实测）**：服务器按 `X-App-Hash` 选密钥，当前版本用 [0]、旧版本用 [1]。另：Nuverse 客户端实际发送的 `X-App-Version` 是四段 `{versionName}.{versionCode}`（`GameConfig.appVersion`，cn 6.4.1 为 `6.4.1.4647`），服务器只看前三段；同步服务可从 meta.json 的 `package` 自行拼出。以后可在 meta.json 加可选字段 `apiAppVersion` |
| Q4 | ~~QooApp 上 jp 的分包里，`assets/bin/Data` 是否完整在主包~~ | **已解决（M4）**：jp 7.1.0 从 QooApp 下载为主包 + 2 个拆分包，按虚拟文件系统查找即可，结果与 workflow Release 包完全一致 |
| Q5 | AssetsTools.NET 读取没有 typetree 的 MonoBehaviour | 按 §5.3 的固定字段顺序手工读；用 jp 7.1.0、cn 6.4.1 的结果与 workflow（Python）对照 |
| Q6 | ~~JP2 出网~~ | **已解决**：2026-11-11 在 JP2 上自检 8/8 通过，cn 官网链接（`lf9-apk.ugapk.cn`）、QooApp 四个区服（`api.qoo-app.com`、`d3`/`d7.qoo-apk.com`）、GitHub App 都可直连 |
| Q7 | QooApp token 的有效期 | 本机 token（2026-10-09 保存）在 2026-11-10 仍有效；失效时报凭据错误（退出码 78），同步服务应告警而不是重试 |

## 10. 测试

- **样本**（本机已有，不进仓库）：jp 7.1.0（workflow Release）、hmt 6.4.0（tw 原始包）、cn 6.4.1（官网）。
- **对照基准**：
  - 身份：与 workflow `identity.py` / sssekai `apphash` 的结果一致；
  - 密钥：SHA-256 前缀与 §5.5 的表一致；cn 的 [0] 能解开 master 186（PKCS7 正确，424 个顶层键）；
  - 结构文件：与 Haruki 生成器的输出逐类、逐字段比对，差异只能是 §5.6 列出的已知项；用 master 186 逐表核对行宽和类型。
- **单元测试**：AXML 解析、metadata 解密（用构造数据）、版本比较、更新规则全部分支、0/1 位串编解码、内容检查、下载源（假 HTTP）、密钥反汇编（ELF 重定位解析、手写 arm64 cctor 的源槽识别、script.json 流式解析）、HMAC 签名与时间窗、回调重试。
- **发布测试**：用本地裸仓库当作产物仓库，覆盖 §4.5 每种情况和分支被别人更新后的重试；GitHub API 部分用假的 HTTP 处理器测试。

## 11. 里程碑

| 里程碑 | 内容 | 完成标志 |
| --- | --- | --- |
| M1 核心（CP） | 虚拟文件系统、AXML、metadata 解密（jp）、身份提取、Il2CppDumper 调用、密钥提取（已知哈希）、`build`/`inspect` | jp 7.1.0 输出的 meta.json 与 workflow 一致，moe-info [0] 哈希正确 |
| M2 Nuverse | Nuverse 格式 metadata 解密、`data.unity3d` 身份、结构文件生成、密钥 [1] | cn 6.4.1 结构文件与 Haruki 输出比对通过，master 186 逐表核对通过 |
| M3 发布 | GitHub App token、更新规则、内容检查、`publish`/`run` | 本地裸仓库覆盖全部规则；对真实 `moesekai-app-meta` 做一次 dry-run |
| M4 下载源 | 官网链接、QooApp、期望 versionCode、错误分类 | 实际下载 cn 和 jp 并跑通 `run --dry-run` |
| M5 服务 | `serve`、HMAC、排队、回调、镜像、部署 JP2 | 用 curl 模拟同步服务发起请求，收到回调，仓库出现对应提交 |
| M6 主动发现 | 轮询器（官网 / QooApp / Google Play）、`SYNC_CALLBACK_URL`、自检加 Google Play | 用假来源和本地裸仓库测到「发现 → 处理 → advanced → 回调」；JP2 上开启后日志显示各来源检查正常 |
