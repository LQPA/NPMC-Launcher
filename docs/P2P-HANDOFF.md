# NPMC P2P 联机模块 · 开发交接文档

> 日期：2026-10-31
> 面向：下一个接手 P2P 联机模块的会话/开发者
> 必读顺序：**本文件 → `docs/p2p/DESIGN-OUTLINE.md`（完整设计大纲）→ `docs/HANDOFF-CURRENT.md`（启动器现状）**

---

## 新会话启动协议（先做这 6 步）

1. 工作目录设为 `D:\Agents\NPMC-Launcher`（不是 `D:\Agents`），先 `git status` 确认干净。
2. 按顺序读：本文件 → `docs/p2p/DESIGN-OUTLINE.md` 的 §1、§2、§5、§11、§12。
3. **不要假设仓库里已有 P2P 代码**：房间、节点、Gateway、TPM、Named Pipe 都还没有。
4. 向用户确认 §9 的前四项：目标版本、Agent 注入策略、支持平台、正版/离线账号。
5. 选择第一个 gate：
   - **Gate 0-B（TPM）可以立即开始**，不依赖 Minecraft 版本；
   - **Gate 0-A（Gateway）必须先有目标版本**；
   - **Gate 0-C（真实网络）需要两台机器/网络**。
6. 在写业务代码前，先建 ADR 和项目骨架；**Gate 0 跑通前不要动 `MainWindowViewModel` 的房间/节点 UI**。

“按现在的顺序开发”指的是 **Gate 0-A/0-B/0-C → D → P → S → T**，不是从 D1 开始逐条写。
D/P/S/T 的完整表格在 `docs/p2p/DESIGN-OUTLINE.md` §11。

## 0. 一分钟结论

- 这里说的 **P2P 联机模块目前只有设计文档，没有任何代码**。
- 现有仓库是 **NPMC Launcher**（.NET 8 + Avalonia 的 Minecraft 启动器）；P2P 是计划中的独立子系统，
  不要假设启动器里已经有房间、节点、Gateway、TPM 或 Named Pipe 的桩。
- 设计文档已从桌面复制到仓库：`docs/p2p/DESIGN-OUTLINE.md`。
- 设计最核心的三条：**设备身份（TPM）**、**控制面 / 游戏数据面分离且无公共中继**、
  **原版客户端 + 启动器注入 Java Agent 的 Host Gateway**。
- 最高优先级不是大厅 UI，而是三个原型：**Host Gateway 登录链路、TPM 身份、TCP P2P 直连**。
- 最大的未知数是 **Minecraft Login 协议 + Integrated Server 身份衔接**；在它跑通前，
  不要大规模开发房间/节点/ACL/运营后台。

---

## 1. 设计文档是什么

`docs/p2p/DESIGN-OUTLINE.md` 是一份 1287 行的架构大纲，定义了
**Multi-Node P2P Multiplayer System（多节点 P2P 联机系统）**：

- 不是传统「房主开服、其他人加入」；
- 一个 Lobby Room 可以同时有多个 Game Node，每个 Node 是一个独立 Minecraft 实例；
- 节点之间通过 TCP 直连传输 Minecraft 游戏流量；
- 大厅只做控制面（身份、房间、节点发现、权限、地址交换、信令）；
- 不使用公共中继，不把游戏流量转发到大厅服务器；
- 设备身份用 TPM，不使用传统账号；
- 加入者仍走 Minecraft 原版「多人游戏」入口，通过 Host Gateway 接入。

它同时给出了数据库表、HTTP API、SignalR 事件、DDoS/紧急熔断方案、
第一阶段容量（1000 设备 / 500 在线 / 300 节点 / 单区域）和 D/P/S/T 里程碑。

---

## 2. 必须记住的架构边界

### 2.1 六个子系统

| 子系统 | 职责 |
|---|---|
| A. Launcher Client | 设备管理、TPM 密钥、档案绑定、节点管理、紧急熔断 |
| B. Device Identity | TPM 证明、设备注册、签名、凭证、撤销 |
| C. Lobby Control Plane | 房间、成员、节点发现、权限、地址/信令 |
| D. Host Gateway | Minecraft TCP 监听、登录协议、身份验证、透明转发 |
| E. P2P Network | IPv6/TCP 直连、端口映射、地址轮换、连接管理 |
| F. Emergency Defense | DDoS、UDP 紧急指令、客户端熔断、临时 IP 隔离 |

