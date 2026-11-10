# pjsk-meta-builder 设计文档

> 状态：设计中，尚未实现
> 代码仓库：`MetaSekaiLab/pjsk-meta-builder`（私有，尚未创建）
> 产物仓库：`StarMoe-org/moesekai-app-meta`（公开，尚未创建）
> 最后更新：2026-11-10

## 1. 背景与目标

masterdata 同步服务（另一个独立项目，Go）需要每个服当前版本的「App 元数据」才能工作：

- CP 服（jp / en）登录要用 appVersion、appHash，所有 API 请求都要用 API 密钥加解密；
- Nuverse 服（hmt / kr / cn）的 master 是数组格式，要用和版本对应的**结构文件**还原字段名，CDN 上的 master 要用客户端里的密钥解密。

这些都只能从安装包里取得，而且每次 App 更新都可能变化。pjsk-meta-builder 负责：**拿到安装包 → 提取元数据 → 发布到公开产物仓库 → 通知同步服务**。

**第一版目标**

- 地区：jp、cn（en / hmt / kr 以后再加）。
- 先做核心库和命令行，在本机用真实安装包验证稳定；再包装成 HTTP 服务，部署到 JP2。
- 产物发布到 `moesekai-app-meta`，格式见 §4。

**不做的事**

- 不拉取、不处理 master 本身（那是同步服务的事），只在测试中用 master 做核对。
- 不下载、不解包素材资源。
- 不保存任何账号凭据，不调用需要登录的游戏 API。
- 不决定「什么时候处理哪个版本」：由同步服务发起请求（§7）。

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

- **官网链接（cn）**：`https://pjskyy.ugurl.cn/d9WHL`，用 GET 跟随跳转（HEAD 返回 404），单个 APK，约 586MB，实测跳转到字节的 APK CDN。
- **QooApp（jp）**：参照 pjsk-reverse-workflow `scripts/qooapp_dl.py`：
  1. `GET https://api.qoo-app.com/store/v11/apps/<条目id>` → packageId、apk.versionCode/versionName、baseApkMd5、splitApks[]；
  2. 主包 `GET /v11/apps/<包名>/download?type=app`，302 跳转到文件地址；拆分包直接用 splitApks[].url；
  3. 请求头要带 `X-User-Token`（QooApp token，部署 Secret）等一组客户端头；token 失效时 HTTP 仍是 200，要看 body 里的 code。
  4. jp 条目 id：9038。
- **本地文件**：命令行可直接给一个或多个 APK。
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

- **dataVersion**：production_android 里的 clientData* 字段在 jp 7.1.0 和 cn 6.4.1 上都是占位的 `1.0.0.0`。jp 的真实值在一个名为 `JenkinsBuildManifest` 的 MonoBehaviour 里（m_Name 之后的第一个字符串，形如 `tags/7.1.0_7.1.0.10_1007`，即「客户端版本_dataVersion_构建号」，jp 7.1.0 在 `assets/bin/Data/a18e73db…` 中）。CP 服优先用它，并校验其中的客户端版本与 production_android 一致；Nuverse 服没有这个对象，dataVersion 只能记占位值，真实值以同步服务登录拿到的为准。
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
| cn 6.4.1 | `BA2B1D52…` | `3673BB77…`（在 metadata 中按哈希找到并成功解开 master 186） | | |

难点在于：**DummyDll 里没有方法体**，看不到静态构造函数把哪个 `<PrivateImplementationDetails>` 字段赋给了哪个静态字段。可选方案（见 §9 Q1）：

1. 用 Il2CppDumper 的 `script.json` 找到 `APIManager..cctor` 的地址，反汇编这段 arm64 代码，找出它引用的 metadata usage（`Field$<PrivateImplementationDetails>.<哈希>`），按赋值顺序对应到四个字段。
2. 候选穷举：枚举所有 16 字节的 `<PrivateImplementationDetails>` 字段，用实际数据验证（比如解 CDN 上当前的 master）。
3. 已知哈希兜底：上表中的哈希命中就直接用，命中不了就报错并人工处理。

第一版可以先用方案 3 跑通，同时实现方案 1，两者结果必须一致。

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

