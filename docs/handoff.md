# 交接说明（Handoff）

> 写于 2026-10-26，对应提交 `56ce5a3`；下午的页签回归修复见第 8 节（`54913d3`），
> 设置页与存档数字修复见第 9 节（`852bd96`）。
> 新会话请先读本文件，再读 `D:\Agents\AGENTS.md`（工作区规则）与 `docs/architecture.md`。

---

## 1. 项目概况

- **名称**：NPMC Launcher —— Minecraft 启动器（启动 / 管理 / 下载）
- **技术栈**：C# / .NET 8 + Avalonia 11.3.2，Windows 桌面
- **仓库**：`https://github.com/LQPA/NPMC-Launcher.git`，分支 `main`，许可 AGPL-3.0
- **本地路径**：`D:\Agents\NPMC-Launcher`
- **数据目录**：`D:\Agents\NPMC-Launcher\.data`（不入库）
- **发布产物**：`dist\NPMC.Launcher.App.exe`（单文件自包含，约 43 MB）

### 工程结构

```
src/NPMC.Launcher.Core            纯逻辑：版本 JSON、参数拼装、依赖解析、内容扫描
src/NPMC.Launcher.Infrastructure  网络与落盘：镜像、下载、安装、启动
src/NPMC.Launcher.App             Avalonia 界面（MainWindowViewModel 是唯一的大 VM）
src/NPMC.Launcher.Cli             命令行（有自己的 install 实现，尚未迁到 MinecraftInstaller）
tests/NPMC.Launcher.Core.Tests    SelfTestRunner 自测（270 项）
tools/publish.ps1                 发布 exe
tools/verify-commit.ps1           提交前检查
tools/sign-local.ps1              本地签名
```

---

## 2. 已完成的功能

| 领域 | 状态 |
|---|---|
| 实例管理 | 创建 / 重命名（**显示名与实例目录一起改**）/ 删除（两次点击确认）/ **按住拖动排序**（顺序写入 instances.json）/ **外部改文件夹名自动同步** / 版本隔离 / 独立内存与窗口尺寸 |
| 版本下载 | 官方 + BMCLAPI 镜像；版本类型筛选（仅正式 / 正式+快照 / 全部 / 仅愚人节） |
| Java | 本地运行时自动发现 / 系统 JDK / 自定义路径；**启动时按需自动下载**对应版本；候选列表按实例缓存（706ms → 151ms） |
| Fabric | 安装、加载器版本选择、按 intermediary 检测版本是否受支持 |
| Fabric | 安装、加载器版本选择、按 intermediary 检测版本是否受支持（下载页加载器四选一，没有对应版本时置灰） |
| Forge | ≤1.12 解安装包（内嵌 universal jar，不需要 java）；**1.13+ 跑官方安装器**（`--installClient`，安装阶段需要 Java）；下载页可选 Forge 版本 |
| NeoForge | 与 Forge 1.13+ 同一套官方安装器（1.20.1 走 `net/neoforged/forge` 老坐标，其余走 `net/neoforged/neoforge`）；下载页可选版本 |
| 旧版本启动 | `minecraftArguments` 旧格式、`-cp`/`-Djava.library.path` 手补、natives 解压、`${user_properties}` 等旧占位符 |
| 内容管理 | 模组 / 资源包 / 光影 / 数据包：搜索安装（Modrinth）、启停、删除、图标、更新检查、R·B·A 通道标记 |
| 账号 | **离线账号**：账号页（左列表 + 右详情）/ 新建 / 改用户名（重算离线 UUID）/ 删除 / 设为默认；每个实例可单独选账号；启动时写入 `--username` 与 `--uuid`；CLI `npmc account list/add/default/remove` |
| 数据包 | 按世界管理：存档页签列世界 → 行内「数据包」进入该世界的数据包页 → 「← 返回存档」 |
| 存档 | 世界列表（打开目录 / 数据包 / 删除）；**拖入文件夹或 zip 导入**（含嵌套布局、防 zip-slip）；**不支持 rar/7z**（明确不做） |
| 拖入导入 | 模组 `.jar`、其余 `.zip`；同名不覆盖；扩展名按页签校验 |
| 进度 | 下载与整合包安装的进度条 |
| 发布 | `tools/publish.ps1`，README 说明"开发用 VBS，发布用 exe" |
| 设置 | 下载源/镜像/回退、并发/超时、代理、默认内存、**默认账号**、**全局默认 Java**、**预下载 Java 运行时**（Mojang 官方清单 + 进度）；改一项即存；CLI `npmc settings show/set` 同步支持 |

---

## 3. 待办

| 优先级 | 事项 | 备注 |
|---|---|---|
| 中 | CurseForge 整合包 | 后端已通（manifest 解析 + 逐文件 download-url + 8 路并发），ATM10 8.3 实测装好；还差下载页的 CF 搜索/版本列表 UI |
| 中 | CLI 迁移到 `MinecraftInstaller` | 现在 CLI 有自己一份 install 实现（natives 就是两边都要改才暴露的） |
| 低 | 界面多语言 | 文案还没抽出来；`Language` 目前只影响 Modrinth 描述翻译。设置页只写了一行说明，别做成"能选但没用"的下拉 |
| 低 | CurseForge 数据包分类 | classId 未核实，宁可不提供也不猜 |
| 低 | 死代码清理 | `SelectedWorld`、`ManageDataPacksCommand`、`HasDatapacksEntry` 已无 UI 使用 |
| 低 | `docs/architecture.md` 补录 | 新增的坑（占位符、natives、数据包按世界、可见性通知）应补进去 |

---

## 4. 工程约定（务必遵守）

### 4.1 XAML 只绑"会被显式通知"的属性

**这是本会话踩得最多的坑**，三次以上的"界面不刷新"都源于此：

