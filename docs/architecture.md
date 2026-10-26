# NPMC Launcher 架构大纲

> 非官方第三方 Minecraft: Java Edition 启动器 · C# / .NET 8 / Avalonia
> 定位：**个人自用、不公开发布**。这个定位决定了绝大部分合规负担可以省去，
> 但"不得绕过身份验证"和"不分发游戏本体"这两条是架构上的硬约束。

本文档中标注 **【实测】** 的结论都经过真实请求验证，不是推断。

---

## 1. 目标与边界

| 项 | 定版 |
|---|---|
| 平台 | Windows only |
| 运行时 | .NET 8 (LTS)，`net8.0` / `net8.0-windows` |
| UI | Avalonia 11 + MVVM（CommunityToolkit.Mvvm） |
| 分发 | **不分发**。仅本机使用 |
| 账号 | 离线优先；正版登录受外部审批阻塞（见 §9） |
| 游戏本体 | **永不打包**。运行时按 manifest 下载 |
| 便携性 | 根目录全部相对推导，整个文件夹可整体迁移 |

---

## 2. 分层与工程结构

```
NPMC-Launcher\
├─ NPMC.Launcher.sln
├─ Directory.Build.rsp           # -maxcpucount:1（本机 SDK 必需，见 §11）
├─ build.ps1                     # 构建入口，已固化上述参数
├─ docs\
│  └─ architecture.md            # 本文档
├─ src\
│  ├─ NPMC.Launcher.Core\            ← 纯领域，零第三方依赖
│  │  ├─ Models\VersionJson.cs       # 版本元数据模型 + arguments 多态转换器
│  │  ├─ Rules\RuleEvaluator.cs      # rules 求值 + OsInfo + FeatureSet
│  │  ├─ Paths\LauncherPaths.cs      # 全部路径从根目录推导
│  │  └─ Launch\LaunchPlan.cs        # classpath / 参数展开 / LaunchPlan
│  ├─ NPMC.Launcher.Infrastructure\  ← 实现层
│  │  ├─ Http\HttpFileDownloader.cs             # SHA1 + 原子落盘 + 跳过已存在
│  │  ├─ Minecraft\VersionManifestSource.cs     # 版本清单 / 版本 JSON
│  │  └─ Runtime\MojangRuntimeInstaller.cs      # Mojang 官方 JRE 安装器
│  ├─ NPMC.Launcher.Cli\             ← 命令行探针（M0 工具）
│  │  └─ Program.cs                  # probe / install / runtime / launch
│  └─ NPMC.Launcher.App\             ← Avalonia 前端（占位，待接入）
└─ tests\
   └─ NPMC.Launcher.Core.Tests\      ← 单元测试（xunit，50 个用例）
```

**依赖方向严格单向**：`App → Infrastructure → Core`，`Cli → Infrastructure → Core`。
Core 不引用任何外部包，也不做 I/O。

### 构建状态

| 工程 | 构建 | 说明 |
|---|---|---|
| Core | ✅ | 0 警告 0 错误 |
| Infrastructure | ✅ | 0 警告 0 错误 |
| Cli | ✅ | 0 警告 0 错误，已实测联网跑通 |
| App | ✅ | 占位入口；接入 Avalonia 需联网 restore |
| Tests | ✅ | 50 个用例全部通过，含真实 1.20.1 元数据回归测试 |

---

## 3. 核心域模型

```
VersionJson      id, type, mainClass, inheritsFrom, assets, assetIndex,
                 downloads{client}, libraries[], arguments{game[],jvm[]},
                 minecraftArguments, javaVersion{component,majorVersion},
                 logging{client{argument,file}}, minimumLauncherVersion

Library          name, downloads{artifact,classifiers}, rules[], natives{}

ArgumentEntry    rules?[], values[]      // 字符串或 {rules,value} 多态

Rule             action, os{name,arch,version}, features{}

LaunchContext    playerName, uuid, accessToken, clientId, xuid, userType,
                 launcherName, launcherVersion, width, height

LaunchPlan       javaPath, workingDirectory, mainClass, jvmArguments[], gameArguments[]

RuntimeComponent name, platform, version, manifestUrl, manifestSha1, manifestSize
```

---

## 4. 已实测的关键事实（设计依据）

这些数字和结论都是实测得到的，是后续所有实现的基准。

### 4.1 1.20.1 元数据【实测】

| 项 | 值 |
|---|---|
| libraries 总数 | 88 |
| Windows x64 按 rules 生效 | **64** |
| classpath 条目 | **65**（64 库 + client.jar） |
| 缺 `downloads.artifact` 的库 | **0**（原版不需要 maven 兜底） |
| 含 `natives` 字段的库 | **0**（现代版本无需解压 natives） |
| client.jar | 21.96 MB |
| libraries 体积 | 58.06 MB |
| assetIndex 对象数 | **3598**，621.54 MB |
| 一次完整安装 | **约 701.55 MB** |
| javaVersion | major **17**，component `java-runtime-gamma` |
| mainClass | `net.minecraft.client.main.Main` |

### 4.2 参数展开基准【实测】

| 项 | 值 |
|---|---|
| JVM 参数（含 -Xms/-Xmx/log4j） | **12** |
| 游戏参数 | **26** |
| 未解析占位符 | **0** |

