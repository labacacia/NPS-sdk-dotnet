# NPS .NET SDK

[![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.Core)](https://www.nuget.org/packages/LabAcacia.NPS.Core)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)

Reference implementation of the **Neural Protocol Suite (NPS)** for .NET 10 / C# — a complete internet protocol suite purpose-built for AI Agents and models.

---

## NPS Repositories

| Repo | Role | Language |
|------|------|----------|
| [NPS-Release](https://github.com/labacacia/NPS-Release) | Protocol specifications (authoritative) | Markdown / YAML |
| **[NPS-sdk-dotnet](https://github.com/labacacia/NPS-sdk-dotnet)** (this repo) | Reference implementation | C# / .NET 10 |
| [NPS-sdk-py](https://github.com/labacacia/NPS-sdk-py) | Async Python SDK | Python 3.11+ |
| [NPS-sdk-ts](https://github.com/labacacia/NPS-sdk-ts) | Node/browser SDK | TypeScript |
| [NPS-sdk-java](https://github.com/labacacia/NPS-sdk-java) | JVM SDK | Java 21+ |
| [NPS-sdk-rust](https://github.com/labacacia/NPS-sdk-rust) | Async SDK | Rust stable |
| [NPS-sdk-go](https://github.com/labacacia/NPS-sdk-go) | Go SDK | Go 1.23+ |

---

## Packages

All packages target **.NET 10** and are published on NuGet under the `LabAcacia.NPS.*` prefix.

| Package | Version | Description |
|---------|---------|-------------|
| [`LabAcacia.NPS.Core`](https://www.nuget.org/packages/LabAcacia.NPS.Core) | 1.0.0-alpha.1 | NCP frame types, dual-tier codec (JSON / MsgPack), AnchorFrame cache, frame registry |
| [`LabAcacia.NPS.NWP`](https://www.nuget.org/packages/LabAcacia.NPS.NWP) | 1.0.0-alpha.1 | NWM manifest, `QueryFrame` / `ActionFrame`, Memory Node middleware (SQL Server / PostgreSQL) |
| [`LabAcacia.NPS.NIP`](https://www.nuget.org/packages/LabAcacia.NPS.NIP) | 1.0.0-alpha.1 | Ed25519 key management, CA issuance / revocation / OCSP / CRL, Ident verification |
| [`LabAcacia.NPS.NDP`](https://www.nuget.org/packages/LabAcacia.NPS.NDP) | 1.0.0-alpha.1 | Announce / Resolve frames, in-memory registry, Ed25519 announce validator |
| [`LabAcacia.NPS.NOP`](https://www.nuget.org/packages/LabAcacia.NPS.NOP) | 1.0.0-alpha.1 | Task / Delegate / Sync / AlignStream frames, DAG validator, orchestration engine |

## Repository Layout

```
NPS-sdk-dotnet/
├── doc/                # SDK documentation (see below)
├── src/
│   ├── NPS.Core/       # Frame codec, cache, registry, exceptions
│   ├── NPS.NWP/        # Neural Web Protocol
│   ├── NPS.NIP/        # Neural Identity Protocol
│   ├── NPS.NDP/        # Neural Discovery Protocol
│   └── NPS.NOP/        # Neural Orchestration Protocol
├── tests/NPS.Tests/    # 429 tests across all five protocols
├── nip-ca-server/      # Standalone NIP CA server (ASP.NET Core + SQLite + Docker)
└── NPS.sln
```

## Installation

```bash
dotnet add package LabAcacia.NPS.Core --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NWP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NIP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NDP  --version 1.0.0-alpha.1
dotnet add package LabAcacia.NPS.NOP  --version 1.0.0-alpha.1
```

## Quick Start

### Encode and decode an AnchorFrame

```csharp
using Microsoft.Extensions.DependencyInjection;
using NPS.Core.Codecs;
using NPS.Core.Caching;
using NPS.Core.Extensions;
using NPS.Core.Frames;
using NPS.Core.Frames.Ncp;

var services = new ServiceCollection().AddNpsCore();
using var sp  = services.BuildServiceProvider();

var codec = sp.GetRequiredService<NpsFrameCodec>();
var cache = sp.GetRequiredService<AnchorFrameCache>();

var schema = new FrameSchema
{
    Fields = new[]
    {
        new SchemaField("id",    "uint64"),
        new SchemaField("price", "decimal", Semantic: "commerce.price.usd"),
    }
};

var anchorId = AnchorIdComputer.Compute(schema); // "sha256:..."
var frame    = new AnchorFrame { AnchorId = anchorId, Schema = schema, Ttl = 3600 };

byte[] wire  = codec.Encode(frame);                       // MsgPack, ~60% smaller than JSON
var    clone = (AnchorFrame)codec.Decode(wire);
cache.Set(clone);
```

### Host a Memory Node (NWP)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddNpsCore()
    .AddNpsNwp()
    .AddMemoryNode<OrdersProvider>(options =>
    {
        options.AnchorId = "sha256:...";
        options.DefaultLimit = 50;
    });

var app = builder.Build();
app.UseMemoryNode("/orders");
app.Run();
```

### Issue a NIP certificate (CA)

```csharp
var services = new ServiceCollection()
    .AddNpsCore()
    .AddNpsNip(o => o.CaName = "urn:nps:ca:labacacia.com")
    .AddSingleton<INipCaStore, PostgreSqlNipCaStore>()
    .BuildServiceProvider();

var ca   = services.GetRequiredService<NipCaService>();
var cert = await ca.IssueAsync(new NipCsr
{
    Subject  = "urn:nps:agent:example.com:worker-1",
    PubKey   = publicKey,
    ValidFor = TimeSpan.FromDays(90),
});
```

### Submit a NOP task DAG

```csharp
var dag = new TaskDag
{
    Nodes =
    [
        new TaskNode { Id = "fetch",     Action = "data.fetch",      Agent = "urn:nps:node:data.example.com" },
        new TaskNode { Id = "summarise", Action = "llm.summarise",   Agent = "urn:nps:node:llm.example.com",
                       Inputs = new Dictionary<string, string> { ["text"] = "$fetch.output" } }
    ],
    Edges = [new TaskEdge { From = "fetch", To = "summarise" }]
};

var task   = new TaskFrame { TaskId = Guid.NewGuid().ToString(), Dag = dag };
var result = await orchestrator.SubmitAsync(task);
await orchestrator.WaitAsync(result.TaskId, timeout: TimeSpan.FromMinutes(2));
```

---

## Documentation

Comprehensive API documentation covering every public class and method:

| Document | Scope |
|----------|-------|
| [doc/overview.md](./doc/overview.md) | SDK architecture, DI model, encoding tiers |
| [doc/NPS.Core.md](./doc/NPS.Core.md) | Frame header, codecs, anchor cache, registry, exceptions |
| [doc/NPS.NWP.md](./doc/NPS.NWP.md) | NWM manifest, Query/Action frames, Memory Node middleware |
| [doc/NPS.NIP.md](./doc/NPS.NIP.md) | Key manager, CA service, OCSP/CRL, Ident verifier |
| [doc/NPS.NDP.md](./doc/NPS.NDP.md) | Announce/Resolve/Graph frames, registry, validator |
| [doc/NPS.NOP.md](./doc/NPS.NOP.md) | DAG models, validator, orchestrator engine |
| [doc/sdk-usage.md](./doc/sdk-usage.md) · [中文](./doc/sdk-usage.cn.md) | End-to-end usage walkthrough |

---

## NIP CA Server

A standalone NIP Certificate Authority server is bundled under [`nip-ca-server/`](./nip-ca-server/) — ASP.NET Core, SQLite-backed, Docker-ready.

```bash
cd nip-ca-server
docker-compose up
```

---

## Build & Test

```bash
dotnet build NPS.sln
dotnet test                # 429 tests across all 5 protocols
```

Coverage target: **≥ 90 %** line coverage (current: ≥ 95 %).

---

## Protocols

| Protocol | Analogue | Version |
|----------|----------|---------|
| NCP — Neural Communication Protocol | Wire / Framing | v0.4 |
| NWP — Neural Web Protocol | HTTP | v0.4 |
| NIP — Neural Identity Protocol | TLS / PKI | v0.2 |
| NDP — Neural Discovery Protocol | DNS | v0.2 |
| NOP — Neural Orchestration Protocol | SMTP / MQ | v0.3 |

Protocol dependency: `NCP ← NWP ← NIP ← NDP` / `NCP + NWP + NIP ← NOP`.

---

## License

Apache 2.0 — see [LICENSE](./LICENSE) and [NOTICE](./NOTICE).

Copyright 2026 INNO LOTUS PTY LTD