- 绑定**派生属性**（`=>` 现算）→ 只在绑定求值时算一次，之后没人 `OnPropertyChanged` → 界面永远停在初始值。
- 典型翻车：按钮可见但禁用、列表扫描到了却不显示、切页面后旧面板残留。

**做法**：组合逻辑放 VM，并在**每个输入变化处**通知；或把属性改为字段 + `SetProperty`。

### 4.2 提交前四步（缺一不可）

```
1. dotnet build NPMC.Launcher.sln -c Release        必须 0 error
2. 启动冒烟 7 秒                                     MainWindow 能构造出来（XAML 错误在这步暴露）
3. SelfTestRunner                                   必须 270/270
4. tools\verify-commit.ps1                          必须通过
```

> 冒烟测试抓到过两次"按行号改 XAML 改错位置"的问题（一次插进日志页签，一次把属性加到 `/>` 之后）。**涉及 XAML 的改动必须先冒烟再提交。**

### 4.3 构建前先结束运行中的启动器

否则 `MSB3027 / MSB3021`：DLL 被占用。文件名是 `NPMC.Launcher.App.exe` 或宿主 `dotnet`。

```powershell
Get-Process -Name 'NPMC.Launcher.App' -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
```

### 4.4 构建环境变量

```powershell
$env:DOTNET_CLI_HOME = "<repo>\.dotnet-home"
$env:NUGET_PACKAGES  = "<repo>\.nuget"
dotnet build NPMC.Launcher.sln -c Release --nologo -maxcpucount:1 -nodeReuse:false
```

测试**不要用 `dotnet test`**：

```powershell
dotnet build tests\NPMC.Launcher.Core.Tests\...csproj -c Release -p:SelfTestRunner=true
tests\NPMC.Launcher.Core.Tests\bin\Release\net8.0\NPMC.Launcher.Core.Tests.exe
```

---

## 5. 诊断入口

界面逻辑无法靠 UI 直接验证，所以 VM 上有一批 `--exercise-*` 诊断（`src/NPMC.Launcher.App/InstanceDump.cs`）：

```powershell
dotnet src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.dll `
  --dump-instances .data\dump.txt --root .data --exercise-nav
```

| 参数 | 验证内容 |
|---|---|
| `--exercise-caps` | 加载器/光影支持、入口可见性、按钮可用性、列表行文案 |
| `--exercise-java` | Java 候选、当前选中、提示文本 |
| `--exercise-java-cache` | 首次与再次进入同一实例的耗时 |
| `--exercise-datapacks` | 存档页签：世界列表 → 数据包页 → 返回；**页签数字 + 跨实例切换后数字不残留** |
| `--exercise-datapack-race` | 紧邻点击（切页签立即点数据包）是否还能扫到内容 |
| `--exercise-nav` | 面板/头部/列表/空提示/日志的可见性随导航变化，**并核对 PropertyChanged 是否真的发出** |
| `--exercise-global-settings` | 设置页：字段 ↔ `settings.json` ↔ 启动计划（默认 Java）；改完**还原原设置** |
| `--exercise-tab-counts` | 停在日志页双向切实例，逐项核对页签数字与实例实际数量 |
| `--exercise-instance-order` | 拖动排序：VM → `instances.json` → 重新读取，最后还原原值 |
| `--exercise-instance-rename` | 改名：显示名与目录一起改，旧目录消失/新目录出现，最后改回 |
| `--exercise-instance-folder-sync` | 直接在磁盘上改实例文件夹名，确认刷新后 Id/名称同步，再改回 |
| `--exercise-accounts` | 离线账号：新建 → 设默认 → 绑定实例 → 启动解析 → 还原并删除临时账号 |
| `--exercise-loader-cache [mc]` | 加载器检查：首次联网耗时 vs 切回命中缓存耗时（顺带验证不会卡在"正在检查"） |
| `--exercise-download-loader <mc> [fabric|forge|neoforge] [loader]` | 走界面同一条路：下载本体 → 装加载器 → 建实例 |
| `--exercise-forge-versions` | Forge / NeoForge 加载器清单（两个 maven 的 metadata，只读不下载） |
| `--exercise-launch-plan [实例名]` | 只组装启动计划：占位符残留、`-p`/`-cp` 里缺失的 jar、问题列表 |
| `--exercise-world-import` | 文件夹 / zip(根) / zip(嵌套) / 坏包 四种导入 |
| `--exercise-launch [实例名]` | 真实启动并等 45 秒，返回 PID 与日志尾部 |
| `--print-launch-command` | 只打印命令行，不启动 |

---

## 6. 关键坑位（已解决，别踩回去）

1. **完整性级别**：`D:\Agents` 是 Low 完整性，`java.exe` 继承后 JDK 认为文件不可写 → Fabric 重映射失败（`client-intermediary.jar.tmp can't be written`）。`IntegrityGuard` 会把它抬到 Medium。**不要**"删掉目录的完整性标签"来绕过。
2. **旧版参数占位符**：`${user_properties}` 必须展开成合法 JSON（`{}`），否则游戏报 `Expected BEGIN_OBJECT but was STRING`。
3. **natives**：旧版用 `natives` 映射 + `${arch}`，必须解压 `.dll` 到 `versions/<id>/natives`，否则 `UnsatisfiedLinkError: no lwjgl`。
4. **Forge 1.7.10 坐标**：是 `1.7.10-10.13.4.1614-1.7.10`（版本号重复一次），单写会 404。
5. **数据包按世界**：`saves/<世界>/datapacks`，不是实例级；启停靠改名 `.zip ↔ .zip.disabled`。
6. **扫描版本号竞态**：`RefreshContentAsync` 只在**解析出目录之后**才递增版本号，否则"还没进世界的那次刷新"会把稍后有效扫描判为过期。
7. **切页签/切页面要双向清理**：进入存档页签要清 `DataPackWorld`，**离开也要清**，否则「← 返回存档」会跟到别的页签。
8. **空提示不要和搜索面板同 Grid.Row**：两者都放 Row 0/1 会叠在一起，提示应在列表区（Row 2）。
9. **派生可见性属性要集中通知**：`ShowOverview` / `ShowContentPane` / `ShowContentList` / `ShowLog` /
   `IsContentEmptyOrNotList` 全是 `=>` 现算的。切页签时只通知 `IsOverview` 之类不够 —— 旧面板会继续
   画在新页签下面（两排操作按钮叠在一起、概览表单透到存档页、日志页签点开是空白）。
   统一走 `RaisePaneVisibilityChanged()`，它在选中实例、切页签、切页面、数据包视图变化时都会被调到。
