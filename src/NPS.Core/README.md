# LabAcacia.NPS.Core

> Core frame types, codec pipeline, and AnchorFrame cache for the **NPS — Neural Protocol Suite**.
>
> [![NuGet](https://img.shields.io/nuget/v/LabAcacia.NPS.Core.svg)](https://www.nuget.org/packages/LabAcacia.NPS.Core/)
> Target: `net10.0` · License: Apache-2.0 · Spec: [NPS-1 NCP v0.5](https://github.com/labacacia/nps/blob/main/spec/NPS-1-NCP.md)

This is the foundation package every other NPS library depends on. It implements
the wire framing (4-byte default / 8-byte extended headers), the encoding tiers
(Tier-1 JSON / Tier-2 MsgPack), the NCP frame set (`AnchorFrame`, `DiffFrame`,
`StreamFrame`, `CapsFrame`, `ErrorFrame`, `HelloFrame`), and the content-addressed
`AnchorIdComputer` + cache used by NCP Schema Anchoring.

## Install

```bash
dotnet add package LabAcacia.NPS.Core
```

## Quick start

```csharp
using NPS.Core;
using NPS.Core.Codec;
using NPS.Core.Frames;
using NPS.Core.Registry;
using Microsoft.Extensions.DependencyInjection;

// Build the frame registry + codec
var registry = new FrameRegistryBuilder().AddNcp().Build();
var codec    = new NpsFrameCodec(registry);

// Encode & decode an AnchorFrame
var anchor = new AnchorFrame
{
    AnchorId = "sha256:…",                 // computed by AnchorIdComputer
    Schema   = new FrameSchema { /* … */ },
    Ttl      = 3600,
};
byte[] bytes = codec.Encode(anchor, EncodingTier.MsgPack);
IFrame decoded = codec.Decode(bytes);

// Wire it into DI
var services = new ServiceCollection()
    .AddNpsCore()       // registry + codec + AnchorFrameCache
    .BuildServiceProvider();
```

## Key types

| Type | Purpose |
|------|---------|
| `IFrame`, `FrameType`, `FrameFlags` | Frame contract and header bits |
| `FrameHeader` | Parse / write the 4/8-byte header (EXT=0x80 for >64 KiB payloads) |
| `NpsFrameCodec`, `Tier1JsonCodec`, `Tier2MsgPackCodec` | Encoder / decoder pipeline |
| `AnchorFrame`, `DiffFrame`, `StreamFrame`, `CapsFrame`, `ErrorFrame`, `HelloFrame` | NCP frame set |
| `AnchorIdComputer`, `AnchorFrameCache` | Content-addressed schema anchoring |
| `FrameRegistry`, `FrameRegistryBuilder` | Pluggable frame type registration |
| `NpsStatusCodes`, `NcpErrorCodes` | Protocol status + error codes |

## Documentation

- **Full API reference:** [`doc/NPS.Core.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/NPS.Core.md)
- **SDK overview:** [`doc/overview.md`](https://github.com/labacacia/NPS-sdk-dotnet/blob/main/doc/overview.md)
- **Spec:** [NPS-1 NCP](https://github.com/labacacia/nps/blob/main/spec/NPS-1-NCP.md)

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
