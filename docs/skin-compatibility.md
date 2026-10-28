# 皮肤机制版本/加载器对照（2026-10-28）

离线皮肤 agent 的兼容性资料：**哪个版本改了什么、我们靠哪一处钩子接管、哪些已验证**。
代码在 `tools\skin-agent\src\nmpc\skin\`，构建脚本 `tools\skin-agent\build.ps1`。

> 判断某个实例走哪套机制：先看版本 JSON 的 `libraries` 里 `com.mojang:authlib:<版本>`，
> 再对照下面两张表。运行时的决定性日志是游戏 `latest.log` 里的
> `Environment: Environment[sessionHost=..., servicesHost=..., profilesHost=..., name=...]`，
> 以及 `E:\...\data\logs\skin-agent.log`。

## 1. 版本 → authlib → 机制（以本机实际装过的版本为准）

| 游戏版本 | authlib | 皮肤入口（authlib 侧） | 我们用的 jar | 本轮验证 |
|---|---|---|---|---|
| 1.7.10 | 1.5.21 | `getTextures(GameProfile, boolean) → Map`；另需对付 SkinPort 模组 | stub + `impl-legacy`（Java 8） | 以前验证过，本轮未回归 |
| 1.8 – 1.14.x | 1.x – 3.x | 同上（`getTextures`） | stub + `impl-legacy` | 未验证 |
| 1.15 | 1.5.25 | 同上（`getTextures`） | stub + `impl-legacy` | **已实测（Fabric 0.20.1）** |
| 1.20.1 | 4.0.43 | `getTextures(...)` 仍在；开始有 `getSecurePropertyValue`（签名校验） | `modern`（Java 17） | 未验证 |
| 1.20.5 – 1.20.6 | 5.x（推断，未本地验证） | 切到 `unpackTextures(Property) → MinecraftProfileTextures` | `modern` | 未验证 |
| 1.21.1 | 6.0.54 | `unpackTextures`；`getPackedTextures`；`fetchProfile(UUID, boolean)` | `modern` | **已实测（NeoForge 21.1.265）** |
| 1.21.6 | 6.0.58 | 同上（与 6.0.54 同一套） | `modern` | 未验证 |
| 1.21.11 | 7.0.61 | 同上；**环境属性必须三个全有**（见 §3） | `modern` | **已实测（Fabric 0.20.1）** |
| 26.1.2 | 7.0.63 | 同上；客户端 `SkinManager.get` 在没有 textures 时直接返回空 | `modern` | 上一轮验证过（见交接文档） |
| 26.2 | 9.0.75 | `Yggdrasil*` 类族退役，改用 `MinecraftServices*`（推断） | **不挂 agent** | 未验证 / 未支持 |
| 26.3 | 10.0.77 | `MinecraftServicesSessionService implements SessionService`（`unpackTextures` 仍在） | **不挂 agent** | 未验证 / 未支持 |

`SkinAgentProvider.SupportsSkinAgent` 目前只认 authlib **主版本 1–7**：
26.2（9.x）与 26.3（10.x）**连 session host 都不改**，所以那两个版本现在没有离线皮肤，
但也不会因为 agent 崩游戏。要支持它们得走 §7 的方案。

## 2. 三个时代的 authlib API（决定我们 hook 哪个方法）

| 时代 | 取皮肤入口 | 返回 | 备注 |
|---|---|---|---|
| 1.x – 4.x | `YggdrasilMinecraftSessionService.getTextures(GameProfile, boolean)` | `Map<Type, MinecraftProfileTexture>` | 我们的做法：把原方法改名 `nmpc$getTextures$impl`，另生成同名方法按 UUID 分流（本地账号返回本地纹理，其他人走原实现） |
| 5.x – 7.x | `YggdrasilMinecraftSessionService.unpackTextures(Property)` | `MinecraftProfileTextures` | 整段替换；`textures` 属性的 base64 JSON 里带 `127.0.0.1:<端口>` 才算"我们自己的" |
| 9.x – 10.x | `SessionService.unpackTextures(Property)`（实现类 `MinecraftServicesSessionService`） | 同上 | 类名变了，方法签名没变；但**环境变量机制也变了**（§3） |

另外两个随版本变化的点：

- **签名纹理**：1.19 起纹理属性带签名（authlib 4 的 `getSecurePropertyValue`、
  6/7 的 `SignatureState`）。我们的桥接直接构造 `UNSIGNED` 的
  `MinecraftProfileTextures`，绕开校验；线上账号（微软）不挂 agent，不受影响。
- **ProfileResult**：authlib 6 起 `fetchProfile` 返回 `ProfileResult` 而不是 `GameProfile`。
  我们不改它，只靠本地 HTTP 接口给它"喂"带 textures 的 profile。

## 3. `minecraft.api.*.host` 属性：6 和 7 不一样（1.21.11 的坑）

| authlib | 读取的属性 | 缺一个会怎样 |
|---|---|---|
| 1.x | 无（老 authlib 不读系统属性） | — |
| 4.x | `auth` / `account` / `session` / `services` | 退回官方 |
| 6.0.54 / 6.0.58 | `session` + `services` | 退回官方 |
| **7.x** | `session` + `services` + **`profiles`** | **整体忽略**，打一行 `Ignoring hosts properties. All need to be set: [...]`，客户端仍然去 `sessionserver.mojang.com` 查离线账号 → 没有 textures 属性 → 皮肤静默不生效 |
| 9.x / 10.x | `discovery.host` / `env`（`EnvironmentParser`） | 旧的 session/services 写法失效 |

`SkinAgent.premain` 现在把 `auth`/`account`/`session`/`services`/`profiles` 全设上：
session 指本地，其余指官方。**多设没用，少设是致命的**。

> 1.21.11 Fabric 当时的表现：游戏能启动、`unpackTextures` 也替换成功了，但
> `texturesFor` 一次都没被调用 —— 因为客户端根本没拿到 textures 属性。
> 修好属性后日志才有 `profile 963a... -> <皮肤路径>` 和 `texturesFor: ours=true`。

## 4. 加载器差异（谁改了类名 / 谁能看见 agent 的类）

| 加载器 | 类加载方式 | 游戏类名 | 对皮肤机制的影响 |
|---|---|---|---|
| 原版 | 普通 AppClassLoader | mojmap（官方名） | 无模块问题；`-javaagent` 直接可用 |
| Forge 1.7.10 | LaunchWrapper（自带 ASM 5，无模块） | 官方名（部分 SRG） | Java 8 → 用 `legacy` jar；SkinPort 等模组会自己接管皮肤，需额外 hook `ForgeSkinPort$DefaultSkinProvider.getSkin` |
| Forge 1.12.2 | LaunchWrapper | 官方类名 + SRG 方法名 | 用 `legacy` jar；`SkinManager` 方法名是 `m_xxx_`，按名字的钩子不会命中（也不需要） |
| Forge 1.16 – 1.20.1 | ModLauncher + **JPMS 模块** | 官方类名 + SRG 方法名 | authlib 在 module layer：桥接类必须进 **bootstrap**，且给模块加 **read edge**，否则 `NoClassDefFoundError` / `IllegalAccessError` |
| NeoForge 1.21+ | ModLauncher + JPMS 模块 | **mojmap**（方法名也是 mojmap） | 同上；`SkinManager` 钩子能命中（1.21.1 日志里打出 `getInsecureSkin` / `getOrLoad` / `registerTextures`） |
| Fabric 1.14+ | KnotClassLoader（无 JPMS 模块） | **intermediary**（`net/minecraft/class_xxxx` / `method_xxxx`） | ① `-javaagent` 的 jar 在系统类路径上，**里面不能有 ASM**（`LoaderUtil.verifyClasspath` 会拒绝重复 ASM）；② 桥接类**只能挂 bootstrap**：`appendToSystemClassLoaderSearch` 对 Knot 不算数，会报 `ClassNotFoundException: ... hasn't been exposed to the game`（1.15 Fabric 实测）；③ authlib 类名不变（authlib 不参与 remap），`getTextures` / `unpackTextures` 钩子照常命中；④ 写死 mojmap 的 `SkinManager` 钩子**匹配不上** |
| Quilt | 与 Fabric 同一套（Knot 派生） | intermediary | 理论上同 Fabric；未验证 |

