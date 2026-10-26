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
| 实例管理 | 创建 / 改名 / 删除（两次点击确认）/ 版本隔离 / 独立内存与窗口尺寸 |
| 版本下载 | 官方 + BMCLAPI 镜像；版本类型筛选（仅正式 / 正式+快照 / 全部 / 仅愚人节） |
| Java | 本地运行时自动发现 / 系统 JDK / 自定义路径；**启动时按需自动下载**对应版本；候选列表按实例缓存（706ms → 151ms） |
| Fabric | 安装、加载器版本选择、按 intermediary 检测版本是否受支持 |
| Forge | ≤1.12 解安装包（内嵌 universal jar，不需要 java）；**1.13+ 跑官方安装器**（`--installClient`，安装阶段需要 Java）；下载页现在可选 Forge 版本 |
| 旧版本启动 | `minecraftArguments` 旧格式、`-cp`/`-Djava.library.path` 手补、natives 解压、`${user_properties}` 等旧占位符 |
| 内容管理 | 模组 / 资源包 / 光影 / 数据包：搜索安装（Modrinth）、启停、删除、图标、更新检查、R·B·A 通道标记 |
| 数据包 | 按世界管理：存档页签列世界 → 行内「数据包」进入该世界的数据包页 → 「← 返回存档」 |
| 存档 | 世界列表（打开目录 / 数据包 / 删除）；**拖入文件夹或 zip 导入**（含嵌套布局、防 zip-slip）；**不支持 rar/7z**（明确不做） |
| 拖入导入 | 模组 `.jar`、其余 `.zip`；同名不覆盖；扩展名按页签校验 |
| 进度 | 下载与整合包安装的进度条 |
| 发布 | `tools/publish.ps1`，README 说明"开发用 VBS，发布用 exe" |
| 设置 | 下载源/镜像/回退、并发/超时、代理、默认内存、**全局默认 Java**、**预下载 Java 运行时**（Mojang 官方清单 + 进度）；改一项即存；CLI `npmc settings show/set` 同步支持 |

---

## 3. 待办

| 优先级 | 事项 | 备注 |
|---|---|---|
| 中 | 整合包在线列表 | Modrinth 搜索整合包，现在只能拖本地 `.mrpack` |
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
| `--exercise-forge-versions [mc]` | Forge 加载器清单（maven-metadata.xml）：数量、最新/最早（不下载安装包） |
| `--exercise-download-forge <mc> [loader]` | 走界面同一条路：下载本体 → 装 Forge（1.13+ 官方安装器）→ 建实例 |
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

- 工作区干净。`main` 上比远端多五个本地提交：`54913d3`（页签可见性）、`852bd96`（设置页 + 存档数字）、
  下载页高亮修复、`4728ab0`（Forge 1.13+ 自动安装），以及记录它们的文档提交；
  **没有 push**，要同步远端请显式执行。
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

### 9.2 设置页（原先左侧导航是灰的）

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

入口三处都通了：**下载页勾选 Forge**（列出 `maven-metadata.xml` 里该游戏版本的全部加载器）、
**Forge 整合包**（`.mrpack` 声明 forge 时自动装）、**CLI**（`npmc loader forge`）。

实测记录（2026-10-26）：

- `npmc loader forge --version 1.20.1 --loader 47.2.0` → 官方安装器跑完，写出
  `versions/1.20.1-forge-47.2.0/`，`--exercise-launch-plan` 显示占位符无残留、`-p` 缺 0 个；
- 用该版本建实例后真实启动，日志出现 `Forge Version Check ... Current: 47.2.0`，
  游戏窗口正常（45 秒后由诊断结束进程）；
- `--exercise-download-forge 1.20.1 47.2.0` 走界面同一条路（下载本体 → 装 Forge → 建实例），
  实例名 `1.20.1-forge-47.2.0`、版本正确；
- 下载页勾选 Forge 后：版本下拉出现（该游戏版本 13~163 个候选）、实例名自动带 `-forge-<版本>`、
  状态行显示候选数量（截图已核对）。

### 10.3 代价与限制（别当成 bug）

- 安装阶段**必须有 Java**（1.13~1.16 的安装器按 Java 8 编译，1.17+ 通常要 17/21）；
- 安装器的下载走 **Forge 官方源**，不受启动器的 BMCLAPI 镜像设置影响（要镜像得自己实现 processors）；
- 安装器会往根目录 `launcher_profiles.json` 注入一个带大图标的 profile，属正常副作用；
- 每个 Forge 版本首次安装要跑几十秒到几分钟（补丁是逐类打的，输出很长，进度区会刷屏）。

### 10.4 顺带修的启动参数缺口

Forge 1.17+ 的版本 JSON 用 `${library_directory}` / `${classpath_separator}` 拼 `-p` 与 `ignoreList`。
`architecture.md` 的"占位符全集"里一直写着这两个，但 `ArgumentBuilder.CreateDefault` **从来没设**，
所以哪怕安装成功也起不来。现已补上，`--exercise-launch-plan` 会打印未替换占位符与 `-p` 缺失文件数
（实测 1.20.1 + Forge 47.2.0：占位符无残留、缺失 0 个，真实启动到游戏窗口并加载 Forge）。