第一阶段可以同机部署；但**代码模块和权限边界必须分开**，方便以后拆服务。
持久化建议 PostgreSQL + Redis。

### 2.2 控制面 / 数据面

- **控制面**：HTTPS / WSS / UDP 控制通道，传身份、房间、节点地址、ACL、信令、心跳。
- **数据面**：Minecraft TCP 直接连接，**不经过大厅**，不使用公共中继。
- 大厅短暂故障时，已经建立的 P2P 游戏连接原则上应继续运行。
- 这是整个设计最重要的原则；任何“为了提高成功率偷偷加中继”的方案都违反设计。

### 2.3 身份的三层分离

必须区分：

1. **设备身份**：谁在使用启动器（`device_id` + TPM 业务密钥）。
2. **Minecraft 身份**：哪个游戏档案在登录（正版 / 离线 UUID，另算）。
3. **节点授权**：这个设备是否有权访问目标节点（`node_acl`）。

不能因为设备通过 TPM，就认为 Minecraft 玩家身份可信；
也不能仅凭 `device_id` 或离线 UUID 判断玩家身份。
正版会话认证（Mojang/Microsoft）与 TPM 认证是两条独立链路。

### 2.4 多节点模型

- 一个 Lobby Room 有多个 Game Node。
- 每个 Node 独立拥有 `node_id`、Minecraft 实例绑定、监听端口、Gateway、地址候选集、
  ACL、生命周期。
- 一个成员可以同时创建多个 Node；**Room Owner 不自动拥有所有 Node 的控制权**。
- 节点权限模式：`Room` / `Private` / `Closed`；Private 的 ACL 必须服务端执行，不能靠客户端隐藏。
- 两种 ACL 不能混：
  - `node_acl`：哪些设备可以接入节点；
  - `node_minecraft_acl`：哪些 Minecraft 玩家 UUID 可以进入游戏。

---

## 3. 设计里已经定下的关键决策

### 3.1 设备身份

- 无传统账号系统，**设备即用户**；`device_id` 是成员关联主键。
- 强制 TPM 2.0；推荐 **Microsoft Platform Crypto Provider** + Windows CNG。
- 业务密钥：**ECDSA P-256 / SHA-256，私钥不可导出，永远留在 TPM**。
- 本地检查至少包括：TPM 2.0 存在、可用、Provider 可用、创建硬件签名密钥、
  检查不可导出、签名验签测试。
- **任何一步失败都禁用 P2P，不自动降级到 Software KSP。**
- 远程证明必须由服务器验证，不能相信客户端自报的 `key_provider = TPM`。
- 三把密钥不能混：
  - **EK**：TPM 身份根；
  - **AK**：TPM 证明；
  - **业务设备密钥**：日常签名。
- 设备状态建议：
  `NOT_CHECKED / LOCAL_CHECKING / LOCAL_FAILED / LOCAL_PASSED / ATTESTING /
  TPM_VERIFIED / ATTESTATION_FAILED / REVOKED`。
  只有服务器确认的 `TPM_VERIFIED` 才能用 P2P。
- 封禁要区分：设备公钥封禁、TPM 身份封禁、临时设备会话撤销。
  TPM 身份索引用 `HMAC-SHA256(server_secret, canonical_EK_public_key)`，
  不广泛暴露原始 EK 公钥。

### 3.2 C# 启动器 ↔ Java Agent

- Java Agent 不持有 TPM 私钥，只能通过 **Windows Named Pipe** 请求启动器签名。
- Named Pipe 必须：
  - 当前 Windows 用户 ACL；
  - 会话标识；
  - 请求完整性校验；
  - 请求类型白名单；
  - 随机 nonce；
  - 签名频率限制；
  - 绑定目标房间、节点、档案、游戏会话。
- 不能让本机任意进程通过管道签署任意消息。
- 与现有离线皮肤 Agent **功能隔离**：
  - 皮肤 Agent 只处理 `textures`；
  - P2P Agent 只处理 P2P 身份认证；
  - P2P 身份 UUID 来自启动器可信档案绑定，不从被皮肤 Agent 修改过的 GameProfile 反推；
  - 不修改正版登录结果。
- 两个 Agent 可以多个 `-javaagent` 同时加载，但必须实测加载顺序、类转换器冲突、Authlib 冲突。
- 反作弊：P2P 模式才加载；不隐藏 Agent；不伪造 JVM 参数；尽量不改 Minecraft 核心类；
  不加载不必要 Native；公开用途/签名/哈希；被反作弊拒绝就停止并报告。

