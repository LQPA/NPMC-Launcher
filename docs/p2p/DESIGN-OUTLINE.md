# Minecraft Java 多节点 P2P 联机模块：完整设计大纲

根据目前确定的需求，我建议将整个模块正式定义为一个独立的 Multi-Node P2P Multiplayer System（多节点 P2P 联机系统）。

它不是传统的「一个房主开服，其他人加入」模式，而是一个由大厅控制多个独立游戏节点、节点之间直接建立 TCP 连接的分布式联机系统。

设计核心可以概括为：

* 无传统账号系统，以设备作为用户身份。

* 强制可信 TPM，未通过 TPM 验证的设备不能使用 P2P。

* 支持多个成员同时创建多个 Minecraft 游戏节点。

* Minecraft 游戏数据通过 TCP P2P 直接传输。

* 不使用公共中继，不将游戏流量转发到大厅服务器。

* 加入者使用 Minecraft 原版多人游戏入口。

* 通过 Host Gateway 实现原版游戏实例与公网连接之间的桥接。

* 通过设备签名、短期凭证和 ACL 实现访问控制。

* 通过 DDoS 防护、UDP 紧急熔断和临时 IP 隔离保护控制服务器。

下面按照正式项目技术文档的方式整理。

# 1. 总体系统架构

## 1.1 系统组成

整个 P2P 模块可以分成六个主要子系统。

## P2P Multiplayer Architecture

A. Launcher Client（启动器客户端）

设备管理、TPM 密钥管理、游戏档案、节点管理、紧急熔断

B. Device Identity System（设备身份系统）

TPM 证明、设备注册、数字签名、凭证管理、设备撤销

C. Lobby Control Plane（大厅控制平面）

房间、成员、节点发现、权限、地址交换、实时信令

D. Host Gateway（主机网关）

Minecraft TCP 监听、登录协议处理、身份验证、透明转发

E. P2P Network（P2P 网络）

IPv6、TCP 直连、端口映射、地址轮换、连接管理

F. Emergency Defense（紧急防御系统）

DDoS 检测、UDP 紧急指令、客户端熔断、临时 IP 隔离

PostgreSQL + Redis

持久化数据、在线状态、节点租约、短期授权和撤销缓存

这些子系统虽然在逻辑上分开，但第一阶段可以部署在同一台云服务器上。需要保持代码模块和权限边界独立，方便未来拆分。

## 1.2 两条完全不同的数据通道

这是整个架构最重要的原则。

控制平面（Control Plane）

启动器 A、B、C、D

大厅服务器

身份认证、房间管理、节点发现、权限、地址和信令

采用 HTTPS / WSS / UDP 控制通道。

游戏数据平面（Data Plane）

成员 A

Minecraft + Gateway

成员 B

Minecraft 客户端

TCP P2P 直接连接

游戏数据不经过大厅服务器，不使用公共中继。

大厅服务器负责协调，不负责传输 Minecraft 游戏数据。

因此，大厅服务器出现短暂故障时，已经建立的 P2P 游戏连接原则上可以继续运行。

# 2. 设备身份与 TPM 认证系统

已确定：P2P 强制可信 TPM

## 2.1 用户身份模型

不建立传统账号系统。

| 项目        | 设计                 |
| --------- | ------------------ |
| 用户身份      | 设备身份               |
| 身份标识      | `device_id`        |
| 设备密钥      | TPM 内的 ECDSA P-256 |
| 设备公钥      | 服务器保存              |
| 设备私钥      | 永远留在本地 TPM         |
| 用户名、邮箱、密码 | 不需要                |
| 更换设备      | 创建新的设备身份           |

一台设备对应一个独立用户身份。

房间成员直接关联 `device_id`，不使用 `account_id`。

## 2.2 TPM 本地验证

C# 启动器通过 Windows CNG 管理 TPM 密钥。

推荐使用：

* Microsoft Platform Crypto Provider。

* TPM 2.0。

* ECDSA P-256。

* SHA-256。

* 不可导出私钥。

本地检查流程：

1. 检查 TPM 2.0 是否存在。

2. 检查 TPM 是否处于可用状态。

3. 检查 Platform Crypto Provider 是否可用。

4. 创建 TPM 硬件签名密钥。

5. 检查私钥不可导出属性。

6. 执行签名和验签测试。

任何一步失败，均禁用 P2P 功能，不自动降级为 Software KSP。

## 2.3 TPM 远程证明

本地检测只是第一道筛选，服务器才是最终判断者。

推荐注册流程：