10. **绑定不会回写"没变化的源值"**：不可用页签被点中后，ListBox 已经改了选中项，而 VM 里
    `OnPropertyChanged(nameof(SelectedTab))` 因为源值没变，绑定不会把目标值拉回去 ——
    界面上就是"光影高亮、内容还是日志"。回退必须在控件层做（`MainWindow.OnTabSelectionChanged`）。
    用 `Dispatcher.Post` 推迟通知也**不管用**，别白试。
11. **Forge 1.13+ 的 install_profile 里没有 `versionInfo`**：版本 JSON 在安装包的 `version.json`，
    要安装器写出来；顶层字段是 `spec` / `version` / `minecraft` / `processors`（1.12 及以前才是
    `install` + `versionInfo`）。写解析代码时别只认老结构。
12. **Forge 1.17+ 的启动参数依赖 `${library_directory}` 与 `${classpath_separator}`**：
    它们拼在 `-p` 模块路径与 `-DignoreList` 里。漏设不会报错，只会把字面量 `${...}` 交给 JVM，
    表现是"启动闪退 / 找不到主类"。`architecture.md` 的占位符全集里一直列着这两个，
    但代码 2026-10-26 之前从没 `Set` 过 —— 文档全 ≠ 实现全。

---

## 7. 未提交/未完成的事项

- 工作区干净。`main` 上比远端多若干本地提交（页签可见性、设置页、下载页高亮、
  Forge/NeoForge 安装、加载器四选一，以及记录它们的文档提交）；**没有 push**，要同步远端请显式执行。
- 会话末尾有两个回归已修（空提示行、离开页签清理），见 `56ce5a3` 说明。

---

## 8. 2026-10-26 下午：页签可见性回归（已修）

用户实测报的三处错位，根因是同一个：**切页签时没有通知派生可见性属性**。

| 现象 | 根因 |
|---|---|
| 资源包/模组页签：底部两排按钮叠在一起，列表顶部还露出一条 `内存` 数字框的残影 | `ShowOverview` 仍为真，概览面板和概览操作行没被收起来，只在 Row 1/Row 2 之间的间隙里露出来 |
| 存档页签：概览表单调到世界列表上面 | 同上 |
| 光影页签：同样两排按钮重叠 | 同上 |
| **（顺带发现）日志页签点开是空白** | `ShowLog` 从来没被通知过，日志面板一直隐藏 |
| **（顺带发现）点不可用页签会"高亮但不切换"** | 见第 6 节第 10 条，回退放进 `OnTabSelectionChanged` |

改法：

- `MainWindowViewModel.RaisePaneVisibilityChanged()` 统一通知那 5 个派生属性，供 `SelectedInstance`
  的 setter、`SelectedTab` 的 setter、`Navigate`、`RaiseWorldViewChanged` 复用。
- `MainWindow.axaml` 的页签 ListBox 加 `SelectionChanged="OnTabSelectionChanged"`，把被拒绝的
  选中项拉回 VM 的有效页签。
- `--exercise-nav` 现在订阅 `PropertyChanged`，逐个页签打印"通知：齐全 / 缺 xxx"，
  并把资源包、日志页签也走一遍。**它跑通只说明值对，值对不等于界面会刷新** —— 这个诊断专门补这一课。

验证方式（本机）：

```powershell
$env:DOTNET_CLI_HOME = "<repo>\.dotnet-home"; $env:NUGET_PACKAGES = "<repo>\.nuget"
dotnet src\NPMC.Launcher.App\bin\Release\net8.0\NPMC.Launcher.App.dll `
  --dump-instances .data\nav.txt --root .data --exercise-nav