### 3.3 Host Gateway 与 Minecraft 登录

设计路线：

```text
公网 IPv6:41000
      |
      v
Host Gateway
      |
      v
127.0.0.1:<integrated server 端口>
      |
      v
Minecraft Integrated Server
```

- 主机用普通 Minecraft Java 客户端，不需要安装 Fabric/Forge/项目 Mod。
- 加入者仍用原版「多人游戏」，输入 `[IPv6]:41000`。
- 如果要验证加入者 TPM 设备身份，设计了一个 `p2p:auth` 自定义 Login Plugin Request：
  Gateway 发 nonce → P2P Agent 通过 Named Pipe 请求签名 → Gateway 验证设备凭证/签名/节点授权。
- **但原版客户端在完全不加载 Java Agent 时不会响应自定义登录请求**。所以设计建议：
  **Minecraft 游戏文件保持原版，由启动器在 P2P 模式注入 Agent。**
- Gateway 认证成功后，如何把身份安全传给 Integrated Server、如何兼容正版在线模式认证，
  **必须实际验证**；Gateway 不能简单当成普通 TCP 转发器。
- 建议第一阶段先做最小可运行原型，跑通目标 Minecraft 版本的完整 Login Protocol，
  再决定是否大规模开发。

### 3.4 P2P 网络

- Minecraft Java 用 **TCP**，UDP 只用于紧急控制通道，不应作为游戏数据传输。
- 连接优先级：
  1. 局域网；
  2. 公网 IPv6 直连；
  3. 已验证的 IPv4 端口映射；
  4. 其他经过验证的 TCP 候选地址。
- 可用技术：IPv6、UPnP IGD、NAT-PMP、PCP、STUN over TCP、ICE-TCP；
  但 **严格 P2P 模式下所有直连失败就提示用户，不引入公共中继**。
- 必须接受 CGNAT、运营商入站限制、企业防火墙、对称 NAT 导致失败。
- 地址广播必须先告知 IP 暴露风险；地址绑定
  `device_id / room_id / node_id / IPv6 / port / candidate_generation / expiry`。
- IPv6 地址状态：`ACTIVE / DEPRECATED / DRAINING / REVOKED / RETIRED`；
  建议初始地址租约 10 分钟，节点心跳 120 秒。
- 地址轮换时旧地址停止接新连接，**已建立连接继续运行**，不能无条件断所有会话。

### 3.5 设备准入 / 请求签名

- TPM 注册成功后签发**短期设备凭证**（建议 10–15 分钟），绑定：
  `device_id / key_id / public_key / scope / issued_at / expires_at / audience / token_id`。
- 客户端内置服务器签名公钥。
- 每个请求签名覆盖：
  `protocol_version / HTTP_method / canonical_path / body_sha256 / timestamp / request_id / token_id`。
- 请求头建议：
  `Authorization: DeviceToken <token>`、
  `X-Device-Key-Id`、`X-Request-Timestamp`、`X-Request-Id`、`X-Request-Signature`。
- 服务端验证顺序：格式/长度 → 边缘限流 → 凭证签名 → 请求签名 → 时间戳/重放 →
  撤销状态 → 房间/节点 ACL → 业务/DB。
- 首次 TPM 证明应独立排队，不能拖垮主业务线程。
- 合法签名不等于合法行为：仍需行为检测、请求配额、审计。

### 3.6 紧急熔断 / DDoS

- 多层入口：云清洗 → CDN/WAF/反代 → 源站防火墙 → ASP.NET Core 限流 →
  Device Admission → Redis 撤销缓存。
- **HTTP CDN 不一定支持任意 TCP**；大厅和 Minecraft Gateway 必须分别设计网络防护。
- 独立 UDP Control Gateway：客户端启动时注册探测 + 低频保活；服务器记录实际公网地址，
  攻击时向已登记地址逐播签名指令（不是互联网广播）。
- 紧急指令用 Ed25519，客户端内置公钥；指令含
  `protocol / command_id / epoch / action / severity / issued_at / expires_at /
  reason_code / scope / signature`。
