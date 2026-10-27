# NPMC Launcher 交接（2026-10-27 22:52）

## 当前状态

- 仓库：`D:\Agents\NPMC-Launcher`
- 分支：`main`
- 最新代码提交：`8f9659c`（本文件在它之后单独提交）
- 最新 exe：`D:\Agents\NPMC-Launcher\dist\NPMC.Launcher.App.exe`（22:52，44.1 MB，已抬 Medium）
- 工作副本：`E:\aaaa`，已同步同一个 exe + `start.cmd`
- 运行数据：`E:\aaaa\data`、`E:\aaaa\minecraft`（`dist\data` / `dist\minecraft` 目前为空，用户有备份）

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

## 关键文件

- `src/NPMC.Launcher.App/LauncherRoot.cs`：根目录优先级。
- `src/NPMC.Launcher.Core/Paths/LauncherPaths.cs`：`Portable`、`GameRoot`。
- `src/NPMC.Launcher.Core/Instances/InstanceLayout.cs`：游戏目录/SharedRoot/Paths。
- `src/NPMC.Launcher.App/ViewModels/MainWindowViewModel.cs`：搜索、分页、复制/导出、命令状态。
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

## 用户偏好

- 界面中文；按钮不要堆在底部；目录入口放概览上方。
- 搜索要高级筛选/排序/分页，参考 PCL2。
- 不要未经确认清理 dist/实例数据。
- 发布后要签名/保持 Medium 完整性；exe 标题显示构建时间。
- 装完模组**不要清空搜索列表**：要能接着装下一个。
- 用户已授权：以后发布完**自动**把 `dist\NPMC.Launcher.App.exe` 与 `start.cmd` 覆盖复制到
  `E:\aaaa`（`data\`、`minecraft\` 不动），无需每次再问。
