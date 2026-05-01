[English Version](./README.md) | 中文版

# NPS .NET 参考实现

C# / .NET 10 Neural Protocol Suite 参考实现。

## NuGet 包

| 包名 | 版本 | 说明 |
|------|------|------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.5 | 共享帧类型（AnchorFrame、DiffFrame、StreamFrame、CapsFrame、HelloFrame、ErrorFrame）、JSON/MsgPack 编解码、AnchorFrame 缓存、帧注册表 |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.5 | Neural Web Protocol — NWM 清单、Query / Action / Subscribe / Diff 帧、Memory / Action / Complex / Anchor / Bridge Node 中间件 |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.5 | NWP Anchor Node：把 ActionFrame 无状态翻译到 NOP TaskFrame 的 AaaS 入口；`AnchorNodeClient` 支持 `topology.snapshot` / `topology.stream` 拓扑查询 |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.5 | NWP Bridge Node：NPS 帧到非 NPS 协议（HTTP / gRPC / MCP / A2A 目标适配器）的无状态翻译器 |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.5 | Neural Identity Protocol — CA、Ed25519 密钥生成、IdentFrame 签发 / 吊销、OCSP、CRL；X.509 + ACME `agent-01` challenge（RFC-0002 原型） |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.5 | Neural Discovery Protocol — announce / resolve 帧、内存注册表、Ed25519 校验；DNS TXT 回退（`ResolveViaDns`、`IDnsTxtLookup`） |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.5 | Neural Orchestration Protocol — Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |

## 构建

```bash
dotnet build NPS.sln
```

## 测试

```bash
dotnet test
```

## 状态

积极开发中（v1.0.0-alpha.5）。665 个测试全部通过。

alpha.4 主要内容：NCP 原生模式连接前导字节（`NPS/1.0\n`）覆盖全部 5 个非 .NET SDK；NWP Anchor 拓扑查询（`topology.snapshot` / `topology.stream`）+ `AnchorNodeClient`；NIP X.509 + ACME `agent-01` 原型（RFC-0002）；nps-registry SQLite 后端；nps-ledger Phase 2（SQLite + RFC 9162 Merkle 树 + operator 签名 STH + 包含证明端点）。

alpha.5 主要内容：NWP 错误码常量（`NwpErrorCodes`，30 个）；`NPS-SERVER-UNSUPPORTED` 状态码；NIP gossip 错误码（`REPUTATION_GOSSIP_FORK` / `REPUTATION_GOSSIP_SIG_INVALID`）；`AssuranceLevel` 空字符串修复；**NDP DNS TXT 回退**（`ResolveViaDns` / `IDnsTxtLookup` / `SystemDnsTxtLookup`）。