- 动作：`REDUCE_TRAFFIC`、`PAUSE_CONTROL_PLANE`、`FULL_P2P_PAUSE`、`RESUME_ALL`、`IP_QUARANTINE`。
- 客户端必须验证签名/有效期/单调递增 `epoch`，持久化最高 epoch，防重放旧指令。
- 熔断期间只有 UDP 紧急保活始终允许；普通 API/房间/新节点/新信令按 Level 1/2/3 限流或禁止；
  已建立 P2P 连接 Level 1/2 保持，Level 3 断开。
- IP 临时隔离必须注意：
  - 未 ACK 熔断指令的客户端不能直接按违规处理；
  - ACK 不能证明客户端没有继续发其他请求；
  - 只统计确认的违规业务流量；
  - 共享 NAT 的 IPv4 封禁会误伤同出口用户；
  - UDP 伪造源 IP 不能作为封禁依据；
  - IPv6 尽量精确到单个地址，不轻易封 /64；
  - 不能为无限伪造 IP 创建无限封禁记录；
  - 封禁通知最好在网络层规则前发送；通知丢失时客户端重连仍能查询状态。

---

## 4. 现有 NPMC Launcher 能复用什么

仓库：`D:\Agents\NPMC-Launcher`（Windows，.NET 8，Avalonia，单文件 exe 发布）。

### 4.1 代码结构

| 路径 | 用途 |
|---|---|
| `src/NPMC.Launcher.Core` | 版本元数据、规则、路径、启动参数、实例/内存等纯逻辑 |
| `src/NPMC.Launcher.Infrastructure` | 下载、Mojang/Modrinth/CurseForge、账号、启动、整合包、皮肤 |
| `src/NPMC.Launcher.App` | Avalonia UI 与 ViewModel |
| `src/NPMC.Launcher.Cli` | `npmc` 命令行入口 |
| `tools/skin-agent` | 现有离线皮肤 Java Agent（stub / modern / legacy / bridge） |
| `tests/NPMC.Launcher.Core.Tests` | Core/Infrastructure 测试（393 个） |
| `tests/NPMC.Launcher.App.Tests` | App ViewModel 测试（7 个） |

当前基线：**400 通过、0 失败**；最近提交 `81d7652`。
P2P 相关代码为 **0**。

### 4.2 启动注入点

- `src/NPMC.Launcher.Infrastructure/Launch/GameLauncher.cs` 的 `Prepare(...)` 组装 JVM 参数；
  现有离线皮肤就是在这里注入 `-javaagent`（约 181 行）。
- `src/NPMC.Launcher.Infrastructure/Skins/SkinAgentProvider.cs` 是“按版本/账号准备 Agent jar、
  拼 `-javaagent:...=k=v` 参数、启动本地端口”的现成模式。
  P2P Agent 可以照这个模式做 `P2PAgentProvider`，但**必须是独立 agent 和独立参数**，
  不要修改皮肤 Agent 的转换器。
- `src/NPMC.Launcher.Core/Paths/LauncherPaths.cs`：便携目录布局（`data` / `minecraft` / 实例）。
- `src/NPMC.Launcher.Core/Instances/InstanceLayout.cs`：实例目录与共享目录。
- `src/NPMC.Launcher.App/ViewModels/MainWindowViewModel*.cs`：实例、启动、设置、账号；
  多实例运行状态已有 `RunningInstances`，可作为 Node 与 Minecraft 实例绑定的基础。
- `src/NPMC.Launcher.Infrastructure/Accounts/TokenDirectoryAcl.cs`：Windows ACL 收紧的现成写法
  （P2P 的 Named Pipe / 设备状态目录可参考；注意它用 `icacls`，且文件必须单独用 `:F` 授权）。
- `tools/publish.ps1`：发布 + 自签名 + SHA256；P2P 服务端是独立部署，不应塞进启动器 exe。

### 4.3 Java Agent 参考

- `tools/skin-agent/src/nmpc/skin/AgentStub.java`：薄入口，不含 ASM；
- `SkinAgent.java`：agentmain/premain，参数解析；
- `SkinTransformer.java`：类转换；
- `SkinBridgeModern.java` / `SkinBridgeLegacy.java`：桥接，避免 Agent jar 自带 ASM 进入系统类路径；
- `SkinModuleSupport.java`：模块化加载器 read edge；
- `tools/skin-agent/build.ps1`：编译/打包/manifest 流程。

P2P Agent 的新代码应该是 **独立 jar**（例如 `nmpc-p2p-agent.jar` + 可选 impl/bridge），
职责只做：
1. 连接 Named Pipe；
2. 在登录阶段拿到 nonce/挑战；
3. 请求启动器用 TPM 业务密钥签名；
4. 把证明回给 Gateway；
5. 不修改皮肤、不修改正版会话。
加载顺序/类转换冲突必须与皮肤 Agent 实测共存。

