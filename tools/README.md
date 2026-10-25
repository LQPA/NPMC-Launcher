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
