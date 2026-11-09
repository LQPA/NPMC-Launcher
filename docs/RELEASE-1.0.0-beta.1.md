# NPMC Launcher 1.0.0-beta.1（预发布）

首个公开测试版（beta）。Windows x64 单文件，自带 .NET 运行时，**不需要安装任何前置**（不需要装 .NET、不需要 Java —— 游戏运行时由启动器自动下载）。

> 这是预发布版本：功能已基本跑通，但仍可能有未覆盖的边界情况，遇到问题请带上 `logs/` 目录反馈。

## 这一版做了什么

### 整合包

- **三种格式都能导入**：
  - Modrinth `.mrpack`（`modrinth.index.json`）
  - CurseForge `.zip`（`manifest.json`，需要 CurseForge API key）
  - **MultiMC / Prism / HMCL 实例包**（`mmc-pack.json` + `instance.cfg`，文件全在包里，导入不需要联网）
- **加载器版本严格按包内声明**，不做"就近换版本"：包里写 Fabric 0.18.4 就装 0.18.4，官方没有就明确报错。
- **装之前先预检**：Fabric / Quilt 查 profile 是否存在，Forge / NeoForge 提前拉安装包验证 —— 不会再"白下几百 MB 才报错"。
- 整合包作者的内存预设变成**自动内存的下限**，不再覆盖你在设置页选的"自动内存"。

### 实例管理

- 拖动排序不再清空右侧详情；收藏的实例固定置顶（收藏顶到组首，取消收藏移到非收藏组首）。
- 「只显示收藏」+ 搜索框（实例名 / 版本号 / 整合包名，Esc 清空）。
- 内容页显示 `.trash` 回收站占用并支持两击确认清空（删除 / 升级模组的备份不再悄悄吃磁盘）。

### 启动诊断

- 崩溃诊断改成**证据分级**：结构化缺失依赖 > jar 交叉校验 > 崩溃点前的 `NoClassDefFoundError` > Mixin 警告（只作次生症状）。
- 新增模组元数据索引（`mods.toml` / `neoforge.mods.toml` / `fabric.mod.json` / `quilt.mod.json`）与类名反查的包名索引缓存。
- 归档目录里的 `result.json` 现在会回填诊断结论（以前永远是 null）。
- 启动耗时埋点：`NPMC_TIMING=1` 时把各阶段耗时写进 `logs/launcher.log`（实测 37 个实例首屏约 0.5 s）。

### 工程

- 发布脚本：目标 exe 被占用时先温和关闭再强杀、复制后 SHA256 校验（不会再"脚本说完成、实际是旧版"）。
- 命令行：`--root` 支持 `--no-remember`，诊断用临时目录不会污染 `last-root.txt`。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1607+ / Windows 11，**x64** |
| 运行时 | 无需安装（exe 自带 .NET 8 运行时，不依赖 VC++ 运行库） |
| 位置 | 放在**可写目录**（会在 exe 旁创建 `data/`、`minecraft/`、`runtime/`） |
| 网络 | 首次安装游戏/Java 需要联网 |

放 `Program Files` 会因权限失败；`%TEMP%` 被策略禁用时会启动不了（单文件需要解压原生库）。

## 已知限制（beta 期）

- **联机（P2P）入口目前置灰**，代码保留但不开放。
- 未做代码签名：别的机器首次运行会提示"未知发布者"。
- 搜索标签分类仍偏宽（Modrinth 的 `technology` 标签），暂未细分。
- 类名反查依赖包名启发式，极少数会指向同名包的模组。

## 文件校验

```
NPMC.Launcher.App.exe
sha256=1b95479aec01c4b1229093e3bd6ca8d8363036cf64c414819f76787f0a228d5a
```

`SHA256SUMS.txt` 和 `NPMC.Launcher.App.exe.sha256` 由发布脚本一起生成。

## 快速开始

1. 双击 `NPMC.Launcher.App.exe`；
2. 「下载」页选择游戏版本 → 点「下载并创建实例」（首次会自动下载本体与 Java 运行时）；
3. 想装整合包：下载页切到「整合包」，在线搜索或把 `.mrpack` / `.zip` 拖进页面；
4. 启动失败时看「日志」页或实例概览里的诊断卡片，`logs/launches/<时间>/` 里有完整归档。