### 4.4 环境事实

- 本机已有 Java 8/17/21/25 运行时；皮肤 Agent 已实测到 26.4 快照（authlib 12）。
- 最近启动器 exe 在 `E:\aaaa\NPMC.Launcher.App.exe`，便携数据在 `E:\aaaa\data`，
  游戏在 `E:\aaaa\minecraft`。
- TPM 需要单独确认：`Get-Tpm`、`tpm.msc`、设备安全性页面；没有 TPM 2.0 的机器按设计禁用 P2P。
- Gateway 绑定公网端口可能需要 Windows 防火墙规则；高端口不一定需要管理员，
  但防火墙放行通常要管理员。IPv6 还取决于运营商是否分配全球单播地址。
- 当前发布签名是自签名证书（本机受信任根）；正式公开分发仍需 OV/EV 证书。

---

## 5. 推荐的新代码边界

不要把 P2P 全部塞进 `NPMC.Launcher.App`。建议新增项目（monorepo）：

```text
src/
  NPMC.P2P.Contracts/        纯 DTO / 协议版本 / 序列化，无依赖
  NPMC.P2P.DeviceIdentity/   TPM/CNG、设备密钥、证明、凭证、Named Pipe 服务端
  NPMC.P2P.Gateway/          TcpListener、Minecraft 登录、转发、连接监控
  NPMC.P2P.Server/           ASP.NET Core、SignalR、PostgreSQL/Redis（可独立部署）
tools/
  p2p-agent/                 Java Agent（独立于 skin-agent）
tests/
  NPMC.P2P.Tests/            协议/签名/ACL/状态机单元测试
```

约束：

- `Contracts` 不依赖启动器 Core/Infrastructure，方便服务端与 Gateway 复用。
- `Server` 不引用 Avalonia；`Gateway` 不引用 UI。
- 启动器只引用 `DeviceIdentity` + `Contracts`，通过接口启动 Gateway/Node。
- Java Agent 只用 JDK + 项目 bridge，不要自带 ASM 进系统类路径（参考皮肤 Agent 的教训）。
- 可先做一个 `NPMC.P2P.sln` 或在现有 solution 加文件夹；服务端最终部署 Linux。

---

## 6. 开发顺序（不要先做 UI）

设计明确要求按技术风险排序。建议按以下 gate 推进：

### Gate 0-A：Host Gateway 最小原型（最高优先级）

目标：**原版客户端通过 Gateway 的 TCP 入口完成完整登录并进入游戏**。

建议步骤：

1. 先用一个普通 **dedicated vanilla server** 验证纯 TCP 转发/登录/加密；
2. 再做 Integrated Server 端口发现（日志/进程/端口联合检测）与转发；
3. 抓包/记录 Login 阶段，明确 Gateway 需要解析/修改哪些包；
4. 先不加入自定义 TPM 认证，跑通原版登录；
5. 再加入 `p2p:auth`/Agent 挑战，验证 Gateway 能否在不破坏登录/加密的前提下完成设备认证；
6. 记录：正版 online-mode 会话认证、玩家 UUID、Integrated Server 身份衔接、失败模式。

验收：

- 原版客户端从公网 IPv6（或局域网）连 `[addr]:port` 能进入 Integrated Server；
- 登录、加密、资源包/配置阶段不崩；
- 明确“纯代理”和“需要 Agent 认证”两种模式的差异；
- 有一份协议时序文档和抓包记录。

### Gate 0-B：TPM 身份最小原型

目标：C# 启动器创建 TPM 业务密钥，完成本地检查、签名/验签，并跑通一个模拟远程证明。

验收：

- `Microsoft Platform Crypto Provider` 可用时创建不可导出 ECDSA P-256 密钥；
- 无 TPM / Provider 不可用时明确禁用 P2P，不降级 Software KSP；
- 本地签名/验签通过；
- 服务器 nonce 挑战 → 客户端签名 → 服务器验证；
- Named Pipe：当前用户 ACL、nonce、白名单、频率限制、会话绑定；
- Java Agent 只能请求“为指定挑战签名”，不能签任意消息。

### Gate 0-C：TCP P2P 直连

目标：两台真实设备、不同家庭网络/运营商，IPv6 直连并进入 Integrated Server。

