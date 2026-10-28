# NPMC Launcher 交接（2026-10-28 10:40）

## 当前状态

- 仓库：`D:\Agents\NPMC-Launcher`
- 分支：`main`
- 最新提交：以 `git log -1 --oneline` 为准（本文档不记录自己的提交号）
- 最新 exe：`D:\Agents\NPMC-Launcher\dist\NPMC.Launcher.App.exe`（01:07，44.1 MB，已抬 Medium）
- 工作副本：`E:\aaaa`，已同步同一个 exe + `start.cmd`
- 本次会话：2026-10-28 上午在昨天的基础上继续（数据包下载界面），提交与 exe 时间看下面。
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

1. **搜索质量**（小）：①「硬核科技」过滤（Modrinth technology 太宽）②末页探测
   `total_hits`/CF `pagination.totalCount`，避免整页时出现空页。
2. **一键全部升级**（小~中）：目前检查更新后要逐行点「升级」；要做批量、进度与失败汇总。
3. **Fabric/Quilt 的 Low 完整性**（中）：`java.exe` 是 Low 时 `SetFileSecurity` 失败（错误 5），
   重映射会挂；正解是让 JVM 的临时/remap 目录落到可写位置，而不是硬抬标签。
4. **补 VM 层测试**（中）：这轮踩过的坑（Avalonia CanExecute 只求值一次、facet 分组、
   识别缓存新增字段的反序列化、版本号重复时的 Display、数据包 loaders 过滤、mrpack 选项）
   都值得写成测试。
5. **CurseForge 整合包导出**（中，可选）：CF 的 `manifest.json` 需要 projectID/fileID，
   只能靠 CF 的指纹接口（要 API key）反查；Modrinth 侧已经是 mrpack，CF 侧需要另做映射。
6. 顺带可做：资源包/光影分类中译、CF 资源包分类映射、右侧详情/计划布局、发布签名与
   SHA256 清单、用户手册。

已完成：数据包下载界面、导出整合包表单（mrpack）。

## 本次会话新增的诊断入口（收尾时整理）

- `--exercise-search --exercise-kind mod|resourcepack|shader|datapack --exercise-query X`
  `--exercise-category 名字 --exercise-secondary 名字`：搜索 + 可选分类
  （数据包会自动进入第一个世界）。
- `--exercise-search-install`：真下载一次，校验结果保留 + 新行认领 + 点图标跳转。
- `--exercise-reinstall-version`：已有 A 版本再装 B 版本。
- `--render-search-panel <png> [--render-kind] [--render-list] [--render-height] [--render-font]`：
  离屏渲染核对布局。