1. 用 GitHub App 私钥换 installation token，**只申请 `moesekai-app-meta` 一个仓库**的 `contents: write`。token 通过环境变量注入 git，不出现在命令行参数和 `.git/config` 里。
2. 浅克隆 `main` → 按 §4.5 判定 → 写文件 → 一次提交 → push。push 被拒（别人先推了）时重新克隆、重新判定，最多 5 次。
3. 提交者：App 的 bot 身份；提交信息：`<region>: <旧版本> → <新版本> (versionCode <n>)`。
4. 返回结果、commit sha 和每个文件的 sha256，回调时带上。
5. 提交身份固定为 `pjsk-meta-builder <pjsk-meta-builder@users.noreply.github.com>`（可配置），git 子进程会清掉外部的 `GIT_AUTHOR_*` / `GIT_COMMITTER_*`，避免被运行环境的身份覆盖。
6. 产物仓库为空（第一次发布）时直接 `git init` 并推到 `main`。

**M3 状态**：`publish` / `run` 已实现；用本地裸仓库覆盖了首发、重复（noop）、旧版本补目录、同版本新构建（`+versionCode` 目录）、拒绝、已存在目录属于其他构建时报错、dry-run 不写入；JWT 签名有单元测试。push 被拒后重试的路径还没有自动化测试。2026-11-10 已建公开空仓库 `StarMoe-org/moesekai-app-meta`，用 jp 7.1.0、cn 6.4.1、hmt 6.4.0 的 build 产物做 dry-run，均判 `advanced`（空仓库首发路径），仓库保持为空。GitHub App 已建，真实写入待验证。

## 6. 命令行

```
pjsk-meta-builder build   --region cn --apk cn.apk [--apk split.apk …] --out ./out
pjsk-meta-builder build   --region jp --source qooapp [--expected-version-code N] --out ./out
pjsk-meta-builder publish --region cn --dir ./out [--dry-run]
pjsk-meta-builder run     --region cn --source official          # build + publish
pjsk-meta-builder serve   --listen :8080                         # HTTP 服务（§7）
pjsk-meta-builder inspect --apk x.apk                            # 只读：清单、身份、密钥哈希前缀（不打印密钥）
```

- `build` 不需要任何凭据；`--dry-run` 时匿名克隆公开仓库，判定逻辑完全相同，但不推送。
- 退出码：0 成功；75 网络类失败（可重试）；65 数据类失败；其他为内部错误。

## 7. HTTP 服务

只在集群内网提供，Service 不对外。

### 7.1 接口

```
POST /v1/builds          同步服务发起处理
  { "requestId": "…", "region": "cn", "source": "official|qooapp", "expectedVersionCode": 4647,
    "callback": "http://<同步服务>.apps.svc.cluster.local/v1/app-meta/callback" }
  → 202 { "requestId": "…", "status": "queued" }
     409：同一地区已有任务在跑（同一地区串行）
GET  /v1/builds/{requestId}   查状态（排查用）
GET  /healthz
```

### 7.2 回调

产物**推到仓库之后**才回调：

```
POST <callback>
  { "requestId": "…", "region": "cn", "status": "succeeded|failed",
    "result": "advanced|snapshot_only|noop|rejected",
    "appVersion": "6.4.1", "versionCode": 4647,
    "commit": "<sha>", "path": "cn/6.4.1",
    "files": { "meta.json": "<sha256>", "moe-info.json": "…", "schema.json": "…" },
    "error": { "kind": "network|data|internal", "message": "…" } }
```

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

- 同一时间只跑一个任务（内存考虑），其余排队；同一地区的重复请求合并。
- 每个任务一个临时工作目录，权限 700，结束后删除（安装包、解密后的 metadata、DummyDll 都不保留）。
- 任务状态只在内存里，重启后丢失；同步服务收不到回调时会重新发起或检查仓库。

## 8. 配置与部署

| 配置 | 来源 | 说明 |
| --- | --- | --- |
| `GITHUB_APP_ID`、`GITHUB_APP_PRIVATE_KEY` | Secret | StarMoe-org 的 App，与同步服务共用 |
| `MOE_HMAC_SECRET` | Secret | 与同步服务共用 |
| `QOOAPP_TOKEN` | Secret | jp 下载 |
| `APP_META_REPO` | 配置 | 默认 `StarMoe-org/moesekai-app-meta` |
| 地区表 | 配置 | 包名、QooApp 条目 id、官网链接、是否 Nuverse |