命中与排除（已验证）：
- `-XX:HeapDumpPath=...`（`os.name=windows` 命中）✅
- `-XstartOnFirstThread`（`os.name=osx`）正确排除 ✅
- `-Xss1M`（`os.arch=x86`）正确排除 ✅
- `--width 1280 --height 720` 仅在 `has_custom_resolution` feature 为真时出现 ✅
- `--demo` 与 quickPlay 系列正确排除 ✅

### 4.3 Java 运行时【实测】

| 项 | 值 |
|---|---|
| 清单入口 | `launchermeta.mojang.com/v1/products/java-runtime/<hash>/all.json` |
| 清单结构 | `all[platform][component][0]` → `{manifest{url,sha1,size}, version{name}}` |
| windows-x64 组件 | `jre-legacy`(8) / `java-runtime-alpha`(16) / `-beta`(17) / `-gamma`(17) / `-delta`(21) / `-epsilon`(25) |
| gamma 体量 | **486 条目 = 403 file + 83 directory + 0 link**，**86.9 MB** |
| delta 体量 | 484 条目 / 94.5 MB |
| 串行下载耗时 | **116.9 秒**（≈3.4 文件/秒）→ 必须并发 |
| 可迁移性 | **通过**：移到带空格与中文的路径后仍可运行 |
| 入口 | `<root>\runtime\<component>\bin\java.exe`（仅 38.5 KB，是 stub） |

**三个必须遵守的结论：**
1. `bin\java.exe` 是 stub，真正的 JVM 在同目录 `bin\server\jvm.dll` → **必须整包安装、整包迁移**
2. 进度必须按**字节**统计（`legal\` 下 150+ 个几 KB 文件会让"文件数进度"假性卡死）
3. `legal\` 目录是 GPLv2+CE 分发要求的许可证文本，**不要为了减体积删掉**

### 4.4 下载源【实测】

| 源 | 形态 | 关键结论 |
|---|---|---|
| Mojang 官方 | 直连静态文件 | 支持 Range（206） |
| **BMCLAPI** | **302 重定向链 + 签名 URL** | 不能当字符串替换用 |
| Modrinth | 直连 JSON | 免 key，字段完备 |
| CurseForge | 直连 JSON | **必须申请 apiKey**（无 key 返回 403） |
| Fabric meta | 直连 JSON | 免 key，直接给 mainClass 与库清单 |

BMCLAPI 拓扑（实测）：

```
/mc/game/version_manifest_v2.json → 302 签名节点(bmclapiN.*.sld.tw / .hurtmc.com)
/version/<id>/json                → 302 /v1/packages/<sha1>/<id>.json → 签名节点
/maven/<path> ＝ /libraries/<path> → 302 mirrors.cernet.edu.cn/bmclapi/<path>
                                     → 302 mirror.nju.edu.cn/... （CERNET 负载均衡）
/assets/<前2位>/<hash>            → 302 签名节点
/v1/packages/<sha1>/<file>        → 302 签名节点（assetIndex 可走这里）
/v1/objects/<sha1>/client.jar     → 302 piston-data.mojang.com（回源官方）
/v1/objects/<sha1>/client-1.12.xml→ 404（log4j 配置必须走官方！）
```

### 4.4.1 实测补充（2026-10，`npmc mirror test` 采集）

| 资源 | BMCLAPI 行为 | 结论 |
|---|---|---|
| 版本清单 | 1 跳 302 → `bmclapiN.sakura.sld.tw` | 可用 |
| 版本 JSON | 1～2 跳 → 签名节点 → 有时再跳 S3 预签名 | 可用但不稳定 |
| client.jar | 1 跳 302 → **`piston-data.mojang.com`** | **走镜像零收益**，只多一次往返 |
| 库 | 1 跳 302 → 志愿者节点（`*.openbmclapi.933.moe:4000` 等） | 可用 |
| assetIndex | 1 跳 302 → 签名节点 | 可用 |
| 资源对象 | 1 跳 302 → 签名节点 | 可用 |
| log4j 配置 | — | **404，必须官方** |

由此新增四条约束：

1. **分发节点每次不同**（`bmclapi1/2/6/17/19/24.sakura.sld.tw`、`openbmclapi.933.moe`、`minio.*.xyz`），
   且 URL 带 `?name=&s=&e=` 签名与过期时间 → **最终地址绝不可缓存**，每次都要从入口重新解析。
2. **`client.jar` 必须直连官方**：镜像只是把它 302 回 `piston-data`，反而多一跳。
3. **Range 支持因节点而异**：实测有节点对同名请求返回 `200` 而非 `206` →
   断点续传必须能容忍"服务端不支持 Range，退回整包重下"。
4. **镜像节点偶发 TLS 握手失败**：全新安装 1.20.1 时，66 个可镜像文件里有 **4～7 个
   （约 6%～11%）** 首次请求报 `The SSL connection could not be established`。
   同一文件重试或退回官方即可成功。**没有候选回退的镜像实现会随机装坏** ——
   这也是 `HttpFileDownloader.EnsureFromAsync` 存在的直接原因；
   `npmc install` 会打印"镜像 N / 官方 M"和逐条回退记录。
4. **代理环境会干扰判断**：本机挂 TUN 时 DNS 会返回 `198.18.0.0/15` 的 fake-IP，
   但这**只说明 TUN 接管了 DNS**，不代表流量走代理 —— DIRECT 规则下 DNS 依然返回假地址
   （`fake-ip-filter` 才影响 DNS 答案，规则只影响走向）。
   判断规则是否生效要看实测延迟，或代理软件的连接日志。`npmc doctor` 会同时打印
   "系统解析（可能是 fake-IP）"与"公共 DoH 解析（真实 A 记录）"以便对照。

**因此不要把下载源写死**：`MirrorRouter` 只负责候选排序，真正的选择应交给运行时的
连通性自测（`MirrorProbe`）—— 这样 TUN 开关、所在地区、运营商差异都不需要在代码里预判。

**三条由此产生的架构约束：**
1. 下载器核心能力是"URL 重写 **+ 跟随重定向**"，不是纯字符串替换
2. 签名 URL 带 `s=`(签名) `e=`(过期)，**绝不能持久化**；断点续传按"已完成字节数"恢复，不按 URL
3. 资源要分两类：**可镜像**（manifest / version json / libraries / assets / assetIndex）与**必须官方**（log4j 配置）

---

## 5. 下载子系统

```
DownloadTask   url → 目标路径, 期望 sha1, 期望 size, 优先级
MirrorRouter   官方 URL → 镜像入口 URL（按用户偏好排序，可回退）
HttpFileDownloader.EnsureAsync(url, dest, sha1)
                ├─ 已存在且 SHA1 匹配 → 跳过（返回 false）
                ├─ 下载到 <dest>.tmp
                ├─ 校验 SHA1，不符则删除并抛错
                └─ File.Move(tmp, dest, overwrite)   ← 原子落盘
