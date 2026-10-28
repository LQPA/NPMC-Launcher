# NPMC Launcher 交接（2026-10-28 19:45）

## ⚠ 下一会话先读这一段（2026-10-28 晚，已实测通过）

**离线皮肤 agent 的加载器兼容已经做完并验证**：NeoForge 1.21.1 与 Fabric 1.21.11
（0.20.1 loader）都能正常启动、皮肤生效。这两处结论都是今晚实机跑出来的，日志在
`E:\aaaa\data\logs\skin-agent.log`。

### 1. Fabric 拒绝启动 = -javaagent 的 jar 自带 ASM

实测（19:32 的探针）：`-javaagent:foo.jar` 的 jar **本身就在系统类加载器的搜索路径里**，
`ClassLoader.getSystemClassLoader().getResources("org/objectweb/asm/ClassReader.class")`
能直接返回它。Fabric 的 `LoaderUtil.verifyClasspath()` 数的正是这个，于是
「Fabric 自带 asm-9.10.1 + agent jar 里的 ASM」= 两份 → abort：

```
IllegalStateException: duplicate ASM classes found on classpath:
  jar:file:/.../asm-9.10.1.jar!/org/objectweb/asm/ClassReader.class,
  jar:file:/.../nmpc-skin-agent.jar!/org/objectweb/asm/ClassReader.class
```

所以「不追加到 bootstrap」还不够：**-javaagent 的 jar 里一个 ASM 类都不能有**。
现在的结构（4 个 jar，见 `tools\skin-agent\build.ps1` 顶部注释）：

| jar | 谁加载 | 内容 |
|---|---|---|
| `nmpc-skin-agent.jar` | 系统类加载器（-javaagent） | 只有 `AgentStub`（3.8 KB，无 ASM）；用子加载器加载 impl |
| `nmpc-skin-agent-impl.jar` | AgentStub 的 `URLClassLoader`（ASM child-first） | 全部实现 + ASM 9.11（modern，Java 17） |
| `nmpc-skin-agent-legacy.jar` | 系统类加载器（Java 8） | 自包含（含 ASM、桥接类），1.7.10 用；老 Forge 不查重复 ASM |
| `nmpc-skin-bridge.jar` | bootstrap（`appendToBootstrapClassLoaderSearch`） | 只有被改写的游戏代码调用的桥接类，纯反射 |

- 启动器把 impl 路径写进 agent 参数（`impl=...`），AgentStub 找不到就取自己旁边的
  `nmpc-skin-agent-impl.jar`；任何失败只写日志、不抛异常（最差没皮肤，不会崩）。
- ASM 在子加载器里是 **child-first**：必须保证「带 ASM 的类」和「ASM」来自同一个 jar，
  否则就是上次那个 `VerifyError: Bad type in putfield/putstatic`。

### 2. 1.21.11 皮肤不生效 = authlib 7 要求三个 host 属性全有

authlib 7（1.21.11）的 `EnvironmentParser.fromHostNames()` 要求
`minecraft.api.session.host`、`minecraft.api.services.host`、`minecraft.api.profiles.host`
**三个都存在**，缺一个就整体忽略：

```
Ignoring hosts properties. All need to be set: [minecraft.api.services.host,
  minecraft.api.session.host, minecraft.api.profiles.host]
Environment: Environment[sessionHost=https://sessionserver.mojang.com, ... name=PROD]
```

于是客户端去 `sessionserver.mojang.com` 查离线账号的 profile（超时/查不到）→ 没有
textures 属性 → `unpackTextures` 根本不会被调用 → 皮肤静默回退。
authlib 6（1.21.1）只读 session + services，所以 1.21.1 一直是好的。
`SkinAgent.premain` 现在把三个（外加老的 auth/account）都设上：session 指本地，
services / profiles 指官方；这样 `fetchProfile` 会打到本地皮肤 API 拿到带 textures
的 profile，再由被替换的 `unpackTextures` 返回本地纹理。

### 3. Fabric 下 `SkinManager` 钩子匹配不上，但不影响皮肤

Fabric 运行时游戏类用 intermediary 名（`net/minecraft/class_xxxx` /
`method_xxxx`），所以 transformer 里写死的
`net/minecraft/client/resources/SkinManager` 不会命中（NeoForge 是 mojmap，能命中）。
皮肤走的是 authlib 的 `unpackTextures` 替换 + 本地 profile 接口，两条路径都验证过
`texturesFor: ours=true`。以后要是某个版本必须在 SkinManager 层接管，得按
intermediary 名再匹配一次（先看 `skinmanager:` 那几行有没有打出来）。

### 4. Java 8 的 Fabric（1.15）也查重复 ASM → legacy 也必须拆分

原来的 legacy jar 是自包含的（含 ASM + 桥接类），1.15 Fabric 一启动就
`duplicate ASM classes found on classpath: ...asm-9.10.1.jar..., ...nmpc-skin-agent-legacy.jar!...`。
现在 **两套实现共用同一个无 ASM 的 stub**：
`nmpc-skin-agent-impl.jar`（Java 17）与 `nmpc-skin-agent-impl-legacy.jar`（Java 8）。
启动器按 authlib 主版本选：1–3 用 legacy，4–7 用 modern。

### 5. Fabric Java 8 不认 `appendToSystemClassLoaderSearch` → 桥接必须挂 bootstrap

1.15 Fabric 第二次启动没崩，但 `getTextures` 注入后抛：

```
NoClassDefFoundError: nmpc/skin/SkinBridgeLegacy
Caused by: ClassNotFoundException: can't load class nmpc.skin.SkinBridgeLegacy
  at ...nmpc-skin-bridge.jar as it hasn't been exposed to the game
```

KnotClassLoader 不把「系统类加载器搜索路径里多出来的 jar」当自己的类路径。
所以 `prepareBridge` 现在**所有 Java 版本都挂 bootstrap**（Java 8 也支持
`appendToBootstrapClassLoaderSearch`），只有 Java 17+ 才继续加模块 read edge。
桥接类只依赖 JDK + 反射，放 bootstrap 没有副作用。

**不要**再把带 ASM 的 jar 直接当 `-javaagent`；也**不要**用字节替换重定位 ASM
（会改坏字节码）。桥接类必须保持「纯反射、不 import authlib、配置读系统属性」，
而且**不能再引用 SkinAgent 的字段**（SkinPortBridge 已改成读 `nmpc.skin.*` 系统属性）。

### 6. Forge 1.21.1 启动失败：合并 profile 时把 `forge:universal` 顶掉了

Forge 1.21.1（52.1.20）的 profile 在 `libraries` 里同时列了
`net.minecraftforge:forge:<ver>:universal` 与 `:client`。`VersionInheritance.Merge`
原来按 `group:artifact` 去重（**忽略 classifier**），于是 universal 先被加进来、
紧接着被 client 顶掉；classpath 里只剩 client。Forge 的
`net.minecraftforge.bootstrap.Bootstrap` 是从 **classpath** 挑模块的
（`findAllClassPathEntries`），找不到 `forge` 模块就报
`IllegalStateException: Failed to find system mod: forge`。

现在 `ArtifactKey` 保留 classifier（`group:artifact[:classifier]`，版本仍不参与比较），
两条都留在库里。实测：补上 universal 后 Forge 1.21.1 正常启动，皮肤也生效
（`texturesFor: ours=true loader=SecureModuleClassLoader[SECURE-BOOTSTRAP]`）。
注意：**不要**为了"去重"把 classifier 再吃掉，natives 变体同样会因此覆盖主 jar。