- **部署位置**：JP2，`apps` 命名空间，单副本。
- **镜像**：.NET 运行时 + Il2CppDumper；在 MetaSekaiLab 仓库的 Actions 里构建，推到公开的 GHCR，JP2 不用凭据就能拉取。
- **资源**：内存上限约 3GB（cn 的包 586MB，加上 Il2CppDumper），按 StarMoe 要求必须设置 request 和 limit；临时存储约 3GB。
- **待验证**：JP2 能否访问 cn 官网链接和 QooApp。

## 9. 未决项与待验证

| # | 问题 | 当前打算 |
| --- | --- | --- |
| Q1 | 密钥字段的定位方法（§5.5） | 先用已知哈希跑通，同时实现「反汇编 `APIManager..cctor`」，两者结果对得上再去掉哈希表 |
| Q2 | ~~`compact*` 表和 SuiteMaster 之外的顶层键怎么描述~~ | **已解决（M2）**：compact 表就是 SuiteMaster 的成员（按列存放的类），见 §4.4；SuiteMaster 之外的表行都是对象，原样输出 |
| Q3 | Nuverse 服 API 请求实际用 [0] 还是 [1] | 等同步服务拿到 cn 账号 accessToken、实测 `user/auth` 时确认；不影响 builder |
| Q4 | QooApp 上 jp 的分包里，`assets/bin/Data` 是否完整在主包 | 用真实下载验证；不完整就按虚拟文件系统在所有分包里查找 |
| Q5 | AssetsTools.NET 读取没有 typetree 的 MonoBehaviour | 按 §5.3 的固定字段顺序手工读；用 jp 7.1.0、cn 6.4.1 的结果与 workflow（Python）对照 |
| Q6 | JP2 出网 | 部署前测试 cn 官网链接、QooApp |

## 10. 测试

- **样本**（本机已有，在 `~/pjsk-analysis/`，不进仓库）：jp 7.1.0（workflow Release）、hmt 6.4.0（tw 原始包）、cn 6.4.1（官网）。
- **对照基准**：
  - 身份：与 workflow `identity.py` / sssekai `apphash` 的结果一致；
  - 密钥：SHA-256 前缀与 §5.5 的表一致；cn 的 [0] 能解开 master 186（PKCS7 正确，424 个顶层键）；
  - 结构文件：与 Haruki 生成器的输出逐类、逐字段比对，差异只能是 §5.6 列出的已知项；用 master 186 逐表核对行宽和类型。
- **单元测试**：AXML 解析、metadata 解密（用构造数据）、版本比较、更新规则全部分支、0/1 位串编解码、内容检查、HMAC 签名与时间窗、回调重试。
- **发布测试**：用本地裸仓库当作产物仓库，覆盖 §4.5 每种情况和 push 被拒后的重试。

## 11. 里程碑

| 里程碑 | 内容 | 完成标志 |
| --- | --- | --- |
| M1 核心（CP） | 虚拟文件系统、AXML、metadata 解密（jp）、身份提取、Il2CppDumper 调用、密钥提取（已知哈希）、`build`/`inspect` | jp 7.1.0 输出的 meta.json 与 workflow 一致，moe-info [0] 哈希正确 |
| M2 Nuverse | Nuverse 格式 metadata 解密、`data.unity3d` 身份、结构文件生成、密钥 [1]、Q1 反汇编方案 | cn 6.4.1 结构文件与 Haruki 输出比对通过，master 186 逐表核对通过 |
| M3 发布 | GitHub App token、更新规则、内容检查、`publish`/`run` | 本地裸仓库覆盖全部规则；对真实 `moesekai-app-meta` 做一次 dry-run |
| M4 下载源 | 官网链接、QooApp、期望 versionCode、错误分类 | 实际下载 cn 和 jp 并跑通 `run --dry-run` |
| M5 服务 | `serve`、HMAC、排队、回调、镜像、部署 JP2 | 用 curl 模拟同步服务发起请求，收到回调，仓库出现对应提交 |