```
C# Launcher
    |
    | 本地检测 TPM
    v
生成 TPM 业务密钥对
    |
    | 提交公钥 + TPM 证明材料
    v
服务器生成随机 nonce
    |
    v
客户端提交证明
    |
    v
服务器验证 EK / AK / 业务密钥绑定
    |
    v
验证密钥属性和证明链
    |
    v
服务器确认 device_id / key_id
    |
    v
签发短期设备凭证
```

需要区分三把密钥：

| 密钥     | 用途      |
| ------ | ------- |
| EK     | TPM 身份根 |
| AK     | TPM 证明  |
| 业务设备密钥 | 设备日常签名  |

不能直接把 EK 当作普通业务签名密钥，也不能仅凭客户端自报 `key_provider = TPM` 就认定可信。

## 2.4 设备状态

建议：

```
NOT_CHECKED
LOCAL_CHECKING
LOCAL_FAILED
LOCAL_PASSED
ATTESTING
TPM_VERIFIED
ATTESTATION_FAILED
REVOKED
```

只有服务器认可的 `TPM_VERIFIED` 状态允许使用 P2P。

## 2.5 设备封禁

建议分为：

* 设备公钥封禁。

* TPM 身份封禁。

* 临时设备会话撤销。

TPM 身份封禁可以使用服务器专属 HMAC 计算身份索引：

```
tpm_identity_hash =
    HMAC-SHA256(server_secret, canonical_EK_public_key)
```

不要将原始 EK 公钥作为普通业务标识广泛暴露。

更换主板、TPM 或重新注册设备后，应该按新的设备身份处理；是否对疑似规避封禁的关联设备进行复核，由服务器风险策略决定。

# 3. Java Agent 与 C# 启动器协作

## 3.1 模块分工

| 模块                       | 负责内容                        |
| ------------------------ | --------------------------- |
| C# Launcher              | TPM 密钥、凭证、设备状态、档案绑定         |
| P2P Authentication Agent | Minecraft 登录阶段身份认证          |
| 离线皮肤 Agent               | 离线档案皮肤加载                    |
| Host Gateway             | Minecraft 协议处理、身份验证和 TCP 转发 |

推荐通信结构：

```
Minecraft Java
     |
P2P Authentication Agent
     |
Windows Named Pipe
     |
C# Launcher
     |
Windows CNG
     |
TPM
```

Java Agent 不持有 TPM 私钥，只能通过受控 Named Pipe 请求启动器签名。

## 3.2 Named Pipe 安全要求

必须具备：

* 当前 Windows 用户 ACL。

* 会话标识。

* 请求完整性校验。

* 请求类型白名单。

* 随机 nonce。

* 签名频率限制。

* 目标房间、节点、档案和游戏会话绑定。

不能让本机任意进程通过 Named Pipe 请求签署任意消息。

## 3.3 与离线皮肤 Agent 共存

目前的离线皮肤方案采用 Authlib 档案属性拦截。

两者应保持功能隔离：

* 皮肤 Agent 只处理 `textures`。

* P2P Agent 只处理 P2P 身份认证。

* P2P 身份 UUID 从启动器可信档案绑定中取得。

* 不从被皮肤 Agent 修改过的 GameProfile 反向推断身份。

* 不修改正版登录结果。

两个 Agent 可以通过多个 `-javaagent` 加载，但要测试加载顺序、类转换器和 Authlib 冲突。

## 3.4 反作弊兼容性

Java Agent 可能被某些客户端反作弊系统检测到。

建议：

* 只在 P2P 模式加载。

* 不隐藏 Agent。

* 不伪造 JVM 参数。

* 尽量不修改 Minecraft 核心类。

* 不加载不必要的 Native Library。

* 对外公开 Agent 的用途、签名和哈希。

* 如果反作弊明确拒绝，应停止加载并报告兼容问题。

# 4. Lobby Room 与 Game Node 管理

这是区别于传统 P2P 房间设计的关键模块。

## 4.1 两个核心概念

| 名称         | 定义                     |
| ---------- | ---------------------- |
| Lobby Room | 成员组织、权限和节点发现的逻辑大厅      |
| Game Node  | 一个实际运行的 Minecraft 游戏实例 |

一个 Lobby Room 可以同时存在多个 Game Node。

例如：

Lobby Room：Room A

成员 A、成员 B、成员 C

Game Node 01

在线

创建者：成员 A

Minecraft 实例 A1

Game Node 02

在线

创建者：成员 B

Minecraft 实例 B1

Game Node 03

在线

创建者：成员 A

Minecraft 实例 A2

Game Node 04

离线

创建者：成员 C

Minecraft 实例 C1

每个 Game Node 都拥有独立的：