### 7. 1.21.11 Forge/NeoForge：`SkinManager.get` 钩子的两个坑

1.21.11 的 mojmap `SkinManager` 有 `get(GameProfile) → CompletableFuture<Optional<PlayerSkin>>`，
我们的钩子会命中（Fabric 是 intermediary，不命中，所以 Fabric 一直正常）。两个坑都实测过：

- **`registerTextures` 的第一个参数是 UUID，不是 GameProfile**。旧代码传 profile →
  `IllegalArgumentException: argument type mismatch`，桥接吞掉异常后返回
  `Optional.empty()` → 默认皮肤。现在按声明参数类型转换（UUID 或 GameProfile 都行）。
- **改名必须发生在创建 target 之前**：`visitMethod` 里若先
  `super.visitMethod(access, "get", ...)` 再改走 `nmpc$get$impl`，ClassWriter 里会留下
  一个没有 Code 的 `get`，游戏直接 `ClassFormatError: Absent Code attribute ...` 崩掉。
  现在 rename 分支在 target 创建之前（与 `getTextures` 一致）。
- 另外：非本地账号现在会**交回原实现** `nmpc$get$impl`（以前返回空），
  否则多人游戏里别人的皮肤也会被我们挡掉。

实测结果：`wrapping ...SkinManager.get(...)` + `bridge get: id=… ours=true`，
Forge 61.2.4 / NeoForge 21.11.48 皮肤均生效。

### 8. 26.2 / 26.3 的支持（authlib 9 / 10）

- **26.2（authlib 9.0.75）** 仍是 `YggdrasilMinecraftSessionService`，host 属性同 7；
  原有 `unpackTextures` 钩子直接能用。
- **26.3（authlib 10.0.77）** 换成 `MinecraftServicesSessionService`，`EnvironmentParser`
  只认 `minecraft.api.discovery.host` / `env` —— **session host 重定向失效**，
  客户端拿不到带 textures 的 profile。新增 `getPackedTextures(GameProfile)Property` 钩子：
  本地账号直接返回合成的 textures 属性（base64 JSON，URL 指本地），其它玩家交回原实现。
  之后 SkinManager 缓存的 CacheLoader 调 `unpackTextures`（我们替换过）→ 本地纹理。
- transformer 现在用 `isSession`（Yggdrasil 或 `MinecraftServicesSessionService`）；
  `getPackedTextures` 的改名同样要在创建 target **之前**（否则重现 Absent Code）。
- `SupportsSkinAgent` 放宽到 authlib 1–10；`SkinModuleSupport` 的 HOOK_CLASSES 加了
  services 类，模块环境（Forge/NeoForge 26.3）才有 read edge。
- 实测：26.2 / 26.3 的原版、Forge、NeoForge 全部确认皮肤生效。
- 诊断：`--dump-instances <文件> --instance <名称|id> --print-launch-command` 现在
  **支持便携布局**（`LauncherPaths.Portable` 会识别 `<root>\data`+`<root>\minecraft`）并能点名实例；
  这是本轮为了核对 26.x 启动命令顺手修的。

### 9. Quilt 安装界面（2026-10-28 晚）

- 新增 `Infrastructure/Minecraft/QuiltLoaderInstaller.cs`：和 Fabric 一样不需要安装器，
  取 `https://meta.quiltmc.org/v3/versions/loader/<game>/<loader>/profile/json`，
  写版本 JSON + 下载库。两个区别：
  1. Quilt 的版本列表是**全局的**（任何游戏版本都返回同一份 307 条），支不支持某个游戏版本
     只能靠 profile 端点判断（不支持返回 404，例如 1.7.10）；
  2. 没有 `stable` 字段，按版本号里有没有 `-` 区分正式版 / 预览版。
- 下载页加载器改为**五选一**（原版 / Fabric / Quilt / Forge / NeoForge），复用现有的
  版本下拉、R / B 徽标、默认实例名（`<mc>-quilt-<loader>`）。
- `LoaderAvailabilityCache` 增加 `Quilt` 段；**旧缓存文件没有这一段**，读到时按过期处理
  （否则「该版本没有可用版本」会一直灰着，实测踩过）。
- 整合包安装也认 `quilt-loader`（`ModpackIndex.IsQuilt`）。
- 诊断：`--exercise-download-loader <mc> quilt [loader]` 走与界面同一条链路
  （下载本体 → 装 Quilt → 建实例）；`--instance <名称>` 可直接给新实例生成启动命令。
  实测 1.21.1 + Quilt 0.31.0-beta.4：启动正常，皮肤生效
  （`bridge packedTextures: ours=true` → `texturesFor: ours=true`，用户确认）。
- 注意：该诊断不指定 loader 版本时取**最新**（可能是 beta）；界面默认选最新正式版。

### 10. 1.13 之前的分辨率设置失效（2026-10-28 晚，已修复）

用户报的现象：1.7.10 等早期版本，实例里设的 1280×720 不生效，窗口永远是客户端默认的 854×480。

原因：1.13 之前的版本 JSON 用的是 `minecraftArguments`（一整串空格分隔的参数），
里面**只有** username / gameDir / assetsDir / accessToken 这些，**没有** `--width` / `--height`
（1.13+ 才有 `arguments.game` 里带 `has_custom_resolution` 规则的那两条）。
官方启动器是自己把分辨率加进去的；我们的 `ExpandLegacyArguments` 只做了占位符替换，
所以旧版一直拿默认值。

修复：`LaunchPlanFactory.Create` 在 legacy 分支展开完参数后补
`--width <w> --height <h>`（`hasCustomResolution` 为 false 时补 `--fullscreen`）。
实测 1.7.10 / 1.12.2 的 `net.minecraft.client.main.Main` 都解析 width/height/fullscreen
（joptsimple 选项名；`javap` 确认过），窗口客户区从 854×480 变成设置里的 1280×720
（用户确认）。太老、不认识的版本会忽略未知参数，不会崩。

### 11. 概览页「高级选项」（2026-10-28 晚）

概览页设置卡片下方新增可收起的「高级选项（启动参数 / 游戏标题 / 横幅）」，默认收起；
值都保存在实例设置里（instances.json），不填就完全不影响启动：

- **自定义启动参数**：JVM 参数 / 游戏参数，每行一个，分别追加到 JVM 参数与游戏参数末尾。
- **自定义游戏标题**：启动后由启动器用 Win32 `SetWindowText` 把游戏窗口标题改成填的文字
  （运行期间每秒重新应用一次，加载世界后游戏自己改标题也会被盖回来）。仅 Windows。
- **自定义横幅（主界面标语）**：改 Minecraft 标题右侧的黄色 splash。
  实现是生成 `resourcepacks/nmpc-splash.zip`（覆盖 `assets/minecraft/texts/splashes.txt`）
  并在 options.txt 的 `resourcePacks` 里启用：1.13+ 写 `file/nmpc-splash.zip`，
  1.12.2 及以前写裸文件名 `nmpc-splash.zip`。清空后下次启动删除资源包并移除条目。
  `pack.mcmeta` 同时写 `pack_format`（按版本表）与 `supported_formats` 1..999。

实测（1.12.2，设置里填标题/标语/JVM 参数后从界面启动）：窗口标题变成「NPMC 测试标题」、
主界面标语变成「NPMC 自定义横幅」、`logs/nmpc-command.txt` 里有 `-Dnmpc.advanced=1`；
`options.txt` 为 `resourcePacks:["nmpc-splash.zip"]`，资源包 396 字节。用户确认生效。
测试：365 通过 + 2 个既有失败（`SplashTextPackTests` 覆盖 pack_format、options 改写、apply/clear）。