```

- 并发：`SemaphoreSlim`，默认 12（JRE 实测 403 文件）
- 重试：建议 3 次指数退避
- 进度：**双维度（字节优先）**
- 限流：**按最终域名**限流，不是按 `bmclapi2`（真实压力落在 CERNET / 签名节点 / piston-data）

---

## 6. 启动流水线

```
1. 解析版本 JSON（沿 inheritsFrom 合并 —— Forge/Fabric 需要，原版不需要）
2. rules 求值 → 过滤 libraries 与 arguments
3. 增量校验并补全文件（走下载器）
4. natives 目录：新建空目录即可
   （现代版本无 natives 字段；-Djava.library.path 必须指向存在的目录，
     LWJGL 运行时自行解压到 -Dorg.lwjgl.system.SharedLibraryExtractPath）
5. 选 Java：runtime\ <javaVersion.component> \bin\java.exe → 实例覆盖 → 系统探测
6. 组装 classpath：client jar 放最前 + 生效库
7. 模板替换 → JVM 参数 + mainClass + 游戏参数
8. 启动进程（ProcessStartInfo.ArgumentList，不要手拼字符串）
```

### rules 求值语义（最容易写错的地方）

1. 无 rules（null 或空数组）→ **允许**
2. 有 rules → 从 `false` 开始
3. 按数组顺序逐条判断，命中的条目把结果**覆盖**为 `action == "allow"`
   —— 后续命中优先，不是 allow 简单压过 disallow
4. `os.version` 是**正则**（`^10\.` 匹配 `10.0`），不是等值比较
5. `features` 里为 `true` 的键必须被 FeatureSet 启用

### 占位符全集

`${auth_player_name}` `${version_name}` `${game_directory}` `${assets_root}`
`${assets_index_name}` `${auth_uuid}` `${auth_access_token}` `${clientid}`
`${auth_xuid}` `${user_type}` `${version_type}` `${natives_directory}`
`${launcher_name}` `${launcher_version}` `${classpath}` `${classpath_separator}`
`${library_directory}` `${resolution_width}` `${resolution_height}`
`${quickPlayPath}` `${quickPlaySingleplayer}` `${quickPlayMultiplayer}` `${quickPlayRealms}`

> 每次组装完都应做一次残留检查（`FindUnresolvedPlaceholders`），有残留即视为 bug。
>
> **Forge 1.17+ 会用 `${library_directory}` 与 `${classpath_separator}`**（拼 `-p` 模块路径和
> `-DignoreList`）。这两个占位符 2026-10-26 才真正在 `ArgumentBuilder.CreateDefault` 里设上 ——
> 在那之前文档列了、代码没设，表现是 Forge 装好了也起不来（JVM 收到字面量 `${...}`）。

### 关键实现细节

```csharp
var psi = new ProcessStartInfo(javaPath)
{
    WorkingDirectory = gameDir,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardOutputEncoding = Encoding.UTF8,   // 否则中文日志乱码
    StandardErrorEncoding = Encoding.UTF8,
};
foreach (var a in plan.ToProcessArguments()) psi.ArgumentList.Add(a);
```

---

## 7. 实例与隔离（已实现）

三级隔离，**默认「版本隔离」**：

| 级别 | gameDir | mods / 资源包 / 光影 / 存档 | versions / libraries / assets |
|---|---|---|---|
| 无隔离 `None` | 启动器根目录 | 共享 | 共享 |
| **版本隔离 `Version`（默认）** | `instances\<Id>\` | **独立** | 共享 |
| 完全隔离 `Full` | `instances\<Id>\` | 独立 | **独立**（整个实例可单独拷走） |

### 实现结构

| 文件 | 职责 |
|---|---|
| `Core/Instances/LauncherInstance.cs` | 实例模型 + `IsolationLevel` + `InstanceSettings` |
| `Core/Instances/InstanceId.cs` | 显示名 → 目录名（保留中文，只替换非法字符）、同名去重、合法性校验 |
| `Core/Instances/InstanceLayout.cs` | 由实例推导全部路径：`GameDirectory` / `SharedRoot` / 各子目录 |
| `Core/Instances/InstanceCatalog.cs` | 增删改查 + 校验（纯逻辑，无 I/O） |
| `Core/Abstractions/IInstanceStore.cs` | 持久化抽象（Core 不关心落盘格式） |
| `Infrastructure/Storage/JsonInstanceStore.cs` | JSON 落盘，枚举与中文都保持可读 |
| `Infrastructure/Storage/AtomicFile.cs` | 临时文件 + rename 的原子写入 |

### 关键设计点

- **`LauncherPaths` 有两个根**：`Root`（runtime、instances 固定在此）与 `SharedRoot`
  （versions / libraries / assets 的根）。`InstanceLayout.Paths` 按隔离级别设置 `SharedRoot`，
  于是"完全隔离"只改一处就能把库与资源搬进实例目录。
- **`OwnsGameDirectory` 是删除的安全闸**：无隔离实例的游戏目录就是启动器根目录，
  必须拒绝删除。CLI 里 `instance remove --delete-files` 会检查它，并且再校验目标路径
  确实位于 `instances\` 之下。
- **`instances.json` 可手改**：缩进 + 枚举写成字符串 + 中文不转义（`UnsafeRelaxedJsonEscaping`）。
- **列表顺序 = `SortIndex` → 名称**：`SortIndex` 由界面拖动写入（0..n-1），旧数据全为 0 时
  等价于"按名称排序"；新建实例在已有显式顺序时取 `max+1` 排到最后。
  拖动的实现细节（ListBoxItem 会吃掉 PointerPressed、必须用 Tunnel 接管；
  用实时 `ObservableCollection.Move` 而不是 OLE 拖放）见 handoff 10.6。
- **Id 就是实例目录名**：改名会同时改显示名与目录（`InstanceCatalog.Relocate` +
  `Directory.Move`，失败回滚）；纯改显示名的旧行为已废弃，旧数据里"名字与目录不一致"的实例，
  再保存一次同名即可把目录同步过去。
- **目录里的 `.nmpc-instance.json` 是身份标记**：`InstanceReconciler` 用它把
  "用户在资源管理器里改的文件夹名"反向同步成实例 Id / 显示名（应用启动与 CLI `instance`
  命令都会先对账）；没有标记、又不在列表里的目录只报告"未纳管"，不会自动收编。

### 目录访问入口

`mods` / `resourcepacks` / `shaderpacks` / `saves` / `config` / `logs` / `crash-reports`
全部由 `InstanceLayout` 推导，UI 侧「打开文件夹」直接取这些属性。

### CLI

```
npmc instance list
npmc instance create <名字> --version <版本> [--isolation none|version|full]
npmc instance paths  <Id>
npmc instance remove <Id> [--delete-files]
npmc launch <版本> --instance <Id>
```

---

## 8. 设置项（两级继承，已实现）

**全局**放在 `<root>\settings.json`（`JsonSettingsStore`，原子写入）。界面入口是左侧「设置」，
CLI 是 `npmc settings show` / `npmc settings set <键> <值>`。

| 项 | 字段 | 生效时机 |
|---|---|---|
| 首选下载源 | `PreferredSource`（Official / Bmclapi） | 每次建安装器时读 → 立即生效 |
| 镜像地址 / 失败回退 | `BmclapiBaseUrl` / `AllowSourceFallback` | 同上 |
| 并发下载 | `DownloadConcurrency`（1–64） | 同上；预下载 Java 运行时也用它 |
| 请求超时 | `HttpTimeoutSeconds`（30–3600 秒） | **启动时**建 HttpClient 时设定 → 重启生效 |
| 代理 | `HttpProxy`（如 `http://127.0.0.1:7890`；空 = 直连） | **启动时** → 重启生效 |
| 默认内存 | `DefaultMemoryMb` | 新建实例的初始内存；实例内存 ≤0 时也是启动兜底 |
| 全局默认 Java | `DefaultJavaPath` | 启动计划：实例 JavaPath → 实例 JavaComponent → 全局默认 → 版本元数据 |
| 语言 | `Language` | 目前只影响 Modrinth 描述的翻译语种；界面文案未抽，只有简体中文 |