* `node_id`

* Minecraft 实例绑定

* TCP 监听端口

* Gateway

* 地址候选集

* 节点访问权限

* ACL

* 节点生命周期

成员 A 可以同时创建多个节点，成员 B 也可以独立创建节点。

Room Owner 不自动拥有所有节点的控制权。

## 4.2 节点权限

建议保留三种模式：

| 模式      | 含义                  |
| ------- | ------------------- |
| Room    | 房间有效成员可查看和加入        |
| Private | 只有节点 ACL 授权成员可查看和加入 |
| Closed  | 不接受新连接，不提供新地址       |

Private 节点应当在服务端执行 ACL 检查，不能只依赖客户端隐藏节点。

建议增加两种权限：

* `node_acl`：决定哪些设备可以访问节点。

* `node_minecraft_acl`：决定 Minecraft 游戏层面允许哪些玩家 UUID 进入。

前者控制 P2P 接入，后者控制游戏内玩家访问。两者不能混为一谈。

## 4.3 节点生命周期

```
CREATED
   |
REGISTERING
   |
ONLINE
   |
DEGRADED
   |
OFFLINE
   |
CLOSED
```

建议节点心跳周期 120 秒，租约 5～10 分钟。

服务器超过租约期限没有收到有效心跳时，节点转为离线。

# 5. Minecraft 原版直连与 Host Gateway

最高优先级技术验证项目

## 5.1 基本架构

用户选择的是原版直连路线：

```
公网 IPv6:41000
       |
       v
Host Gateway
       |
       v
127.0.0.1:54321
       |
       v
Minecraft Integrated Server
```

主机可以使用普通 Minecraft Java 客户端，不需要安装 Fabric、Forge 或项目 Mod。

Host Gateway 是本机程序，负责监听公网 TCP 端口并将连接转发到 Minecraft Integrated Server。

加入者在原版 Minecraft「多人游戏」中输入：

```
[2001:db8:1234::abcd]:41000
```

即可发起连接。

## 5.2 Minecraft 登录阶段身份认证

这是目前架构中最需要做原型验证的部分。

如果要让 Gateway 验证加入者的 TPM 设备身份，可以设计：

1. Gateway 接收 Minecraft Login 握手。

2. Gateway 生成随机 nonce。

3. Gateway 通过自定义 Login Plugin Request 发起 `p2p:auth` 认证。

4. P2P Agent 通过 Named Pipe 请求 C# 启动器签名。

5. Agent 返回签名证明。

6. Gateway 验证设备凭证、签名、节点授权。

7. Gateway 决定是否允许连接。

但这里存在一个不可忽略的约束：

完全不加载 Java Agent 的原版 Minecraft 客户端，不会主动响应你定义的自定义登录认证请求。

因此，目前有一个必须明确的产品取舍：

| 方案                                 | 结果                           |
| ---------------------------------- | ---------------------------- |
| 不加载任何 Agent，完全原版 JVM               | 无法直接完成自定义 TPM 登录认证           |
| 原版 Minecraft 文件 + 启动器注入 Java Agent | 不需要 Mod，但 JVM 运行环境带有项目 Agent |
| 完全不修改客户端，使用预先签发的外部授权               | 可以尝试，但需要重新设计如何将设备身份绑定到具体游戏连接 |

我建议优先研究第二种：Minecraft 游戏文件保持原版，由启动器在 P2P 模式注入 Agent。

另外，Gateway 认证成功后如何把身份安全地传递给 Integrated Server，以及如何兼容正版在线模式认证，是必须实际验证的协议问题。不能简单把 Gateway 当作普通 TCP 转发器。

建议第一阶段先做一个最小可运行原型，验证目标 Minecraft 版本的完整 Login Protocol，再决定是否进入大规模开发。

## 5.3 正版认证与 P2P 身份必须分开

TPM 身份认证不能代替 Mojang/Microsoft 正版会话认证。

应该分别处理：

* 设备身份：谁在使用启动器。

* Minecraft 身份：哪个游戏档案正在登录。

* 节点授权：这个设备是否有权限访问目标节点。

不能仅凭 `device_id` 或 Offline UUID 就认定 Minecraft 玩家身份可信。

# 6. P2P 网络与 TCP 连接

## 6.1 网络技术路线

Minecraft Java 使用 TCP，因此不应该把 UDP P2P 当作游戏数据传输方案。

| 技术            | 用途               |
| ------------- | ---------------- |
| IPv6 全球单播     | 优先直连             |
| IPv4          | 通过 NAT 端口映射等方式尝试 |
| UPnP IGD      | 自动申请端口映射         |
| NAT-PMP       | 自动申请端口映射         |
| PCP           | 自动申请端口映射         |
| STUN over TCP | 发现映射地址，不保证穿透     |
| ICE-TCP       | TCP 候选地址和连接协调    |