### 12. 单启动器多实例启动（2026-10-28 晚）

原来 `IsGameRunning` 是一个全局 bool：只要有一个游戏在跑，所有实例的启动/改名/删除/复制
都被禁用。现在改成按实例跟踪：

- `RunningInstances`（Core）只负责"哪些实例 Id 正在运行"；
- `InstanceListItem.IsRunning` 给列表项加「运行中」角标；
- 启动/改名/删除/复制命令的 CanExecute 只看**当前选中的实例**是否在运行；
- `LaunchGameAsync` 启动进程后**立刻返回**，由 `AwaitGameExitAsync` 在后台等待退出并清标记。
  这一点很关键：之前一直 await 到游戏退出，`AsyncRelayCommand` 会一直处于"执行中"，
  按钮整个灰掉，等于第二个实例永远起不来（用户实测就是这个现象）。
- 游戏临时目录从共享的 `cache\tmp\game` 改为 `cache\tmp\game\<实例Id>`
  （`-Djava.io.tmpdir` 每实例独立，避免 LWJGL/mod 的临时文件互相踩）。

同一实例仍然只能开一个：`GameLauncher.Start` 里的 `.nmpc-running` 文件锁保留，
`RunningInstances` 再挡一层。无隔离实例共用游戏目录，也会被这层锁挡住。

实测（GUI，1.12.2 + 1.15）：两个游戏窗口同时存在；列表两个「运行中」角标；
选中未运行的实例时启动按钮可用；杀掉 1.15 后角标消失、可在 1.12.2 继续运行时重新启动 1.15。
`cache\tmp\game\1.12.2` 与 `...\1.15` 各自独立，命令文件里的 `-Djava.io.tmpdir` 也对得上。

### 13. 自动内存（2026-10-28 晚）

概览页「内存」行右侧新增「自动」复选框（实例设置 `AutoMemory`）。开启后手填数字框禁用，
下方显示一行建议值，例如：

> 自动：8192 MB（ATM10 推荐 8192 MB · 空闲 13.1 GB / 物理 30.9 GB）

推荐顺序（`MemoryAutoCalculator.Recommend`）：

1. 有整合包推荐值的直接用（`ModpackMemoryRecommendations`：ATM10/ATM9/ATM8 = 8192，
   ATM7/ATM6 = 7168，GTNH = 8192，RLCraft/DawnCraft/Better Minecraft = 6144 等，
   名字匹配忽略大小写与标点）；
2. 没命中就按模组数量分档：纯原版（0 个模组）→ 2048，1–20 → 4096，≤60 → 5120，≤120 → 6144，≤200 → 8192，
   ≤300 → 10240，更多 → 12288；
3. 再用**当前空闲物理内存**兜底：不超过空闲的 75%、物理内存的一半、16 GB；
   最低 2048 MB，按 512 MB 对齐。空闲不够时会在说明里注明「已降到 X MB」。

每次启动都重新计算（`GameLauncher.Prepare`），所以夜里挂机、白天开一堆程序时得到的值会不同；
界面上的数字只是"此刻的建议值"。只影响启动，不改用户手填的 `MemoryMb`。

实测（无界面，直接 dump 启动命令）：
`All-the-Mods-10`（`.nmpc-modpack.json` 里 name=All the Mods 10）→ `-Xmx8192M`；
`1.12.2`（无整合包、0 模组）→ `-Xmx4096M`；当时物理 30.9 GB、空闲 13.1 GB，
按公式两者都未被空闲内存压低，与预期一致。

### 14. 设置页的「默认内存 → 自动」（2026-10-28 晚）

设置 → 实例默认 → 默认内存新增「自动」复选框（`LauncherSettings.DefaultMemoryAuto`）。
开启后数字框禁用，只影响**之后新建**的实例：`NewInstanceSettings()` 会把 `AutoMemory = true`
写进新实例，老的实例保持原样（想改就去概览页勾自动）。
提示文字说明推荐依据（整合包推荐 / 模组数量 / 当前空闲内存），实例页里仍可单独改回手动。

**逐版本/加载器的对照表见 `docs/skin-compatibility.md`**（authlib 1–10 的 API 变化、
Forge/NeoForge/Fabric 的类名与加载器差异、测试清单）。
用户已全部实机确认（2026-10-28 晚）：**1.7.10（原版/Forge+SkinPort）、1.9、1.12.2、1.15、1.20.1、
1.21.1（含 ATM10）、1.21.11、26.1.2、26.2、26.3 的原版 + Fabric/Forge/NeoForge 组合全部皮肤生效**
。
Quilt 也已实测通过（1.21.1 + Quilt 0.31.0-beta.4，皮肤生效），启动器现在有 Quilt 安装入口
（见 §9）。只剩 1.21.6 未测（机制同 1.21.1，按用户决定跳过）。

## 当前状态

- 仓库：`D:\Agents\NPMC-Launcher`
- 分支：`main`
- 最新提交：以 `git log -1 --oneline` 为准（本文档不记录自己的提交号）
- 最新 exe：`D:\Agents\NPMC-Launcher\dist\NPMC.Launcher.App.exe`（22:21，46.3 MB，
  已同步 `E:\aaaa`）
- 工作副本：`E:\aaaa`，已同步同一个 exe + `start.cmd`
- 本次会话：2026-10-28 从上午的“数据包下载界面”一路做到晚上（自定义标题栏、导出规则、
  整合包文件识别、Low 完整性、离线皮肤加载器兼容）；**离线皮肤已覆盖 1.7.10 ~ 26.3 的
  原版 / Fabric / Forge / NeoForge 组合（用户实机确认）**，只剩 1.21.6 / 快照 / Quilt 未测。
- 测试：`dotnet test tests\NPMC.Launcher.Core.Tests` → 365 通过 + 2 个既有失败
  （`SharedRootTests.SharedRoot_overrides_only_the_shared_directories`、
  `InstanceLayoutTests.Full_isolation_moves_libraries_and_assets_into_the_instance`）。
- 运行数据：`E:\aaaa\data`、`E:\aaaa\minecraft`（`dist\data` / `dist\minecraft` 目前为空，用户有备份）
- 仓库可见性：**公开**（`github.com/LQPA/NPMC-Launcher`，API `visibility=public`），源码按
  **AGPL-3.0** 公开。README 原写“本阶段不公开源码”，2026-10-27 已按用户决定（方案 B）改成
  “源码公开”，并补了 Repository layout（说明 `src/` 就是完整源码；`dist/`、运行目录、账号与
  密钥都不入库）。以后不要再往 README 里写“不开源”。

## 启动与数据目录

- 默认便携布局：`<exe>\data`（配置、账号、缓存、日志）和 `<exe>\minecraft`（实例、库、版本、资源、runtime、launcher_profiles）。
- `LauncherPaths.Portable` **不会自动迁移旧布局**；根目录检测到旧 `settings.json/libraries/instances` 时继续用旧根目录。
- `LauncherRoot.Resolve` 优先级：
  1. `--root`
  2. `NPMC_ROOT`
  3. exe 旁边已有 `data`/`minecraft` → 用 exe 目录（本会话新增）
  4. `%APPDATA%\NPMC-Launcher\last-root.txt`
  5. exe 目录
- 所以只复制 exe 而不复制 `data`/`minecraft`，仍会读 last-root 的旧路径；完整便携需要一起复制两个目录。

## 本会话完成的功能

