# LabAcacia.NPS.NDP

> **Neural Discovery Protocol** frames, in-memory registry, and announce validator for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.NDP.svg)](https://www.nuget.org/packages/LabAcacia.NPS.NDP/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-4 NDP v0.3](https://github.com/labacacia/nps/blob/main/spec/NPS-4-NDP.md)

NDP is the DNS of NPS. This package provides the three NDP frame types
(`AnnounceFrame`, `ResolveFrame`, `GraphFrame`), a thread-safe in-memory registry
with TTL eviction, and an `NdpAnnounceValidator` that verifies announce
signatures against a node's `IdentFrame` public key.

Depends on `LabAcacia.NPS.Core` and `LabAcacia.NPS.NIP` (signature verification).

## Install

```bash
dotnet add package LabAcacia.NPS.NDP
```

## Quick start

```csharp
using NPS.NDP;
using NPS.NDP.Extensions;
using NPS.NDP.Registry;
using NPS.NIP;
using Microsoft.Extensions.DependencyInjection;

// 1) Register frames + registry
var frameRegistry = new FrameRegistryBuilder().AddNcp().AddNip().AddNdp().Build();
var services      = new ServiceCollection()
    .AddSingleton(new NpsFrameCodec(frameRegistry))
    .AddNdp()
    .BuildServiceProvider();

var discovery = services.GetRequiredService<INdpRegistry>();
var validator = services.GetRequiredService<NdpAnnounceValidator>();

// 2) Announcer: sign and publish
var identity = new NipKeyManager();
identity.Generate("node.key", "…");
var nid   = "urn:nps:node:api.example.com:products";
var frame = new AnnounceFrame
{
    Nid          = nid,
    NodeType     = "memory",
    Addresses    = [new NdpAddress { Host = "10.0.0.5", Port = 17433, Protocol = "nwp+tls" }],
    Capabilities = ["nwp:query", "nwp:stream"],
    Ttl          = 300,
    Timestamp    = DateTime.UtcNow.ToString("O"),
    Signature    = "placeholder",
};
frame = frame with { Signature = NipSigner.Sign(identity.PrivateKey, frame) };

validator.RegisterPublicKey(nid, NipSigner.EncodePublicKey(identity.PublicKey));
if (!validator.Validate(frame).IsValid) throw new InvalidOperationException();
discovery.Announce(frame);

// 3) Resolver
var resolved = discovery.Resolve("nwp://api.example.com/products/items/42");
// → NdpResolveResult { Host = "10.0.0.5", Port = 17433, Ttl = 300 }
```

## Key types

| Type | Purpose |
|------|---------|
| `AnnounceFrame` (0x30), `ResolveFrame` (0x31), `GraphFrame` (0x32) | NDP frame set |
| `NdpAddress`, `NdpResolveResult`, `NdpGraphNode` | Supporting records |
| `INdpRegistry`, `InMemoryNdpRegistry` | TTL-evicting registry (lazy purge) |
| `NdpAnnounceValidator`, `NdpAnnounceResult` | Signature verification |
| `NdpErrorCodes` | `NDP-*` error code constants |

## Documentation

- **Full API reference:** [`doc/NPS.NDP.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.NDP.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-4 NDP](https://github.com/labacacia/nps/blob/main/spec/NPS-4-NDP.md)

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