## 6.2 连接优先级

建议：

1. 局域网地址。

2. 公网 IPv6 直连。

3. 已验证的 IPv4 端口映射。

4. 其他经过验证的 TCP 候选地址。

严格 P2P 模式下，所有直连方式失败后提示用户，不使用公共中继。

需要向用户说明：CGNAT、运营商入站限制、企业防火墙和对称 NAT 都可能使连接失败。

## 6.3 地址广播

房间成员可以查看节点公网 IPv6 和 TCP 端口。

但需要先展示 IP 暴露告知：

* 房间授权成员可以获得节点公网 IP 和端口。

* 实际连接双方可以看到对方公网 IP。

* 对方可能保存、转发已获得的地址。

* 退出房间无法远程收回已披露地址。

节点地址需要绑定：

```
device_id
room_id
node_id
IPv6
port
candidate_generation
expiry
```

不要仅根据 IP 地址判断设备身份。

## 6.4 IPv6 地址轮换

建议地址状态：

```
ACTIVE
DEPRECATED
DRAINING
REVOKED
RETIRED
```

初始地址租约可以设为 10 分钟，节点心跳 120 秒。

地址更新时：

1. 发现新 IPv6。

2. 验证 Gateway 监听和公网可达性。

3. 生成新的 `candidate_generation`。

4. 向授权成员广播新地址。

5. 停止旧地址接受新连接。

6. 已建立连接继续运行，直到正常结束。

7. 必要时执行紧急撤销。

地址变化不能导致所有现有 TCP 会话被无条件断开。

# 7. Device Admission Protocol：统一设备请求准入

这一层负责保护大厅服务器免受无效请求和伪造身份攻击。

## 7.1 短期设备凭证

TPM 注册成功后，服务器签发短期设备凭证。

建议有效期先设为 10～15 分钟，后续根据实测调整。

凭证绑定：

```
device_id
key_id
public_key
scope
issued_at
expires_at
audience
token_id
```

服务器使用自己的签名密钥签发，客户端内置验证公钥。

设备持有 TPM 私钥，通过 Proof of Possession 证明自己确实持有对应私钥。

## 7.2 每个请求签名

建议请求包含：

```
Authorization: DeviceToken <token>

X-Device-Key-Id: <key_id>
X-Request-Timestamp: <timestamp>
X-Request-Id: <unique_id>
X-Request-Signature: <signature>
```

签名内容覆盖：

```
protocol_version
HTTP_method
canonical_path
body_sha256
timestamp
request_id
token_id
```

这样可以防止：

* 请求参数被篡改。

* 凭证被盗后直接复用。

* 请求重放。

* 设备冒用。

* 未授权接口调用。

但需要记住：合法签名不等于合法行为。已被控制的真实设备仍然可能发送恶意请求，因此还需要行为检测和请求配额。

## 7.3 服务器验证顺序

建议尽可能廉价的检查放在前面：

```
请求进入
   |
请求长度和格式检查
   |
边缘限流
   |
服务器凭证签名验证
   |
设备请求签名验证
   |
时间戳与重放检查
   |
设备撤销状态检查
   |
房间和节点 ACL 检查
   |
数据库 / Redis / 业务操作
```

首次 TPM 证明应独立排队处理，不能让大量注册请求直接拖垮主业务线程。

# 8. DDoS 防护与 UDP 紧急熔断系统

已纳入整体架构

这是你最近重点讨论的模块。

## 8.1 服务器入口防护

建议采用多层防护：

| 层级  | 技术                        | 作用                      |
| --- | ------------------------- | ----------------------- |
| 第一层 | 云厂商 / 运营商 DDoS 清洗         | 网络层洪泛、SYN Flood、带宽攻击    |
| 第二层 | CDN / WAF / 反向代理          | HTTP、HTTPS、WebSocket 防护 |
| 第三层 | 源站防火墙                     | 防止攻击者绕过代理               |
| 第四层 | ASP.NET Core 限流           | 限制业务请求                  |
| 第五层 | Device Admission Protocol | 验证设备和请求签名               |
| 第六层 | Redis 撤销缓存                | 快速识别失效凭证和封禁设备           |

特别注意：HTTP CDN 不一定支持任意 TCP 服务。大厅服务器和 Minecraft Gateway 必须分别设计网络防护。

## 8.2 UDP 紧急控制通道

建议独立部署一个 UDP Control Gateway。