验收：

- 公网 IPv6 直连成功；
- 记录 UPnP/NAT-PMP/PCP/STUN/ICE-TCP 的实际可用性；
- CGNAT/对称 NAT 失败时有明确用户提示，不偷偷走中继；
- 地址轮换不切断已有连接；
- 有连接状态机和失败原因分类。

### 之后才做

- D1–D7 设备身份/注册/撤销/档案绑定；
- P1–P10 房间/成员/多节点/ACL/地址/连接监控；
- S1–S8 准入签名/限流/DDoS/UDP 控制/熔断/IP 隔离；
- T 容量测试（500 设备、300 节点、600 SignalR、100 QPS 等）。

---

## 7. 数据库与 API 基线（供脚手架参考）

核心表（第一阶段）：

`devices`、`device_keys`、`device_attestations`、`device_sessions`、`device_bans`、
`rooms`、`room_members`、`game_nodes`、`node_acl`、`node_addresses`、
`node_minecraft_acl`、`audit_logs`、`ip_quarantine`、`emergency_commands`、
`request_violations`。

`device_bans` 与 `ip_quarantine` 必须分开：前者针对设备身份，后者针对临时公网地址。

HTTP API 统一 `/api/v1/`：

- 设备：`register / attest / challenge / authenticate / refresh / revoke`
- 房间：`create / join / leave / members / kick / close`
- 节点：`register / heartbeat / unregister / list / detail / access / acl / minecraft-acl / close`
- 信令：`offer / candidates / answer / complete / cancel`
- UDP：`heartbeat / ack / emergency`

SignalR 事件：`DeviceOnline/Offline`、`RoomMemberJoined/Left/Kicked`、
`NodeOnline/Offline/AddressChanged/AccessChanged/AclChanged/StatusChanged`、
`SignalOffer/Answer/Candidate/Complete/Cancel`。

**所有节点事件必须按 Room + Node ACL 过滤**，Private 节点不能广播给全房间。

---

## 8. 安全不变量（写代码前先当 checklist）

- [ ] 设备身份、Minecraft 身份、节点授权三层不混。
- [ ] TPM 私钥永不离开 TPM；不导出、不软件降级。
- [ ] 服务器独立验证 TPM 证明，不信任客户端自报。
- [ ] EK 不作为业务签名密钥；EK 索引用 HMAC，不广泛暴露。
- [ ] 设备凭证短期（10–15 分钟）+ 每请求签名 + 时间戳/重放保护。
- [ ] Named Pipe 当前用户 ACL + nonce + 白名单 + 频率限制 + 会话绑定。
- [ ] node ACL 服务端执行，不靠客户端隐藏。
- [ ] 无公共中继；控制面故障不无故切断已有 P2P 连接。
- [ ] IP 暴露有告知；退出房间不能“收回”已披露地址。
- [ ] 紧急指令 Ed25519 + 有效期 + 单调 epoch；客户端持久化最高 epoch。
- [ ] IP 隔离只针对确认违规；考虑 NAT 误伤、IPv6 /64、伪造源 IP。
- [ ] 所有设备/节点/ACL 操作有审计日志。
- [ ] 任何“失败”都 fail closed（禁用 P2P），不要 fail open。

注意：**TPM 只证明设备密钥的硬件来源与属性，不证明 Windows、启动器或 Minecraft
进程没有被篡改。** 不要把它宣传成“整机可信”。

---

## 9. 尚未决定、开工前必须问用户的问题

| 问题 | 为什么必须先定 | 建议 |
|---|---|---|
| 目标 Minecraft 版本/加载器？ | Integrated Server 端口、Login 包、online-mode 行为都随版本变化 | 先锁定一个版本（1.20.1 或 26.3）做原型，不要一开始追全版本 |
| 是否接受“原版游戏文件 + 启动器注入 P2P Agent”？ | 不注入 Agent，原版客户端无法响应自定义 TPM 登录请求 | 推荐接受；这是设计文档的首选 |
| 目标操作系统？ | TPM/CNG 是 Windows 方案；macOS/Linux 无同等 TPM 路径 | 先 Windows-only，明确不支持平台 |
| 正版/离线账号都要支持 P2P 吗？ | online-mode 会话认证与设备认证是两条链路 | 先支持正版 + 离线分离，不要让 TPM 替代正版 |
| TPM 远程证明怎么做？ | EK 证书链/AK 证明/服务端验证是 P0 | 先做本地 + mock 证明，再接真实 EK/AK |
| 服务器部署区域/系统/域名/IPv6？ | 影响 TLS、UDP 控制、IPv6 直连 | 单区域 Linux + IPv6 优先 |
| 是否允许任何公共中继？ | 设计明确“不使用公共中继” | 不允许；失败就提示 |
| 设备丢失/重装系统怎么恢复？ | 无账号、设备即身份，TPM 换机就是新设备 | 先按新设备处理，不做账号找回 |
| 第一阶段只做原型还是可运营？ | 影响是否先写 UI/后台 | 先三个原型，再房间/节点 |
| 是否要兼容反作弊/客户端模组环境？ | Agent 可能被检测 | P2P 模式才加载，公开实现，拒绝就停 |