- CurseForge API key 移到设置页；缺 key 时提示去设置。
- CurseForge 整合包支持；光影/资源包按 `classId`（6552/12）归类，ATM10 的 4 个光影 zip 已从 `mods` 移到 `shaderpacks`。
- 自动补齐 blocked 文件会写入 `.nmpc-modpack.json` 的 `PackFiles`，不再被“隐藏整合包自带模组”误判为用户模组。
- 实例概览：
  - 目录按钮（游戏目录/mods/资源包/光影/存档）移到概览上方；
  - 底部只保留启动/复制/删除；
  - 新增「复制实例」（隔离实例才允许，跳过 logs/crash-reports）；
  - 新增「导出」页签，打包实例为 ZIP，含 `instance.nmpc.json`。
- 模组高级搜索：
  - 14 个分类（科技/辅助/生存/世界生成/魔法/存储/优化/食物/装备/运输/生物/装饰/库 API）；
  - 排序（相关度/最多下载/最近更新）；
  - 每页 20 条，翻页固定在搜索面板底部；
  - 搜索面板展开时占满内容区剩余高度，窗口拉高时结果区变高；
  - 移除了废弃的“收起”按钮。
- **资源包 / 光影 也有了和模组一样的下载界面**（`MainWindowViewModel.ContentViews.cs`）：
  - 三种内容页签统一成「本地列表 ⇄ 下载模式」两个视图，两种视图互斥，不再同屏抢地方；
  - 列表上方一行：本地筛选框 + 「下载模组 / 下载资源包 / 下载光影」，文案跟着页签走；
  - 下载模式下是完整搜索面板：搜索框、来源（Modrinth / CurseForge）、排序、
    结果详情、版本下拉 + 依赖计划、翻页；回列表用「← 返回 X 列表」；
  - 分类筛选仍然只对模组显示（那 14 个是模组分类，资源包/光影的 Modrinth 分类是另一套）；
  - CurseForge 没有光影分类：切到光影时来源自动回到 Modrinth；
  - 数据包**不**走这套（`ContentKind.SupportsDownload`：按世界存放，要先选世界）。
  - 安装目录改为 `ResolveContentDirectory(kind)`：资源包进 `resourcepacks`、光影进
    `shaderpacks`，顺带修掉数据包会被装到实例根 `datapacks` 的隐患。
  - 删除 `IsSearchPanelExpanded` / `SearchPanelToggleText` / `ToggleSearchPanelCommand`
    这些"收起"残留：面板只在下载模式出现，固定占满内容区。