客户端启动时主动向其发送注册探测包和低频保活包。

服务器记录实际公网来源地址和端口，然后在发生攻击时，通过 UDP 单播向在线客户端发送签名指令。

这不是互联网广播，而是服务器向已登记的客户端地址逐一发送。

## 8.3 紧急指令签名

推荐使用 Ed25519。

客户端内置服务器控制公钥，服务器控制私钥独立保护。

指令结构：

```
protocol
command_id
epoch
action
severity
issued_at
expires_at
reason_code
scope
signature
```

支持的主要指令：

| 指令                    | 行为                     |
| --------------------- | ---------------------- |
| `REDUCE_TRAFFIC`      | 减少心跳，停止非必要请求           |
| `PAUSE_CONTROL_PLANE` | 暂停大厅业务请求和新信令           |
| `FULL_P2P_PAUSE`      | 禁止新 P2P 操作，并根据策略断开现有连接 |
| `RESUME_ALL`          | 恢复正常状态                 |
| `IP_QUARANTINE`       | 通知客户端公网 IP 被临时隔离       |

客户端必须验证签名、有效期和单调递增的 `epoch`，并将最高版本持久化，避免重放旧指令。

## 8.4 熔断期间的通信规则

建议明确允许和禁止的行为：

| 请求类型       | Level 1 | Level 2 | Level 3 |
| ---------- | ------- | ------- | ------- |
| UDP 紧急保活   | 允许      | 允许      | 允许      |
| 普通 API     | 限流      | 禁止      | 禁止      |
| 房间管理       | 允许或限流   | 禁止      | 禁止      |
| 新节点注册      | 限流      | 禁止      | 禁止      |
| 新节点授权      | 限流      | 禁止      | 禁止      |
| 新信令        | 限流      | 禁止      | 禁止      |
| 已建立 P2P 连接 | 保持      | 保持      | 断开      |

我推荐普通 DDoS 时默认进入 Level 2，尽量不影响已经运行的 Minecraft 游戏。

## 8.5 紧急熔断后的违规 IP 临时封禁

这是你明确提出的策略：

客户端已经收到熔断指令，但仍持续发送被禁止的业务请求，就对其公网 IP 实施临时封禁。

建议流程：

```
服务器下发 PAUSE_CONTROL_PLANE
       |
客户端收到并返回签名 ACK
       |
进入隔离观察期
       |
服务器继续监控请求
       |
检测到被禁止的业务请求
       |
达到违规阈值
       |
生成临时 IP 封禁规则
       |
防火墙 / 上游 ACL 执行
       |
通知客户端 IP_QUARANTINE
       |
自动倒计时
       |
到期解除
```

建议初始封禁时长：

| 违规次数   | 封禁时间  |
| ------ | ----- |
| 第一次    | 1 分钟  |
| 第二次    | 5 分钟  |
| 第三次    | 15 分钟 |
| 第四次及以上 | 30 分钟 |

这些次数按滚动时间窗口计算，例如最近 24 小时。

必须注意：

* 未确认收到熔断指令的客户端不能直接按违规处理。

* ACK 不能绝对证明客户端已经停止发送其他请求。

* 只有确认的违规业务流量才应该计入违规次数。

* 共享 NAT 的 IPv4 封禁可能误伤同一公网出口的其他用户。

* UDP 伪造源 IP 不能作为可靠的封禁依据。

* IPv6 尽量精确到单个地址，不轻易封禁整个 `/64`。

* 不允许为无限量伪造 IP 创建无限量封禁记录。

* 封禁通知最好在网络层规则生效前发送；如果通知丢失，客户端下次连接时仍应能查询封禁状态。

## 8.6 客户端封禁显示

客户端应该显示：

* 当前网络限制状态。

* 封禁原因。

* 封禁范围。

* 剩余时间。

* 自动解除提示。

倒计时只负责展示，真正的解除时间由服务器防火墙或上游 ACL 控制。

# 9. 数据库与 API 设计

## 9.1 核心数据库表

建议第一阶段至少建立以下表：

| 表名                    | 用途                |
| --------------------- | ----------------- |
| `devices`             | 设备信息和设备状态         |
| `device_keys`         | TPM 业务公钥          |
| `device_attestations` | TPM 证明结果          |
| `device_sessions`     | 设备会话              |
| `device_bans`         | 设备和 TPM 封禁        |
| `rooms`               | 大厅房间              |
| `room_members`        | 房间成员              |
| `game_nodes`          | 游戏节点              |
| `node_acl`            | 节点访问权限            |
| `node_addresses`      | 节点 IPv4/IPv6 候选地址 |
| `node_minecraft_acl`  | Minecraft 玩家访问权限  |
| `audit_logs`          | 审计日志              |
| `ip_quarantine`       | 临时 IP 封禁记录        |
| `emergency_commands`  | 紧急控制指令和版本         |
| `request_violations`  | 熔断期间违规记录          |

