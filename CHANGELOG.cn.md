[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— .NET SDK

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

在 NPS 达到 v1.0 稳定版之前，套件内所有仓库同步使用同一个预发布版本号。

---

## [1.0.0-alpha.14] —— 2026-06-26

### 新增

- `NPS.NWP.Bridge` 现在内置 HTTP/HTTPS、gRPC JSON unary、MCP JSON-RPC、A2A JSON-RPC 出站 dispatcher。
- 新增 `BridgeNode`、`BridgeDispatcherRegistry`、`IBridgeDispatcher`、`BridgeNodeMiddleware`、`AddBridgeNode`、`UseBridgeNode`，同时支持宿主无关调用与 ASP.NET 托管。
- 新增 `BridgeEndpointValidator`，默认启用 private/loopback SSRF 防护，并提供 URI-aware `allowed_prefixes` 校验。
- 新增 `HealthProbeRenderer`，支持 transport-neutral daemon health/readiness 输出。
- 新增 `TrustFrameValidator`，提供开源 pinned-grantor TrustFrame 基础验证。
- `NpsFrameCodec` 新增 `CreateDefault()`、`Create(...)` 与实例 `Peek(...)`。
- 新增 `NcpServerOptions`，支持 pre-preamble authenticated stream hook、handshake read timeout 与 Hello payload size 上限。
- `NipIdentVerifier` 新增 live revocation hook：`NipRevocationCheck`、`NipVerifierOptions.RevocationStore`，并让 OCSP transport failure 默认 fail-closed。
- `INipCaStore` 新增 `ListAsync()`，并提供 package 内置 `InMemoryNipCaStore`，用于测试、demo 与本地 dev stack。
- 新增 `NipCaClient` 类型化远程 CA client，覆盖 discovery、CRL、Ed25519 注册/续期/吊销/验证与 RFC-0002 X.509 注册。
- 新增 `NwpNativeNodeServer`，让 Memory / Action Node 可以通过 `NcpSession` 或任意 NCP stream 服务 native-mode NWP traffic。
- 新增 `LabAcacia.NPS.Conformance`，提供 Node L1/L2 case catalog、run manifest 与 CI 自认证 validation helper。

### 变更

- 公开包项目现在产出启用 SourceLink 的 `.snupkg` 符号包。
- NuGet 发布 workflow 现在打包并校验完整公开 .NET 包族，包含 `LabAcacia.NPS.Conformance`。
- `ActionNodeMiddleware` 与 `ComplexNodeMiddleware` 避免 nullable `CapsFrame.AnchorRef` 赋值。
- NIP CA CRL 响应现在包含 `issued_at` 与 CA detached signature；CA discovery 的 OCSP URL 指向已映射的 verify endpoint。
- SDK test gate 的测试/构建输出已清理为无 warning。

### 文档

- 新增 Bridge target schema 文档，并扩展 .NET quickstart：Core codec、DI、TrustFrame 验证、observability、storage、native NWP serving、Bridge、ingress、remote CA 与 conformance packages。

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

[1.0.0-alpha.2]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.2
[1.0.0-alpha.1]: https://github.com/LabAcacia/nps/releases/tag/v1.0.0-alpha.1
