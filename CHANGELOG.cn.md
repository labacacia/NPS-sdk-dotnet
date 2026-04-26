[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— .NET SDK

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

在 NPS 达到 v1.0 稳定版之前，套件内所有仓库同步使用同一个预发布版本号。

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

[1.0.0-alpha.3]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.3
[1.0.0-alpha.2]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/NPS-Dev/releases/tag/v1.0.0-alpha.1
