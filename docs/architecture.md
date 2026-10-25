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

## 8. 设置项（两级继承）

| 分类 | 项 |
|---|---|
| 全局 | 下载源、并发数、代理、数据目录 |
| Java | 内置运行时优先 / 系统探测 / 实例覆盖 |
| 内存 | Xmx、Xms（默认 4096 MB，上限 = 物理内存 − 1.5 GB） |
| 窗口 | 预设分辨率、自定义宽高、全屏 ← 依赖 `has_custom_resolution` feature |
| 高级 | 额外 JVM 参数、环境变量、启动前后钩子 |

---

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
| **vstest testhost 崩溃** | `dotnet test` 直接中止，报 `Win32Exception (5): 拒绝访问`——testhost 在 `SetParentProcessExitCallback` 里无法 `OpenProcess` 父进程 | 用内置反射 runner 绕过 vstest：`dotnet build -p:SelfTestRunner=true` 后直接运行测试工程 exe。换到正常环境仍可用 `dotnet test` |

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
| M6 | Forge / Fabric / NeoForge 安装 | ⏳ Fabric 可通过 launcherMeta 直接合成，无需跑安装器 |
| M7 | 整合包导入（`.mrpack` 优先） | ⏳ |

---

## 13. 下一步

**已完成**：M0 参数链与启动（实际进世界并正常存档）、内置 Java 运行时（可整体迁移）、
3598 个资源对象并发下载、xunit 回归测试（78 用例）、实例管理与三级隔离。

待做，按依赖顺序：

1. `MirrorRouter`：官方 ↔ BMCLAPI 切换，注意 §4.4 的三条约束
2. 全局设置持久化（下载源、默认内存、代理）
3. 接入 Avalonia（需联网 restore）与 MVVM 骨架
4. 正版登录：等 `aka.ms/AppRegInfo` 审批，通过后接 `MicrosoftAuthProvider`
5. Modrinth 搜索与依赖解析