实例级设置（`instances.json`）覆盖内存 / 窗口 / Java / 额外 JVM 参数；两侧各写各的文件，互不覆盖。

界面约定：**改一项立刻存**（没有"保存"按钮，文件只有几 KB），底部显示"已保存 · 时间"；
另有「恢复默认」「打开设置文件所在目录」。代理与超时在控件下方明确写"重启后生效"，
不要假装能热切换 —— HttpClient 建好之后改不了这两项。

`--exercise-global-settings` 会走一遍：读文件 → 改字段 → 落盘 → 直接读文件核对 →
验证全局默认 Java 进了启动计划 → **把原设置写回去**（诊断不该改掉用户的配置）。

---

## 8.5 离线账号（已实现）

| 文件 | 职责 |
|---|---|
| `Core/Accounts/PlayerAccount.cs` | 账号模型 + 离线 UUID（MD5 + UUID v3/variant） |
| `Core/Accounts/AccountCatalog.cs` | 增删改查、用户名唯一校验（纯逻辑，无 I/O） |
| `Core/Abstractions/IAccountStore.cs` | 持久化抽象 |
| `Infrastructure/Storage/JsonAccountStore.cs` | `<root>\accounts.json`，原子写入、可手改 |

- **启动时的账号解析**：`instance.Settings.AccountId` → `settings.json` 的 `DefaultAccountId`
  → 账号列表第一个；都没有时用 `Player` 兜底，不阻断启动。