### Fabric 的 SkinManager 具体名字（1.21.11）

- mojmap：`net/minecraft/client/resources/SkinManager`
- intermediary：`net/minecraft/class_1071`
- 方法（intermediary）：
  - `method_52863` = `get(GameProfile) → CompletableFuture<Optional<PlayerSkin>>`
  - `method_73544` = `getInsecureSkin(GameProfile, boolean) → Supplier<PlayerSkin>`
  - `method_52859` = `registerTextures(UUID, MinecraftProfileTextures) → CompletableFuture<PlayerSkin>`

（来源：1.21.11 客户端 jar + `intermediary-1.21.11.jar` 的 `mappings.tiny` 对照。
每个版本的 intermediary 名字都不同，26.x 要重新查。）

## 5. 我们现有的钩子（`SkinTransformer`）

| 目标 | 触发条件 | 注入的调用 |
|---|---|---|
| `YggdrasilMinecraftSessionService.getTextures(GameProfile, Z)Map` | authlib 1–4 | 改名保留原方法 + 生成分流方法，调 `SkinBridgeLegacy.isLocal/textures` |
| `YggdrasilMinecraftSessionService.unpackTextures(Property)MinecraftProfileTextures` | authlib 5–7（要求 `modernReady`） | 整段替换为 `SkinBridgeModern.texturesFor(property)` |
| `SkinManager.get(<单参数>)CompletableFuture` | **mojmap** 名字、`modernReady` | 整段替换为 `SkinBridgeModern.get(skinManager, profile)`（26.1.2 需要；1.21.1 没有 `get`，不触发；Fabric 下类名是 intermediary，不触发） |
| `ForgeSkinPort$DefaultSkinProvider.getSkin(IPlayerProfile)ISkin` | 1.7.10 SkinPort | `SkinPortBridge.get(provider, profile)` |

