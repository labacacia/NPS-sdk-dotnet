[English Version](./README.md) | 中文版

# NPS .NET 参考实现

C# / .NET 10 Neural Protocol Suite 参考实现。

## 项目

| 项目 | 版本 | 说明 |
|------|------|------|
| `NPS.Core` | 1.0.0-alpha.2 | 共享帧类型（AnchorFrame、DiffFrame、StreamFrame、CapsFrame、HelloFrame、ErrorFrame）、编解码（JSON/MsgPack）、AnchorFrame 缓存、帧注册表 |
| `NPS.NWP` | 1.0.0-alpha.2 | Neural Web Protocol — NWM 清单、Query/Action 帧、Memory / Action / Complex / Gateway Node 中间件 |
| `NPS.NIP` | 1.0.0-alpha.2 | Neural Identity Protocol — CA、密钥生成、证书签发 / 吊销、OCSP、CRL |
| `NPS.NDP` | 1.0.0-alpha.2 | Neural Discovery Protocol — announce / resolve 帧、内存注册表、Ed25519 校验 |
| `NPS.NOP` | 1.0.0-alpha.2 | Neural Orchestration Protocol — Task / Delegate / Sync / AlignStream 帧、DAG 校验器、编排引擎 |

## 构建

```bash
dotnet build NPS.sln
```

## 测试

```bash
dotnet test
```

## 状态

积极开发中（v1.0.0-alpha.2）。五个协议包全部实现，495 个测试全部通过。

包含 NCP 原生模式 HelloFrame（0x06）握手、Tier-1 JSON / Tier-2 MsgPack 编解码、AnchorFrame JCS 规范化、NWP Memory/Action/Complex/Gateway 四种节点、NIP CA + OCSP/CRL、NDP 注册表 + announce 校验、NOP DAG 编排（含委托 / 同步 / 对齐）。
