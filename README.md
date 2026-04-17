English | [中文版](./README.cn.md)

# NPS .NET SDK

[![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.Core)](https://www.nuget.org/packages/LabAcacia.NPS.Core)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)

.NET 10 / C# reference implementation of the **Neural Protocol Suite (NPS)** — a complete internet protocol suite designed for AI Agents and models.

---

## Packages

| Package | Version | Description |
|---------|---------|-------------|
| `LabAcacia.NPS.Core` | 1.0.0-alpha.1 | Shared frame types, encoding/decoding (JSON/MsgPack), AnchorFrame cache |
| `LabAcacia.NPS.NWP` | 1.0.0-alpha.1 | Neural Web Protocol — NWM manifest, Query/Action frames, Memory Node middleware |
| `LabAcacia.NPS.NIP` | 1.0.0-alpha.1 | Neural Identity Protocol — CA, key generation, certificate issuance/revocation, OCSP, CRL |
| `LabAcacia.NPS.NDP` | 1.0.0-alpha.1 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `LabAcacia.NPS.NOP` | 1.0.0-alpha.1 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |

## Installation

```bash
# Core framing + codec (required by all packages)
dotnet add package LabAcacia.NPS.Core --version 1.0.0-alpha.1

# Neural Web Protocol
dotnet add package LabAcacia.NPS.NWP --version 1.0.0-alpha.1

# Neural Identity Protocol
dotnet add package LabAcacia.NPS.NIP --version 1.0.0-alpha.1

# Neural Discovery Protocol
dotnet add package LabAcacia.NPS.NDP --version 1.0.0-alpha.1

# Neural Orchestration Protocol
dotnet add package LabAcacia.NPS.NOP --version 1.0.0-alpha.1
```

## Quick Start

```csharp
using NPS.Core.Frames;
using NPS.Core.Codecs;

// Create and encode an AnchorFrame
var anchor = new AnchorFrame("my-node", schema, ttl: 3600);
var codec  = new NpsFrameCodec(EncodingTier.MsgPack);
byte[] bytes = codec.Encode(anchor);

// Decode
var decoded = (AnchorFrame)codec.Decode(bytes);
```

See the full usage guide: [doc/sdk-usage.md](./doc/sdk-usage.md) | [中文文档](./doc/sdk-usage.cn.md)

## NIP CA Server

A standalone NIP Certificate Authority server is available in [`nip-ca-server/`](./nip-ca-server/) built on ASP.NET Core with SQLite and Docker support.

```bash
cd nip-ca-server
docker-compose up
```

## Build & Test

```bash
dotnet build NPS.sln
dotnet test
```

429 tests passing.

## Protocols

| Protocol | Analogue | Version |
|----------|----------|---------|
| NCP — Neural Communication Protocol | Wire Format / Framing | v0.4 |
| NWP — Neural Web Protocol | HTTP | v0.4 |
| NIP — Neural Identity Protocol | TLS/PKI | v0.2 |
| NDP — Neural Discovery Protocol | DNS | v0.2 |
| NOP — Neural Orchestration Protocol | SMTP/MQ | v0.3 |

## Documentation

- [SDK Usage Guide (English)](./doc/sdk-usage.md)
- [SDK 使用文档 (中文)](./doc/sdk-usage.cn.md)

## License

Apache 2.0 — see [LICENSE](./LICENSE) and [NOTICE](./NOTICE).

Copyright 2026 INNO LOTUS PTY LTD
