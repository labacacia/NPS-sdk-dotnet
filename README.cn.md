[English Version](./README.md) | 中文版

# NPS .NET 参考实现

C# / .NET 10 Neural Protocol Suite 参考实现。

## 项目

| 项目 | 版本 | 说明 |
|------|------|------|
| `NPS.Core` | 1.0.0-alpha.3 | 共享帧类型（AnchorFrame、DiffFrame、StreamFrame、CapsFrame、HelloFrame、ErrorFrame）、编解码（JSON/MsgPack）、AnchorFrame 缓存、帧注册表、NCP 连接前导（RFC-0001）|
| `NPS.NWP` | 1.0.0-alpha.3 | Neural Web Protocol — NWM 清单、Query/Action 帧、Memory / Action / Complex Node 中间件 |
| `NPS.NWP.Anchor` | 1.0.0-alpha.3 | Anchor Node 中间件（CR-0001 —— 替换原 Gateway Node 包）—— 无状态 AaaS 入口，把 ActionFrame 翻译为 NOP TaskFrame |
| `NPS.NWP.Bridge` | 1.0.0-alpha.3 | Bridge Node 骨架（CR-0001）—— 无状态翻译器，把 NPS 帧翻译为非 NPS 协议（HTTP / gRPC / MCP / A2A）|
| `NPS.NIP` | 1.0.0-alpha.3 | Neural Identity Protocol — CA、密钥生成、证书签发 / 吊销、OCSP、CRL、保证等级（RFC-0003）、声誉日志（RFC-0004）|
| `NPS.NDP` | 1.0.0-alpha.3 | Neural Discovery Protocol — announce / resolve 帧、内存注册表、Ed25519 校验、AnnounceFrame `node_kind`/`cluster_anchor`/`bridge_protocols` |
| `NPS.NOP` | 1.0.0-alpha.3 | Neural Orchestration Protocol — Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |

## 构建

```bash
dotnet build NPS.sln
```

## 测试

```bash
dotnet test
```

## 状态

积极开发中（v1.0.0-alpha.3）。NWP/NIP/NDP/NOP 七个包全部实现，**575 个测试全部通过**。

包含 NCP 原生模式 HelloFrame（0x06）握手 + RFC-0001 连接前导、Tier-1 JSON / Tier-2 MsgPack 编解码、AnchorFrame JCS 规范化、NWP Memory/Action/Complex 三种节点 + 新增 Anchor 与 Bridge 节点包（CR-0001）、NIP CA + OCSP/CRL + RFC-0003 保证等级 + RFC-0004 声誉日志、NDP 注册表 + announce 校验（含 `node_kind`/`cluster_anchor`/`bridge_protocols`）、NOP DAG 编排（含委托 / 同步 / 对齐）。