特别注意：

`device_bans` 与 `ip_quarantine` 必须分开。

前者针对设备身份，后者针对临时公网网络地址。

## 9.2 API 设计

建议统一使用：

```
/api/v1/
```

设备身份：

```
POST /devices/register
POST /devices/attest
POST /devices/challenge
POST /devices/authenticate
POST /devices/refresh
POST /devices/revoke
```

房间：

```
POST /rooms/create
POST /rooms/join
POST /rooms/leave
GET  /rooms/members
POST /rooms/kick
POST /rooms/close
```

节点：

```
POST  /nodes/register
POST  /nodes/heartbeat
POST  /nodes/unregister
GET   /nodes/list
GET   /nodes/detail
PATCH /nodes/access
PUT   /nodes/acl
PUT   /nodes/{node_id}/minecraft-acl
POST  /nodes/close
```

信令：

```
POST /signal/offer
POST /signal/candidates
POST /signal/answer
POST /signal/complete
POST /signal/cancel
```

紧急控制：

```
UDP /control/heartbeat
UDP /control/ack
UDP /control/emergency
```

临时封禁状态查询可以作为独立的轻量接口，但应避免依赖已经被封禁的普通 API 入口。

## 9.3 SignalR 事件

建议包括：

```
DeviceOnline
DeviceOffline

RoomMemberJoined
RoomMemberLeft
RoomMemberKicked

NodeOnline
NodeOffline
NodeAddressChanged
NodeAccessChanged
NodeAclChanged
NodeStatusChanged

SignalOffer
SignalAnswer
SignalCandidate
SignalComplete
SignalCancel
```

所有节点事件都应该经过 Room 和 Node ACL 过滤，不能把 Private 节点的信息广播给所有房间成员。

# 10. 第一阶段服务器部署与容量规划

你目前确定的第一阶段目标：

注册设备

# 1,000

同时在线设备

# 500

同时在线 Game Node

# 300

部署区域

# 单一区域

## 10.1 推荐配置

| 项目  | 建议配置                         |
| --- | ---------------------------- |
| CPU | 8 vCPU                       |
| 内存  | 16 GB                        |
| 系统盘 | 80 GB SSD                    |
| 数据盘 | 100～200 GB SSD/NVMe          |
| 网络  | 100 Mbps 起步                  |
| 系统  | Ubuntu 24.04 LTS 或同等级受支持 LTS |
| 数据库 | PostgreSQL 16 或更新的受支持稳定版本    |
| 缓存  | Redis 1 GB 起                 |

成本敏感时可以从 4 vCPU / 8 GB 起步，但需要进行实际压力测试。

## 10.2 技术栈

| 模块         | 技术                                     |
| ---------- | -------------------------------------- |
| 启动器        | C# / .NET                              |
| 后端         | ASP.NET Core                           |
| 实时通信       | SignalR / WebSocket                    |
| 数据库        | PostgreSQL                             |
| 缓存         | Redis                                  |
| 反向代理       | Nginx                                  |
| Gateway    | C# Socket / TcpListener                |
| Java Agent | Java Instrumentation API               |
| IPC        | Windows Named Pipe                     |
| TPM        | Windows CNG / Platform Crypto Provider |
| 设备签名       | ECDSA P-256                            |
| 紧急控制签名     | Ed25519                                |
| TLS        | HTTPS / WSS                            |

## 10.3 资源预算

| 服务                     | CPU 预算 | 内存预算   |
| ---------------------- | ------ | ------ |
| Nginx                  | 0.25 核 | 256 MB |
| ASP.NET Core + SignalR | 2 核    | 2～3 GB |
| PostgreSQL             | 2 核    | 4 GB   |
| Redis                  | 0.5 核  | 1 GB   |
| TPM Worker             | 1 核    | 1～2 GB |
| 系统和其他服务                | 剩余     | 3～4 GB |

这些是初始容量规划，不是实测结果。

## 10.4 带宽规划

大厅服务器只传输：

* 设备认证。

* 房间状态。

* 节点地址。

* 节点授权。

* 信令。

* 心跳。

* 紧急控制消息。

Minecraft 游戏流量不经过大厅服务器。

因此建议初期预留至少 1 TB/月的出站流量作为规划起点，再根据实际用户行为监控调整。

# 11. 开发顺序与里程碑