---

## 10. 下一会话的第一步（建议）

1. 读本文件 + `docs/p2p/DESIGN-OUTLINE.md` 的 §1、§2、§5、§11、§12。
2. 向用户确认 §9 的前四个问题（版本、Agent 注入、OS、正版/离线）。
3. 写 3 个 ADR（放 `docs/p2p/adr/`）：
   - ADR-001 目标 Minecraft 版本与 Agent 注入策略；
   - ADR-002 控制面/数据面与“无公共中继”；
   - ADR-003 TPM 证明与设备身份模型。
4. 建立 `NPMC.P2P.Contracts` + `NPMC.P2P.DeviceIdentity` 空项目与测试项目；
   先写纯逻辑：设备状态机、挑战/nonce、签名 payload 规范化、ACL 判定。
5. 做 Gate 0-B（TPM）和 Gate 0-A（Gateway）的 spike；**Gate 0-A 先用 dedicated server**
   验证 TCP 转发，再处理 Integrated Server 端口发现。
6. 只有 P0 原型跑通后，才动 `MainWindowViewModel` 的房间/节点 UI。

---

## 11. 仓库命令与环境

```powershell
# 构建 / 测试
cd D:\Agents\NPMC-Launcher
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
$env:NUGET_PACKAGES="$PWD\.nuget"
dotnet build NPMC.Launcher.sln -c Release --nologo -maxcpucount:1 -nodeReuse:false
dotnet test tests\NPMC.Launcher.Core.Tests --nologo
dotnet test tests\NPMC.Launcher.App.Tests --nologo

# 发布（自签名 + SHA256；正式证书用 -PfxPath）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish.ps1
```

- 测试基线（2026-10-31）：Core 393 + App 7 = **400 通过**。
- 当前启动器 exe：`E:\aaaa\NPMC.Launcher.App.exe`；数据 `E:\aaaa\data`，游戏 `E:\aaaa\minecraft`。
- 发布签名是自签名 `CN=NPMC Launcher (self-signed)`；公开分发前换正式证书。
- 现有 Java Agent 构建：`tools/skin-agent/build.ps1`；P2P Agent 建议独立目录/独立 jar。

---

## 12. 术语

| 术语 | 含义 |
|---|---|
| Device | 一台装有启动器的设备；身份由 TPM 业务密钥标识 |
| EK / AK / Business Key | TPM 身份根 / 证明密钥 / 日常签名密钥 |
| Lobby Room | 逻辑大厅：成员、权限、节点发现 |
| Game Node | 一个实际 Minecraft 实例及其 Gateway/端口/ACL |
| Host Gateway | 公网 TCP 入口，转发到本机 Integrated Server |
| Control Plane | 身份、房间、节点发现、ACL、信令、心跳 |
| Data Plane | Minecraft TCP 游戏流量，P2P 直连，不经过大厅 |
| Admission | 设备凭证 + 每请求签名的准入协议 |
| Emergency Epoch | 紧急指令单调递增版本，防重放 |

---

## 13. 最后提醒

这份设计的价值在于**边界清晰**，不是功能清单。真正难的是：

1. **Minecraft 原版登录协议**：Gateway 如何在 TCP 转发中完成设备认证，同时不破坏
   Mojang 会话认证、加密和 Integrated Server 的 UUID 衔接。
2. **TPM 远程证明**：Windows CNG 能做什么、EK/AK 链怎么验、没有 TPM 的机器如何处理。
3. **真实网络**：IPv6 可用性、CGNAT、端口映射、地址轮换。

在这三点跑通之前，房间列表、节点管理、漂亮的 UI、数据库表都不是优先级。
