[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— .NET SDK

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

在 NPS 达到 v1.0 稳定版之前，套件内所有仓库同步使用同一个预发布版本号。

---

## [1.0.0-alpha.11] —— 2026-05-31

### 新增

- **`NPS.NWP` — `SubscribeFrame` CR-0006**（Breaking 重写）：旧协议字段（`action`、`stream_id`、`heartbeat_interval`、`resume_from_seq`）替换为 CR-0006 正式规范——`subscription_id`（UUID v4）、`filter`（`JsonElement?`）、`heartbeat_interval_ms`、`max_events`、不透明 `cursor`（可无损续订）。**相对 alpha.8–10 为 wire 破坏性变更。**

- **`NPS.NWP.Anchor` — `AnchorNodeOptions.TrustAnchors`**：`IReadOnlyList<string>? TrustAnchors` 注入 `/.nwm` JSON 响应的 `trust_anchors` 字段（NWP v0.13 §4.1）。每条 URN 必须以 `urn:nps:` 开头。

- **`NPS.NIP` — `IdentFrame.OcspStaple`**：可空 `ocsp_staple` wire 字段——base64url DER OCSP 响应（NIP v0.9 §8.2）。接收方 MUST 验证装订签名；过期时抛出 `NIP-OCSP-STAPLE-EXPIRED`。

- **`NPS.NIP.X509` — `NpsX509Oids.IdNpsCapabilities`**：新增常量 `"1.3.6.1.4.1.65715.2.3"`，用于代理能力集 X.509 扩展（NIP v0.9 §8.2）。

- **`NPS.NOP` — AlignStream ack/NAK**：`AlignStreamFrame` 新增 `AckSeq`（`ulong?`）和 `NakSeq`（`ulong?`），支持 NOP v0.6 `window_size=16` 滑动窗口确认协议。

- **`NPS.NOP` — Webhook HMAC 签名**：`TaskFrame` 新增 `CallbackSecret`（`string?`）。回调必须携带 `X-NPS-Signature: sha256=…`（缺失时返回 `NOP-CALLBACK-HMAC-MISSING`）。

- **`NPS.NOP` — 跨集群委托**：`DelegateFrame` 新增 `TargetClusterAnchor`（`string?`），用于将任务路由到指定集群锚点（NOP v0.6）。

- **`NPS.NOP.Models` — `AggregateStrategy`**：新增常量 `WeightedFirstK = "weighted_first_k"` 和 `MergeAll = "merge_all"`（NOP v0.6）。

- **`NPS.NDP` — GraphFrame §5 拓扑快照格式**（Breaking 重写）：`NdpGraphNode` 携带 `Nid`、`ClusterAnchor`、`NodeRoles`；新增 `NdpGraphEdge`（`FromNid`、`ToNid`、`LatencyMs`、`Protocol`）；`GraphFrame` 携带 `GraphId`、`Nodes`、`Edges`、`Ttl`、`Metadata`。最多 256 节点 / 1024 边。

- **`NPS.Core` — NCP 原生模式传输**（NPS-1 §4.6 / RFC-0006）：`NcpNativeClient` 通过 TCP/QUIC 连接节点，发送 8 字节 preamble + `HelloFrame`（Tier-1 JSON），读取 `NcpHandshakeCapsFrame` 响应，返回已协商的 `NcpSession`。`NcpServer` 接受 TCP 连接、验证 preamble、反序列化 `HelloFrame`，向应用层暴露 `NcpServerConnection`。`NcpHandshakeException` 携带服务端 `ErrorFrame` 的协议错误码。`NcpHandshakeCapsFrame` 记录（与查询响应 `CapsFrame` 独立）携带 `NodeId`、`Caps`、`AnchorRef?`、`Payload?`。

### 跟随套件

本次跟随 NPS 套件 `v1.0.0-alpha.11`。NCP v0.7 / NWP v0.13 / NIP v0.9 / NDP v0.8 / NOP v0.6。

---

## [1.0.0-alpha.10] —— 2026-05-28

### 新增

- **`NPS.NOP` — Saga 补偿**：`CompensationPolicy` 类型；`DagNode` 新增 `CompensateAction` / `CompensateParamsMapping` 字段；`TaskState` 枚举新增 `Compensating` / `Compensated` 状态。

- **`NPS.NDP` — `SecurityProfile`**：新枚举——`LocalDev` / `OrgPrivate` / `PublicFederated`（NDP §6）。`LocalDev` 注册表强制 TTL 上限。

- **`NPS.NIP` — `IdentReputationPolicyHint`**：用于身份帧中声誉策略提示的结构化类型；`IdentMetadata` 不透明元数据封装类型。

### 跟随套件

本次跟随 NPS 套件 `v1.0.0-alpha.10`。

---

## [1.0.0-alpha.9] —— 2026-05-28

### 变更

- **`NPS.NIP` — `IdentFrame` 保证级别提取改进**：结构化保证级别提取对齐 NPS-RFC-0003 草案。

### 新增

- **`NPS.NWP` — `SubscribeFrame`（0x12）**：初始 `SubscribeFrame` 类型（pre-CR-0006 wire 格式，已在 alpha.11 替换）。

### 跟随套件

本次跟随 NPS 套件 `v1.0.0-alpha.8`。NPS-RFC-0005（声誉策略执行）和 NPS-RFC-0002（NPS X.509 OID 注册表）晋升为 Accepted。

---

## [1.0.0-alpha.8] —— 2026-05-28

### 新增

- **`NPS.NWP.Anchor` — `ReputationPolicyEvaluator`（RFC-0005）**：`IReputationPolicyEvaluator` / `DefaultReputationPolicyEvaluator`，含进程内封禁缓存和 per-NID 查询缓存。`AnchorNodeOptions.ReputationPolicy`。`GET /.nwm` 发布 NWM `reputation_policy` JSON 块。新增错误码：`NWP-REPUTATION-THROTTLED`、`NWP-REPUTATION-REJECTED`、`NWP-REPUTATION-BANNED`。响应头：`X-NWP-Reputation-Status`、`X-NWP-Ban-Expires`。`AnchorNodeMiddleware` 中 `cgn_limit` 预执行预算执行（token-budget.md §7.2 MUST）。

### 跟随套件

本次跟随 NPS 套件 `v1.0.0-alpha.8`。

---

## [1.0.0-alpha.7] —— 2026-05-17

### 新增

- **`NPS.NIP.Reputation` — `ReputationLogClient`（NPS-RFC-0004 Phase 2）**：完整的声誉日志 operator HTTP 客户端。`SubmitAsync`、`QueryAsync`、`GetSthAsync`、`GetProofAsync`、`GetGossipSthAsync`。静态方法 `VerifyInclusion` 在本地执行 RFC 9162 §2.1.3.2 Merkle audit-path 验证。新增 `SignedTreeHead`、`InclusionProof` wire 类型。`ReputationLogException` 携带 `NipErrorCode` + `NpsStatus`。15 条回归测试。

- **NPS-CR-0005 三级 RA 注册授权模型**（`EnrollmentTier`）：`AllowList`（仅 operator 预审 NID）、`BootstrapToken`（`nps-bootstrap-*` 一次性令牌）、`ApprovalQueue`（异步审批工作流）。`IBootstrapTokenStore` / `InMemoryBootstrapTokenStore` / `IPendingStore` 接口及内存实现。令牌管理端点（`POST /v1/ra/bootstrap-tokens`、`GET /v1/ra/pending`、`POST /v1/ra/pending/{id}/approve`、`POST /v1/ra/pending/{id}/reject`）。`NipCaOptions` 新增 `EnrollmentTier`、`BootstrapTokenMaxTtl`、`AllowedNids`。

- **遥测**（`NPS.NWP`、`NPS.NOP`）：`ActivitySource`（`LabAcacia.NPS.NWP`、`LabAcacia.NPS.NOP`）和 `Meter` 埋点。`NpsActivitySource.StartQuery`、`StartStream`、`StartOrchestration` span；`nps.nwp.requests.total`、`nps.nop.tasks.total` 计数器。无监听者时零开销。

### 跟随套件

本次跟随 NPS 套件 `v1.0.0-alpha.7`，依赖 `LabAcacia.NPS.NIP` ≥ `1.0.0-alpha.7`。

---

## [1.0.0-alpha.6] —— 2026-05-14

### 变更

- **`NPS.NWP.Anchor` — `topology.filter.node_kind` 已停用（Breaking）**：对 `node_roles` 的向后兼容别名已移除。仍发送 `node_kind` 的客户端将收到 HTTP 400 / `NPS-CLIENT-BAD-PARAM` / `NWP-TOPOLOGY-FILTER-UNSUPPORTED`。请改用 `topology.filter.node_roles`（自 alpha.4 / NPS-CR-0001 起支持）。

- **`NPS.NIP` / `NPS.NWP.Anchor` — IANA PEN 65715（Breaking，CR-0004）**：所有 NPS X.509 OID 常量现在锚定到已分配的弧 `1.3.6.1.4.1.65715`（替换临时弧 `1.3.6.1.4.1.99999`）。在临时弧下签发的证书必须吊销并重新签发，方可声称符合 alpha.6 规范。

- **版本升级至 `1.0.0-alpha.6`** —— 与 NPS 套件 alpha.6 版本同步。

---

## [1.0.0-alpha.5] —— 2026-05-01

.NET SDK 仍是 NPS 套件的**参考实现**。本次带来 alpha.5 规范（STH gossip 错误码、
`NPS-SERVER-UNSUPPORTED`、topology 能力门控、`cgn_est` 逐事件字段）、
`AssuranceLevel` 空字符串修复，以及完整的 NDP DNS TXT 回退解析。

### 新增

- **`NPS.NWP.Anchor` —— `NWP-RESERVED-TYPE-UNSUPPORTED` 强制执行**：`AnchorNodeMiddleware`
  对 `/anchor/query` 或 `/anchor/subscribe` 收到未知保留 `type` 值时，返回 HTTP 501 /
  `NPS-SERVER-UNSUPPORTED` / `NWP-RESERVED-TYPE-UNSUPPORTED`。原来错误地返回 404 /
  `NWP-ACTION-NOT-FOUND`。

- **`NPS.NWP.Anchor` —— `topology:read` 能力门控**：新增 `AnchorNodeOptions.RequireTopologyCapability`
  （默认 `false`）。启用后，`/query` 与 `/subscribe` 均检查请求头 `X-NWP-Capabilities` 是否包含
  `"topology:read"`（大小写不敏感、逗号分隔）；缺少时返回 HTTP 403 / `NPS-AUTH-FORBIDDEN` /
  `NWP-TOPOLOGY-UNAUTHORIZED`。新增常量 `NwpHttpHeaders.Capabilities = "X-NWP-Capabilities"`。

- **`NPS.NWP.Anchor` —— `TopologyEventEnvelope.cgn_est`**：新增可空字段 `cgn_est: uint?`，
  每次推送事件时填充 `Math.Max(1, UTF8.GetByteCount(payload) / 4)`（符合 spec/token-budget.md §7.2 SHOULD）。

- **`NPS.NDP` —— `ResolveViaDns` DNS TXT 回退解析**：新增
  `InMemoryNdpRegistry.ResolveViaDns(target, dnsLookup?)`，当内存注册表无匹配时回退查询
  `_nps-node.{host}` TXT 记录（NPS-4 §5）。新增 `IDnsTxtLookup` 接口 + `SystemDnsTxtLookup`
  （DnsClient v1.8.0 NuGet）；`InMemoryNdpRegistry.ParseNpsTxtRecord` 辅助方法；
  `INdpRegistry` 接口新增 `ResolveViaDns`（dnsLookup 默认 `null`，向后兼容）。测试数：655 → 665。

### 变更

- **`NPS.NIP` —— `AssuranceLevels.FromWireOrAnonymous("")` 修复**：空字符串 `""` 现在返回
  `Anonymous`（与 `null` 一致）。新增 `FromWireOrAnonymous_UnknownNonEmpty_Throws` 测试，
  验证 spec m6 前向兼容规则。

- **`NPS.NWP.Anchor` / `NPS.NOP` —— CGN 属性 + wire 重命名（破坏性）**：C# 属性
  `EstimatedNpt`、`BudgetNpt`、`AvailableNpt` 及局部变量 `budgetNpt` 重命名为
  `EstimatedCgn`、`BudgetCgn`、`AvailableCgn`、`budgetCgn`。Wire 字段
  `AnchorActionSpec.estimated_npt` 重命名为 `cgn_est`，与
  `TopologyEventEnvelope.cgn_est` 保持一致。**Wire 破坏性变更** —— 固定旧字段名的
  客户端升级后将看到该字段为 null，不保留别名。关联
  [labacacia/NPS-Dev#17](https://github.com/labacacia/NPS-Dev/issues/17)。

- **`NPS.NWP` —— `NptMeter` → `CognMeter` 重命名**：静态工具类 `NptMeter` 重命名为
  `CognMeter`；所有内部调用方（`MemoryNodeMiddleware`、`NeuralWebManifest`）同步更新。
  包内所有 XML 文档注释由 "NPT" 改为 "CGN/Cognon"。

- **`NPS.NIP` —— 运营商鉴权 + ACME API**：`NipCaOptions` 新增 `OperatorApiKey`（对
  `X-Operator-Key` 做 HMAC-SHA256 恒时比较）、`AcmeEnabled`（默认 `false`）、
  `AcmePathPrefix`（默认 `"/acme"`）。新增扩展方法 `UseNipAcme(WebApplication)`，
  在 `AcmeEnabled` 为 `true` 时挂载 ACME 中间件。`NPS.NipCaServer` 的 Program.cs 依赖这些 API。

- **全部 7 个包升至 `1.0.0-alpha.5`** —— 与 NPS 套件 alpha.5 同步。

### 测试

- 测试数：**629 → 665**（全部通过）。
  - 新增 13 个 `GossipStateTests`（STH gossip 间隔、peer 存储、多 peer、`FromEnvironment` 解析、NipSigner 往返）。
  - `AnchorTopologyTests` —— 501 断言从 404 更新；新增 `MissingCapability_Returns403`。
  - `AssuranceLevelTests` —— `NullOrEmpty_ReturnsAnonymous` + `UnknownNonEmpty_Throws`（m6 修复）。
  - `NdpRegistryTests` —— 10 个 DNS TXT 测试（正常路径、非法记录、host 提取、内存优先、可注入 mock resolver）。

---

## [1.0.0-alpha.4] —— 2026-04-30

.NET SDK 仍是 NPS 套件的**参考实现**。本次落地 NPS-RFC-0002（X.509 + ACME）
Phase A/B、NPS-RFC-0001 Phase 2（NCP preamble 运行时对齐）、
以及 NPS-CR-0002（Anchor topology 查询）。

### 新增

- **`NPS.NIP.X509`** + **`NPS.NIP.Acme`**（NPS-RFC-0002 Phase A/B —— X.509
  NID 证书 + ACME `agent-01` 参考实现）。新增类型：
  - `X509.NipCertBuilder` —— 构造携带 NID 自定义 OID 的 X.509 证书
    （subject、assurance level、key authorisation）。
  - `X509.NipX509Verifier` —— 校验 X.509 leaf + chain 与配置的 trust
    anchor，返回提取出的 NID + assurance level。
  - `Acme.AcmeAgent01Server` —— ACME `agent-01` 挑战签发 + 校验的服务端
    （challenge mint、key authorisation 哈希、JWS 签名 wire 包络）。
  - `Acme.AcmeAgent01Client` —— 客户端：nonce 拉取、account 注册、
    order、key-authorisation 应答、finalize、获取签发的证书链。
- **`NPS.NIP.Verification.NipIdentVerifier` dual-trust 路径** —— 校验器
  现在同时接受 `cert_format=v1`（Ed25519，alpha.3 wire 格式）与
  `cert_format=x509`（X.509 leaf + chain）的 IdentFrame。alpha.3 写出
  的 v1 IdentFrame 仍可被 alpha.4 校验。
- **`NPS.NWP.Anchor.Topology`** + **`NPS.NWP.Anchor.Client`**
  （NPS-CR-0002 —— Anchor Node topology 查询）。新增类型：
  - `Topology.TopologyTypes` —— `topology.snapshot` + `topology.stream`
    查询类型，含 `IncludeMembers` / `IncludeCapabilities` /
    `IncludeTags` / `IncludeMetrics` 过滤标志，以及
    `AnchorStateVersionRebased` rebase 帧。
  - `IAnchorTopologyService`（在 `Topology/`）—— 可插拔的 topology
    存储后端接口（默认内存实现）。
  - `Client.AnchorNodeClient` —— 强类型客户端，调用
    `topology.snapshot`（一次性）或订阅 `topology.stream`（长连接，
    显式处理 `AnchorStateVersionRebased`）。
  - 10 个 NPS-AaaS L2 conformance 测试（`AnchorTopology*Tests`）全绿。
- **NPS-RFC-0001 Phase 2** —— 线缆级 NCP preamble 运行时对齐：alpha.3
  的 `NcpPreamble` helper 类不变，但运行时已经把它接到原生模式传输边界
  （preamble parser + writer 接进 .NET 帧管线）。

### 变更

- 全部已发布包升至 `1.0.0-alpha.4`：`LabAcacia.NPS.Core`、
  `LabAcacia.NPS.NWP`、`LabAcacia.NPS.NWP.Anchor`、
  `LabAcacia.NPS.NWP.Bridge`、`LabAcacia.NPS.NIP`、`LabAcacia.NPS.NDP`、
  `LabAcacia.NPS.NOP`。
- `NPS.NIP.Frames.IdentFrame` wire 形状扩展，可携带可选
  `cert_format` 判别器 + `x509_chain`（DER 编码 leaf chain），与现有
  v1 字段并存。
- `NPS.NIP.Crypto.NipSigner` 增加 X.509 格式 IdentFrame 签发分支（按
  X.509 issuer 配置触发）；默认行为（仅 Ed25519）不变。
- 639 tests 全绿（alpha.3 时 575；+64 来自 RFC-0002 X.509 + ACME 端口、
  CR-0002 Anchor topology、RFC-0001 Phase 2 运行时）。

### 套件级 alpha.4 要点

- **NPS-RFC-0002 X.509 + ACME** —— 完整跨 SDK 端口波：
  .NET（本仓）/ Java / Python / TypeScript / Go / Rust 全部携带 X.509
  builder + ACME `agent-01` 全流程。本包内的
  `NPS.NIP.Acme.AcmeAgent01Server` 是 ACME 的 canonical server 端
  实现，SDK 消费者可以直接嵌入自己的 HTTP host。
- **`nps-registry` SQLite 实仓** + **`nps-ledger` Phase 2**（RFC 9162
  Merkle 树 + operator 签名 STH + inclusion proof）在对应 daemon 仓库
  交付。

### 推迟到 alpha.5+

- **NPS-RFC-0004 Phase 3** —— `nps-ledger` operator 之间 STH gossip 联邦
  （跨组织审计冗余）。
- **NPS-CR-0002 Phase 2** —— 来自 Anchor Node 中间件的服务端推送
  topology 更新（alpha.4 交付协议 + 客户端 + conformance 测试；
  中间件侧推送集成是后续工作）。
- 入库 reputation log entry 的完整 Ed25519 签名验证（alpha.4 仍只校验
  `ed25519:` 前缀结构）。

---

## [1.0.0-alpha.3] —— 2026-04-25

.NET SDK 是 NPS `v1.0.0-alpha.3` 套件里程碑的**参考实现** —— 每条新 Accepted 的 RFC 和 CR 都先在这里以可工作代码落地。

### Added

- **`NPS.Core.Ncp.NcpPreamble`**（NPS-RFC-0001 —— NCP 连接前导）。原生模式连接现以字面量 `b"NPS/1.0\n"`（8 字节）开头的参考 helper，便于接收侧把 NPS 帧与随机字节 / TLS / HTTP 区分开。提供 `Bytes` / `ToArray()` / `Matches()` / `TryValidate()` / `Validate()` / `WriteAsync()`，`Length=8`、`ReadTimeout=10s`、500 ms 关闭截止时间。`NPS.Core.NcpErrorCodes` 新增 `NCP-PREAMBLE-INVALID` 错误码，映射到 NPS 状态 `NPS-PROTO-PREAMBLE-INVALID`。
- **`NPS.NIP.AssuranceLevel`** + `AssuranceLevels` 静态类 + STJ converter（NPS-RFC-0003 —— Agent 身份保证等级）。三态 `anonymous` / `attested` / `verified`。接入 `IdentFrame`（新增 `AssuranceLevel?` 字段）、`NipVerifyContext`（新增 `MinAssuranceLevel`）、`NeuralWebManifest.MinAssuranceLevel`。不匹配 / 未知错误 `NIP-AUTH-ASSURANCE-MISMATCH` / `NIP-AUTH-ASSURANCE-UNKNOWN`。NWP 层可通过 `NWP-AUTH-ASSURANCE-TOO-LOW` / `NWP-AUTH-REPUTATION-BLOCKED` 拒绝低于阈值的调用方。
- **`NPS.NIP.Reputation`** namespace（NPS-RFC-0004 —— NID 声誉日志，CT 风格，Phase 1）。新增类型 `IncidentType`、`Severity`、`ReputationLogEntry`、`ReputationLogEntrySigner` —— append-only Merkle 日志条目结构，Ed25519 签名。错误 `NIP-REPUTATION-ENTRY-INVALID`、`NIP-REPUTATION-LOG-UNREACHABLE`。
- **`LabAcacia.NPS.NWP.Anchor`** —— 新包，替换旧的 **`LabAcacia.NPS.NWP.Gateway`**（NPS-CR-0001 —— Anchor / Bridge 节点拆分）。旧的 "Gateway Node" 角色现称 **Anchor Node**：集群控制面、无状态 AaaS 入口。公共类型 `Gateway*` → `Anchor*` 重命名（`AnchorNodeMiddleware`、`AnchorNodeOptions`、`IAnchorRouter`、`IAnchorRateLimiter`、`InMemoryAnchorRateLimiter`、`AnchorNodeMetadata`）。`LegacyGatewayShim.cs` 提供编译期 `[Obsolete(error: true)]` 兼容垫片，保留旧符号 `GatewayNodeMiddleware` / `IGatewayRouter` / `IGatewayRateLimiter` / `LegacyGatewayServiceExtensions.UseGatewayNode` / `AddGatewayNode`，给消费者明确的迁移错误指向新类型。
- **`LabAcacia.NPS.NWP.Bridge`** —— 新包（Phase 1 仅类型骨架）。"NPS↔外部协议翻译" 单独成为 **Bridge Node** 类型。包含 `BridgeNodeMetadata.NodeType="bridge"`、`BridgeProtocols.{Http,Grpc,Mcp,A2a}`、`BridgeNodeDescriptor`、`BridgeTarget`。NDP 的 AnnounceFrame 新增 `node_kind` / `cluster_anchor` / `bridge_protocols`。
- 新增测试：`NcpPreambleTests`（20）、`AssuranceLevelTests`（27）、`ReputationLogEntryTests`（33）、`AnchorNodeMiddlewareTests`（由 `GatewayNodeMiddlewareTests` 重命名）。

### Changed

- 全部发布包升级至 `1.0.0-alpha.3`，与套件同步：`LabAcacia.NPS.Core`、`LabAcacia.NPS.NWP`、`LabAcacia.NPS.NWP.Anchor`（原 `.Gateway`）、`LabAcacia.NPS.NWP.Bridge`（新）、`LabAcacia.NPS.NIP`、`LabAcacia.NPS.NDP`、`LabAcacia.NPS.NOP`。
- `NPS.NWP.Nwm.NeuralWebManifest` `node_type` 枚举现允许 `"anchor"` 和 `"bridge"`，与现有 `memory` / `action` / `complex` 并列（旧 `gateway` 值 parser 仍接受但文档标记为 Deprecated，建议迁至 `anchor`）。
- `NPS.Core.NpsStatusCodes` 新增 `ProtoPreambleInvalid`、`DownstreamUnavailable`（引入 NPS-PROTO 类别）。
- 575 测试全绿（alpha.2 是 495；+80 来自 RFC-0001/0003/0004 + Anchor 中间件刷新）。

### 套件级要点

- **6 个 NPS 常驻 daemon**（NPS-Dev `daemons/` 目录）。新增的 daemon 居于 NPS-Dev —— 不在 NPS-sdk-dotnet 内 —— 但消费者应知晓存在：
  - `npsd` —— 本机协议入口 + 状态宿主（alpha.3 提供 L1 功能性参考实现）
  - `nps-runner` —— 本机 FaaS 调度器（骨架）
  - `nps-gateway` —— 公网入口（骨架）
  - `nps-registry` —— 跨机 NDP registry（骨架）
  - `nps-cloud-ca` —— X.509 签发（骨架 —— 在 alpha.4 之前 deferred 至各语言的 `tools/nip-ca-server*`）
  - `nps-ledger` —— append-only NPS-RFC-0004 日志收集器（in-memory 功能性参考实现）

  三层拓扑详见 [`docs/daemons/architecture.cn.md`](https://github.com/LabAcacia/NPS-Dev/blob/v1.0.0-alpha.3/docs/daemons/architecture.cn.md)。

### 推迟到 alpha.4

- **NPS-RFC-0001 Phase 2** —— `NPS.Core` NCP transport 中的 wire 级前导 runtime（Phase 1 提供 helper + 错误码 + 20 单元测试；原生模式监听端真正接入在 alpha.4 落地）。
- **NPS-RFC-0002** —— NID 证书 X.509 + ACME（仍为 Draft）。
- **NPS-CR-0002** —— Anchor 拓扑查询。

### 涵盖模块

- NPS.Core / NPS.NWP / NPS.NWP.Anchor / NPS.NWP.Bridge / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.2] —— 2026-04-19

### Changed

- `NPS.Core` README 显式列出全套 NCP 帧（AnchorFrame / DiffFrame / StreamFrame / CapsFrame / HelloFrame / ErrorFrame）。
- `NPS.NWP` README 列出四种节点类型（Memory / Action / Complex / Gateway）。
- 状态：495 测试全绿，包含新增的 wire-size 基准、Gateway Node 中间件、A2A Bridge。

### 涵盖模块

- NPS.Core / NPS.NWP / NPS.NIP / NPS.NDP / NPS.NOP

---

## [1.0.0-alpha.1] —— 2026-04-10

作为 NPS 套件 `v1.0.0-alpha.1` 的一部分首次公开 alpha。

[1.0.0-alpha.7]: https://gitee.com/labacacia/NPS-sdk-dotnet/releases/tag/v1.0.0-alpha.7
[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