这里我建议不要一开始就开发完整系统，而是把几个高风险技术点提前验证。

建议开发顺序

## 阶段 D：设备身份系统

| 编号 | 任务          | 验收目标                  |
| -- | ----------- | --------------------- |
| D1 | C# CNG 密钥管理 | 能创建 TPM 密钥并签名         |
| D2 | 本地 TPM 检测   | 无可信 TPM 禁止 P2P        |
| D3 | TPM 远程证明    | 服务器能验证证明              |
| D4 | 设备注册与认证     | 完成 challenge-response |
| D5 | Named Pipe  | Java Agent 安全请求签名     |
| D6 | 档案绑定        | 设备、档案、UUID 关联         |
| D7 | 撤销与封禁       | 设备身份可撤销               |

## 阶段 P：P2P 联机系统

| 编号  | 任务           | 验收目标                    |
| --- | ------------ | ----------------------- |
| P1  | 房间系统         | 设备可创建和加入大厅              |
| P2  | 成员权限         | Owner、成员、踢出机制           |
| P3  | 多实例检测        | 检测多个 Minecraft 实例       |
| P4  | Game Node 管理 | 多成员、多节点同时在线             |
| P5  | IPv6 与端口映射   | 获取并验证公网地址               |
| P6  | Host Gateway | 原版客户端 TCP 直连            |
| P7  | Gateway 登录认证 | TPM 设备身份验证              |
| P8  | ACL          | Room / Private / Closed |
| P9  | 地址轮换         | 新旧地址切换                  |
| P10 | 连接监控         | 节点连接状态管理                |

## 阶段 S：安全与抗攻击系统

| 编号 | 任务                        | 验收目标       |
| -- | ------------------------- | ---------- |
| S1 | Device Admission Protocol | 请求签名和凭证验证  |
| S2 | 请求限流                      | 防止业务接口洪泛   |
| S3 | DDoS 防护入口                 | 源站隔离       |
| S4 | UDP 控制通道                  | 客户端保活和指令接收 |
| S5 | 紧急熔断                      | 客户端暂停指定功能  |
| S6 | 熔断 ACK                    | 记录客户端确认情况  |
| S7 | IP Quarantine             | 自动临时 IP 隔离 |
| S8 | 自动恢复                      | 服务器签名恢复指令  |

## 阶段 T：容量和稳定性测试

建议测试：

* 500 台设备同时在线。

* 300 个 Game Node 同时在线。

* 600 条 SignalR 连接作为容量目标。

* 100 次/秒突发设备登录，持续 30 秒。

* 普通 API 100 QPS，持续 5 分钟。

* 100 个 TPM 注册任务排队。

* 大量节点地址轮换。

* UDP 紧急指令批量发送。

* 服务器进入熔断和恢复状态。

* 模拟 Redis 暂时不可用。

* 模拟大厅服务器重启时已有 P2P 连接继续运行。

# 12. 必须重点关注的技术风险

以下是我认为整个项目最值得提前处理的风险清单。

| 优先级 | 风险                                 | 处理建议                  |
| --- | ---------------------------------- | --------------------- |
| P0  | Gateway 与 Integrated Server 登录身份衔接 | 必须先做协议原型              |
| P0  | 完全原版客户端与 TPM 登录认证的冲突               | 明确是否允许注入 Java Agent   |
| P0  | IPv6 / NAT / TCP 穿透失败              | 提前测试真实运营商环境           |
| P0  | TPM 证明可信度                          | 使用服务器独立验证             |
| P1  | Java Agent 反作弊兼容                   | 公开实现、限制加载范围           |
| P1  | IP 地址泄露                            | 房间内授权披露、权限过滤          |
| P1  | IP 临时封禁误伤共享 NAT 用户                 | 结合设备身份和违规证据           |
| P1  | UDP 指令丢失                           | 重发、ACK、独立控制入口         |
| P1  | 服务器 DDoS                           | 上游清洗、WAF、源站隔离         |
| P1  | 设备被封禁但仍有有效凭证                       | 撤销版本和短期凭证             |
| P2  | IPv6 地址轮换影响现有连接                    | 连接排空机制                |
| P2  | Redis 临时状态丢失                       | PostgreSQL 保存权威数据     |
| P2  | 客户端重启绕过熔断                          | 本地持久化 emergency epoch |

## 12.1 特别需要强调的五个原则

第一，身份认证不能只依赖 IP。

IP 只是网络地址，TPM 设备密钥才是设备身份依据。

第二，TPM 认证不代表整个系统可信。