- 解析出的账号交给 `GameLauncher.Prepare(..., account)`，由
  `LaunchContext.Offline(username, uuid)` 填入 `${auth_player_name}` / `${auth_uuid}`；
  `user_type=legacy`、`access_token` 为占位值（离线不需要真令牌）。
- 账号 Id 是随机 GUID（不是用户名），改用户名只重算 UUID、不破坏实例的 `AccountId` 引用；
  删除账号时如果它正好是全局默认，会同时清空 `settings.json` 里的 `DefaultAccountId`。
- 新建账号是"空档案 → 填表 → 自动落盘"：草稿只存在内存里（`IsCreatingAccount`），
  用户名合法后延迟 700ms 自动写入 `accounts.json`；空名取消/离开页面不会产生账号。
- 皮肤字段：`Skin`（Steve/Alex/Custom/MojangName）、`SkinPath`（`<root>\cache\skins\*.png`）、
  `SkinModel`（classic/slim）。`MojangSkinClient` 走
  `api.mojang.com/users/profiles/minecraft/{name}` → `sessionserver.../profile/{id}`
  → 解码 textures 属性 → 下载皮肤 PNG。离线客户端不会自动用这些皮肤，
  进游戏显示仍需皮肤站 / 模组（authlib-injector 后续接入）。
- 微软 / authlib-injector 的令牌不会写进 `accounts.json`；后续接它们时单独存受保护位置。

## 9. 正版登录（受外部阻塞）

### 完整链路

```
1. Microsoft OAuth 2.0 设备码    login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode
                                  scope = XboxLive.signin offline_access
2. XBL 用户认证                  POST user.auth.xboxlive.com/user/authenticate
3. XSTS 授权                     POST xsts.auth.xboxlive.com/xsts/authorize
                                  RelyingParty = rp://api.minecraftservices.com/
4. 换 Minecraft 令牌             POST api.minecraftservices.com/authentication/login_with_xbox
5. 游戏所有权校验                GET  api.minecraftservices.com/entitlements/mcstore
6. 账号档案                      GET  api.minecraftservices.com/minecraft/profile
```

### 已实测的配置陷阱【实测】

| 现象 | 原因 | 修法 |
|---|---|---|
| `AADSTS50059`（/common 无法确定租户） | 应用是单租户 | 改「受支持的帐户类型」为「任何 Entra ID 租户 + 个人 Microsoft 帐户」 |
| `AADSTS700016`（/consumers 找不到应用） | 同上 | 同上 |
| 改账户类型时报 `api.requestedAccessTokenVersion is invalid` | 个人账号只支持 v2 令牌 | 清单里 `requestedAccessTokenVersion` 改为 `2` |
| `AADSTS70011`（scope 未在租户中配置） | **Xbox Live 不在「我的组织使用的 API」列表** | 见下 |

### 当前阻塞点

Microsoft 官方答复明确：**`XboxLive.signin` 权限只有在应用完成 Microsoft Partner Center 接入后才会出现在 API 列表里**。新建的个人租户中，「我的组织使用的 API」只有 10 项（IAM Supportability、Microsoft Graph、Azure Resource Manager 等），没有任何 Xbox 相关条目。

因此门槛是两道：

```
① 拿到 XboxLive.signin 权限   ← 当前卡在这里（AADSTS70011）
② login_with_xbox 被放行       ← 已有多起 403「Invalid app registration」报告
```

### 处置

- 已提交 `aka.ms/AppRegInfo` 申请（每周审一次）
- 代码侧把登录关在 `IAuthProvider` 接口后：`OfflineAuthProvider` 立刻可用，`MicrosoftAuthProvider` 待审批
- **离线模式不调用任何 Minecraft Services API，也不使用该 client id**，因此在技术上完全独立
- 硬红线：**不得用离线模式连接 online-mode 服务器**

---

## 10. 数据与配置（待实现）

```
<root>\
  settings.json      全局设置（只存相对路径与组件名）
  instances.json     实例索引
  accounts.json      账号（refresh token 用 DPAPI 加密，CurrentUser）
  logs\              启动器自身日志
```

写入一律走「临时文件 → `File.Replace`」，断电不损坏配置。

---

## 11. 已知环境问题

