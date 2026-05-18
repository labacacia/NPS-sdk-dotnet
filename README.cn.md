[English Version](./README.md) | 中文版

# NPS .NET 参考实现
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Release](https://img.shields.io/badge/release-v1.0.0--alpha.7-orange.svg)](CHANGELOG.cn.md)
[![NCP](https://img.shields.io/badge/NCP-v0.6-5b8cff.svg)]()
[![NWP](https://img.shields.io/badge/NWP-v0.12-4af0b0.svg)]()
[![NIP](https://img.shields.io/badge/NIP-v0.8-7b61ff.svg)]()
[![NDP](https://img.shields.io/badge/NDP-v0.7-f0a050.svg)]()
[![NOP](https://img.shields.io/badge/NOP-v0.5-ff8c42.svg)]()

C# / .NET 10 Neural Protocol Suite 参考实现。

## NuGet 包

| 包名 | 版本 | 说明 |
|------|------|------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.7 | 共享帧类型（AnchorFrame、DiffFrame、StreamFrame、CapsFrame、HelloFrame、ErrorFrame）、JSON/MsgPack 编解码、AnchorFrame 缓存、帧注册表 |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.7 | Neural Web Protocol — NWM 清单、Query / Action / Subscribe / Diff 帧、Memory / Action / Complex / Anchor / Bridge Node 中间件 |
| `LabAcacia.NPS.NWP.Anchor` | 1.0.0-alpha.7 | NWP Anchor Node：把 ActionFrame 无状态翻译到 NOP TaskFrame 的 AaaS 入口；`AnchorNodeClient` 支持 `topology.snapshot` / `topology.stream` 拓扑查询 |
| `LabAcacia.NPS.NWP.Bridge` | 1.0.0-alpha.7 | NWP Bridge Node：NPS 帧到非 NPS 协议（HTTP / gRPC / MCP / A2A 目标适配器）的无状态翻译器 |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.7 | Neural Identity Protocol — CA、Ed25519 密钥生成、IdentFrame 签发 / 吊销、OCSP、CRL；X.509 + ACME `agent-01` challenge（RFC-0002 原型） |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.7 | Neural Discovery Protocol — announce / resolve 帧、内存注册表、Ed25519 校验 |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.7 | Neural Orchestration Protocol — Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |

## 构建

```bash
dotnet build NPS.sln
```

## 测试

```bash
dotnet test
```

## 状态

积极开发中（v1.0.0-alpha.7）。699 个测试全部通过。

当前主要内容：NCP 原生模式连接前导字节（`NPS/1.0\n`）覆盖全部 5 个非 .NET SDK；NWP Anchor 拓扑查询（`topology.snapshot` / `topology.stream`）+ `AnchorNodeClient`；NIP X.509 + ACME `agent-01` 原型（RFC-0002）；nps-registry SQLite 后端；nps-ledger Phase 2（SQLite + RFC 9162 Merkle 树 + operator 签名 STH + 包含证明端点）。
