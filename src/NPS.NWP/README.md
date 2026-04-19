# LabAcacia.NPS.NWP

> **Neural Web Protocol** frames, NWM manifest model, and ASP.NET Core integration for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.NWP.svg)](https://www.nuget.org/packages/LabAcacia.NPS.NWP/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-2 NWP v0.5](https://github.com/labacacia/nps/blob/main/spec/NPS-2-NWP.md)

NWP is the HTTP-of-AI for NPS. This package provides the `QueryFrame` / `ActionFrame`
request set, the `NeuralWebManifest` (`.nwm.json`) model, the `X-NWP-*` header
constants, and a ready-to-mount `MemoryNodeMiddleware` that exposes any data
source as an NWP Memory Node over `/.nwm`, `/.schema`, `/query`, `/stream`.

## Install

```bash
dotnet add package LabAcacia.NPS.NWP
```

## Quick start — Memory Node

```csharp
using NPS.NWP.MemoryNode;
using Microsoft.Extensions.DependencyInjection;

sealed class ProductsProvider : IMemoryNodeProvider
{
    public ValueTask<MemoryNodeSchema>       GetSchemaAsync(CancellationToken ct) => …;
    public ValueTask<MemoryNodeQueryResult>  QueryAsync(MemoryNodeQueryContext ctx, CancellationToken ct) => …;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddNpsCore()
    .AddNwp()                        // registers frames in the codec
    .AddMemoryNode<ProductsProvider>(o =>
    {
        o.NodeId    = "urn:nps:node:api.example.com:products";
        o.BasePath  = "/products";
    });

var app = builder.Build();
app.UseMemoryNode<ProductsProvider>();
app.Run();
```

The middleware answers:

- `GET  /products/.nwm`    — the Neural Web Manifest
- `GET  /products/.schema` — the Memory Node schema
- `POST /products/query`   — `QueryFrame` request/response
- `POST /products/stream`  — paginated `StreamFrame`s

## Key types

| Type | Purpose |
|------|---------|
| `QueryFrame`, `QueryOrderClause`, `VectorSearchOptions` | NWP query request |
| `ActionFrame`, `AsyncActionResponse` | NWP action invocation (sync + async) |
| `NeuralWebManifest`, `NodeCapabilities`, `NodeEndpoints`, `NodeGraph` | `.nwm.json` model |
| `NwpHttpHeaders` | `X-NWP-Depth`, `X-NWP-Agent-NID`, `X-NWP-Trace-ID`, `X-NWP-Budget`, … |
| `IMemoryNodeProvider`, `MemoryNodeMiddleware`, `MemoryNodeOptions` | Memory Node host |
| `NptMeter` | NPS Token budget accounting |
| `NwpErrorCodes` | `NWP-*` error code constants |

## Documentation

- **Full API reference:** [`doc/NPS.NWP.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.NWP.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-2 NWP](https://github.com/labacacia/nps/blob/main/spec/NPS-2-NWP.md)

## NPS Repositories

| Purpose | Repo |
|---------|------|
| Spec + Release notes | [NPS-Release](https://github.com/labacacia/NPS-Release) |
| .NET SDK (this package) | [NPS-sdk-dotnet](https://github.com/labacacia/NPS-sdk-dotnet) |
| Python SDK | [NPS-sdk-py](https://github.com/labacacia/NPS-sdk-py) |
| TypeScript SDK | [NPS-sdk-ts](https://github.com/labacacia/NPS-sdk-ts) |
| Java SDK | [NPS-sdk-java](https://github.com/labacacia/NPS-sdk-java) |
| Rust SDK | [NPS-sdk-rust](https://github.com/labacacia/NPS-sdk-rust) |
| Go SDK | [NPS-sdk-go](https://github.com/labacacia/NPS-sdk-go) |

## License

Apache 2.0 © LabAcacia / INNO LOTUS PTY LTD