| 问题 | 现象 | 处置 |
|---|---|---|
| **并行 MSBuild 崩溃** | `dotnet build` 报「生成失败 / 0 错误」，无任何错误输出 | 必须 `-maxcpucount:1 -nodeReuse:false`；已写入 `Directory.Build.rsp` 与 `build.ps1` |
| 执行策略禁止运行 .ps1 | 「未对文件进行数字签名」 | `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1` |
| `dotnet new` 写用户目录被沙箱拦 | `.templateengine` 访问被拒 | 重定向 `DOTNET_CLI_HOME` / `NUGET_PACKAGES` 到工程内 |
| 菱形项目引用还原失败 | 同一项目被直接与间接引用时还原失败 | Cli 只引用 Infrastructure，Core 走传递引用 |
| NuGet 缓存空 | Avalonia / xunit 无法离线 restore | 需联网执行 restore |
| **vstest testhost 崩溃** | `dotnet test` 直接中止，报 `Win32Exception (5): 拒绝访问`——testhost 在 `SetParentProcessExitCallback` 里无法 `OpenProcess` 父进程。**受限与提权两种模式下都复现（已实测）** | 用内置反射 runner 绕过 vstest：`dotnet build -p:SelfTestRunner=true` 后直接运行测试工程 exe。换到真实用户环境仍可用 `dotnet test` |
| **`.gitignore` 大小写误伤源码** | `core.ignorecase=true` 时，为运行时数据写的 `instances/`、`runtime/` 会同时匹配源码目录 `Core/Instances/`、`Infrastructure/Runtime/`，把新文件静默排除在提交之外（已实际发生：`MojangRuntimeInstaller.cs` 从首次提交起就没进过仓库，导致远端工程一直编译不过） | 运行时数据模式必须**锚定到仓库根**：`/.data/` `/runtime/` `/instances/` …… 提交前用 `git status --ignored=matching -- src tests` 核对，只应看到 `bin/`、`obj/` |
| **临时目录不完全可用** | 同一套测试在受限模式下 6/6 通过，在 `danger-full-access` 下 3/3 失败，报 `UnauthorizedAccessException`（在真实用户 `%TEMP%` 里建目录）；同样提权下 `npmc doctor` 又能成功建目录。**机制已确认**：受限模式的令牌是 Low（工作区被标 Low），而 `%TEMP%` 是 Medium —— Low 写 Medium 属于"向上写"，被 `NO_WRITE_UP` 拦；`npmc doctor` 只写自己目录，所以成功 | 结论不变：**测试与工具不要依赖 `%TEMP%`**。测试统一用测试程序集所在目录下的 `test-scratch/`（见 `TestTemp`）——构建产物已写进 bin，证明该目录必然可写 |
| **`OpenProcess` 部分受限** | 受限模式下：对自身 PID 设置 `EnableRaisingEvents`、用 CIM 查父进程都返回"拒绝访问"，testhost 因此崩溃。提权后：**打开自身进程句柄恢复可用**，但 testhost 打开**父进程**句柄仍然失败 | 用 `npmc doctor` 实测当前环境（它直接报告"进程句柄 可用/被禁止"）。真实用户环境是 Medium 完整性，不受此限；本机已实测游戏进程的 `WaitForExitAsync` 在提权下可正常工作 |
| **自建 apphost 被降权到 Low 完整性** | 双击或从任意终端启动 `NPMC.Launcher.App.exe`，进程令牌都是 `S-1-16-4096`（Low），即使父进程是 Medium 的 PowerShell 或 Explorer。Low 会**同时**禁掉 `SHOpenFolderAndSelectItems`（`0x80070005`）、ShellExecute（"拒绝访问"）和 explorer.exe（`0xC0000142`），于是"打开文件夹"必然失败。显式 `asInvoker` 清单、自签名证书**都改变不了**这一点 | 根因**不是清单**：Windows 创建进程时取「令牌级别」与「镜像文件标签」中较低者，而工作区被标成 Low 后，构建出来的 `NPMC.Launcher.App.exe` 文件本身带 Low 标签。`dotnet.exe` 在 `C:\Program Files` 下、没有 Low 标签，所以 `dotnet NPMC.Launcher.App.dll` 得到 Medium。修复：`icacls NPMC.Launcher.App.exe /setintegritylevel Medium`（重建后会再次继承 Low），或继续用 `launch.cmd` |
| **未签名 exe 触发 SmartScreen** | 自建 exe 每次启动弹「Windows 已保护你的电脑」。自签名 + 导入受信任存储后，弹窗里的发行者由"未知发布者"变成证书名（说明签名链路有效），但**仍然拦截** —— SmartScreen 还有一层**信誉**判定，自签名证书永远拿不到信誉 | 自签名只能消除"未知发布者"，无法通过信誉检查。实际可用的做法是走 `dotnet` 宿主（`dotnet.exe` 已受信任），或每次点「仍要运行」；要真正发布则需 OV/EV 代码签名证书（EV 要企业资质） |
| **Modrinth 禁止使用其品牌图形** | `modrinth/code` 的 `COPYING.md` 明确写着：未经 **Rinth, Inc.** 书面许可，禁止使用其品牌元素（"including but not limited to the wrench-in-labyrinth logo … and **any variations thereof**"），且 fork 仓库时必须删除这些资源。也就是说"改个颜色/做个单色版"同样不允许 | 界面里**不使用任何平台 logo**，改用填充色块 + 字母标识来源：`M`（绿 #2E7D46）/ `C`（红 #A93226）/ `?`（灰 #4D5560）。这属于指示性使用（说明文件来自哪个平台），不是商标性使用；需要官方 logo 只能向 Rinth 书面申请 |
| **CurseForge 指纹的"空白"定义容易搞错** | CurseForge 用 MurmurHash2（seed=1），计算前跳过空白字符。但"空白"是 4 个还是 6 个字符，社区实现并不一致。**实测：只跳过 `\t \n \r 空格` 才与服务端一致**；多跳 `\v`(0x0B) `\f`(0x0C) 会让任何 jar 的指纹都算错 —— 因为二进制文件里这两个字节随机出现，第一次实测就因此全部未命中 | `MurmurHash2` 提供 `Standard` / `Extended` / `None` 三种过滤器，**默认 Standard**。`npmc mods cf-probe <文件>` 会把几种变体各发一次请求，用真实响应确认哪种被命中（诊断用，4 次请求） |
| **PowerShell 双引号吃掉 CurseForge key** | CurseForge 的 API key 形如 `$2a$10$...`（bcrypt 格式）。用 `setx X "$2a$10$..."` 设置时，双引号里的 `$Jjcs...` 会被 PowerShell 当成变量展开成空串，key 被静默改坏；之后 .NET 只在发请求时抛 `Request headers must contain only ASCII characters`，完全指不到根因 | 用**单引号**：`setx CURSEFORGE_API_KEY '$2a$10$...'`，或 `[Environment]::SetEnvironmentVariable('CURSEFORGE_API_KEY','$2a$10$...','User')`。`CurseForgeClient.TryResolveApiKey` 会先剔除 BOM/零宽字符并校验 ASCII，遇到非 ASCII 直接给出"请用单引号"的提示 |