TPM 能证明设备密钥的硬件来源和属性，但不能自动证明 Windows、启动器或 Minecraft 进程没有被篡改。

第三，P2P 网络必须允许失败。

既然你明确不使用公共中继，就必须接受部分用户由于网络环境原因无法连接。不能为了提高成功率而偷偷引入公共流量转发。

第四，紧急控制系统不能依赖普通大厅服务器完全正常。

UDP 控制入口、签名密钥和网络防护应该有独立故障边界。

第五，Minecraft 登录协议是整个项目最大的未知数之一。

即使 TCP 连接成功，也不代表 Gateway 能正确处理登录加密、正版认证、玩家 UUID 和 Integrated Server 的身份衔接。

# 13. 当前设计中还需要最终确定的事项

目前整体架构已经比较完整，但以下事项仍然需要通过原型或正式决策确定，不能当作已经解决的技术问题。

| 事项                   | 当前状态   | 我的建议                    |
| -------------------- | ------ | ----------------------- |
| Gateway 登录协议兼容       | 待验证    | 最先实现                    |
| Minecraft Agent 加载策略 | 存在产品取舍 | 原版游戏文件 + P2P 模式注入 Agent |
| 多实例端口检测              | 待实现    | 日志、进程、端口联合检测            |
| TCP 穿透成功率            | 未实测    | IPv6 优先，真实运营商测试         |
| TPM 远程证明服务           | 待选型    | 独立验证模块                  |
| UDP 紧急控制的独立部署        | 待部署    | 独立网络入口和防护               |
| IP 临时封禁策略            | 已确定方向  | 先限速，再按证据隔离              |
| 离线恢复窗口               | 待定     | 短期凭证 + 有效期限制            |

# 14. 最终推荐的项目目录划分

为了后续 C#、Java 和服务器端可以独立开发，我建议采用下面的逻辑目录：

```
P2P-System/
│
├── Launcher/
│   ├── DeviceIdentity/
│   ├── TPMManager/
│   ├── CredentialManager/
│   ├── ProfileBinding/
│   ├── RoomManager/
│   ├── NodeManager/
│   ├── P2PConnection/
│   └── EmergencyController/
│
├── JavaAgent/
│   ├── AuthenticationAgent/
│   ├── IPCClient/
│   └── MinecraftProtocol/
│
├── HostGateway/
│   ├── TcpListener/
│   ├── MinecraftProtocol/
│   ├── LoginHandler/
│   ├── DeviceAuthentication/
│   ├── BackendForwarder/
│   └── ConnectionMonitor/
│
├── Backend/
│   ├── DeviceService/
│   ├── AttestationService/
│   ├── RoomService/
│   ├── NodeService/
│   ├── ACLService/
│   ├── SignalService/
│   ├── AdmissionService/
│   └── EmergencyService/
│
├── EmergencyControl/
│   ├── DDoSDetection/
│   ├── CommandSigner/
│   ├── UDPControlGateway/
│   ├── IPQuarantine/
│   └── RecoveryManager/
│
├── Database/
│   ├── PostgreSQL/
│   ├── Redis/
│   └── Migrations/
│
└── Documentation/
    ├── Architecture/
    ├── Protocol/
    ├── API/
    ├── Security/
    └── Deployment/
```

# 15. 我的最终开发建议

如果由我来安排这个项目，我不会先开发大厅 UI、房间列表或节点管理页面，而是按照技术风险来排序。

## 最先完成的三个原型

原型一：Host Gateway

最高优先级

让原版 Minecraft 客户端通过公网 IPv6 连接本机 Integrated Server，完整通过 Login、加密和游戏初始化流程。

原型二：TPM 身份认证

最高优先级

C# 创建 TPM 密钥、完成服务器远程证明、Java Agent 通过 Named Pipe 请求签名。

原型三：TCP P2P 网络

高优先级

两台真实设备、不同家庭网络、不同运营商环境下测试 IPv6 直连、端口映射和断线恢复。

这三个原型成功后，再正式开发房间系统、多节点管理、ACL、信令和紧急防御。

最重要的是：先证明 Minecraft 原版客户端能够通过你的 Gateway 正确完成完整登录。 这是整个设计的基础。如果这个环节需要改变认证方式，后续 Java Agent、设备身份绑定和节点授权的接口都会受到影响。

整体来看，你的系统已经具备了比较清晰的架构边界：大厅负责控制、TPM 负责设备身份、Gateway 负责 Minecraft 接入、P2P 负责游戏数据传输、紧急控制负责攻击期间的系统保护。接下来最有价值的工作是把这些模块逐一落实为可测试的协议和接口，而不是继续增加尚未验证的功能。