- 修复：
  - `CopyInstanceCommand` 加入 `RaiseCommandStates`，选中实例后按钮不再一直灰；
  - 下一页在整页返回时启用（`hits.Count >= SearchPageSize`）；
  - 搜索面板 `Grid.RowSpan` + `*` 布局，翻页不会掉到窗口外；
  - 移除搜索面板的“收起”按钮（展开布局已取代它）；
  - `IsSearching` 变化时刷新翻页命令，避免搜索结束时下一页仍灰；
  - **装完模组不再清空搜索结果**：以前 `ConfirmInstallAsync` 会 `SearchResults.Clear()`，
    但查询词、分类、排序、页码都留着，界面就变成“搜索框 + 第 N 页 + 空列表”，
    看上去像搜索列表自己收起来了。现在结果、摘要、页码原样保留（同一实例下它们依然成立），
    装进去的文件由 `RefreshContentAsync` 反映到本地模组列表；
  - **回到内容页签时若还有搜索结果就重新展开面板**：收起按钮早已移除，收起状态下的
    结果没有任何入口能再打开，等于把用户搜到的东西弄丢。
  - 提交：`8f9659c`；`.gitignore` 增加 `/.publish/`（发布暂存目录）。
  - 诊断：新增 `--exercise-search-install`（走与界面相同的确认链路、**会真的下载**到 `--root`），
    打印装前/装后的结果数、摘要、面板状态。实测 `20→20`、摘要保留、面板展开、计划收起；
    资源包页签切换仍会清空（原有语义不变）。
  - **搜索面板与依赖计划不再叠在一起**：结果区原来写死 `MinHeight="220"`，计划面板一出现
    可用高度只剩 ~322px，撑破格子后结果列表同时压住上面的摘要和下面的计划（用户截图的现象）。
    现在结果行是可压缩的 `*` 行、计划里的文件行在框内滚动（≤96px）、确认安装时让出摘要行；
    实测 752 / 640 / 600 高度都不重叠，最小窗口高度也从 520 提到 600（520 时自动行本身
    就超过可用高度，必然挤压）。
  - **装完立即认领**：新增 `RememberInstalledIdentity`，把这次装的文件按"来源 + 版本通道(R/B/A)
    + 图标"直接写进识别缓存。以前要等重启（后台识别按 `实例:页签` 只跑一次，装完不重跑），
    用户看到的就是刚下载那一行空着没有 M / C 徽标。
  - 诊断：新增离屏渲染 `--render-search-panel <png> [--render-height N] [--render-no-plan]
    [--render-font 字体名] [--render-kind mod|resourcepack|shader] [--render-list]`
    （窗口放到屏幕外、不进任务栏），用来核对这类"布局挤在一起"的问题。
  - **诊断：`--exercise-search` 支持 `--exercise-kind` / `--exercise-query`，
    现在模组 / 资源包 / 光影都能跑「搜索 → 计划 → 真下载 → 认领」整条链路。
    实测资源包（faithful，19.4 MB）与光影（bliss，1.79 MB）都落进了正确目录，新行当场认领。
  - **26.3 启动崩溃（FML 早期窗口 / arm64 报错）根因与修复**：
    `versions/26.3/natives/lwjgl` 里留着 LWJGL 3.3 时代**平铺**的 dll（10 个，21:41 那次启动留下），
    而 3.4 的缓存布局是 `<build>/<arch>/`。两者混在一起时 3.4 的平台探测会认错架构，
    报 `Platform available on classpath: windows/arm64`，FML 早期窗口随即崩在
    `DisplayWindow.shutdownAutomaticRenderer`。复现与二分（改造后命令行、临时 gameDir）：
    平铺 dll 在 → 必崩；换空 extract 目录 → 正常到渲染后端；只删平铺 dll → 正常。
    `NativeLibraryCache.RemoveStaleFlatFiles`（`GameLauncher.Prepare` 里调用）现在启动前
    清掉 extract 目录里平铺的动态库，子目录缓存保留，并只在 LWJGL ≥ 3.4 时动手。
  - **同一个库的多个 natives 变体只留一个**：26.3 与 1.20.1 的元数据都只按 `os.name` 过滤，
    `natives-windows` / `-arm64` / `-x86` 三个变体会全部命中。`NativeVariants.SelectForArchitecture`
    按当前架构只留一个（`OsInfo.Current()` 现在读真实进程架构，不再硬编码 x86_64）。
    1.20.1 的 classpath 基准从 65 条变成 51 条（50 个 jar + client.jar），测试注释已说明原因。
  - **安装结果的提示说人话**：一个都没下载、全被跳过时不再只报"新增 0，跳过 1"，而是
    "没有下载：xxx.jar 已经在 mods 里了 —— 选的版本和已装的是同一个？"；全部失败时给出第一个错误。
    （用户就把它当成过 bug：换了版本下拉，但计划其实没变，于是被跳过。）
  - 诊断：新增 `--exercise-reinstall-version`，模拟"已有 A 版本再装 B 版本"：
    准备 → 装 → 再准备 → **用属性 setter** 换版本（和界面同一条路）→ 装。
    实测 setter 会重新解析计划（计划行从 beta 变成 alpha），说明下拉切换本身是通的。
  - **检查更新默认只推荐正式版**（`UpdatePlanner` 一直如此：`includePrerelease: false`），
    所以 Sodium 0.9.2 不会提示 0.9.3-beta。新增设置 `IncludePrereleaseUpdates`
    （设置页「检查更新时包含 beta / alpha」+ 模组页底部「含测试版」复选），打开后
    beta/alpha 也参与升级判定，状态栏会写「N 可升级（含测试版）」。
    实测 sodium 0.9.2：默认 0 可升级，打开后 `mc26.3-0.9.2-neoforge → mc26.3-0.9.3-beta.1-neoforge`。
  - **资源包 / 光影也能按分类筛选**：分类标签从 Modrinth `/v2/tag/category` 按 `project_type`
    取（资源包 29 个、光影 19 个），缓存进 `cache/search-categories.json`（7 天，断网可用）；
    模组的 13 个中文分类仍是内置（带 CurseForge id）。选具体分类时若来源是 CurseForge，
    会自动切回 Modrinth（CF 的包分类 id 没映射），搜索前也会拦下并提示。
  - **分辨率/性能单独成一个下拉**：资源包/光影的标签里，`resolutions`（8x- ～ 512x+）与
    `performance impact`（potato ～ screenshot）跟风格/特性不是一回事，现在拆成第二个下拉
    （标签名随类型变：资源包=分辨率、光影=性能），并按数值/档位排序（不再是 16x 排在 128x 前）。
    两个维度是 Modrinth facet 的两个**组**（组间 AND），所以"16x + vanilla-like"会真正叠加。
    实测：资源包 分类 22 项 + 分辨率 9 项（16x → 7 条）；光影 分类 15 项 + 性能 6 项（low → 5 条）；
    模组页仍是 14 项、没有第二个下拉。
    诊断：`--exercise-search --exercise-category <名字>` 会先找主分类再找次要维度，
    也可以用 `--exercise-secondary <名字>` 直接指定。
  - **版本下拉能区分同号变体**：光影/资源包常见六个文件全叫 `2.02`（Extreme / High / Lite…）。
    现在同一版本号出现多次时，`InstallVersionChoice.Display` 会在版本号后补变体名
    （从 Modrinth 的 `name` 去掉项目名与版本号得到，取不到就用文件名）；版本号唯一时不补，
    模组列表保持干净。实测 sildur：`2.02 · Extreme / · High / · Lite`；sodium 仍是
    `mc26.3-0.9.3-beta.1-neoforge`。
  - **点本地列表的图标 → 直达该项目的下载界面**：`ContentEntryItem.DownloadCommand`（行首图标
    就是按钮，ToolTip「下载其他版本」）→ `OpenDownloadForAsync` 打开下载模式、把搜索框填成项目名、
    并直接解析该项目在当前实例下的版本与安装计划（版本下拉立刻可用，换版本/补依赖都不用重搜）。
    项目身份来自识别缓存的 `ProjectRef` + `Title`（安装时写 `item.ProjectRef` / `item.Title`，
    哈希识别时写 `matched.ProjectId` 与项目名；旧缓存没有这两个字段，反序列化为空串）。
    认不出项目时退回按名字搜索一次。实测：装好 Sodium 后点图标 → 计划可见、可选版本 3 个、
    当前为最新正式版。
  - **数据包也能下载（2026-10-28）**：存档页签 → 进入某个世界 → 「下载数据包」，
    走的是同一套搜索/计划/安装，装到 `saves/<世界>/datapacks`。
    - `ContentKind.SupportsDownload` 现在四类都算；`IsPerWorld()` 的入口把关放在
      `OpenContentDownload`（没进世界时提示"先选一个世界"）与 `BeginInstallAsync`
      （`_pendingDirectory` 为空就退回提示，不会误装到 mods）。
    - 搜索 facet 用 `project_type=datapack`（Modrinth 就是用这个类型返回数据包项目）；
      但**版本列表**要再加 `loaders=["datapack"]`（`ContentKind.PlatformLoaders`），
      否则同一项目的模组版 jar 会混进来（Terralith 就是 mod+datapack 双发布）。
    - 进/出世界都会 `ResetSearchState()` 并退出下载模式：搜索结果只对那个世界有效。
    - 数据包没有分类标签（Modrinth 的 tag API 里 `project_type=datapack` 是 0 项），
      所以分类下拉对它仍隐藏。
    - 实测：`--exercise-kind datapack --exercise-query terralith` → 4 条结果、计划 1 行；
      `--exercise-search-install` → `Terralith_v2.6.5+26.3.zip`（2.98 MB）落到
      `saves/TestWorld/datapacks/`，行当场认领（Modrinth），点图标能回到该项目版本。
      离屏渲染（752 高）确认列表模式有「筛选当前数据包… + 下载数据包」，下载模式无重叠。
    - 两处入口修正（用户反馈）：世界行的「打开目录」改为打开**世界文件夹**本身
      （以前直接跳进 `datapacks/`，看不到 level.dat/region）；数据包管理页删掉了与底部
      「打开当前文件夹」重复的「打开该世界的数据包目录」，`OpenDatapacksFolderCommand`、
      `HasDatapacksEntry`、`ResolveDatapacksDirectory` 一并删除（`ShowDataPackActions`
      也换成了 `ShowWorldPicker`）。
  - **自定义窗口（2026-10-28）**：`ExtendClientAreaToDecorationsHint + NoChrome` + 自绘标题栏
    （拖动 `BeginMoveDrag`、双击最大化、— / □ / ✕ 三个按钮），系统边框仍保留，
    所以拉伸/贴边吸附还能用。顶部原来的"root 路径 + 权限"那一条删了（正式版没必要），
    刷新按钮挪进标题栏。注意 `CanHidePackMods` 现在要求 `IsModsTab`：
    资源包/光影页不再出现「隐藏整合包自带模组」。
  - **ATM10 创建世界后崩溃 = 离线皮肤 agent（2026-10-28 修复，已实测通过）**：崩溃报告是
    `NoClassDefFoundError: nmpc/skin/SkinBridgeModern`，栈在
    `authlib@6.0.54 YggdrasilMinecraftSessionService.unpackTextures` ←
    `SkinManager`。原因是 1.16+ 的模组加载器（NeoForge/Forge/Fabric）用 ModLauncher
    的模块化类加载器加载 authlib，看不到 `-javaagent` 追加到系统类路径的类。
    最终做法（不再跳过模组实例）：
    - **桥接 jar 单独一个文件**：`nmpc-skin-bridge.jar` 只含 `SkinBridgeModern` /
      `SkinBridgeLegacy`，被追加到 **bootstrap**；agent jar（含 ASM）只经 `-javaagent`
      加载，**绝不**追加到 bootstrap —— 否则同一个 ASM 类会被两个加载器各定义一次，
      验证器报 `VerifyError: Bad type in putfield/putstatic`（19:10 那版就是这么坏的）；
      Fabric 也看不到 agent jar 里的 ASM，不会再报 duplicate ASM。
    - `SkinModuleSupport`（只编进 modern jar）给被改写的模块加 **read edge**；
    - 桥接类全部改纯反射、配置走系统属性（`nmpc.skin.url/uuid/name/model/port/log`）：
      `SkinBridgeModern`（authlib ≥5，`unpackTextures`）与 `SkinBridgeLegacy`
      （authlib ≤5，`getTextures`）；
    - 任一准备步骤失败时 `modernReady=false`，不做替换 —— 最差是没皮肤，不会崩；
    - 踩过的坑：authlib 6/7 的 `Property` 是 record（只有 `value()`，没有 `getValue()`，
      用错会一直 `ours=false` 返回 EMPTY）；1.21.1 的 `SkinManager` 没有 `get`，只有
      `getInsecureSkin` / `getOrLoad` / `registerTextures(UUID, …)`。
    - 独立探针（authlib 7 与 1.5.21）验证：自己的属性返回带本地 URL 的纹理，别人的返回 EMPTY；
      `TriggerProbe`（-javaagent + 强制加载 authlib 类）验证转换成功、无 VerifyError。
    - `SkinAgentTests` 覆盖 NeoForge / Forge / Fabric 都挂 agent、原版保留、微软账号不用。
    - `tools/skin-agent/build.ps1` 产出三个 jar：`nmpc-skin-agent.jar`（modern）、
      `nmpc-skin-agent-legacy.jar`（Java 8）、`nmpc-skin-bridge.jar`；前两个都内嵌 ASM，
      bridge 不嵌。**不要再引入 Relocator/字节替换**（已删除，重定位方案被证伪）。
    - **进程不退出**：`com.sun.net.httpserver` 的 dispatcher 线程不是 daemon，游戏启动失败后
      JVM 被吊住，启动器永远显示"运行中"。`SkinHttpServer` 已改成 `ServerSocket` + daemon
      线程，路由不变。
    - **PowerShell 脚本必须存成 UTF-8 with BOM**：PS 5.1 会把无 BOM 的 UTF-8 当 ANSI，
      中文注释的字节会把下一行吞进注释（`skin-agent\build.ps1` 曾因此整段不执行、只生成旧 jar）。
      另外 `$ErrorActionPreference='Stop'` 会把 javac 的 stderr（`注: 使用已过时 API`）当成
      终止错误，脚本里对 javac 调用用 `$null = & ... 2>&1` 或临时切 Continue。
  - **权限状态（2026-10-28 重装后）**：用户完整删除 `data`/`minecraft`/`runtime` 重新下载后，
    `data`、`minecraft`、`runtime`、`java.exe`、`cache\tmp\game` 全部**没有 Low 标签**
    （继承 Medium）——之前那个"java.exe 是 Low、%TEMP% 写不进"的根源消失；
    `data\cache\tmp\{game,forge-installer}` 这套临时目录仍然保留，作为低完整性环境的兜底。用户的 `java.exe` 带
    `Low Mandatory Level` 标签（`icacls` 可见；启动器尝试提升到 Medium 时报 SetFileSecurity: 5，
    因为 ACL 里只有 Modify、没有 WRITE_OWNER）。Windows 会按镜像标签把进程降到 Low，
    而 `%TEMP%`（`C:\Users\…\AppData\Local\Temp`）是 Medium 且没有 Low 标签 ——
    Low 进程在那里建目录直接 AccessDenied。表现：
    - NeoForge 官方安装器 `PostProcessors.process` → `Files.createTempDirectory` 抛
      AccessDenied（ATM10 的安装失败）；
    - 游戏内 EuphoriaPatcher 报 `Error creating temporary directory`。
    - **不是 Java 版本问题**：安装器日志 `E:\aaaa\minecraft\neoforge-*.installer.jar.log`
      显示用的是 Java 21.0.7，完全正确；重装 Java 没用。
    - 修复：`LauncherPaths` 新增 `TempRoot` / `GameTemp` / `InstallerTemp`
      （`<root>\cache\tmp\{game,forge-installer}`），启动游戏和安装器时显式传
      `-Djava.io.tmpdir=…`，并把目录标成 Low（`IntegrityGuard.TryRaiseFileLevel(..., Low)`），
      保证 Low 子进程一定写得进去。实测目录标签变成 `Low (NW)`，游戏参数里能看到
      `java.io.tmpdir`。
    - 若以后要让 Fabric/Quilt 重映射也工作，需要管理员执行
      `icacls "…\runtime\java-runtime-*\bin\java.exe" /setintegritylevel Medium`
      （用户自己拥有文件才做得到）。
  - **空列表提示分两种情况（2026-10-28）**：以前只要 `Content` 为空就报"文件夹是空的"，
    用户输入筛选词后看到"这个世界还没有数据包"会以为文件丢了。现在 `_contentFileTotal`
    记录筛选前的总数：
    - 有筛选词 → 「没有匹配「q」的 X（共 N 个）；清空筛选框就能看到全部。」
      （N=0 时补一句"这里还没有任何文件"）；
    - 没筛选词但总数>0 → 模组页说明"被折叠的整合包自带模组"，其他类型提示检查筛选；
    - 总数=0 → 原来的空文件夹文案。
    实测：数据包世界输入 `ad` → 「没有匹配「ad」的数据包；这里还没有任何文件…」；
    模组页输入 `zzz` → 「没有匹配「zzz」的模组（共 2 个）…」。
  - **整合包自带模组识别（2026-10-28 修复）**：安装整合包时 `PackFiles` 原先只记
    `modrinth.index.json` 的 `files[]`，而 `overrides/` 里带进来的模组（作者直接打包、
    不需要联网下载的那些）没被记录 → 导入后它们被当成用户自己加的模组，不折叠、也没有
    整合包来源标记（用户实测：MFQM 的 4 个模组，其中 3 个还是 `.disabled`）。
    - 新增 `Core.Instances.PackFileCollector`：合并清单与 mods 目录里实际存在的 jar，
      按**逻辑名**（去掉 `.disabled`）去重；安装流程改用它。
    - 所有比较改写为逻辑名（`ApplyPackModFilter`、`RebuildModContent`、检查更新过滤、
      识别打标），否则被禁用的整合包模组仍然折叠不到。
    - 曾经加过一个「标记为整合包自带」按钮给旧实例补录；修好安装路径后它就是死按钮
      （用户指出"彻底成无效按钮"），已删除 —— 旧实例要纠正只能重新导入整合包。
    - 实测：3 个 jar（含一个 `.jar.disabled`）→ `PackFiles` 记成 3 个逻辑名、折叠 3 个；
      `PackFileCollectorTests` 5 个用例覆盖 overrides 补录、sha1 保留、`.disabled` 归一、
      去重与目录缺失。
    - **检查更新会把徽标清成 `?`（2026-10-28 修复）**：`CheckModUpdatesAsync` 为了清掉
      "可升级"标记，对整合包自带文件调了 `item.SetIdentification(null, null)` ——
      把哈希识别出来的来源也一起抹了。现在只 `SetUpdate(null)`，识别徽标保持不动。
      实测（MFQM 副本）：检查更新前后，清单里的 CrashAssistant / journeymap 始终 M=True，
      overrides 里的 4 个自制模组始终 `?`。
    - 资源包/光影同样处理（2026-10-28）：`PackFileCollector` 扫 `mods` / `resourcepacks` /
      `shaderpacks` 三个目录；`PackFileEntry.FromIndex` 标出"是否真的来自清单"，
      只有清单文件才强制打来源徽标，`overrides/` 里的内容保持 `?`（真能被哈希认出才认领）。
      旧 JSON 没有 `FromIndex` → 默认 true。
  - **导出整合包（.mrpack）表单（2026-10-28）**：导出页从"一个按钮"改成"格式 + 整合包名称/版本 +
    导出内容清单 + 高级选项"，参照 PCL 的整合包制作界面，但**只做模组包导出，不打包启动器自身**。
    - 内容清单：Mod（含已禁用）/ Mod 配置 / 资源包 / 光影包 / 游戏设置 / 单人存档（默认不勾）；
    - 高级：`ExportBundleExtra`（默认开）关掉就只写下载清单（实测条目只有 `modrinth.index.json`）；
    - `.mrpack` 规则：Modrinth 能按 sha1 认出来的文件只写 `downloads`（sha1+sha512），
      认不出的进 `overrides/`；dependencies 写 `minecraft` + 加载器键
      （`fabric-loader`/`quilt-loader`/`forge`/`neoforge`）。
    - **坑：新版 NeoForge 的 profile 里没有 `net.neoforged:neoforge:<ver>` 这个库**，
      加载器版本只在版本 id 上（`neoforge-26.3.0.99`）；`LoaderDetector.DetectWithVersion`
      先从 libraries 取，取不到再从 `id`/`inheritsFrom` 用正则兜底。
    - 实测：`--exercise-mrpack` → name/deps=`minecraft=26.3,neoforge=26.3.0.99`、
      1 个下载项（sodium）、`overrides/config/…`+`overrides/options.txt`+认不出的 iris jar；
      `--exercise-mrpack-list-only` → 只有 index。
    - 导出页离屏渲染：`--render-search-panel <png> --render-export`（会选中导出页并切到 mrpack）。
    - 导出内容清单的两处坑（2026-10-28 实测）：
      1. `mods/.trash` 是删除操作的回收站，**不能**算进导出（哈希能认出来，但用户显然不想导出删掉的东西）；
         `MrpackExporter` 与导出摘要都跳过 `.trash`。
      2. 同一个 jar 的"启用 + 禁用"两份内容完全相同 → sha1 相同，
         之前的 `ToDictionary` 直接抛 `An item with the same key has already been added`；
         现在用"哈希 → 路径列表"，两份都会成为下载项（路径分别是 `x.jar` 与 `x.jar.disabled`）。
    - 禁用项（`*.jar.disabled` / `*.zip.disabled`）也会导出：内容不变，导入方会得到同样禁用的文件。
    - 换实例时导出表单会整体刷新（`RefreshExportForm(force: true)`）：包名与"将导出…"摘要
      都换成新实例的；以前会保留上一个实例的摘要（实测 495 个 Mod 的摘要留在 6 个 Mod 的实例上）。
    - 取消「打包配置/设置/存档」时（只生成下载清单）：`Mod 配置`/`游戏设置`/`单人存档`
      移到打包开关**下面并缩进**（勾选关系一眼可见），同时 `IsEnabled={Binding ExportBundleExtra}`
      置灰；摘要改成「只生成下载清单：…；配置、游戏设置、存档都不会打包（上面的三个勾选这次不生效），
      无法识别的文件也会被丢掉。」——否则勾了也不生效，容易让人误解。
    - **导出内容改成 PCL 风格的规则文件（2026-10-28）**：`<数据根>\export-rules.txt`，启动时读一次
      （不存在就写入默认规则）。语法：
      - `config/` 目录、`options.txt` 单文件、`!logs/` 排除、`*` `?` 通配、靠后的行覆盖靠前的；
      - `=====` 之后的行是**绝对路径**的额外文件（结尾 `/` 表示文件夹），与 PCL 一样直接放到包根；
      - 默认规则包含 options*/config/defaultconfigs，排除 saves/logs/crash-reports/*.log/*.dat_old。
      导出页有「规则 N 条」摘要 + 编辑规则文件 + 重新读取 + 「添加目录」（自动追加一行如 `schematics/`）。
      实测：默认规则 → `overrides/config/mod.toml` + `overrides/options.txt`；加 `schematics/` 后
      `overrides/schematics/castle.litematic` 进包；`=====` 里的绝对文件 → `extra.txt`、`folder/a.txt` 在包根。
      - 两个坑：目录规则的匹配写成 `pattern[1..]`（去掉了首字符而不是结尾的 `/`），导致 `config/`
        一条都不命中；以及规则只在进过导出页时才加载，直接导出会拿到空规则 —— 现在构造时就加载，
        且 `ExportMrpackAsync` 开头再读一次（用户可能刚改过文件）。
      - PCL 的 `IncludeJava` / `IncludeLauncher` / 启动器个性化不实现（我们只导出模组包）；
        `DontCheckHostedAssets` 大致对应「打包 overrides + 认不出的文件」；`ModrinthUploadMode`
        （只打包非 Modrinth 托管文件）暂未做，需要逐文件判断托管状态。

## 关键文件

- `src/NPMC.Launcher.App/LauncherRoot.cs`：根目录优先级。
- `src/NPMC.Launcher.Core/Paths/LauncherPaths.cs`：`Portable`、`GameRoot`。
- `src/NPMC.Launcher.Core/Instances/InstanceLayout.cs`：游戏目录/SharedRoot/Paths。
- `src/NPMC.Launcher.App/ViewModels/MainWindowViewModel.ContentViews.cs`：内容页签的
  「列表 ⇄ 下载模式」、按类型变化的文案（下载 X / 返回 X 列表 / 筛选提示）。
- `src/NPMC.Launcher.Core/Content/ContentKind.cs`：`SupportsDownload`（模组/资源包/光影）。
- `src/NPMC.Launcher.App/ViewModels/MainWindowViewModel.cs`：搜索、分页、复制/导出、命令状态；
  `RememberInstalledIdentity`（装完写识别缓存）、`ShowSearchSummary`。
- `src/NPMC.Launcher.App/Diagnostics/PanelRenderProbe.cs`：离屏渲染搜索面板，用来核对布局。
- `src/NPMC.Launcher.App/ViewModels/MainWindowViewModel.BlockedMods.cs`：blocked 导入/PackFiles。
- `src/NPMC.Launcher.App/ViewModels/SearchCategoryOption.cs`
- `src/NPMC.Launcher.App/ViewModels/SearchSortOption.cs`
- `src/NPMC.Launcher.App/MainWindow.axaml`：实例概览、搜索面板、导出页签。
- `src/NPMC.Launcher.Infrastructure/Modrinth/ModrinthClient.cs`：`offset`、categories facet。
- `src/NPMC.Launcher.Infrastructure/CurseForge/CurseForgeClient.cs`：`index`、`categoryId`、`sortField`。
- `src/NPMC.Launcher.Infrastructure/Modpacks/CurseForgeModpackPreparer.cs`：zip 分类。
- `src/NPMC.Launcher.Infrastructure/Mods/ForgeInstaller.cs`：用 `paths.SharedRoot` 作为安装器目标；BMCLAPI 预补。
- `tools/publish.ps1`：先发布到 `.publish` staging，再复制产物；**不会清空 dist 数据**。
- `tools/start.cmd`：便携启动，先 `icacls /setintegritylevel Medium` 再启动。

## 构建/发布

```powershell
cd D:\Agents\NPMC-Launcher
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget"
dotnet build NPMC.Launcher.sln -c Release --nologo -maxcpucount:1 -nodeReuse:false
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish.ps1
```

注意：不要用旧的 `publish.ps1` 逻辑把 `dotnet publish -o dist` 直接指到 dist；会清空 `dist\data` / `dist\minecraft`。当前脚本已改为 `.publish` staging。

## 已知问题 / 下一步

1. Modrinth `technology` 标签偏宽：VeinMiner、Euphoria Patches、Nature's Compass 等都会被归类为科技。用户希望更像 PCL2 的“硬核科技”；建议加排除 `utility/equipment` 或关键词过滤。
2. 分页的“下一页”在恰好整页时启用，可能最后一页为空；如需精确可改为先探测 `total_hits`（Modrinth）或 CF 的 `pagination.totalCount`。
3. `dist\data` / `dist\minecraft` 为空是之前发布脚本误清空导致的；用户已有复制备份，未恢复。不要自动往 dist 搬数据。
4. `E:\aaaa` 是可运行工作副本；`icacls` 上不应出现 `Low`。若复制后变 Low，删掉、从 Medium 的 dist 重拷，或使用 `start.cmd`。
5. CurseForge 需要 key：`%APPDATA%\NPMC-Launcher\curseforge.key`；设置页也可保存。
6. 账号：`E:\aaaa\data\accounts.json`；正式微软设备码登录仍需用户完成一次授权。
7. 用 `--root` 跑 `--dump-instances` 会改写 `%APPDATA%\NPMC-Launcher\last-root.txt`
   （`LauncherRoot.Resolve` 无条件 `Remember`），跑完要改回 `E:\aaaa`；否则从没有
   `data`/`minecraft` 的目录启动时会去找那个临时路径。
8. 诊断用的临时根目录要按**旧式布局**准备：`--root` 直接就是数据根
   （`<root>\instances.json`、`<root>\versions\<id>\<id>.json`、`<root>\instances\<id>`），
   `--dump-instances` 不走 `LauncherPaths.Portable`。
9. 离屏渲染（`--render-search-panel`）不会做字体回退：不传 `--render-font "Microsoft YaHei UI"`
   的话中文会画成方框、宽度还会算成 0（文字叠在一起）。它只适合核对几何布局，别拿它判断字体。
10. 搜索结果右侧详情栏没选中时是空白的（列表只用左半屏）；如果把计划面板挪到右侧，
    列表就能用满整行高度 —— 需要的话再和用户确认这个改法。
11. **Avalonia 只在控件挂上时问一次 `CanExecute`**，之后不会自动重算。凡是
    `RelayCommand` 的 CanExecute 依赖页签/可见性这类外部状态，必须自己
    `RaiseCanExecuteChanged()`（或干脆用可见性控制、不挂 CanExecute）。
    2026-10-27 的「下载模组」按钮变灰就是踩了这个：按钮在概览页就挂上了（CanExecute=false），
    切到模组页没人通知它，于是永远是灰的。现在那个命令不挂 CanExecute，靠
    `IsContentFilterVisible` 控制显隐。
12. 复现启动问题时**不要**直接用真实实例跑：我按 `nmpc-command.txt` 复现时，虽然换了 gameDir 与
    org.lwjgl extract 路径，但 `-Djna.tmpdir` / `-Dio.netty.native.workdir` 仍指向实例所在的
    `versions\26.3\natives\{jna,netty}`，于是往 `E:\aaaa` 写了这两个缓存目录。复现请在临时根里
    把这四个 -D 参数一起改掉。
13. 两个**既有**测试失败（与本次改动无关）：`SharedRootTests.SharedRoot_overrides_only_the_shared_directories`
    与 `InstanceLayoutTests.Full_isolation_moves_libraries_and_assets_into_the_instance`，
    它们仍断言"Full 隔离时 runtime 留在启动器根目录"，与现在的实现不一致。
14. 26.3 + Sodium 0.9.2 的 mixin 崩溃是模组侧不兼容（`LevelExtractorMixin` 找不到
    `extractVisibleBlockEntities`），与本机库缓存问题无关；建议换 Sodium 更新版本或反馈上游。
15. **Sodium 在 26.3 上只有 beta 能用**（2026-10-27 实测）：
    - 客户端 26.3（原版与 NeoForge 补丁 jar 一致）：`extractVisibleBlockEntities(Camera, float, LevelRenderState)`
    - `sodium-neoforge-0.9.2+mc26.3`（release，作者推荐那个）：mixin 目标多一个 `Frustum` 参数 → 必崩
    - `sodium-neoforge-0.9.3-beta.1+mc26.3`：重构掉该 mixin，配 Iris 1.11.7 实测 90 秒未崩、图形引导正常
    所以给用户推荐 Sodium 时要选 0.9.3-beta.1，别选 release 的 0.9.2。

## 用户偏好

- 界面中文；按钮不要堆在底部；目录入口放概览上方。
- 搜索要高级筛选/排序/分页，参考 PCL2。
- 不要未经确认清理 dist/实例数据。
- 发布后要签名/保持 Medium 完整性；exe 标题显示构建时间。
- 装完模组**不要清空搜索列表**：要能接着装下一个。
- 用户已授权：以后发布完**自动**把 `dist\NPMC.Launcher.App.exe` 与 `start.cmd` 覆盖复制到
  `E:\aaaa`（`data\`、`minecraft\` 不动），无需每次再问。

## 下次开工先做（2026-10-28 更新）

1. **离线皮肤：只剩 1.21.6 / 快照 / Quilt 没测**（小）：26.2 / 26.3 已支持并实测
   （见 §8）。按 `docs/skin-compatibility.md` §6 的表补这几项即可；
   26.2 Forge/NeoForge、26.3 Forge 没逐个点开，逻辑同已验证版本。
2. **authlib 11+ 的预研**（可选）：上限目前截在 10；新主版本出现时先核对
   `SessionService.getPackedTextures` / `unpackTextures` 的签名。
3. **搜索质量**（小）：①「硬核科技」过滤（Modrinth technology 太宽）②末页探测
   `total_hits`/CF `pagination.totalCount`，避免整页时出现空页。
4. **一键全部升级**（小~中）：目前检查更新后要逐行点「升级」；要做批量、进度与失败汇总。
5. **Fabric/Quilt 的 Low 完整性**（中）：`java.exe` 是 Low 时 `SetFileSecurity` 失败（错误 5），
   重映射会挂；正解是让 JVM 的临时/remap 目录落到可写位置，而不是硬抬标签。
6. **补 VM 层测试**（中）：这轮踩过的坑（Avalonia CanExecute 只求值一次、facet 分组、
   识别缓存新增字段的反序列化、版本号重复时的 Display、数据包 loaders 过滤、mrpack 选项）
   都值得写成测试。
7. **CurseForge 整合包导出**（中，可选）：CF 的 `manifest.json` 需要 projectID/fileID，
   只能靠 CF 的指纹接口（要 API key）反查；Modrinth 侧已经是 mrpack，CF 侧需要另做映射。
8. 顺带可做：资源包/光影分类中译、CF 资源包分类映射、右侧详情/计划布局、发布签名与
   SHA256 清单、用户手册。

已完成：数据包下载界面、导出整合包表单（mrpack）、离线皮肤覆盖 1.7.10 ~ 26.3
（-javaagent 拆分、authlib 7 三个 host 属性、SkinManager.get 包装、26.3 的
getPackedTextures 钩子），用户实机确认。

## 本次会话新增的诊断入口（收尾时整理）

- `--exercise-search --exercise-kind mod|resourcepack|shader|datapack --exercise-query X`
  `--exercise-category 名字 --exercise-secondary 名字`：搜索 + 可选分类
  （数据包会自动进入第一个世界）。
- `--exercise-search-install`：真下载一次，校验结果保留 + 新行认领 + 点图标跳转。
- `--exercise-reinstall-version`：已有 A 版本再装 B 版本。
- `--render-search-panel <png> [--render-kind] [--render-list] [--render-height] [--render-font]`：
  离屏渲染核对布局。