---

## 12. 里程碑与状态

| 阶段 | 内容 | 状态 |
|---|---|---|
| **M0** | 手工拼参数启动 1.20.1 | ✅ **参数链已验证**（64/65/12/26/无残留）；实际弹窗待本地跑 |
| M0.5 | 内置 Java 运行时 | ✅ **下载/校验/运行/迁移全部验证** |
| M1 | 原版安装 + 启动 + 日志窗口 | 🚧 Cli 已有 install / launch，UI 待接入 |
| M2 | 镜像切换 + 内存/窗口设置 | ⏳ 参数层已就绪 |
| M3 | 实例管理与三级隔离 | ✅ 模型 / 目录 / JSON 持久化 / CLI 均已完成，28 个相关测试通过 |
| M4 | Modrinth 搜索与依赖安装 | ⏳ API 已核实 |
| M5 | 正版登录 | 🔒 **阻塞于外部审批** |
| M6 | Forge / Fabric / NeoForge 安装 | ✅ Fabric 直接合成 profile；**Forge ≤1.12 解安装包、1.13+ 跑官方安装器**（`--installClient`，需 Java）；**NeoForge 同一套官方安装器**（1.20.1 用 `net/neoforged/forge` 老坐标） |
| M7 | 整合包导入（`.mrpack` 优先） | ⏳ |

---

## 13. 下一步

**已完成**：M0 参数链与启动（实际进世界并正常存档）、内置 Java 运行时（可整体迁移）、
3598 个资源对象并发下载、实例管理与三级隔离、镜像路由与候选回退（`install` / `probe` / `launch`
已全部走 `MirrorRouter`，含同候选重试）、全局设置持久化（`settings.json`）、运行时源自测
（`npmc net selftest`）、环境诊断（`npmc doctor`）；xunit 回归测试 112 用例。

待做，按依赖顺序：

1. 接入 Avalonia（需联网 restore）与 MVVM 骨架
2. 正版登录：等 `aka.ms/AppRegInfo` 审批，通过后接 `MicrosoftAuthProvider`
3. Modrinth 搜索与依赖解析
4. 运行时文件（JRE）镜像化 —— 实测该路径一律 302 回官方 `piston-data`，收益为零，暂不接
5. 大文件分片并发下载（client.jar 22 MB 目前是单连接）

## 启动链路：已确认的问题与结论（2026-10-25）

### 已修复

| 问题 | 症状 | 结论 |
|---|---|---|
| classpath 只收 `downloads` 的库 | `找不到主类 …KnotClient` | Fabric/Quilt 的库只有 `name`（Maven 坐标）+ `url`，必须按坐标推路径；名字不是坐标时跳过，不能抛异常 |
| client jar 放在 classpath 最前 | 同上（顺序影响 Fabric 定位游戏 jar） | 与官方启动器一致，放**最后** |
| 子进程 stdout/stderr 被丢弃 | 只看得到"退出码 1" | 早期 JVM 失败不写 `latest.log`；现在落盘 `logs/nmpc-stdout.log` / `nmpc-stderr.log`，失败时显示尾部 |
| 同一实例可被启动两次 | 第二个进程拿不到 `session.lock`，退出码 1 | 实例目录下 `.nmpc-running` 记录启动器 PID，启动前检查、退出时释放，残留锁按 PID 判活 |
| Fabric 重映射留下半成品 `*.tmp` | 后续启动全部在同一处失败 | 启动前发现 `*.tmp` 就删掉整个 `.fabric/remappedJars`（可重建） |
| 子进程工作目录没设成 gameDir | 实例目录里的 `logs/latest.log` 永远是旧的（游戏的相对路径日志落到启动器自己的 CWD） | `ProcessStartInfo.WorkingDirectory = Plan.WorkingDirectory`；客户端 log4j 配置写的是相对路径 `logs/latest.log` |
| Java 以 Low 完整性运行 | Fabric 重映射报 `client-intermediary.jar.tmp can't be written`（详见下一节） | `IntegrityGuard` 在启动前把 `java.exe` 的完整性标签提升到 Medium，修不了则给可操作的错误 |

