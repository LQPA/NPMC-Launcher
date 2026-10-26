# tools

仓库自带的开发脚本。两个文件都是纯文本、不含机器相关路径、不含密钥，所以随仓库分发。

> 判定标准与本仓库 `.gitignore` 的用语一致：**"本机专用脚本"不入库**（如 `build.ps1`、`push.ps1`，
> 它们绑定了本机的 `DOTNET_CLI_HOME` 与 `-m:1` 之类 workaround）；**可复现的工具脚本入库**。

| 脚本 | 用途 |
|---|---|
| `verify-commit.ps1` | 提交前守卫：拦截"文件数异常多"、"误提交运行时目录"、"单文件超过 5 MB" |
| `sign-local.ps1` | 生成自签名代码签名证书并给 exe 签名，用于把"未知发布者"换成证书名 |

## verify-commit.ps1

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-commit.ps1
```

退出码非 0 表示未通过。建议在 `git commit` 之前跑。

为什么需要它：本仓库曾经因为 `.gitignore` 只写了 `/.data/`，把 357 个游戏文件（170 MB）
提交并推送了出去，之后必须重写历史才能清除。

## sign-local.ps1

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\sign-local.ps1
```

它做三件事：

1. 在当前用户的证书存储里创建（或复用）一张自签名代码签名证书
2. 把它的**公钥证书**导入 `Cert:\CurrentUser\Root` 与 `TrustedPublisher`
3. 给 `src/NPMC.Launcher.App/bin/Release/net8.0/NPMC.Launcher.App.exe` 签名

### 两点必须知道

- **会写系统状态**：第 2 步修改当前用户的证书存储，属于对系统的持久改动，可逆但不留痕迹地回滚
  需要手动从证书管理器删除。仅在你有意消除"未知发布者"弹窗时运行。
- **不会泄露私钥**：脚本只用 `Export-Certificate`（公钥）导出到 `%TEMP%`，私钥始终留在证书存储中。
  `.gitignore` 另外拦住了 `*.pfx` / `*.p12` / `*.snk` 等可能含私钥的文件。

### 实测结论

自签名**只能**把弹窗里的"未知发布者"换成证书名，**无法**通过 SmartScreen 的信誉判定。
换句话说，签名链路是有效的，但要真正不再被拦截，需要 OV/EV 代码签名证书。
详细记录见 `docs/architecture.md` 的环境问题表。

## publish.ps1

产出可分发的启动器。默认打成**自包含单文件**（目标机器不需要预装 .NET 运行时）：

```powershell
.\tools\publish.ps1                        # dist\NPMC.Launcher.App.exe，约 150 MB
.\tools\publish.ps1 -FrameworkDependent    # 框架依赖版（体积小，需 .NET 8 Desktop Runtime）
.\tools\publish.ps1 -OutDir E:\nmpc-dist   # 换输出目录
```

### 开发用 VBS，发布用 exe

`launch.cmd` / `launch-hidden.vbs` **只服务于本机开发**：

| 现象 | 原因 |
|---|---|
| 必须用 dotnet 宿主启动 | `D:\Agents` 被标成 Low 完整性，自建 apphost 继承该标签后以 Low 运行 → 打不开资源管理器、Fabric 重映射写不出缓存 |
| 必须用 VBS 隐藏窗口 | `dotnet.exe` 是控制台子系统程序，会留一个黑窗 |

发布版本装在正常目录（`%LOCALAPPDATA%`、`Program Files`）时，apphost 以 **Medium** 运行；
而 App 项目本身是 `<OutputType>WinExe</OutputType>`，**exe 自己不会弹控制台**。

所以：**不要把 VBS 打进发布包**，也不要为了发布去改 `D:\Agents` 的完整性标签。

### 发布后注意

- 启动器首次启动会在 **exe 同目录** 建立 `runtime\`（Java 运行时）、`instances\` 等，
  要放在可写位置，推荐 `%LOCALAPPDATA%\NPMC Launcher`。
- 若把程序放在被标记为 Low 完整性的目录里，启动器自己无法提升 Java 的标签，
  Fabric 实例会启动失败并给出提示 —— 换个目录即可（原版不受影响）。

### 单文件 exe 的解包行为

`PublishSingleFile` 会把原生库（SkiaSharp、Avalonia 等）打进 exe，**首次运行时解包到临时目录**
（默认 `%TEMP%\.net\<程序名>\`）。正常环境下无需干预。

若把 exe 放在**低完整性目录**（如被标记为 Low 的工作区）里运行，解包会失败并提示：

```
Failed to create directory [...\.net\NPMC.Launcher.App\] for extracting bundled files.
```

两种处理：

```powershell
# 1) 最省事：放到正常目录（推荐 %LOCALAPPDATA%）
# 2) 或指定解包目录到可写位置
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "D:\某个可写目录\bundle-cache"
.\NPMC.Launcher.App.exe
```

实测：指定该环境变量后，单文件 exe 在低完整性目录里也能正常运行。
