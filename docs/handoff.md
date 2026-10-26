# 交接说明（Handoff）

> 写于 2026-10-26，对应提交 `56ce5a3`。
> 新会话请先读本文件，再读 `AGENTS.md`（项目根）与 `docs/architecture.md`。

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
| Forge | **仅 ≤1.12**（读 `install_profile.json`，内嵌 universal jar，不需要跑安装器）；1.13+ 需要 processors，**未实现** |
| 旧版本启动 | `minecraftArguments` 旧格式、`-cp`/`-Djava.library.path` 手补、natives 解压、`${user_properties}` 等旧占位符 |
| 内容管理 | 模组 / 资源包 / 光影 / 数据包：搜索安装（Modrinth）、启停、删除、图标、更新检查、R·B·A 通道标记 |
| 数据包 | 按世界管理：存档页签列世界 → 行内「数据包」进入该世界的数据包页 → 「← 返回存档」 |
| 存档 | 世界列表（打开目录 / 数据包 / 删除）；**拖入文件夹或 zip 导入**（含嵌套布局、防 zip-slip）；**不支持 rar/7z**（明确不做） |
| 拖入导入 | 模组 `.jar`、其余 `.zip`；同名不覆盖；扩展名按页签校验 |
| 进度 | 下载与整合包安装的进度条 |
| 发布 | `tools/publish.ps1`，README 说明"开发用 VBS，发布用 exe" |

---

## 3. 待办

| 优先级 | 事项 | 备注 |
|---|---|---|
| 高 | **设置页**（左侧导航仍是灰的） | 全局默认 Java、预下载指定 Java、镜像（官方/BMCLAPI）、并发/超时、代理、语言 |
| 中 | 1.13+ Forge | 必须跑官方安装器 jar 的 processors；目前直接拒绝并有提示 |
| 中 | 整合包在线列表 | Modrinth 搜索整合包，现在只能拖本地 `.mrpack` |
| 中 | CLI 迁移到 `MinecraftInstaller` | 现在 CLI 有自己一份 install 实现（natives 就是两边都要改才暴露的） |
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
| `--exercise-datapacks` | 存档页签：世界列表 → 数据包页 → 返回 |
| `--exercise-datapack-race` | 紧邻点击（切页签立即点数据包）是否还能扫到内容 |
| `--exercise-nav` | 面板/头部/列表/空提示的可见性随导航变化 |
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

---

## 7. 未提交/未完成的事项

- 无未提交改动；`main` 与远端同步于 `56ce5a3`。
- 会话末尾有两个回归已修（空提示行、离开页签清理），见该提交说明。