```

界面侧只能靠截图核对：逐页签点一遍，确认①只有一排操作按钮 ②概览只出现在概览页签
③存档页签的世界列表上方没有概览残影 ④日志页签能出内容 ⑤点不可用页签后高亮不跟走。

---

## 9. 2026-10-26 晚：设置页 + 存档数字（已修/已做）

### 9.1 存档页签数字残留（用户报："没有存档却显示 1"）

根因有两层，都在 `RefreshCountsAsync`：

1. 数据包页签的目录是"按世界"的，没进世界时 `ResolveContentDirectory` 返回 null，
   而代码写的是 `return`（不是 `continue`）→ 整个循环提前结束，页签 `Count` **保留上一次的值**。
2. 那个"上一次的值"可能是别的实例/别的世界留下的 → 换个 0 存档的实例，页签还写着「存档 1」。

改法：数据包的数字统一由 `UpdateDataPackTabCount()` 维护 —— 没进世界时是**世界数**（这一页列的就是世界），
进了世界才是**该世界的数据包数**；它在 `LoadWorlds` / `BackToWorlds` / `RefreshContentAsync` 之后都会重算。
顺带修了同源的另一个：**停在存档页签时切换实例**，旧实例的世界列表不刷新（点进去会打开别人的存档目录）——
现在 `SelectedInstance` 的 setter 里会清 `DataPackWorld` 并 `LoadWorlds()`。

`--exercise-datapacks` 增加"页签数字"与"26.1.2 管理世界后切到无存档实例"的检查。

#### 9.1.1 停在日志页换实例，所有页签数字都不刷新（2026-10-26 再修）

用户报：实例 A（3 资源包）→ 切到日志页 → 换实例 B（2 资源包），页签仍是 A 的 3。
根因：`SelectedInstance` 的 setter 只在 **非日志页** 调 `RefreshContentAsync()`，
而页签数字只在它里面刷新；停在日志页时只跑了 `RefreshLogAsync()`，数字完全没重算。

改法：
- setter 里**无条件**先 `_ = RefreshCountsAsync(value)`（模组/资源包/光影/存档 + 左侧实例行 + 目录入口），
  再决定刷新日志还是内容；
- `UpdateDataPackTabCount()` 的"世界数"改用 `SelectedInstance.SaveCount`，
  不再读 `Worlds.Count` —— 停在日志页时世界列表根本没加载，读它会清成 0。

`--exercise-tab-counts` 覆盖双向切换（A→日志→B、B→日志→A），逐项对比页签数字与实例实际数量。

### 9.1.2 CurseForge 整合包（ATM10 实测，2026-10-26 晚）

- key 来源：`CURSEFORGE_API_KEY` 环境变量（User 级即可）或 `%APPDATA%\\NPMC-Launcher\\curseforge.key`；
  已开着的进程读不到之后 setx 的变量，要重启。
- CF 整合包是 `manifest.json`（minecraft.version / modLoaders / files[{projectID,fileID}]）+ `overrides/`，
  **没有**下载地址、文件名、SHA1；每个文件要用 `/v1/mods/{id}/files/{fileId}/download-url` 换 URL，
  URL 末段就是文件名。
- 实现：`Core/Modpacks/CurseForgeModpackManifest`（解析）、
  `Infrastructure/Modpacks/CurseForgeModpackPreparer`（下载 zip、解覆盖层、并发换 URL），
  然后合成一份 Modrinth 风格索引，**复用**现有 `ModpackInstaller` / 加载器 / 建实例流程。
- ATM10 8.3 实测：1.21.1 + NeoForge 21.1.259，493 个模组、覆盖层 3467 个；
  最终 489 个 jar（493 条清单里有 4 条同名/非 jar）。
- 两个坑：
  1. 首次 NeoForge 安装失败时旧代码会"按原版创建"继续装模组 → 实例版本记成 1.21.1，
     模组全不生效。现在**加载器失败直接中止**，不再建半成品实例。
  2. 中断后重跑会优先复用同名实例，并把 `VersionId` 更新成新的加载器版本（之前会留在 1.21.1）。
- 下载目录：只监听**默认浏览器**的下载目录（注册表 `http\UserChoice` 的 ProgId 识别 Chrome/Edge/Brave/Vivaldi/Opera/Firefox，读各自配置），\n  识别不到就用系统 Downloads；用户可手动「选择下载目录」追加。\n  实测 Chrome 未自定义下载目录时退回 `C:\\Users\\<用户>\\Downloads`，多目录监听测试通过。\n- 作者禁止 API 下载的文件**默认不回退**：安装计划里会列成「只能从 CurseForge 手动下载：<名字>（<slug>）」，\n  界面用黄色提示展示，并说明 Modrinth 同项目版本不一定与作者测试过的一致；\n  只有显式传 `allowModrinthFallback: true` 才会用 Modrinth 同名项目补齐（slug 必须完全一致）。\n  ATM10 实测教训：Structory 的 Modrinth 最新版是给新版本的包，放进 1.21.1 会被 NeoForge 判为\n  `not a valid mod file`，直接导致「broken mod state」；所以默认不能静默替换。\n- 现在 blocked 文件会带 `fileName` + `sha1` + CurseForge 文件页 URL：\n  `BlockedModFile`（Core）→ `ModpackPlan.Blocked` → 界面黄色面板；\n  「打开全部下载页」用 `Process.Start(UseShellExecute)` 呼起默认浏览器；\n  `BlockedModImporter` 监听系统 Downloads（或用户选的目录），文件稳定后算 SHA1，命中就复制到\n  实例的 `mods/`，并显示 ✔ 已导入；错误内容会被哈希拒绝。\n  验证：`--exercise-blocked`（合成文件）与 `--exercise-cf-blocked`（ATM10 真实 6 个文件，带 SHA1 与链接）。\n  版本取该游戏版本 + 加载器下的最新正式版；ATM10 的 6 个阻塞文件已全部补齐。\n  注意：CF 清单钉的是具体文件版本，Modrinth 只能给到"同项目最新版"，版本号可能不同，\n  属于"能跑但未必与作者测试过的完全一致"；CF 独占的模组仍然补不了。\n- 下载速度：`InstallIntoAsync` 原来串行，CF 493 个文件慢到不可用；改成默认 8 路并发
  （取 `settings.DownloadConcurrency`，钳制 1~16），无 SHA1 的文件"存在且非空"即跳过。
  实测从 288 → 488 个模组约 2 分钟；全部已存在的 493 个文件重跑 47 秒。

### 9.1.3 实例的整合包标记（2026-10-26 晚）

- 每个整合包实例在游戏目录写 `.nmpc-modpack.json`：`Source`（curseforge/modrinth）、`ProjectId`、
  `Name`、`Version`、`Missing`（还没补齐的 blocked 文件，含文件名 + SHA1 + 文件页 URL）。
- 实例列表与详情标题下显示：`CurseForge 整合包 · 缺少 6 个模组`；普通实例不显示。
- 下载页安装完成后出现 blocked 面板；关闭启动器后再回来，选中实例时概览页会重新加载同样的列表
  （`InstanceModpackStore` + `BlockedModImporter`），可随时点「打开下载页」补文件，导入后标记与数字自动更新。
- ATM10 实测：列表与概览显示「缺少 6 个模组」，面板 6 条；手动放入正确 SHA1 的文件后应变为 0
  （合成测试已覆盖：错误内容被拒、正确内容导入）。

### 9.1.4 整合包实例的模组整理（2026-10-26 晚）\n\n- 模组页默认勾选「隐藏整合包自带模组」：按 `.nmpc-modpack.json` 的 `PackFiles` 过滤，\n  ATM10 实测 495 个自带模组折叠后列表为 0；取消勾选可看全部。\n- 空列表提示改为「没有手动添加的模组；整合包自带的 N 个已折叠」。\n- 更新检查：整合包自带文件**不检查更新**（作者钉的是整套版本），只检查用户手动添加的文件；\n  没有手动模组时状态栏直接写明「整合包实例：整合包内模组不做单独更新检查」。\n  实测：折叠后 0 个 → 跳过；加一个 `zzz-user-mod.jar` → 只检查这 1 个。\n- 说明：`PackFiles` 在新安装时自动写入；老实例（如当前 ATM10）已按现有 mods 文件名补了一份。\n\n### 9.2 设置页（原先左侧导航是灰的）

- 代码：`ViewModels/MainWindowViewModel.Settings.cs`（partial，设置页专用的字段/命令都在这）、
  `ViewModels/RuntimeChoiceItem.cs`；XAML 在 `MainWindow.axaml` 末尾的「设置页」块。
- 全局设置模型新增 `HttpTimeoutSeconds` / `HttpProxy` / `DefaultJavaPath`；
  `JsonSettingsStore.Load()` 同步重载供**建 HttpClient 之前**读取（代理/超时只能那时生效）。
- 启动计划 Java 优先级补了一档：实例 JavaPath → 实例 JavaComponent → **全局默认** → 版本元数据。
- 新建实例（下载页 / 整合包）现在用 `NewInstanceSettings()`，初始内存取设置页的「默认内存」。
- 预下载 Java 运行时：`MojangRuntimeInstaller.GetComponentsAsync("windows-x64")` 列真实组件（8 个，含版本号与"已安装"），
  选一个点「下载」即整套装进 `<root>\runtime\<组件名>`；下载完自动刷新组件列表与 Java 候选。
- CLI 同步：`npmc settings show` 增加超时/代理/默认 Java；`set` 增加 `timeout` / `proxy` / `java`。
- 文档：`docs/architecture.md` 第 8 节已改写为"实际字段 + 生效时机"。

界面验证（本轮已截图核对）：侧栏「设置」可点且高亮、实例列表与详情面板完全让位、
四个分区（下载源 / 下载与网络 / 实例默认 / Java）排版正常、底部「恢复默认」与状态文案正常、
返回实例页与下载页无残留。

### 9.3 下载页左侧「游戏本体」首次进入不高亮（用户报）

又一处"派生属性没通知"：`VersionsModeBackground` / `VersionsModeForeground`（以及整合包那份）
都写成 `IsVersionsMode ? … : …`，而 `IsVersionsMode = 在下载页 && 选了游戏本体`。
`Navigate` 只通知了 `IsVersionsMode`，没通知这 4 个颜色属性 → 首屏进下载页时内容出来了、
左侧「游戏本体」还是透明背景。

改法：`RaiseDownloadModeChanged()` 统一通知这 7 个属性（两个分区可见性 + 4 个颜色 + 版本列表），
`Navigate` 与 `SetDownloadMode` 都调它。`--exercise-nav` 在"下载页"这一步额外核对这 4 个颜色属性
（缺了会打印"缺 VersionsModeBackground / …"）。

---

## 10. 2026-10-26：Forge 1.13+ 自动安装（已实现）

### 10.1 以前为什么只能手动装

1.12 及以前，Forge 安装包是"傻瓜包"：`install_profile.json` 里带 `versionInfo`（现成的版本 JSON）
和 `install.filePath`（内嵌 universal jar），所以解包 + 放库 + 写 JSON 就够了，**不用跑 java**。

1.13 起改成了 `processors` 模型：安装器要下载一堆工具（installertools、mcp 数据、srg 映射），
再跑若干个 java 程序去**给 client.jar 打二进制补丁**、合并 mapping，最后才生成
`forge-<mc>-<ver>-client.jar` 与版本 JSON。我们旧代码只认 `versionInfo`，看到 processors 就直接
报"暂不支持"，于是 1.13+ 只能手动装。

### 10.2 现在的做法

不自己实现那套工具链，而是把**官方安装器当黑盒**：

```
java -jar forge-<mc>-<ver>-installer.jar --installClient <启动器根目录>
```

- 安装前检查 `versions/<mc>/<mc>.json` 与 client jar 是否存在（processors 的输入）；
- 缺 `launcher_profiles.json` 就写一个最小的（安装器会往里注入 profile，没有这个文件会失败）；
- Java 由 `ForgeInstaller.ResolveInstallerJava` 选：优先版本元数据的 `javaVersion.component`
  （&lt;root&gt;\runtime\&lt;组件&gt;），其次同主版本的系统 Java；界面上缺运行时会先自动下载；
- 安装器自己下库、跑 processors、写 `versions/<新版本>/`；我们只核对产物 JSON 是否存在；
- 版本 id 先取 `install_profile.version`，取不到再按 `versions/<mc>-forge*` 找最新的一个。

入口三处都通了：**下载页的加载器四选一**（原版 / Fabric / Forge / NeoForge，
当前游戏版本没有的加载器直接置灰，并在下面一行说明原因）、**整合包**（`.mrpack` 声明 loader 时自动装）、
**CLI**（`npmc loader fabric|forge|neoforge`）。

实测记录（2026-10-26）：

- `npmc loader forge --version 1.20.1 --loader 47.2.0` → 官方安装器跑完，写出
  `versions/1.20.1-forge-47.2.0/`，`--exercise-launch-plan` 显示占位符无残留、`-p` 缺 0 个；
- 用该版本建实例后真实启动，日志出现 `Forge Version Check ... Current: 47.2.0`，
  游戏窗口正常（45 秒后由诊断结束进程）；
- `--exercise-download-loader 26.3 neoforge 26.3.0.98` 走界面同一条路：
  实例 `26.3-neoforge-26.3.0.98` 创建成功，启动计划占位符无残留、`-p` 缺 0 个，
  真实启动日志出现 `NeoForge mod loading, version 26.3.0.98, for MC 26.3`；
- 加载器可用性（实测）：26.3 → Fabric 无、Forge 13 个、NeoForge 95 个；
  26.1.2 → Forge 23 / NeoForge 122；1.20.1 → Forge 137 / NeoForge 62（老坐标）；
  1.21.6 → Forge 10 / NeoForge 21（只有 beta）。
- 下载页截图核对：未选加载器时只有"原版"可选、缺版本的那项灰掉并给出原因；
  选 Forge/NeoForge 后出现版本下拉、实例名自动带 `-forge-` / `-neoforge-`，
  并额外提示"官方安装器、安装阶段需要 Java，可在设置页预下载"。

### 10.3 代价与限制（别当成 bug）

- 安装阶段**必须有 Java**（1.13~1.16 的安装器按 Java 8 编译，1.17+ 通常要 17/21）；
  本机缺时会先自动补装，失败则报错并指到「设置 → 预下载 Java 运行时」；
- 安装器的下载走 **Forge / NeoForge 官方源**，不受启动器的 BMCLAPI 镜像设置影响
  （要镜像得自己实现 processors）；
- 安装器会往根目录 `launcher_profiles.json` 注入一个带大图标的 profile，属正常副作用；
- 每个加载器版本首次安装要跑几十秒到几分钟（补丁是逐类打的，输出很长，进度区会刷屏）；
- NeoForge 1.20.1 的版本号是 `1.20.1-47.1.x`（老坐标 `net/neoforged/forge`），
  与后续 `21.x` / `26.x` 命名不同，`NeoForgeVersions.PrefixesFor` 里单独处理了。

### 10.4 顺带修的启动参数缺口

Forge 1.17+ 的版本 JSON 用 `${library_directory}` / `${classpath_separator}` 拼 `-p` 与 `ignoreList`。
`architecture.md` 的"占位符全集"里一直写着这两个，但 `ArgumentBuilder.CreateDefault` **从来没设**，
所以哪怕安装成功也起不来。现已补上，`--exercise-launch-plan` 会打印未替换占位符与 `-p` 缺失文件数
（实测 1.20.1 + Forge 47.2.0：占位符无残留、缺失 0 个，真实启动到游戏窗口并加载 Forge）。

### 10.5 加载器清单缓存与版本分类（2026-10-26 晚）

- **加载器清单三级缓存**：内存 → `<root>\cache\loader-versions.json`（7 天有效）→ 联网。
  切回看过的游戏版本不再重查；「重新检查」按钮（`RefreshLoadersCommand`）跳过缓存强制刷新。
  缓存文件里按游戏版本存 Fabric / Forge / NeoForge 三份清单 + `CheckedAt`。
- **修掉"切回时卡在正在检查"**：旧代码在缓存命中分支直接 `return`，但调用方已经把
  `_checkingLoaders` 置 true，于是永远停在"正在检查…"、三个加载器全灰。
  现在所有分支（内存命中 / 磁盘命中 / 联网成功 / 失败 / 没选中版本）都会把状态落回 false；
  另加 token，连着切版本时过期结果不会覆盖当前界面。
- **版本清单一次全量拉取**：第一次进下载页就拿到完整 manifest（`_allVersions`），
  之后「正式版 / 快照版本 / 早期版本 / 愚人节版本」四类筛选只在本地过滤，不再每次联网。
  四类互斥：正式版 = release；快照 = snapshot 且非愚人节；早期 = old_alpha / old_beta；
  愚人节 = 已知愚人节清单（清单里同样是 snapshot，只能靠版本号认）。
  实测：正式版 103 / 快照 747 / 早期 61 / 愚人节 9。

验证（`--exercise-loader-cache 26.3`）：首次 7235 ms（联网），切走再切回 **0 ms**、
`检查中=False`、三个加载器可用状态正确；缓存文件 `<root>\cache\loader-versions.json`。

加载器下拉里的版本号前面带 **R / B / A 标识**（绿 / 琥珀 / 红，与内容列表同一套配色）：
Forge / NeoForge 看版本号后缀 `-alpha` / `-beta`，Fabric 的 stable 记 R、其余记 B。
默认优先选最新的**正式版**，没有正式版才退回最新预发布版（NeoForge 有些版本只有 beta）。

### 10.6 实例拖动排序与重命名（2026-10-26 晚）

- **排序**：`LauncherInstance.SortIndex` 持久化在 `instances.json`；列表顺序 = SortIndex → 名称。
  旧数据没有这个字段（全是 0）→ 仍然等价于按名称排序；一旦拖动过，所有实例写入 0..n-1。
  新建实例在有显式顺序时排到最后（`max+1`），没拖过时仍然是名称排序。
- **拖动实现的两个坑**：
  1. `ListBoxItem` 会在 `PointerPressed` 里选中并把事件标记为已处理，**普通 XAML 的
     `PointerPressed=` 收不到** —— 必须 `InstanceList.AddHandler(..., RoutingStrategies.Tunnel)`；
  2. 不要用 OLE `DragDrop.DoDragDrop`：拖动过程中做"指针落在哪一行就 Move 到哪一行"的实时排序，
     半路松手也会停在当前位置，而且不依赖系统拖放循环。
  用 `ObservableCollection.Move` 而不是 Clear+Add，否则 ListBox 会丢选中项。
- **重命名**：概览页标题右侧「重命名」→ 行内编辑（保存/取消）。
  **显示名与实例目录一起改**（Id 就是目录名）：先 `Directory.Move(instances/<旧Id>, instances/<新Id>)`，
  再 `InstanceCatalog.Relocate` 落盘；落盘失败会把目录挪回去。游戏运行时禁止改名（目录被占用）。
  同名保存在"名字没变但目录与 slug 不一致"时仍会执行一次目录同步（用于修正旧数据）。
- **反向同步：在资源管理器里改文件夹名**。每个实例目录里有 `.nmpc-instance.json` 标记
  （Id / Name / VersionId）。启动或刷新时 `InstanceReconciler` 扫描 `instances\`：
  旧 Id 目录不在了、但某个新目录里的标记自称是它 → 认定文件夹被改名，
  把 **Id 与显示名都同步成新目录名** 并落盘；目录在但标记缺失 → 顺手补写标记。
  仅靠目录名不猜（没有标记的目录只报告"未纳管"，不擅自收编）。
- 验证：`--exercise-instance-order` 走排序往返并还原；`--exercise-instance-rename` 走
  「改名 → 旧目录消失/新目录出现 → 重新读取 → 改回原名」；`--exercise-instance-folder-sync`
  直接在磁盘上 `Directory.Move` 改文件夹名，确认启动器刷新后 Id/名称跟着变，再改回。
  合成鼠标拖动实测把第 2 项拖到第 1 项，`instances.json` 的 SortIndex 变为 0/1/2。

### 10.7 离线账号（2026-10-26 晚）

- 账号模型：`PlayerAccount(Id, Username, Uuid, Kind, CreatedAt)`，目前 `Kind` 只有 `Offline`；
  微软正版与 authlib-injector 留了枚举位，等审批 / 有测试环境再接。
- **离线 UUID 规则**：`MD5("OfflinePlayer:" + 用户名)`，再把 version 改成 3、variant 改成 RFC 4122。
  与 Java 的 `UUID.nameUUIDFromBytes` 完全一致（实测 Steve → `5627dd98e6be3c21b8a8e92344183641`），
  服务器/存档看到的是稳定身份。改用户名会重算 UUID。
- 存储：`<root>\accounts.json`（可手改，原子写入）；账号页 = 左侧列表 + 右侧详情
  （用户名 / UUID / 类型 / 创建时间 / 设为默认 / 删除；删除两次点击确认）。
- **账号页交互**（2026-10-26 再改）：
  - 左列上方是**搜索框**（按用户名或 UUID 过滤，`AccountSearchText`），下面是「创建账号」；
  - 点「创建账号」新建一个**空档案**，右侧要求填「用户名」+「验证方式」；
  - 用户名填好（合法）后 **700ms 自动保存**，不需要点保存按钮；
  - 名称为空 → 取消/离开账号页时不落盘（不会留下空账号）；
  - 验证方式目前只有「离线（默认）」，「微软正版」项禁用并注明未接入；没选时按离线创建。
- **皮肤**（每个账号独立）：`Skin` = Steve / Alex / 自定义 / 同名正版；
  - 自定义：选 64×64 PNG，另选手臂粗细（经典 4px / 纤细 3px），文件会复制进
    `<root>\cache\skins\`，原文件删了也不影响；
  - 同名正版：按用户名查 Mojang（`api.mojang.com` → `sessionserver` → 皮肤 PNG），
    取回后缓存，模型（classic/slim）一起记录；
  - 右侧有预览图；Steve / Alex 只做内置预览。
  - **限制**：离线账号进游戏时，原版客户端不会读取启动器的皮肤设置；
    要让皮肤真的显示，需要皮肤站 / CustomSkinLoader 之类的模组（后续接 authlib-injector 时一并处理）。

  原版取皮肤的链路是：客户端按自己的 UUID 请求
  `sessionserver.mojang.com/session/minecraft/profile/<uuid>`，离线 UUID 在 Mojang 不存在，
  所以永远回落到默认 Steve/Alex。要改变这一点，常见有三条路（按"现在能不能做"排序）：

  1. **CustomSkinLoader 本地皮肤**（不需要外置登录，只要有模组）：把 PNG 放到实例的
     `CustomSkinLoader/LocalSkin/skins/<玩家名>.png`，并按模组文档在
     `CustomSkinLoader.json` 的 loadlist 里启用 `local`。具体目录/字段以所用 CSL 版本为准。
  2. **authlib-injector + 皮肤站**：启动加 `-javaagent:authlib-injector.jar=<皮肤站 API>`，
     游戏改从皮肤站取皮肤；需要外置登录账号与可访问的皮肤站（用户目前没有测试环境）。
  3. **自建本地 Yggdrasil/皮肤服务**：能完全离线自控，但要实现签名、材质代理等，工作量大。

  参考：[CustomSkinLoader 离线皮肤讨论](https://www.mcmod.cn/post/6366.html)、
  [authlib-injector 离线按名字查皮肤](https://github.com/yushijinhun/authlib-injector/issues/256)、
  [XMCL 自定义皮肤指南](https://xmcl.app/zh/guide/custom-skins)。

- **2026-10-26 实测：能否不改实例、直接劫持客户端的会话请求？**
  - authlib 4.0.43（1.20.1）的 `EnvironmentParser` 会读
    `minecraft.api.auth.host / account.host / session.host / services.host`，但**四个必须都设**，
    只设 session 会退回官方环境。
  - 四个都指向本地 HTTP 服务后，游戏日志出现
    `Environment: authHost='http://127.0.0.1:8765' …`，本地服务确实收到了
    `/player/attributes` 与 `/publickeys` —— 说明**入口可以被劫持**。
  - 但离线本地玩家启动时**没有请求** `/session/minecraft/profile/<uuid>`：
    客户端只有在 `fillProfileProperties` / 打开社交界面等路径才会去拉纹理，
    而且多人玩家那条路径是 `requireSecure=true`（要求 Mojang 签名，本地伪造的纹理会被拒）。
  - 结论：单纯改 URL 不够；authlib-injector 之所以存在，是因为它还要改
    profile 填充/纹理校验。要真正落地离线皮肤，可行的是
    **javaagent（不是模组）+ 本地 Yggdrasil/皮肤 API**；这套工作量明显大于当前皮肤设置本身。
  - 另：26.3 用的 authlib 10.x 已经**不再读**这些 `minecraft.api.*` 属性（class 里没有这些字符串），
    所以该劫持手段只适用于 authlib 4/6/7 那一代版本。

### 10.8 离线皮肤 javaagent（2026-10-26 晚，原型已跑通）

不改实例目录、不装模组，只在启动参数里加一个 `-javaagent`：

- 源码 `tools/skin-agent/`（`build.ps1` 用 `.data` 里的 JDK 编译，产物是**内含 ASM 的 fat jar**，
  137KB）；jar 作为 EmbeddedResource 打进 Infrastructure，启动时解到 `<root>\cache\agent\`。
- agent 做的事：
  1. `127.0.0.1:<随机端口>` 起一个只服务当前账号的皮肤 API
     （`/session/minecraft/profile/<uuid>`、`/textures/<sha1>.png`、`/player/attributes`、`/publickeys`）；
  2. 四个 `minecraft.api.*.host` **必须都设**：auth/account/services 指回官方，**只把 session 指到本地**
     —— 全指本地会禁用聊天/Realms/在线功能（踩过）；
  3. 用 ASM 把 authlib 的
     `YggdrasilMinecraftSessionService.getTextures(profile, requireSecure)` 整体替换成
     `SkinBridge.textures(profile)`，直接返回本地皮肤纹理表，绕开 Mojang 签名校验。
- 纹理 URL 用**皮肤文件内容的 SHA-1** 命名：客户端拿 URL 文件名当缓存 key，固定 `skin.png`
  会命中上一次的缓存（症状：模型变了、贴图还是旧的/别人的）。
- Steve / Alex 不依赖账号里的 PNG：直接从客户端 jar 的
  `assets/minecraft/textures/entity/player/{wide,slim}/{steve,alex}.png` 取默认皮肤
  （Alex 用 slim 模型）。
- 适用版本：按版本 libraries 里的 `com.mojang:authlib` 主版本判断，**1~7**：
  - `authlib 1~3`（约 1.7.10~1.15.x）→ **legacy jar**（Java 8 编译，只替换老的 `getTextures`）；
  - `authlib 4~7`（1.16~1.20.1 / 1.21.6 / 26.1.2）→ modern jar（Java 17 编译）。
  实测 1.7.10（authlib 1.5.21）、1.20.1（4.0.43）、26.1.2（7.0.63）都生效；
  26.3（authlib 10）暂不支持。账号没有皮肤时跳过。
- **26.1.2 / authlib 7 的额外两个坑**（都已处理）：
  1. 客户端类用 Java 25 编译（class file major 69），ASM 9.6 会报
     `Unsupported class file major version 69`，已换 ASM 9.11；
  2. `GameProfile` 变成 record（`id()/name()/properties()`），且 properties 是
     **ImmutableMultimap**，改不了；同时 `SkinManager.get(profile)` 在
     `packedTextures == null` 时直接返回空、根本不会走 `unpackTextures`。
     所以对 26.1.2 直接替换 `SkinManager.get(GameProfile)`：本地账号调用它自己的
     `registerTextures(uuid, textures)`，多人里其它玩家返回空（各自默认皮肤）。
- legacy（1.7.10）没有 `minecraft.api.*` 属性可读，但它的
  `YggdrasilMinecraftSessionService.getTextures(profile, requireSecure)` 是现成的，
  直接替换即可；agent 仍会设置四个属性（旧版本忽略）。
- 实测（1.20.1 / 原版生存 / 账号 LQPA_）：agent 日志出现
  `textures served … /textures/781ebc…png model=slim` 且客户端下载了该 PNG，
  游戏内皮肤与纤细手臂都正确；换成 Notch 皮肤也正常。
- 限制：**客户端侧生效**——同一离线服务器上的其他玩家看到的仍是默认皮肤；
  皮肤 PNG 建议 64×64；多实例同时启动时各用各的随机端口，互不干扰。
- **账号解析优先级**（启动时）：实例的 `Settings.AccountId` → 全局 `settings.json` 的
  `DefaultAccountId` → 账号列表第一个；一个账号都没有时退回 `Player` 这个名字（不阻断启动）。
- 实例概览新增「账号」下拉；第一项"跟随默认账号"。选择会写进该实例的 `AccountId`。
- 注：离线账号不联网、没有令牌；`access_token` 用占位值，`user_type=legacy`。
- CLI：`npmc account list/add/default/remove`；`npmc launch` 也会打印实际使用的账号。
- 验证：`--exercise-accounts` 走"新建 → 设默认 → 绑定实例 → 解析启动账号 → 还原/删除"，
  自测新增离线 UUID（对照 Java 规则）、账号目录增删改、`LaunchContext.Offline` 等用例。