### 已解决：Fabric 重映射写不出缓存（根因：Java 以 Low 完整性运行）

```
java.lang.RuntimeException: error remapping game jars [versions\1.21.6\1.21.6.jar]
Caused by: java.io.IOException: the jar file
  instances\<实例>\.fabric\remappedJars\minecraft-<版本>-<loader>\client-intermediary.jar.tmp can't be written
```

落点固定是一个 **22 字节**的 tmp（空 zip 的结尾记录）。**不是 Fabric、classpath 或杀软的问题**，
而是三层叠加：

1. Windows 创建进程时取「令牌完整性级别」与「镜像文件标签」中较低者。本机工作区
   `D:\Agents` 被标为 `Low`（`S-1-16-4096`，`NO_WRITE_UP`）并继承给整棵子树，
   `.data/runtime/**/bin/java.exe` 因此带 Low 标签 —— 即使启动器是 Medium
   （`dotnet` 宿主），JVM 也**永远以 Low 运行**。实测：`icacls java.exe` 显示
   `Mandatory Label\Low Mandatory Level`，进程令牌为 `S-1-16-4096`。
2. JDK 21 的 `Files.isWritable` 在 Windows 上走
   `WindowsAclFileAttributeView.getFileSecurity(path, 7)`（只取 OWNER/GROUP/DACL）
   再 `AccessCheck`。安全描述符里**没有完整性标签**，`AccessCheck` 把对象当默认
   （Medium）级别；Low 进程请求写权限就命中"禁止向上写"，**对所有文件都返回 false**
   ——包括它刚创建、刚写成功的文件。实测：Low 进程
   `Files.isWritable(<自己刚写的文件>) = false`，但 `Files.write` 追加成功；
   同一 DACL 换成 Medium 令牌做 `AccessCheck` 则返回允许。
3. JDK 21 的 `ZipFileSystem` 用 `readOnly = !Files.isWritable(zfpath)`
   （`ZipFileSystem` 第 173 行）决定只读标志；tinyremapper 的 `OutputConsumerPath`
   第 102 行看到 `isReadOnly()` 就抛 "can't be written"。22 字节正是它在检查前
   写入的空 zip 结尾记录。

**对照实测**（同一 launcher、同一实例、同一命令行，只改 `java.exe` 的标签）：

| java.exe 标签 | `Files.isWritable` | zipfs `isReadOnly` | 重映射 |
|---|---|---|---|
| Low | false | true | 失败（上述异常） |
| Medium | true | false | 成功：`client-intermediary.jar` 30,653,581 字节；stderr 0 字节；stdout 出现 `Setting user` / `Sound engine started`（进主菜单） |

同样的对照在 `1.20.1 + fabric-loader 0.20.1 + java-runtime-gamma`（Java 17）上复现并验证：
重映射产物 24,733,509 字节、stderr 0 字节、同样进主菜单；`IntegrityGuard` 在该实例首次
启动时把 gamma 的 `java.exe` 从 Low 提到 Medium。

**代码侧修复**：`GameLauncher.Prepare` 调用 `IntegrityGuard`：读启动器令牌级别与
`java.exe` 的标签，取较低者；低于 Medium 且启动器 ≥ Medium 时，把标签提升到 Medium
（`SetFileSecurity` + mandatory label ACE，等价于
`icacls java.exe /setintegritylevel Medium`），结果写进 `PreparedLaunch.Notices` 与
启动器日志。Fabric/Quilt 版本若无法提升（例如双击带 Low 标签的 apphost），
会直接给出可操作的错误，而不是让 tinyremapper 的栈去误导人。

一次性修复本机已有运行时：

```powershell
icacls ".data\runtime\java-runtime-delta\bin\java.exe" /setintegritylevel Medium
icacls ".data\runtime\java-runtime-gamma\bin\java.exe" /setintegritylevel Medium
```

> 运行时若从 Low 目录重新解压，`java.exe` 会重新继承 Low 标签 —— 启动时的
> `IntegrityGuard` 会再修一次；但 **apphost `NPMC.Launcher.App.exe` 自己带 Low
> 标签时无法自救**（Low 进程不能提升别人），此时用 `launch.cmd`（dotnet 宿主）
> 或先对 exe 执行同样的 `icacls`。

**已排除**（都不是原因）：客户端 jar 损坏（28 MB，正常）、intermediary / sponge-mixin
缺失、classpath 缺库与顺序、`CreateNoWindow`、磁盘空间（498 GB）、目录可写性
（同目录手动写 28 MB 成功）、残留 tmp（每次启动前已清理）、Defender 受控文件夹访问。