本地 HTTP 服务（`SkinHttpServer`，`ServerSocket` + daemon 线程）：

- `/session/minecraft/profile/<uuid>`：返回带 `textures` 属性的 profile（base64 JSON，
  纹理 URL 指向 `http://127.0.0.1:<port>/textures/<sha1>.png`）；
- `/textures/<sha1>.png`：皮肤 PNG（文件名用内容 SHA-1，避免客户端缓存住旧皮肤）；
- `/skins/<uuid|name>`：1.7.10 SkinPort 的 CustomServer2 用；
- `/player/attributes`、`/publickeys`：给 authlib 7 的 services 探测兜底。

## 6. 测试清单（按这个跑就能覆盖所有分支）

每个实例进一次世界，然后看 `data\logs\skin-agent.log`：

| 场景 | 期望日志（关键行） |
|---|---|
| NeoForge 1.21.1（authlib 6） | `bootstrap search: ...nmpc-skin-bridge.jar`、`module support installed`、`transforming ...YggdrasilMinecraftSessionService`、`replacing ...unpackTextures`、`profile <uuid> -> ...png`、`texturesFor: ours=true` |
| Fabric 1.15（authlib 1.5.25，Java 8） | `bootstrap search: ...`（**不再是** `system search`）、`wrapping ...getTextures(...)`、`legacy textures served url=http://127.0.0.1:<port>/...`；**不应**出现 `NoClassDefFoundError: nmpc/skin/SkinBridgeLegacy` |
| Fabric 1.21.11（authlib 7） | 同上，且 `read edge added: null -> null`（Fabric 没有模块）；`latest.log` 里 `sessionHost=http://127.0.0.1:<port>`；**不应**出现 `Ignoring hosts properties` |
| Forge 1.20.1（authlib 4） | `wrapping ...getTextures(...)`、`legacy textures served url=...`（桥接日志前缀是 `legacy`） |
| 1.7.10（authlib 1 + SkinPort） | `bootstrap: java 1.8..., skipped`、`wrapping ...getTextures`、`skinport get id=... name=...` |
| Fabric/NeoForge 26.2 / 26.3 | 当前**没有** agent 日志（authlib ≥8 不挂），皮肤是默认皮肤 |

排查顺序：先看 `Environment:` 行的 sessionHost 是不是 `127.0.0.1`；再看有没有
`transforming/replacing`；最后看 `texturesFor: ours=`（false 就看 `property check` 那行的
port / decoded 前缀）。

## 7. 已知缺口 / 下一步

1. **authlib 8/9/10（26.2 / 26.3）没有支持**：`YggdrasilMinecraftSessionService` 换成了
   `MinecraftServicesSessionService`，`EnvironmentParser` 只认 `minecraft.api.discovery.host`
   和 `minecraft.api.env`，旧的 session host 重定向失效。可选做法：
   - 把 transformer 的类名扩到 `com/mojang/authlib/services/MinecraftServicesSessionService`，
     并拦 `fetchProfile(UUID, boolean)` 直接返回带本地 textures 的 `ProfileResult`；
   - 或在客户端 `SkinManager` 层接管（mojmap 对 NeoForge 可行；Fabric 要用该版本的
     intermediary 名，见 §4）。
2. **Fabric/Quilt 的 `SkinManager` 钩子**：目前没接。1.21.11 不需要（authlib 路径已经够用），
   但如果以后某个版本必须靠它，得按 §4 的 intermediary 名字匹配，并且每个版本都要重新查。
3. **Forge 1.20.1 / 1.21.6 / 26.1.2 / Quilt 未回归**：本轮只实测了
   NeoForge 1.21.1 与 Fabric 1.21.11，其它版本建议按 §6 的表逐项跑。
4. **authlib 5 的实际游戏版本**（1.20.5/1.20.6）未本地验证，表里是推断；
   到时候看版本 JSON 就能确认。
