English | [中文版](./README.cn.md)

# NPS .NET Reference Implementation

C# / .NET 10 reference implementation for the Neural Protocol Suite.

## Projects

| Project | Version | Description |
|---------|---------|-------------|
| `NPS.Core` | 1.0.0-alpha.2 | Shared frame types (AnchorFrame, DiffFrame, StreamFrame, CapsFrame, HelloFrame, ErrorFrame), encoding/decoding (JSON/MsgPack), AnchorFrame cache, frame registry |
| `NPS.NWP` | 1.0.0-alpha.2 | Neural Web Protocol — NWM manifest, Query/Action frames, Memory/Action/Complex/Gateway Node middleware |
| `NPS.NIP` | 1.0.0-alpha.2 | Neural Identity Protocol — CA, key generation, certificate issuance/revocation, OCSP, CRL |
| `NPS.NDP` | 1.0.0-alpha.2 | Neural Discovery Protocol — announce/resolve frames, in-memory registry, Ed25519 validation |
| `NPS.NOP` | 1.0.0-alpha.2 | Neural Orchestration Protocol — Task/Delegate/Sync/AlignStream frames, DAG validator, orchestration engine |

## Build

```bash
dotnet build NPS.sln
```

## Test

```bash
dotnet test
```

## Status

Under active development (v1.0.0-alpha.2). All five protocol packages are implemented; 495 tests passing.

Includes NCP native-mode HelloFrame (0x06) handshake, Tier-1 JSON / Tier-2 MsgPack codecs, AnchorFrame JCS canonicalization, NWP Memory/Action/Complex/Gateway Nodes, NIP CA + OCSP/CRL, NDP registry + announce validation, NOP DAG orchestration with delegation/sync/alignment.
